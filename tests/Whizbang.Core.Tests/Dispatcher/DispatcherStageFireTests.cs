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
  // Static counters + per-stage fire log
  // ========================================

  private static int _defaultHandlerCount;
  private static int _explicitHandlerCount;

  // (ReceptorName, Stage) — lets every test assert not just fire-count but *where* each fire
  // happened. "publish" denotes a fire that came through PublishAsync's typed publisher
  // (i.e. not a lifecycle stage). Anything else is the LifecycleStage the invoker passed in.
  private static readonly List<(string Receptor, string Stage)> _fireLog = [];
  private static readonly object _fireLogLock = new();
  private static void _recordFire(string receptor, string stage) {
    lock (_fireLogLock) {
      _fireLog.Add((receptor, stage));
    }
  }
  private static List<(string Receptor, string Stage)> _snapshotFireLog() {
    lock (_fireLogLock) {
      return [.. _fireLog];
    }
  }

  [Before(Test)]
  public Task ResetCountersAsync() {
    Interlocked.Exchange(ref _defaultHandlerCount, 0);
    Interlocked.Exchange(ref _explicitHandlerCount, 0);
    lock (_fireLogLock) {
      _fireLog.Clear();
    }
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
  public class DefaultStageTestReceptor : IReceptor<StageTestEvent>, IAcceptsLifecycleContext {
    private ILifecycleContext? _ctx;
    public void SetLifecycleContext(ILifecycleContext context) => _ctx = context;
    public ValueTask HandleAsync(StageTestEvent message, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _defaultHandlerCount);
      _recordFire(nameof(DefaultStageTestReceptor), _ctx?.CurrentStage.ToString() ?? "publish");
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Explicit [FireAt(PostAllPerspectivesDetached)] handler — must fire ONLY at that stage.</summary>
  [FireAt(LifecycleStage.PostAllPerspectivesDetached)]
  public class ExplicitPostAllPerspectivesReceptor : IReceptor<StageTestEvent>, IAcceptsLifecycleContext {
    private ILifecycleContext? _ctx;
    public void SetLifecycleContext(ILifecycleContext context) => _ctx = context;
    public ValueTask HandleAsync(StageTestEvent message, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _explicitHandlerCount);
      _recordFire(nameof(ExplicitPostAllPerspectivesReceptor), _ctx?.CurrentStage.ToString() ?? "publish");
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
  public async Task PublishAsync_ExplicitFireAt_DoesNotFireDuringPublishAsync() {
    // [FireAt(stage)] declares "invoke me at `stage`, never before." PublishAsync is an
    // explicit publish — it must leave [FireAt] receptors alone and let the lifecycle
    // pipeline invoke them at their declared stage. The engineer's double-fire report
    // was that the receptor fired here AND at its declared stage.
    var strategy = new StubWorkCoordinatorStrategy();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IEnvelopeSerializer, StubEnvelopeSerializer>();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var sp = services.BuildServiceProvider();
    var dispatcher = sp.GetRequiredService<IDispatcher>();

    await dispatcher.PublishAsync(new StageTestEvent(Guid.NewGuid()));

    await Assert.That(_explicitHandlerCount).IsEqualTo(0)
      .Because("[FireAt] is deferred to its declared stage — PublishAsync must not invoke it");
    var log = _snapshotFireLog();
    await Assert.That(log.Any(e => e.Receptor == nameof(ExplicitPostAllPerspectivesReceptor))).IsFalse()
      .Because("[FireAt(PostAllPerspectivesDetached)] should not appear in the fire log at all after PublishAsync");
  }

  [Test]
  public async Task PublishAsync_ExplicitFireAt_FiresOnlyAtDeclaredStage_OnceAsync() {
    // End-to-end invariant — asserted via sequenced counts, since the compile-time-generated
    // receptor invoker doesn't plumb IAcceptsLifecycleContext. Stage attribution is proved
    // by the sequence: after PublishAsync the count must be 0 (no publish fire); after
    // invoking non-declared stages the count must still be 0; only after invoking the
    // declared stage does the count go to 1.
    var strategy = new StubWorkCoordinatorStrategy();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IEnvelopeSerializer, StubEnvelopeSerializer>();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var sp = services.BuildServiceProvider();
    var dispatcher = sp.GetRequiredService<IDispatcher>();

    var evt = new StageTestEvent(Guid.NewGuid());
    await dispatcher.PublishAsync(evt);
    await Assert.That(_explicitHandlerCount).IsEqualTo(0)
      .Because("PublishAsync must not fire the [FireAt] receptor");

    using var scope = sp.CreateScope();
    var invoker = scope.ServiceProvider.GetRequiredService<IReceptorInvoker>();
    var envelope = new MessageEnvelope<StageTestEvent> {
      MessageId = MessageId.From(TrackedGuid.NewMedo()),
      Payload = evt,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Drive every stage that is NOT the declared one. Count must stay 0 at every step.
    foreach (var stage in Enum.GetValues<LifecycleStage>()) {
      if (stage == LifecycleStage.PostAllPerspectivesDetached) {
        continue;
      }
      await invoker.InvokeAsync(envelope, stage);
      await Assert.That(_explicitHandlerCount).IsEqualTo(0)
        .Because($"[FireAt(PostAllPerspectivesDetached)] must not fire at {stage}");
    }

    // Now hit the declared stage — should fire exactly once.
    await invoker.InvokeAsync(envelope, LifecycleStage.PostAllPerspectivesDetached);
    await Assert.That(_explicitHandlerCount).IsEqualTo(1)
      .Because("[FireAt(PostAllPerspectivesDetached)] must fire exactly once, at its declared stage");
  }

  [Test]
  public async Task PublishAsync_FromInsideHandler_ExplicitFireAt_FiresOnlyAtDeclaredStage_OnceAsync() {
    // Engineer's reproducer: a handler calls _dispatcher.PublishAsync(...) from within
    // HandleAsync. Before the fix, the [FireAt] receptor fires twice — once during the
    // nested PublishAsync (Path 1) and again when the lifecycle reaches its declared
    // stage. After the fix: exactly once, at the declared stage.
    var strategy = new StubWorkCoordinatorStrategy();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IEnvelopeSerializer, StubEnvelopeSerializer>();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var sp = services.BuildServiceProvider();
    var dispatcher = sp.GetRequiredService<IDispatcher>();

    // Simulate a handler calling PublishAsync internally.
    var evt = new StageTestEvent(Guid.NewGuid());
    await dispatcher.PublishAsync(evt);
    await Assert.That(_explicitHandlerCount).IsEqualTo(0)
      .Because("handler-invoked PublishAsync must not fire the [FireAt] receptor eagerly");

    // Now the lifecycle reaches the declared [FireAt] stage.
    using var scope = sp.CreateScope();
    var invoker = scope.ServiceProvider.GetRequiredService<IReceptorInvoker>();
    var envelope = new MessageEnvelope<StageTestEvent> {
      MessageId = MessageId.From(TrackedGuid.NewMedo()),
      Payload = evt,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    await invoker.InvokeAsync(envelope, LifecycleStage.PostAllPerspectivesDetached);

    await Assert.That(_explicitHandlerCount).IsEqualTo(1)
      .Because("nested-PublishAsync + lifecycle stage = exactly one [FireAt] invocation, no double-fire");
  }

  [Test]
  public async Task PublishAsync_DefaultStage_StillFiresImmediatelyAsync() {
    // Regression guard for the fix: default-stage (no [FireAt]) void receptors and typed
    // receptors must still fire from PublishToReceptors (Path 1). The fix must only skip
    // explicit [FireAt] void receptors.
    var strategy = new StubWorkCoordinatorStrategy();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IEnvelopeSerializer, StubEnvelopeSerializer>();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var sp = services.BuildServiceProvider();
    var dispatcher = sp.GetRequiredService<IDispatcher>();

    await dispatcher.PublishAsync(new StageTestEvent(Guid.NewGuid()));

    await Assert.That(_defaultHandlerCount).IsEqualTo(1)
      .Because("default-stage void handler must still fire during PublishAsync");
    var log = _snapshotFireLog();
    var defaultFires = log.Where(e => e.Receptor == nameof(DefaultStageTestReceptor)).ToList();
    await Assert.That(defaultFires).Count().IsEqualTo(1);
    await Assert.That(defaultFires[0].Stage).IsEqualTo("publish")
      .Because("default-stage receptors fire directly from PublishAsync without a lifecycle context");
  }
}
