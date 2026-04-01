using System.Text.Json;
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
/// Tests that explicit [FireAt] receptors do NOT fire during default cascade dispatch,
/// and that default-stage receptors DO fire.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[NotInParallel("StageFireTests")]
public class DispatcherStageFireTests {

  // ========================================
  // Test Messages
  // ========================================

  public record StageTestCommand(Guid EntityId);
  public record StageTestEvent([property: StreamId] Guid EntityId) : IEvent;

  // ========================================
  // Static counters
  // ========================================

  private static int _defaultHandlerCount;
  private static int _explicitHandlerCount;

  [Before(Test)]
  public Task ResetCountersAsync() {
    Interlocked.Exchange(ref _defaultHandlerCount, 0);
    Interlocked.Exchange(ref _explicitHandlerCount, 0);
    return Task.CompletedTask;
  }

  // ========================================
  // Receptors (discovered by source generator)
  // ========================================

  /// <summary>Command receptor that returns an unwrapped event.</summary>
  public class StageTestCommandHandler : IReceptor<StageTestCommand, StageTestEvent> {
    public ValueTask<StageTestEvent> HandleAsync(StageTestCommand message, CancellationToken cancellationToken) {
      return ValueTask.FromResult(new StageTestEvent(message.EntityId));
    }
  }

  /// <summary>Default-stage void handler (no [FireAt]) — should fire during cascade.</summary>
  public class DefaultStageTestReceptor : IReceptor<StageTestEvent> {
    public ValueTask HandleAsync(StageTestEvent message, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _defaultHandlerCount);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Explicit [FireAt(PostAllPerspectivesDetached)] handler — should NOT fire during cascade.</summary>
  [FireAt(LifecycleStage.PostAllPerspectivesDetached)]
  public class ExplicitPostAllPerspectivesReceptor : IReceptor<StageTestEvent> {
    public ValueTask HandleAsync(StageTestEvent message, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _explicitHandlerCount);
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
  // DEFAULT DISPATCH: cascade from receptor return
  // ========================================

  [Test]
  public async Task DefaultDispatch_DefaultHandler_FiresAsync() {
    // Arrange — dispatch command, cascade produces event, default handler should fire
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
    await dispatcher.LocalInvokeAsync<StageTestEvent>(new StageTestCommand(Guid.NewGuid()));

    // Assert — default handler fires
    await Assert.That(_defaultHandlerCount).IsEqualTo(1)
      .Because("Default-stage handler should fire during cascade");
  }

  [Test]
  public async Task DefaultDispatch_ExplicitFireAtPostAllPerspectives_DoesNotFireAsync() {
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
    await dispatcher.LocalInvokeAsync<StageTestEvent>(new StageTestCommand(Guid.NewGuid()));

    // Assert — explicit [FireAt(PostAllPerspectives)] handler should NOT fire during cascade
    await Assert.That(_explicitHandlerCount).IsEqualTo(0)
      .Because("Explicit [FireAt(PostAllPerspectivesDetached)] should not fire during default cascade dispatch");
  }

  [Test]
  public async Task DefaultDispatch_OutboxMessageCreatedAsync() {
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
    await dispatcher.LocalInvokeAsync<StageTestEvent>(new StageTestCommand(Guid.NewGuid()));

    // Assert — event still goes to outbox
    await Assert.That(strategy.QueuedOutboxMessages.Count).IsGreaterThanOrEqualTo(1)
      .Because("Cascaded event should still be written to outbox for transport");
  }

  // ========================================
  // PUBLISHASYNC: void receptors fire via PublishAsync
  // ========================================

  [Test]
  public async Task PublishAsync_DefaultVoidHandler_FiresAsync() {
    // Arrange — PublishAsync should invoke default-stage void handlers
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
    await dispatcher.PublishAsync(new StageTestEvent(Guid.NewGuid()));

    // Assert — default void handler fires via PublishAsync
    await Assert.That(_defaultHandlerCount).IsEqualTo(1)
      .Because("Default-stage void handler should fire via PublishAsync");
  }

  [Test]
  public async Task PublishAsync_ExplicitFireAtVoidHandler_FiresAsync() {
    // Arrange — PublishAsync is an explicit publish, not cascade. All handlers should fire.
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
    await dispatcher.PublishAsync(new StageTestEvent(Guid.NewGuid()));

    // Assert — explicit [FireAt] handler also fires via PublishAsync (not cascade, no IsDefaultDispatch filtering)
    await Assert.That(_explicitHandlerCount).IsEqualTo(1)
      .Because("Explicit [FireAt] void handler should fire via PublishAsync (not a cascade path)");
  }
}
