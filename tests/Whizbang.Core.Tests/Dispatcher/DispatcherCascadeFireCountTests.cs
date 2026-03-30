using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
/// Tests that verify a cascaded event fires its handler exactly once.
/// Proves the bug: default cascade fires handler at BOTH the local path
/// AND the PreOutbox worker path, causing double (or more) invocations.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
public class DispatcherCascadeFireCountTests {

  // ========================================
  // Test Messages
  // ========================================

  public record FireCountCommand(Guid EntityId);
  public record FireCountEvent([property: StreamId] Guid EntityId) : IEvent;

  // ========================================
  // Static invocation counter (thread-safe)
  // ========================================

  private static int _handlerFireCount;

  [Before(Test)]
  public Task ResetCounterAsync() {
    Interlocked.Exchange(ref _handlerFireCount, 0);
    return Task.CompletedTask;
  }

  // ========================================
  // Receptors (discovered by source generator)
  // ========================================

  /// <summary>
  /// Command receptor that returns an unwrapped event (default cascade).
  /// </summary>
  public class FireCountCommandHandler : IReceptor<FireCountCommand, FireCountEvent> {
    public ValueTask<FireCountEvent> HandleAsync(FireCountCommand message, CancellationToken cancellationToken) {
      return ValueTask.FromResult(new FireCountEvent(message.EntityId));
    }
  }

  /// <summary>
  /// Void event handler that increments the fire counter.
  /// This is the handler that should fire exactly ONCE per event.
  /// </summary>
  public class FireCountEventReceptor : IReceptor<FireCountEvent> {
    public ValueTask HandleAsync(FireCountEvent message, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _handlerFireCount);
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
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }
  }

  private sealed class StubEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var jsonElement = System.Text.Json.JsonSerializer.SerializeToElement(new { });
      var jsonEnvelope = new MessageEnvelope<System.Text.Json.JsonElement> {
        MessageId = envelope.MessageId,
        Payload = jsonElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      };
      return new SerializedEnvelope(
        jsonEnvelope,
        typeof(MessageEnvelope<>).MakeGenericType(typeof(TMessage)).AssemblyQualifiedName!,
        typeof(TMessage).AssemblyQualifiedName!
      );
    }

    public object DeserializeMessage(MessageEnvelope<System.Text.Json.JsonElement> jsonEnvelope, string messageTypeName) {
      throw new NotImplementedException();
    }
  }

  // ========================================
  // RED TEST: Handler fires more than once for cascaded event
  // ========================================

  [Test]
  public async Task Cascade_DefaultEvent_HandlerFiresExactlyOnceAsync() {
    // Arrange — command receptor returns unwrapped event, void event handler counts invocations
    var strategy = new StubWorkCoordinatorStrategy();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IEnvelopeSerializer, StubEnvelopeSerializer>();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var sp = services.BuildServiceProvider();
    var dispatcher = sp.GetRequiredService<IDispatcher>();

    // Act — dispatch command, receptor returns event, cascade should fire event handler
    var command = new FireCountCommand(Guid.NewGuid());
    await dispatcher.LocalInvokeAsync<FireCountEvent>(command);

    // Assert — handler should fire exactly ONCE
    // RED: currently fires 0 (mode=Outbox, no LocalDispatch) or >1 (PreOutbox re-fires)
    await Assert.That(_handlerFireCount).IsEqualTo(1)
      .Because("A cascaded event should fire its handler exactly once, not zero times or multiple times");
  }

  [Test]
  public async Task Cascade_DefaultEvent_OutboxMessageCreatedAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IEnvelopeSerializer, StubEnvelopeSerializer>();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var sp = services.BuildServiceProvider();
    var dispatcher = sp.GetRequiredService<IDispatcher>();

    // Act
    var command = new FireCountCommand(Guid.NewGuid());
    await dispatcher.LocalInvokeAsync<FireCountEvent>(command);

    // Assert — event should also be written to outbox for transport delivery
    await Assert.That(strategy.QueuedOutboxMessages.Count).IsGreaterThanOrEqualTo(1)
      .Because("Cascaded events should go to the outbox for cross-service delivery");
  }
}
