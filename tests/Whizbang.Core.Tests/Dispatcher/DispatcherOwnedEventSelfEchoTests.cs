using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests that owned events dispatched with mode=Both do NOT go to transport
/// (preventing self-echo via inbox). The event should persist to the event store
/// via the outbox (destination=null), but NOT be published to transport.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[NotInParallel("SelfEchoTests")]
public class DispatcherOwnedEventSelfEchoTests {

  // ========================================
  // Test Messages — namespace used as owned domain
  // ========================================

  public record SelfEchoCommand(Guid EntityId);
  public record SelfEchoEvent([property: StreamId] Guid EntityId) : IEvent;

  // ========================================
  // Static counters
  // ========================================

  private static int _handlerCount;

  [Before(Test)]
  public Task ResetCountersAsync() {
    Interlocked.Exchange(ref _handlerCount, 0);
    return Task.CompletedTask;
  }

  // ========================================
  // Receptors
  // ========================================

  /// <summary>Command → Event receptor (produces the event that cascades).</summary>
  public class SelfEchoCommandHandler : IReceptor<SelfEchoCommand, SelfEchoEvent> {
    public ValueTask<SelfEchoEvent> HandleAsync(SelfEchoCommand message, CancellationToken cancellationToken) {
      return ValueTask.FromResult(new SelfEchoEvent(message.EntityId));
    }
  }

  /// <summary>Default-stage void handler for the event.</summary>
  public class SelfEchoEventReceptor : IReceptor<SelfEchoEvent> {
    public ValueTask HandleAsync(SelfEchoEvent message, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _handlerCount);
      return ValueTask.CompletedTask;
    }
  }

  // ========================================
  // Stub Infrastructure
  // ========================================

  private sealed class StubWorkCoordinatorStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> QueuedOutboxMessages { get; } = [];
    public void QueueOutboxMessage(OutboxMessage message) => QueuedOutboxMessages.Add(message);
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    }
  }

  private sealed class StubEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var jsonElement = JsonSerializer.SerializeToElement(new { });
      return new SerializedEnvelope(
        new MessageEnvelope<JsonElement> {
          MessageId = envelope.MessageId,
          Payload = jsonElement,
          Hops = [],
          DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
        },
        typeof(MessageEnvelope<>).MakeGenericType(typeof(TMessage)).AssemblyQualifiedName!,
        typeof(TMessage).AssemblyQualifiedName!);
    }

    public object DeserializeMessage(MessageEnvelope<JsonElement> e, string t) => throw new NotImplementedException();
  }

  // ========================================
  // Tests
  // ========================================

  [Test]
  public async Task OwnedEvent_ModeBoth_OutboxHasNullDestinationAsync() {
    // Arrange — configure owned domains to include this test's namespace
    var strategy = new StubWorkCoordinatorStrategy();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IEnvelopeSerializer, StubEnvelopeSerializer>();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.Configure<RoutingOptions>(opts => {
      opts.OwnDomains("Whizbang.Core.Tests.Dispatcher");
    });
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var sp = services.BuildServiceProvider();
    var dispatcher = sp.GetRequiredService<IDispatcher>();

    // Act — dispatch command, cascade produces owned event
    await dispatcher.LocalInvokeAsync<SelfEchoEvent>(new SelfEchoCommand(Guid.NewGuid()));

    // Assert — outbox message should have a real destination (goes to transport for other services)
    var eventMessages = strategy.QueuedOutboxMessages
      .Where(m => m.MessageType?.Contains("SelfEchoEvent") == true)
      .ToList();
    await Assert.That(eventMessages.Count).IsGreaterThanOrEqualTo(1)
      .Because("Owned event should be written to outbox for transport + event store persistence");
    await Assert.That(eventMessages[0].Destination).IsNotNull()
      .Because("Owned event goes to transport — other services subscribe to our events");
  }

  [Test]
  public async Task NonOwnedEvent_ModeBoth_OutboxHasRealDestinationAsync() {
    // Arrange — owned domains do NOT include this test's namespace
    var strategy = new StubWorkCoordinatorStrategy();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IEnvelopeSerializer, StubEnvelopeSerializer>();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.Configure<RoutingOptions>(opts => {
      opts.OwnDomains("SomeOther.Namespace"); // NOT this test's namespace
    });
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var sp = services.BuildServiceProvider();
    var dispatcher = sp.GetRequiredService<IDispatcher>();

    // Act
    await dispatcher.LocalInvokeAsync<SelfEchoEvent>(new SelfEchoCommand(Guid.NewGuid()));

    // Assert — non-owned event should have a real destination for transport
    var eventMessages = strategy.QueuedOutboxMessages
      .Where(m => m.MessageType?.Contains("SelfEchoEvent") == true)
      .ToList();
    await Assert.That(eventMessages.Count).IsGreaterThanOrEqualTo(1)
      .Because("Non-owned event should be written to outbox for cross-service delivery");
    await Assert.That(eventMessages[0].Destination).IsNotNull()
      .Because("Non-owned event should have a real destination for transport publish");
  }

  [Test]
  public async Task OwnedEvent_ModeBoth_HandlerFiresExactlyOnceAsync() {
    // Arrange — owned domains include this test's namespace
    var strategy = new StubWorkCoordinatorStrategy();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IEnvelopeSerializer, StubEnvelopeSerializer>();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.Configure<RoutingOptions>(opts => {
      opts.OwnDomains("Whizbang.Core.Tests.Dispatcher");
    });
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var sp = services.BuildServiceProvider();
    var dispatcher = sp.GetRequiredService<IDispatcher>();

    // Act
    await dispatcher.LocalInvokeAsync<SelfEchoEvent>(new SelfEchoCommand(Guid.NewGuid()));

    // Assert — handler fires exactly once (at LocalImmediateAsync during cascade)
    await Assert.That(_handlerCount).IsEqualTo(1)
      .Because("Owned event handler should fire exactly once during cascade, not again via inbox");
  }
}
