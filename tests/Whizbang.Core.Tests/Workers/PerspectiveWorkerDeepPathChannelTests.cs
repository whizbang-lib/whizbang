using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Deep-path coverage for PerspectiveWorker's channel (per-event work) pipeline:
/// - ProcessChannelBatchAsync missing-channel guard
/// - Batch/perspective span tags + trace-context extraction from event hops
/// - Full normal path with PostLifecycle fallback (no coordinator) including detached stages
/// - Bootstrap snapshot with stream locker (lock acquire/release around bootstrap)
/// - Failure path marking sync-tracker events processed
/// - PrePerspective lifecycle error propagation
/// - PostLifecycle coordinator per-event error isolation
/// - RequestImmediatePoll redundant-call coalescing
/// - NOTIFY listener subscribe/coalesce/unsubscribe
/// - ObjectDisposedException exits the consumer loop gracefully
/// </summary>
public class PerspectiveWorkerDeepPathChannelTests {

  [Test]
  public async Task ProcessChannelBatchAsync_WithoutCompletionChannel_ThrowsInvalidOperationExceptionAsync() {
    // Arrange — worker constructed without completion/failure channels
    var services = new ServiceCollection();
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();
    var harness = new PerspectiveWorkerTestHarness();

    var worker = new PerspectiveWorker(
      instanceProvider: new FakeInstanceProvider(),
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      completionStrategy: new InstantCompletionStrategy(),
      perspectiveChannelWriter: harness.ChannelWriter,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    var workItems = new List<PerspectiveWork>();

    // Act & Assert — the guard fires before any scope/coordinator work
    await Assert.That(async () => await worker.ProcessChannelBatchAsync(workItems, CancellationToken.None))
      .Throws<InvalidOperationException>();
  }

  [Test]
  [NotInParallel("WhizbangActivityListener")]
  public async Task Worker_BatchSpansWithListener_SetsBatchTagsAndExtractsTraceContextAsync() {
    // Arrange — a real ActivityListener so StartActivity returns non-null and tag lines execute
    var started = new ConcurrentBag<Activity>();
    using var listener = new ActivityListener {
      ShouldListenTo = source => source.Name == WhizbangActivitySource.Tracing.Name,
      Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
      ActivityStarted = started.Add,
    };
    ActivitySource.AddActivityListener(listener);

    var traceId = ActivityTraceId.CreateRandom();
    var spanId = ActivitySpanId.CreateRandom();
    var traceParent = $"00-{traceId.ToHexString()}-{spanId.ToHexString()}-01";

    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.TracedPerspective";

    var coordinator = new RecordingWorkCoordinator();
    var instanceProvider = new FakeInstanceProvider();
    var runner = new RecordingRunner();
    var registry = new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]);
    var eventStore = new SequencedEventStore();
    eventStore.EnqueueResponse([_envelope(eventId, new DeepChannelEvent("traced"), traceParent)]);

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();

    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions {
        EnableWorkerBatchSpans = true,
        // Verbosity must be non-Off: IsEnabled(component) gates the perspective span on
        // Verbosity != Off && Components.HasFlag(component)
        Verbosity = TraceVerbosity.Normal,
        Components = TraceComponents.Perspectives | TraceComponents.Lifecycle
      }),
      completionStrategy: new InstantCompletionStrategy(),
      eventTypeProvider: new ListEventTypeProvider([typeof(DeepChannelEvent)]),
      logger: new AlwaysEnabledLogger(),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

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
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — batch span exists for THIS worker instance with the batch-size tag set
    var instanceIdString = instanceProvider.InstanceId.ToString();
    var batchActivities = started
      .Where(a => a.OperationName == "PerspectiveProcessWorker ProcessChannelBatch"
        && string.Equals(a.GetTagItem("whizbang.instance.id")?.ToString(), instanceIdString, StringComparison.Ordinal))
      .ToList();
    await Assert.That(batchActivities.Count).IsGreaterThanOrEqualTo(1)
      .Because("EnableWorkerBatchSpans + listener must produce the batch activity with tags");
    // The consumer loop may emit empty-poll batch spans (batch.size "0") alongside the real
    // one; assert SOME batch span carries the size-1 tag rather than relying on capture order.
    var sawBatchSizeOne = batchActivities
      .Any(a => string.Equals(a.GetTagItem("whizbang.batch.size")?.ToString(), "1", StringComparison.Ordinal));
    await Assert.That(sawBatchSizeOne).IsTrue()
      .Because("The batch span for the single-item work batch must set whizbang.batch.size = 1");

    // Assert — the perspective span was parented to the trace context extracted from the event hop
    var linkedToEventTrace = started.Any(a =>
      a.OperationName == $"Perspective {perspectiveName}"
      && a.TraceId == traceId);
    await Assert.That(linkedToEventTrace).IsTrue()
      .Because("The perspective span must adopt the traceparent extracted from the first event's Current hop");
  }

  [Test]
  public async Task Worker_NormalPathWithoutCoordinator_FiresPostLifecycleFallbackAndDetachedStagesAsync() {
    // Arrange — full pipeline: events loaded, completion reported, fallback PostLifecycle
    // (no ILifecycleCoordinator registered) including the static detached stage helper.
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.FallbackPerspective";

    var coordinator = new RecordingWorkCoordinator();
    var instanceProvider = new FakeInstanceProvider();
    var runner = new RecordingRunner();
    var registry = new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]);
    var eventStore = new SequencedEventStore();
    eventStore.EnqueueResponse([_envelope(eventId, new DeepChannelEvent("fallback"))]);
    var invoker = new StageRecordingInvoker();
    var syncSignaler = new RecordingSyncSignaler();
    var syncTracker = new RecordingSyncEventTracker();

    var services = new ServiceCollection();
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
      tracingOptions: null,
      completionStrategy: new InstantCompletionStrategy(),
      eventTypeProvider: new ListEventTypeProvider([typeof(DeepChannelEvent)]),
      syncSignaler: syncSignaler,
      syncEventTracker: syncTracker,
      logger: new AlwaysEnabledLogger(),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    PerspectiveEventProcessedEvent? processedEvent = null;
    var processedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnPerspectiveEventProcessed += e => {
      processedEvent = e;
      processedSignal.TrySetResult();
    };

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
    await processedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await invoker.WaitForStageAsync(LifecycleStage.PostLifecycleInline, TimeSpan.FromSeconds(10));
    await worker.DrainDetachedAsync();
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — the hook payload reflects the processed batch
    await Assert.That(processedEvent).IsNotNull();
    await Assert.That(processedEvent?.PerspectiveName).IsEqualTo(perspectiveName);
    await Assert.That(processedEvent?.StreamId).IsEqualTo(streamId);
    await Assert.That(processedEvent?.EventCount).IsEqualTo(1);

    // Assert — sync surfaces were signaled
    await Assert.That(syncSignaler.SignalCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(syncTracker.MarkedEventIds).Contains(eventId);
    await Assert.That(syncTracker.StreamSweeps).Contains((perspectiveName, streamId));

    // Assert — fallback PostLifecycle fired inline + detached stages
    await Assert.That(invoker.HasFired(LifecycleStage.PostPerspectiveInline)).IsTrue();
    await Assert.That(invoker.HasFired(LifecycleStage.PostLifecycleInline)).IsTrue();
    await Assert.That(invoker.HasFired(LifecycleStage.PostLifecycleDetached)).IsTrue()
      .Because("The fallback path fires PostLifecycleDetached via the static detached-stage helper");
    await Assert.That(invoker.HasFired(LifecycleStage.ImmediateDetached)).IsTrue();
  }

  [Test]
  public async Task Worker_BootstrapWithStreamLocker_AcquiresAndReleasesBootstrapLockOnceAsync() {
    // Arrange — existing cursor + snapshot store with no snapshots + locker → bootstrap under lock
    var streamId = Guid.CreateVersion7();
    var lastEventId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.BootstrapPerspective";

    var coordinator = new RecordingWorkCoordinator();
    coordinator.CursorOverrides[(perspectiveName, streamId)] = new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = lastEventId,
      Status = PerspectiveProcessingStatus.None
    };
    var instanceProvider = new FakeInstanceProvider();
    var runner = new RecordingRunner();
    var registry = new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]);
    var locker = new RecordingStreamLocker();
    var snapshotStore = new NoSnapshotStore();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();

    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      completionStrategy: new InstantCompletionStrategy(),
      snapshotStore: snapshotStore,
      streamLocker: locker,
      streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    // Act — two sequential work items for the same (stream, perspective)
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueWorkAsync(new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastProcessedEventId = lastEventId,
      PartitionNumber = 1
    }, cts.Token);
    await coordinator.WaitForCompletionsAsync(1, TimeSpan.FromSeconds(10));

    await harness.EnqueueWorkAsync(new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastProcessedEventId = lastEventId,
      PartitionNumber = 1
    }, cts.Token);
    await coordinator.WaitForCompletionsAsync(2, TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — bootstrap ran exactly once, under a "bootstrap" lock that was released
    await Assert.That(runner.BootstrapCallCount).IsEqualTo(1)
      .Because("_bootstrappedThisSession must prevent a second bootstrap for the same (stream, perspective)");
    await Assert.That(locker.AcquireCallCount).IsEqualTo(1);
    await Assert.That(locker.LastReason).IsEqualTo("bootstrap");
    await Assert.That(locker.ReleaseCallCount).IsEqualTo(1);
    await Assert.That(snapshotStore.HasAnyCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task Worker_RunnerThrowsWithUpcomingEvents_MarksSyncTrackerForFailedEventsAsync() {
    // Arrange — throwing runner + sync tracker + loaded upcoming events so the failure
    // path marks the failed event IDs as processed-by-perspective (waiter release).
    void handler(object? s, UnobservedTaskExceptionEventArgs e) {
      if (e.Exception.InnerException is InvalidOperationException ioe && ioe.Message == "deep-path run failed") {
        e.SetObserved();
      }
    }
    TaskScheduler.UnobservedTaskException += handler;
    try {
      var streamId = Guid.CreateVersion7();
      var eventId = Guid.CreateVersion7();
      const string perspectiveName = "Deep.FailingPerspective";

      var coordinator = new RecordingWorkCoordinator();
      var instanceProvider = new FakeInstanceProvider();
      var runner = new RecordingRunner { RunException = new InvalidOperationException("deep-path run failed") };
      var registry = new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]);
      var eventStore = new SequencedEventStore();
      eventStore.EnqueueResponse([_envelope(eventId, new DeepChannelEvent("fail"))]);
      var syncTracker = new RecordingSyncEventTracker();

      var services = new ServiceCollection();
      services.AddSingleton<IWorkCoordinator>(coordinator);
      services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
      services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
      services.AddSingleton<IEventStore>(eventStore);
      services.AddLogging();
      var serviceProvider = services.BuildServiceProvider();

      var harness = new PerspectiveWorkerTestHarness();
      var worker = new PerspectiveWorker(
        instanceProvider: instanceProvider,
        scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
        options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
        tracingOptions: null,
        completionStrategy: new InstantCompletionStrategy(),
        eventTypeProvider: new ListEventTypeProvider([typeof(DeepChannelEvent)]),
        syncEventTracker: syncTracker,
        perspectiveChannelWriter: harness.ChannelWriter,
        perspectiveCompletionChannel: harness.CompletionCapture,
        failureChannel: harness.FailureCapture,
        perspectiveDrainChannel: harness.DrainChannel,
        schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

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
      await coordinator.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
      cts.Cancel();
      try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { } catch (InvalidOperationException) { }

      // Assert — the sync tracker was told the failed event is done for this perspective
      await Assert.That(syncTracker.MarkedEventIds).Contains(eventId)
        .Because("Failed events must still release perspective-sync waiters");
      await Assert.That(coordinator.Failures.Count).IsGreaterThanOrEqualTo(1);
      coordinator.Failures.TryPeek(out var failure);
      await Assert.That(failure?.PerspectiveName).IsEqualTo(perspectiveName);
      await Assert.That(failure?.Error).IsEqualTo("deep-path run failed");
    } finally {
      TaskScheduler.UnobservedTaskException -= handler;
    }
  }

  [Test]
  public async Task Worker_PrePerspectiveLifecycleThrows_ReportsFailureForGroupAsync() {
    // Arrange — lifecycle coordinator whose BeginTracking throws during PrePerspective;
    // the group's catch must route the error to the failure path.
    void handler(object? s, UnobservedTaskExceptionEventArgs e) {
      if (e.Exception.InnerException is InvalidOperationException ioe && ioe.Message == "begin-tracking failed") {
        e.SetObserved();
      }
    }
    TaskScheduler.UnobservedTaskException += handler;
    try {
      var streamId = Guid.CreateVersion7();
      var eventId = Guid.CreateVersion7();
      const string perspectiveName = "Deep.PreLifecycleThrows";

      var coordinator = new RecordingWorkCoordinator();
      var instanceProvider = new FakeInstanceProvider();
      var runner = new RecordingRunner();
      var registry = new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]);
      var eventStore = new SequencedEventStore();
      eventStore.EnqueueResponse([_envelope(eventId, new DeepChannelEvent("pre-throw"))]);
      var lifecycle = new ScriptableLifecycleCoordinator { BeginTrackingException = new InvalidOperationException("begin-tracking failed") };

      var services = new ServiceCollection();
      services.AddSingleton<IWorkCoordinator>(coordinator);
      services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
      services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
      services.AddSingleton<IEventStore>(eventStore);
      services.AddSingleton<ILifecycleCoordinator>(lifecycle);
      services.AddScoped<IReceptorInvoker, NoOpInvoker>();
      services.AddLogging();
      var serviceProvider = services.BuildServiceProvider();

      var harness = new PerspectiveWorkerTestHarness();
      var worker = new PerspectiveWorker(
        instanceProvider: instanceProvider,
        scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
        options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
        tracingOptions: null,
        completionStrategy: new InstantCompletionStrategy(),
        eventTypeProvider: new ListEventTypeProvider([typeof(DeepChannelEvent)]),
        perspectiveChannelWriter: harness.ChannelWriter,
        perspectiveCompletionChannel: harness.CompletionCapture,
        failureChannel: harness.FailureCapture,
        perspectiveDrainChannel: harness.DrainChannel,
        schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

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
      await coordinator.FirstFailure.WaitAsync(TimeSpan.FromSeconds(10));
      cts.Cancel();
      try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { } catch (InvalidOperationException) { }

      // Assert — failure reported, runner never invoked (PrePerspective threw first)
      await Assert.That(coordinator.Failures.Count).IsGreaterThanOrEqualTo(1);
      coordinator.Failures.TryPeek(out var failure);
      await Assert.That(failure?.Error).IsEqualTo("begin-tracking failed");
      await Assert.That(runner.RunCallCount).IsEqualTo(0)
        .Because("A PrePerspective lifecycle failure must abort the group before the runner executes");
    } finally {
      TaskScheduler.UnobservedTaskException -= handler;
    }
  }

  [Test]
  public async Task Worker_PostLifecycleAdvanceThrows_IsolatesErrorPerEventAsync() {
    // Arrange — coordinator path: two processed events; the tracking handle throws at
    // PostAllPerspectivesDetached for BOTH. The per-event catch must isolate the first
    // failure so the second event still gets its PostLifecycle attempt.
    var streamId = Guid.CreateVersion7();
    var eventId1 = Guid.CreateVersion7();
    var eventId2 = Guid.CreateVersion7();
    const string perspectiveName = "Deep.PostLifecycleIsolation";

    var coordinator = new RecordingWorkCoordinator();
    var instanceProvider = new FakeInstanceProvider();
    var runner = new RecordingRunner();
    var registry = new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]);
    var eventStore = new SequencedEventStore();
    eventStore.EnqueueResponse([
      _envelope(eventId1, new DeepChannelEvent("first")),
      _envelope(eventId2, new DeepChannelEvent("second"))
    ]);
    var lifecycle = new ScriptableLifecycleCoordinator {
      AllPerspectivesComplete = true,
      ThrowAtStage = LifecycleStage.PostAllPerspectivesDetached,
      ExpectedThrowCount = 2
    };

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddSingleton<ILifecycleCoordinator>(lifecycle);
    services.AddScoped<IReceptorInvoker, NoOpInvoker>();
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();

    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      completionStrategy: new InstantCompletionStrategy(),
      eventTypeProvider: new ListEventTypeProvider([typeof(DeepChannelEvent)]),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

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
    await lifecycle.AllThrowsObserved.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — both events got a PostAllPerspectives attempt despite the first throwing
    await Assert.That(lifecycle.PostAllAdvanceAttempts).IsEqualTo(2)
      .Because("A throwing PostLifecycle stage for one event must not prevent the next event's attempt");
    await Assert.That(lifecycle.ExpectedPerspectiveEventIds).Contains(eventId1);
    await Assert.That(lifecycle.ExpectedPerspectiveEventIds).Contains(eventId2);
  }

  [Test]
  public async Task RequestImmediatePoll_CalledRepeatedly_CoalescesWithoutThrowingAsync() {
    // Arrange — worker not started; redundant wake calls must coalesce via the CAS guard
    var services = new ServiceCollection();
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();
    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: new FakeInstanceProvider(),
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      completionStrategy: new InstantCompletionStrategy(),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    // Act — first call releases the semaphore; subsequent calls hit the CAS short-circuit
    worker.RequestImmediatePoll();
    worker.RequestImmediatePoll();
    worker.RequestImmediatePoll();

    // Assert — no SemaphoreFullException surfaced and worker state is untouched
    await Assert.That(worker.IsIdle).IsTrue();
    await Assert.That(worker.ConsecutiveEmptyPolls).IsEqualTo(0);
  }

  [Test]
  public async Task Worker_WithNotificationListener_SubscribesCoalescesAndUnsubscribesAsync() {
    // Arrange — NOTIFY listener wired: worker subscribes at start, coalesces redundant
    // Perspective signals (SemaphoreFullException path), ignores non-Perspective categories,
    // and unsubscribes on StopAsync.
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.NotifyPerspective";

    var coordinator = new RecordingWorkCoordinator();
    var instanceProvider = new FakeInstanceProvider();
    var runner = new RecordingRunner();
    var registry = new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]);
    var listener = new FakeWorkNotificationListener();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();

    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions {
        PollingIntervalMilliseconds = 50,
        NotifyHealthyPollingIntervalMilliseconds = 30_000
      }),
      tracingOptions: null,
      completionStrategy: new InstantCompletionStrategy(),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      perspectiveNotificationListener: listener,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    // StartAsync returns before ExecuteAsync runs; wait for the subscription signal so the
    // SubscriberCount assertion doesn't race the background subscribe.
    await listener.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(listener.SubscriberCount).IsEqualTo(1)
      .Because("ExecuteAsync must subscribe to the perspective NOTIFY signal");

    // Non-Perspective category is filtered; burst of Perspective signals coalesces
    listener.Raise(WorkSignalCategory.Outbox);
    listener.Raise(WorkSignalCategory.Perspective);
    listener.Raise(WorkSignalCategory.Perspective);
    listener.Raise(WorkSignalCategory.Perspective);

    await harness.EnqueueWorkAsync(new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastProcessedEventId = null,
      PartitionNumber = 1
    }, cts.Token);
    await coordinator.FirstCompletion.WaitAsync(TimeSpan.FromSeconds(10));

    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — processing survived the signal burst and StopAsync detached the handler
    await Assert.That(coordinator.Completions.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(listener.SubscriberCount).IsEqualTo(0)
      .Because("StopAsync must unsubscribe the NOTIFY handler");
  }

  [Test]
  public async Task Worker_ScopeFactoryDisposedMidStream_BreaksConsumerLoopGracefullyAsync() {
    // Arrange — a work item processed after the root provider is disposed produces
    // ObjectDisposedException inside ProcessChannelBatchAsync; the loop must break, not fault.
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.DisposedPerspective";

    var coordinator = new RecordingWorkCoordinator();
    var instanceProvider = new FakeInstanceProvider();
    var runner = new RecordingRunner();
    var registry = new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]);

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();
    var serviceProvider = services.BuildServiceProvider();

    var harness = new PerspectiveWorkerTestHarness();
    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(new PerspectiveWorkerOptions {
        PollingIntervalMilliseconds = 50,
        // Single consumer loop so the break on ObjectDisposedException completes ExecuteAsync
        // deterministically (sibling loops would otherwise keep idling forever).
        MaxConcurrentDrainConsumers = 1
      }),
      tracingOptions: null,
      completionStrategy: new InstantCompletionStrategy(),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // First item proves the loop is running before disposal
    await harness.EnqueueWorkAsync(new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastProcessedEventId = null,
      PartitionNumber = 1
    }, cts.Token);
    await coordinator.WaitForCompletionsAsync(1, TimeSpan.FromSeconds(10));

    // Act — dispose the DI root, then wake the loop with more work
    await serviceProvider.DisposeAsync();
    await harness.EnqueueWorkAsync(new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = Guid.CreateVersion7(),
      PerspectiveName = perspectiveName,
      LastProcessedEventId = null,
      PartitionNumber = 1
    }, CancellationToken.None);

    var executeTask = worker.ExecuteTask ?? Task.CompletedTask;
    await executeTask.WaitAsync(TimeSpan.FromSeconds(10));

    // Assert — the loop exited cleanly (break on ObjectDisposedException, no fault)
    await Assert.That(executeTask.IsCompletedSuccessfully).IsTrue()
      .Because("ObjectDisposedException in the batch path must break the consumer loop, not fault the worker");
  }

  #region Test event

  private sealed record DeepChannelEvent(string Data) : IEvent;

  #endregion

  #region Helpers

  private static MessageEnvelope<IEvent> _envelope(Guid eventId, IEvent payload, string? traceParent = null) => new() {
    MessageId = new MessageId(eventId),
    Payload = payload,
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        CorrelationId = CorrelationId.New(),
        CausationId = MessageId.New(),
        ServiceInstance = new ServiceInstanceInfo {
          InstanceId = Guid.NewGuid(),
          ServiceName = "TestService",
          HostName = "test-host",
          ProcessId = 1234
        },
        TraceParent = traceParent
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  #endregion

  #region Fakes

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "DeepPathChannelTest";
    public string HostName => "test-host";
    public int ProcessId => 4242;
    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class RecordingWorkCoordinator : IWorkCoordinator {
    private readonly TaskCompletionSource _firstCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _completionWaiters = new();
    private int _completionCount;

    public ConcurrentQueue<PerspectiveCursorCompletion> Completions { get; } = new();
    public ConcurrentQueue<PerspectiveCursorFailure> Failures { get; } = new();
    public Dictionary<(string PerspectiveName, Guid StreamId), PerspectiveCursorInfo> CursorOverrides { get; } = [];
    public Task FirstCompletion => _firstCompletion.Task;
    public Task FirstFailure => _firstFailure.Task;

    public Task WaitForCompletionsAsync(int count, TimeSpan timeout) {
      var tcs = _completionWaiters.GetOrAdd(count, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
      if (Volatile.Read(ref _completionCount) >= count) {
        tcs.TrySetResult();
      }
      return tcs.Task.WaitAsync(timeout);
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) {
      Completions.Enqueue(completion);
      var count = Interlocked.Increment(ref _completionCount);
      _firstCompletion.TrySetResult();
      foreach (var waiter in _completionWaiters) {
        if (count >= waiter.Key) {
          waiter.Value.TrySetResult();
        }
      }
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) {
      Failures.Enqueue(failure);
      _firstFailure.TrySetResult();
      return Task.CompletedTask;
    }

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
      if (CursorOverrides.TryGetValue((perspectiveName, streamId), out var cursor)) {
        return Task.FromResult<PerspectiveCursorInfo?>(cursor);
      }
      return Task.FromResult<PerspectiveCursorInfo?>(null);
    }
  }

  private sealed class SingleRunnerRegistry(string perspectiveName, IPerspectiveRunner runner, IReadOnlyList<Type> eventTypes) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string name, IServiceProvider serviceProvider) =>
      name == perspectiveName ? runner : null;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [new PerspectiveRegistrationInfo(
        perspectiveName,
        $"global::{perspectiveName}",
        "global::Test.DeepModel",
        [.. eventTypes.Select(TypeNameFormatter.Format)])];

    public IReadOnlyList<Type> GetEventTypes() => eventTypes;
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class RecordingRunner : IPerspectiveRunner {
    private int _runCallCount;
    private int _bootstrapCallCount;

    public Exception? RunException { get; init; }
    public int RunCallCount => Volatile.Read(ref _runCallCount);
    public int BootstrapCallCount => Volatile.Read(ref _bootstrapCallCount);
    public Type PerspectiveType => typeof(RecordingRunner);

    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _runCallCount);
      if (RunException is not null) {
        throw RunException;
      }
      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.CreateVersion7(),
        Status = PerspectiveProcessingStatus.Completed,
        PerspectiveType = typeof(RecordingRunner)
      });
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      RunAsync(streamId, perspectiveName, null, cancellationToken);

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _bootstrapCallCount);
      return Task.CompletedTask;
    }
  }

  private sealed class SequencedEventStore : IEventStore {
    private readonly ConcurrentQueue<List<MessageEnvelope<IEvent>>> _responses = new();
    private List<MessageEnvelope<IEvent>> _lastResponse = [];

    public void EnqueueResponse(List<MessageEnvelope<IEvent>> events) {
      _responses.Enqueue(events);
      _lastResponse = events;
    }

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
        Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      if (_responses.TryDequeue(out var next)) {
        return Task.FromResult(new List<MessageEnvelope<IEvent>>(next));
      }
      return Task.FromResult(new List<MessageEnvelope<IEvent>>(_lastResponse));
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

  private sealed class ListEventTypeProvider(IReadOnlyList<Type> eventTypes) : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => eventTypes;
  }

  private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T> {
    public T CurrentValue { get; } = currentValue;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
  }

  private sealed class AlwaysEnabledLogger : ILogger<PerspectiveWorker> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      _ = formatter(state, exception);
    }
  }

  private sealed class NoOpInvoker : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;
  }

  private sealed class StageRecordingInvoker : IReceptorInvoker {
    private readonly Lock _lock = new();
    private readonly List<LifecycleStage> _stages = [];
    private readonly ConcurrentDictionary<LifecycleStage, TaskCompletionSource> _stageWaiters = new();

    public bool HasFired(LifecycleStage stage) {
      lock (_lock) {
        return _stages.Contains(stage);
      }
    }

    public Task WaitForStageAsync(LifecycleStage stage, TimeSpan timeout) {
      var tcs = _stageWaiters.GetOrAdd(stage, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
      if (HasFired(stage)) {
        tcs.TrySetResult();
      }
      return tcs.Task.WaitAsync(timeout);
    }

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      lock (_lock) {
        _stages.Add(stage);
      }
      if (_stageWaiters.TryGetValue(stage, out var tcs)) {
        tcs.TrySetResult();
      }
      return ValueTask.CompletedTask;
    }
  }

  private sealed class RecordingSyncSignaler : IPerspectiveSyncSignaler {
    private int _signalCount;
    public int SignalCount => Volatile.Read(ref _signalCount);

    public void SignalCheckpointUpdated(Type perspectiveType, Guid streamId, Guid lastEventId) =>
      Interlocked.Increment(ref _signalCount);

    public void Dispose() {
      // Nothing to dispose — recording fake.
    }

    public IDisposable Subscribe(Type perspectiveType, Action<PerspectiveCursorSignal> onSignal) =>
      throw new NotSupportedException("Subscribe is not used by these tests.");
  }

  private sealed class RecordingSyncEventTracker : ISyncEventTracker {
    public ConcurrentQueue<Guid> MarkedEventIds { get; } = new();
    public ConcurrentQueue<(string PerspectiveName, Guid StreamId)> StreamSweeps { get; } = new();

    public void TrackEvent(Type eventType, Guid eventId, Guid streamId, string perspectiveName) { }
    public IReadOnlyList<TrackedSyncEvent> GetPendingEvents(Guid streamId, string perspectiveName, Type[]? eventTypes = null) => [];
    public void MarkProcessed(IEnumerable<Guid> eventIds) { }
    public IReadOnlyList<Guid> GetAllTrackedEventIds() => [];
    public Task<bool> WaitForEventsAsync(IReadOnlyList<Guid> eventIds, TimeSpan timeout, Guid? awaiterId = null, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);

    public void MarkProcessedByPerspective(IEnumerable<Guid> eventIds, string perspectiveName) {
      foreach (var id in eventIds) {
        MarkedEventIds.Enqueue(id);
      }
    }

    public Task<bool> WaitForPerspectiveEventsAsync(IReadOnlyList<Guid> eventIds, string perspectiveName, TimeSpan timeout, Guid? awaiterId = null, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);
    public Task<bool> WaitForAllPerspectivesAsync(IReadOnlyList<Guid> eventIds, TimeSpan timeout, Guid? awaiterId = null, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);
    public void UnregisterAwaiter(Guid awaiterId) { }
    public int CleanupStaleEntries(TimeSpan maxAge) => 0;

    public void MarkPerspectiveStreamProcessed(string perspectiveName, Guid streamId) =>
      StreamSweeps.Enqueue((perspectiveName, streamId));
  }

  private sealed class RecordingStreamLocker : IPerspectiveStreamLocker {
    private int _acquireCallCount;
    private int _releaseCallCount;

    public int AcquireCallCount => Volatile.Read(ref _acquireCallCount);
    public int ReleaseCallCount => Volatile.Read(ref _releaseCallCount);
    public string? LastReason { get; private set; }

    public Task<bool> TryAcquireLockAsync(Guid streamId, string perspectiveName, Guid instanceId, string reason, CancellationToken ct = default) {
      Interlocked.Increment(ref _acquireCallCount);
      LastReason = reason;
      return Task.FromResult(true);
    }

    public Task RenewLockAsync(Guid streamId, string perspectiveName, Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;

    public Task ReleaseLockAsync(Guid streamId, string perspectiveName, Guid instanceId, CancellationToken ct = default) {
      Interlocked.Increment(ref _releaseCallCount);
      return Task.CompletedTask;
    }
  }

  private sealed class NoSnapshotStore : IPerspectiveSnapshotStore {
    private int _hasAnyCallCount;
    public int HasAnyCallCount => Volatile.Read(ref _hasAnyCallCount);

    public Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, System.Text.Json.JsonDocument snapshotData, CancellationToken ct = default) =>
      Task.CompletedTask;
    public Task<(Guid SnapshotEventId, System.Text.Json.JsonDocument SnapshotData)?> GetLatestSnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<(Guid, System.Text.Json.JsonDocument)?>(null);
    public Task<(Guid SnapshotEventId, System.Text.Json.JsonDocument SnapshotData)?> GetLatestSnapshotBeforeAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      Task.FromResult<(Guid, System.Text.Json.JsonDocument)?>(null);

    public Task<bool> HasAnySnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) {
      Interlocked.Increment(ref _hasAnyCallCount);
      return Task.FromResult(false);
    }

    public Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAllSnapshotsAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) => Task.CompletedTask;
  }

  private sealed class ScriptableLifecycleCoordinator : ILifecycleCoordinator {
    private readonly TaskCompletionSource _allThrowsObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _postAllAdvanceAttempts;

    public Exception? BeginTrackingException { get; init; }
    public bool AllPerspectivesComplete { get; init; }
    public LifecycleStage? ThrowAtStage { get; init; }
    public int ExpectedThrowCount { get; init; } = 1;
    public ConcurrentQueue<Guid> ExpectedPerspectiveEventIds { get; } = new();
    public int PostAllAdvanceAttempts => Volatile.Read(ref _postAllAdvanceAttempts);
    public Task AllThrowsObserved => _allThrowsObserved.Task;

    public ILifecycleTracking BeginTracking(Guid eventId, IMessageEnvelope envelope, LifecycleStage entryStage, MessageSource source, Guid? streamId = null, Type? perspectiveType = null) {
      if (BeginTrackingException is not null) {
        throw BeginTrackingException;
      }
      return new ScriptedTracking(this, eventId);
    }

    public ILifecycleTracking? GetTracking(Guid eventId) => new ScriptedTracking(this, eventId);
    public void ExpectCompletionsFrom(Guid eventId, params PostLifecycleCompletionSource[] sources) { }
    public ValueTask SignalSegmentCompleteAsync(Guid eventId, PostLifecycleCompletionSource source, IServiceProvider scopedProvider, CancellationToken ct) =>
      ValueTask.CompletedTask;
    public void AbandonTracking(Guid eventId) { }

    public void ExpectPerspectiveCompletions(Guid eventId, IReadOnlyList<string> perspectiveNames) =>
      ExpectedPerspectiveEventIds.Enqueue(eventId);

    public bool SignalPerspectiveComplete(Guid eventId, string perspectiveName) => AllPerspectivesComplete;
    public bool AreAllPerspectivesComplete(Guid eventId) => AllPerspectivesComplete;
    public int CleanupStaleTracking(TimeSpan inactivityThreshold) => 0;

    private sealed class ScriptedTracking(ScriptableLifecycleCoordinator owner, Guid eventId) : ILifecycleTracking {
      public Guid EventId => eventId;
      public LifecycleStage CurrentStage => LifecycleStage.PrePerspectiveDetached;
      public bool IsComplete => false;

      public ValueTask AdvanceToAsync(LifecycleStage stage, IServiceProvider scopedProvider, CancellationToken ct) {
        if (owner.ThrowAtStage == stage) {
          var attempts = Interlocked.Increment(ref owner._postAllAdvanceAttempts);
          if (attempts >= owner.ExpectedThrowCount) {
            owner._allThrowsObserved.TrySetResult();
          }
          throw new InvalidOperationException($"scripted lifecycle failure at {stage}");
        }
        return ValueTask.CompletedTask;
      }

      public ValueTask DrainDetachedAsync() => ValueTask.CompletedTask;
    }
  }

  private sealed class FakeWorkNotificationListener : IWorkNotificationListener {
    private Action<WorkSignalCategory>? _onSignal;
    private int _subscriberCount;

    public bool IsHealthy => true;
    public DateTimeOffset? LastSignalAt => null;
    public int SubscriberCount => Volatile.Read(ref _subscriberCount);

    /// <summary>Completes once ExecuteAsync has subscribed to OnSignal, so the test can
    /// await the subscription instead of racing StartAsync's return against it.</summary>
    public TaskCompletionSource Subscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<WorkSignalCategory>? OnSignal {
      add {
        _onSignal += value;
        Interlocked.Increment(ref _subscriberCount);
        Subscribed.TrySetResult();
      }
      remove {
        _onSignal -= value;
        Interlocked.Decrement(ref _subscriberCount);
      }
    }

    public event Action<bool>? OnHealthChanged {
      add { }
      remove { }
    }

    public void Raise(WorkSignalCategory category) => _onSignal?.Invoke(category);
  }

  #endregion
}
