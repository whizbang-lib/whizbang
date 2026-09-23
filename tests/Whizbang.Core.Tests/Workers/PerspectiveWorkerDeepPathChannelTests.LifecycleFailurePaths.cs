using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Execution;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Options;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// What a failing lifecycle collaborator costs, on the two paths where the worker treats that
/// failure differently: the fire-and-forget detached stage, whose failure is recorded and goes no
/// further, and the load of the events a completed run just processed, whose failure is recorded
/// and then re-raised so the stream is reported failed instead of checkpointed.
/// </summary>
/// <remarks>
/// Both are last-resort handlers that no happy-path test reaches. They differ in the decision that
/// matters to a consumer: a detached receptor that throws must not cost the batch its checkpoint,
/// while an event load that throws must cost it, because the events the PostPerspective receptors
/// would have seen are missing and advancing the cursor would silently skip them.
/// </remarks>
public partial class PerspectiveWorkerDeepPathChannelTests {

  [Test]
  public async Task Worker_DetachedPrePerspectiveStageThrows_IsRecordedAndTheBatchStillCheckpointsAsync() {
    // Arrange — no ILifecycleCoordinator, so PrePerspective takes the direct-invocation fallback
    // and fires PrePerspectiveDetached as fire-and-forget. That invocation throws; everything the
    // pipeline does afterwards must be unaffected.
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.DetachedStageFailure";
    var boom = new InvalidOperationException("detached receptor blew up");

    var coordinator = new RecordingWorkCoordinator();
    var instanceProvider = new FakeInstanceProvider();
    var runner = new RecordingRunner();
    var registry = new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]);
    var eventStore = new SequencedEventStore();
    eventStore.EnqueueResponse([_envelope(eventId, new DeepChannelEvent("detached-failure"))]);
    var invoker = new StageFailingInvoker(LifecycleStage.PrePerspectiveDetached, boom);
    var logger = new EventIdSignalingLogger<PerspectiveWorker>();

    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddSingleton<IReceptorInvoker>(invoker);
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();

    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      completionStrategy: new InstantCompletionStrategy(logger: NullLogger<InstantCompletionStrategy>.Instance),
      eventTypeProvider: new ListEventTypeProvider([typeof(DeepChannelEvent)]),
      syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
      syncEventTracker: new SyncEventTracker(),
      logger: logger,
      snapshotStore: NullPerspectiveSnapshotStore.Instance,
      streamLocker: NullPerspectiveStreamLocker.Instance,
      streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
      streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
      processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
      workChannelWriter: new WorkChannelWriter(),
      rewindOptions: Options.Create(new PerspectiveRewindOptions()),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
      perspectiveDrainChannel: harness.DrainChannel,
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      perspectiveNotificationListener: new NoOpWorkNotificationListener(),
      governor: PerspectiveWorker.CreateDefaultGovernor((Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 })).Value));

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueWorkAsync(new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastProcessedEventId = null,
      PartitionNumber = 1
    }, cts.Token);

    await coordinator.FirstCompletion.WaitAsync(TimeSpan.FromSeconds(10));
    await invoker.WaitForStageAsync(LifecycleStage.PostLifecycleInline, TimeSpan.FromSeconds(10));
    // Deterministic join on the fire-and-forget task itself — the handler under test runs inside it.
    await worker.DrainDetachedAsync();
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    // Assert — the detached failure was recorded, naming the stage and carrying the exception
    var recorded = logger.Lines
      .Where(l => l.Message.Contains("Detached lifecycle stage", StringComparison.Ordinal))
      .ToList();
    await Assert.That(recorded).IsNotEmpty()
      .Because("A detached lifecycle stage that throws must leave a record — it has no other trace");
    await Assert.That(recorded.Any(l =>
        l.Message.Contains(nameof(LifecycleStage.PrePerspectiveDetached), StringComparison.Ordinal)
        && ReferenceEquals(l.Exception, boom))).IsTrue()
      .Because("The record must name the stage that failed and carry the exception that failed it");
    await Assert.That(recorded.All(l => l.Level == LogLevel.Error)).IsTrue();

    // Assert — and the batch was otherwise untouched: the runner ran, the cursor was checkpointed,
    // nothing was reported as failed, and the inline stages after the detached one still fired.
    await Assert.That(runner.RunCallCount).IsEqualTo(1);
    await Assert.That(coordinator.Completions.Count).IsGreaterThanOrEqualTo(1)
      .Because("A fire-and-forget stage failure must not cost the batch its checkpoint");
    await Assert.That(coordinator.Failures.Count).IsEqualTo(0);
    await Assert.That(invoker.HasFired(LifecycleStage.PrePerspectiveInline)).IsTrue();
    await Assert.That(invoker.HasFired(LifecycleStage.PostLifecycleInline)).IsTrue();
  }

  [Test]
  public async Task Worker_ProcessedEventLoadThrows_ReportsTheStreamFailedInsteadOfCheckpointingAsync() {
    // Arrange — the runner completes, but the read-back of the events it just processed (the set
    // the PostPerspective receptors are handed) fails. Advancing the cursor here would skip those
    // events forever, so the failure must be recorded and re-raised, not swallowed.
    var streamId = Guid.CreateVersion7();
    var workId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.ProcessedLoadFailure";
    var unavailable = new InvalidOperationException("event store unavailable");

    var coordinator = new RecordingWorkCoordinator();
    var instanceProvider = new FakeInstanceProvider();
    var runner = new RecordingRunner();
    var registry = new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]);
    var eventStore = new FailingReadBackEventStore(
      [_envelope(Guid.CreateVersion7(), new DeepChannelEvent("read-back-failure"))], unavailable);
    var logger = new EventIdSignalingLogger<PerspectiveWorker>();

    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddSingleton<IReceptorInvoker>(new NoOpInvoker());
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();

    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      completionStrategy: new InstantCompletionStrategy(logger: NullLogger<InstantCompletionStrategy>.Instance),
      eventTypeProvider: new ListEventTypeProvider([typeof(DeepChannelEvent)]),
      syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
      syncEventTracker: new SyncEventTracker(),
      logger: logger,
      snapshotStore: NullPerspectiveSnapshotStore.Instance,
      streamLocker: NullPerspectiveStreamLocker.Instance,
      streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
      streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
      processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
      workChannelWriter: new WorkChannelWriter(),
      rewindOptions: Options.Create(new PerspectiveRewindOptions()),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
      perspectiveDrainChannel: harness.DrainChannel,
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      perspectiveNotificationListener: new NoOpWorkNotificationListener(),
      governor: PerspectiveWorker.CreateDefaultGovernor((Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 })).Value));

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueWorkAsync(new PerspectiveWork {
      WorkId = workId,
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastProcessedEventId = null,
      PartitionNumber = 1
    }, cts.Token);

    await coordinator.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    // Assert — the read-back failure was recorded against the perspective and the stream
    var lifecycleErrors = logger.LinesWith(22);
    await Assert.That(lifecycleErrors).IsNotEmpty()
      .Because("A failed read-back of the just-processed events must be recorded before it is re-raised");
    await Assert.That(lifecycleErrors.Any(l =>
        l.Message.Contains(perspectiveName, StringComparison.Ordinal)
        && l.Message.Contains(streamId.ToString(), StringComparison.Ordinal)
        && ReferenceEquals(l.Exception, unavailable))).IsTrue()
      .Because("The record must name the perspective and stream it belongs to, and carry the cause");

    // Assert — and the re-raise is what turned a completed run into a reported failure rather
    // than a checkpoint: no completion, a Failed cursor carrying the cause, and the leased row
    // parked so it gets a retry with backoff instead of re-claiming forever.
    await Assert.That(eventStore.ReadBackAttempts).IsGreaterThanOrEqualTo(1);
    await Assert.That(coordinator.Completions.Count).IsEqualTo(0)
      .Because("A stream whose processed events could not be read back must not advance its cursor");
    coordinator.Failures.TryPeek(out var failure);
    await Assert.That(failure).IsNotNull();
    await Assert.That(failure!.PerspectiveName).IsEqualTo(perspectiveName);
    await Assert.That(failure.StreamId).IsEqualTo(streamId);
    await Assert.That(failure.Status).IsEqualTo(PerspectiveProcessingStatus.Failed);
    await Assert.That(failure.Error).IsEqualTo(unavailable.Message);
    await Assert.That(harness.FailureCapture.Items.Any(i => i.failure.MessageId == workId)).IsTrue()
      .Because("The leased row is parked so the failure is recorded against it, not just against the cursor");
  }

  [Test]
  public async Task Worker_PriorCyclePostLifecycleFaulted_IsRecordedAndTheCurrentCycleStillRunsAsync() {
    // Arrange — no ILifecycleCoordinator, so PostLifecycle takes the direct-invocation fallback,
    // which runs on a background task with nothing guarding it. The inline PostLifecycle receptor
    // throws, so the task the first cycle leaves behind is faulted when the second cycle picks it
    // up. Both cycles are driven through the batch entry point directly, so "the first cycle's
    // task exists before the second cycle starts" is ordering, not timing.
    var firstStreamId = Guid.CreateVersion7();
    var secondStreamId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.PriorPostLifecycleFault";
    var boom = new InvalidOperationException("post-lifecycle receptor blew up");

    var coordinator = new RecordingWorkCoordinator();
    var instanceProvider = new FakeInstanceProvider();
    var runner = new RecordingRunner();
    var registry = new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]);
    var eventStore = new PerStreamEventStore(new Dictionary<Guid, MessageEnvelope<IEvent>> {
      [firstStreamId] = _envelope(Guid.CreateVersion7(), new DeepChannelEvent("first-cycle")),
      [secondStreamId] = _envelope(Guid.CreateVersion7(), new DeepChannelEvent("second-cycle"))
    });
    var invoker = new StageFailingInvoker(LifecycleStage.PostLifecycleInline, boom);
    var logger = new EventIdSignalingLogger<PerspectiveWorker>();

    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddSingleton<IReceptorInvoker>(invoker);
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();

    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      completionStrategy: new InstantCompletionStrategy(logger: NullLogger<InstantCompletionStrategy>.Instance),
      eventTypeProvider: new ListEventTypeProvider([typeof(DeepChannelEvent)]),
      syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
      syncEventTracker: new SyncEventTracker(),
      logger: logger,
      snapshotStore: NullPerspectiveSnapshotStore.Instance,
      streamLocker: NullPerspectiveStreamLocker.Instance,
      streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
      streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
      processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
      workChannelWriter: new WorkChannelWriter(),
      rewindOptions: Options.Create(new PerspectiveRewindOptions()),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
      perspectiveDrainChannel: harness.DrainChannel,
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      perspectiveNotificationListener: new NoOpWorkNotificationListener(),
      governor: PerspectiveWorker.CreateDefaultGovernor((Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 })).Value));

    // Act — first cycle leaves a PostLifecycle task behind that is going to fault
    await worker.ProcessChannelBatchAsync([
      new PerspectiveWork {
        WorkId = Guid.CreateVersion7(),
        StreamId = firstStreamId,
        PerspectiveName = perspectiveName,
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ], CancellationToken.None);

    await Assert.That(worker.PendingPostLifecycle is not null).IsTrue()
      .Because("The first cycle must hand its PostLifecycle work to a background task for the next cycle to drain");

    // The second cycle drains it, finds it faulted, and has to carry on regardless.
    await worker.ProcessChannelBatchAsync([
      new PerspectiveWork {
        WorkId = Guid.CreateVersion7(),
        StreamId = secondStreamId,
        PerspectiveName = perspectiveName,
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ], CancellationToken.None);

    // Assert — the prior cycle's fault was recorded, carrying the receptor's exception
    var priorFaultLines = logger.LinesWith(59);
    await Assert.That(priorFaultLines).IsNotEmpty()
      .Because("A prior cycle's PostLifecycle fault is only ever seen here — nothing else observes that task");
    await Assert.That(priorFaultLines.Any(l => ReferenceEquals(l.Exception, boom))).IsTrue()
      .Because("The record must carry the exception that faulted the prior cycle's task");

    // Assert — and the current cycle was not derailed by it: it checkpointed its own stream and
    // installed its own PostLifecycle task for the cycle after this one.
    await Assert.That(coordinator.Completions.Count).IsEqualTo(2)
      .Because("A prior cycle's fault must not cost the current cycle its checkpoint");
    await Assert.That(coordinator.Completions.Any(c => c.StreamId == secondStreamId)).IsTrue();
    var currentPending = worker.PendingPostLifecycle;
    await Assert.That(currentPending is not null).IsTrue()
      .Because("The current cycle installs its own PostLifecycle task after draining the prior one");

    // Observe the current cycle's task so nothing is left unobserved after the test.
    try { await currentPending!; } catch (InvalidOperationException) { /* the same receptor fault, by design */ }
  }

  #region Fakes for the lifecycle failure paths

  /// <summary>Records every stage invoked and throws for exactly one of them.</summary>
  private sealed class StageFailingInvoker(LifecycleStage failingStage, Exception failure) : IReceptorInvoker {
    private readonly Lock _lock = new();
    private readonly List<LifecycleStage> _stages = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<LifecycleStage, TaskCompletionSource> _waiters = new();

    public bool HasFired(LifecycleStage stage) {
      lock (_lock) {
        return _stages.Contains(stage);
      }
    }

    public Task WaitForStageAsync(LifecycleStage stage, TimeSpan timeout) {
      var tcs = _waiters.GetOrAdd(stage, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
      if (HasFired(stage)) {
        tcs.TrySetResult();
      }
      return tcs.Task.WaitAsync(timeout);
    }

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      lock (_lock) {
        _stages.Add(stage);
      }
      if (_waiters.TryGetValue(stage, out var tcs)) {
        tcs.TrySetResult();
      }
      return stage == failingStage ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
    }
  }

  /// <summary>Answers each stream with its own event, so consecutive batches carry distinct work.</summary>
  private sealed class PerStreamEventStore(IReadOnlyDictionary<Guid, MessageEnvelope<IEvent>> byStream) : IEventStore {
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
        Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(byStream.TryGetValue(streamId, out var envelope)
        ? new List<MessageEnvelope<IEvent>> { envelope }
        : new List<MessageEnvelope<IEvent>>());

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }
    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);
  }

  /// <summary>
  /// Serves the pre-run load of upcoming events normally and fails only the read-back of the range
  /// a completed run just processed. The two are the same call; the read-back is the one that names
  /// a real upper bound, because the pre-run load reads everything after the cursor.
  /// </summary>
  private sealed class FailingReadBackEventStore(List<MessageEnvelope<IEvent>> upcoming, Exception failure) : IEventStore {
    private int _readBackAttempts;

    public int ReadBackAttempts => Volatile.Read(ref _readBackAttempts);

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
        Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      if (upToEventId == Guid.Empty) {
        return Task.FromResult(new List<MessageEnvelope<IEvent>>(upcoming));
      }
      Interlocked.Increment(ref _readBackAttempts);
      return Task.FromException<List<MessageEnvelope<IEvent>>>(failure);
    }

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }
    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);
  }

  #endregion
}
