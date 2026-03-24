using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Integration tests for PerspectiveWorker event deduplication.
/// Verifies that the ProcessedEventCache prevents duplicate Apply calls when SQL re-delivers
/// events during the batched completion window.
/// </summary>
/// <remarks>
/// These tests use a SpyObserver to deterministically capture and assert on dedup decisions,
/// and a CountingRunner to track how many times RunAsync is invoked.
/// </remarks>
public class PerspectiveWorkerDedupTests {
  // ==================== Core Dedup Tests ====================

  [Test]
  public async Task Worker_SameWorkReturnedTwice_RunnerCalledOncePerGroupAsync() {
    // Arrange — coordinator returns same work items on every cycle
    var coordinator = new DedupFakeWorkCoordinator();
    var runner = new CountingPerspectiveRunner();
    var registry = new SingleRunnerRegistry(runner);
    var observer = new SpyDedupObserver();
    var streamId = Guid.CreateVersion7();

    coordinator.ReturnWorkOnEveryCycle = true;
    coordinator.PerspectiveWorkTemplate = new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = streamId,
      PerspectiveName = "Test.FakePerspective",
      LastProcessedEventId = null,
      PartitionNumber = 1
    };

    var worker = _createWorker(coordinator, registry, observer: observer);

    // Act — run 2+ cycles
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForProcessWorkBatchCallsAsync(2, TimeSpan.FromSeconds(5));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert — runner should only be called once (second cycle should dedup the work)
    // NOTE: First cycle processes the work; second cycle sees same WorkId → skips
    await Assert.That(runner.RunAsyncCallCount).IsEqualTo(1)
      .Because("Same WorkId should be deduped on second cycle");
    await Assert.That(observer.DedupCalls.Count).IsGreaterThanOrEqualTo(1)
      .Because("Observer should be notified of dedup");
  }

  [Test]
  public async Task Worker_DifferentWorkIds_BothProcessedAsync() {
    // Arrange — coordinator returns different work items on each cycle
    var coordinator = new DedupFakeWorkCoordinator();
    var runner = new CountingPerspectiveRunner();
    var registry = new SingleRunnerRegistry(runner);
    var streamId = Guid.CreateVersion7();

    coordinator.WorkItemsPerCycle = [
      [new PerspectiveWork {
        WorkId = Guid.CreateVersion7(),
        StreamId = streamId,
        PerspectiveName = "Test.FakePerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }],
      [new PerspectiveWork {
        WorkId = Guid.CreateVersion7(),
        StreamId = streamId,
        PerspectiveName = "Test.FakePerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }]
    ];

    var worker = _createWorker(coordinator, registry);

    // Act — wait for runner to be called twice (once per cycle)
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForRunCallsAsync(2, TimeSpan.FromSeconds(5));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert — both should be processed (different WorkIds)
    await Assert.That(runner.RunAsyncCallCount).IsEqualTo(2)
      .Because("Different WorkIds should each be processed");
  }

  [Test]
  public async Task Worker_AfterRetentionExpires_EventCanBeReprocessedAsync() {
    // Arrange
    var fakeTime = new FakeTimeProvider();
    var coordinator = new DedupFakeWorkCoordinator();
    var runner = new CountingPerspectiveRunner();
    var registry = new SingleRunnerRegistry(runner);
    var workId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();

    var work = new PerspectiveWork {
      WorkId = workId,
      StreamId = streamId,
      PerspectiveName = "Test.FakePerspective",
      LastProcessedEventId = null,
      PartitionNumber = 1
    };

    // Cycle 1: return work, Cycle 2: empty (sends completions + activates retention), Cycle 3+: return same work
    coordinator.ReturnWorkOnEveryCycle = false;
    coordinator.WorkItemsPerCycle = [[work], [], [work], [work], [work]];

    var worker = _createWorker(coordinator, registry, timeProvider: fakeTime);

    // Act — run cycle 1 (processes work) and cycle 2 (activates retention)
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForRunCallsAsync(1, TimeSpan.FromSeconds(5));
    await coordinator.WaitForProcessWorkBatchCallsAsync(2, TimeSpan.FromSeconds(5));

    // Advance time past retention AFTER activation (5 min + buffer)
    fakeTime.Advance(TimeSpan.FromMinutes(6));

    // Wait for cycles 3+ where the work should be reprocessable
    await coordinator.WaitForProcessWorkBatchCallsAsync(5, TimeSpan.FromSeconds(5));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert — runner should be called at least twice (once initial, once after TTL expires)
    await Assert.That(runner.RunAsyncCallCount).IsGreaterThanOrEqualTo(2)
      .Because("After retention period, same WorkId should be reprocessable");
  }

  [Test]
  public async Task Worker_MultipleStreams_IndependentDedupAsync() {
    // Arrange — two different streams with different WorkIds
    var coordinator = new DedupFakeWorkCoordinator();
    var runner = new CountingPerspectiveRunner();
    var registry = new SingleRunnerRegistry(runner);
    var stream1 = Guid.CreateVersion7();
    var stream2 = Guid.CreateVersion7();

    coordinator.WorkItemsPerCycle = [
      [
        new PerspectiveWork {
          WorkId = Guid.CreateVersion7(),
          StreamId = stream1,
          PerspectiveName = "Test.FakePerspective",
          LastProcessedEventId = null,
          PartitionNumber = 1
        },
        new PerspectiveWork {
          WorkId = Guid.CreateVersion7(),
          StreamId = stream2,
          PerspectiveName = "Test.FakePerspective",
          LastProcessedEventId = null,
          PartitionNumber = 1
        }
      ]
    ];

    var worker = _createWorker(coordinator, registry);

    // Act — wait for runner to be called twice (once per stream)
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForRunCallsAsync(2, TimeSpan.FromSeconds(5));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert — both streams processed (different WorkIds = no dedup)
    await Assert.That(runner.RunAsyncCallCount).IsEqualTo(2)
      .Because("Different streams with different WorkIds should both be processed");
  }

  // ==================== Observer Tests ====================

  [Test]
  public async Task Worker_Observer_OnEventsDeduped_FiresWithContextAsync() {
    // Arrange
    var coordinator = new DedupFakeWorkCoordinator();
    var runner = new CountingPerspectiveRunner();
    var registry = new SingleRunnerRegistry(runner);
    var observer = new SpyDedupObserver();
    var workId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();

    coordinator.ReturnWorkOnEveryCycle = true;
    coordinator.PerspectiveWorkTemplate = new PerspectiveWork {
      WorkId = workId,
      StreamId = streamId,
      PerspectiveName = "Test.FakePerspective",
      LastProcessedEventId = null,
      PartitionNumber = 1
    };

    var worker = _createWorker(coordinator, registry, observer: observer);

    // Act — wait for runner to process first cycle, then wait for second cycle to dedup
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForRunCallsAsync(1, TimeSpan.FromSeconds(5));
    await coordinator.WaitForProcessWorkBatchCallsAsync(3, TimeSpan.FromSeconds(5)); // 3rd cycle ensures dedup observed
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(observer.DedupCalls.Count).IsGreaterThanOrEqualTo(1)
      .Because("Observer should be notified when work items are deduped");
    await Assert.That(observer.DedupCalls[0].PerspectiveName).IsEqualTo("Test.FakePerspective");
    await Assert.That(observer.DedupCalls[0].StreamId).IsEqualTo(streamId);
    await Assert.That(observer.DedupCalls[0].EventIds).Contains(workId);
  }

  [Test]
  public async Task Worker_Observer_OnEventsMarkedInFlight_FiresAfterApplyAsync() {
    // Arrange
    var coordinator = new DedupFakeWorkCoordinator();
    var runner = new CountingPerspectiveRunner();
    var registry = new SingleRunnerRegistry(runner);
    var observer = new SpyDedupObserver();
    var workId = Guid.CreateVersion7();

    coordinator.PerspectiveWorkToReturn = [new PerspectiveWork {
      WorkId = workId,
      StreamId = Guid.CreateVersion7(),
      PerspectiveName = "Test.FakePerspective",
      LastProcessedEventId = null,
      PartitionNumber = 1
    }];

    var worker = _createWorker(coordinator, registry, observer: observer);

    // Act — wait for runner to actually process the work
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForRunCallsAsync(1, TimeSpan.FromSeconds(5));
    // Wait for next cycle to ensure in-flight marking is complete
    await coordinator.WaitForProcessWorkBatchCallsAsync(2, TimeSpan.FromSeconds(5));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(observer.InFlightCalls.Count).IsGreaterThanOrEqualTo(1)
      .Because("Observer should be notified when events are marked as in-flight after Apply");
    await Assert.That(observer.InFlightCalls[0]).Contains(workId);
  }

  [Test]
  public async Task Worker_BatchedStrategy_CompletionDeferral_NoDuplicateApplyAsync() {
    // Arrange — BatchedCompletionStrategy defers completion to next cycle
    var coordinator = new DedupFakeWorkCoordinator();
    var runner = new CountingPerspectiveRunner();
    var registry = new SingleRunnerRegistry(runner);
    var workId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();

    // Same work returned on cycles 1 and 2 (simulating batched deferral window)
    var work = new PerspectiveWork {
      WorkId = workId,
      StreamId = streamId,
      PerspectiveName = "Test.FakePerspective",
      LastProcessedEventId = null,
      PartitionNumber = 1
    };
    coordinator.WorkItemsPerCycle = [[work], [work], []];

    var worker = _createWorker(coordinator, registry, useBatchedStrategy: true);

    // Act — run through all 3 cycles
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForProcessWorkBatchCallsAsync(3, TimeSpan.FromSeconds(5));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert — runner should only be called once despite work being returned twice
    await Assert.That(runner.RunAsyncCallCount).IsEqualTo(1)
      .Because("Dedup cache should prevent duplicate Apply during batched completion deferral window");
  }

  [Test]
  public async Task Worker_InstantStrategy_NoDuplicateApplyAsync() {
    // Arrange — InstantCompletionStrategy reports immediately
    var coordinator = new DedupFakeWorkCoordinator();
    var runner = new CountingPerspectiveRunner();
    var registry = new SingleRunnerRegistry(runner);
    var workId = Guid.CreateVersion7();

    var work = new PerspectiveWork {
      WorkId = workId,
      StreamId = Guid.CreateVersion7(),
      PerspectiveName = "Test.FakePerspective",
      LastProcessedEventId = null,
      PartitionNumber = 1
    };
    coordinator.WorkItemsPerCycle = [[work], [work], []];

    var worker = _createWorker(coordinator, registry, useBatchedStrategy: false);

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForProcessWorkBatchCallsAsync(3, TimeSpan.FromSeconds(5));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(runner.RunAsyncCallCount).IsEqualTo(1)
      .Because("Dedup cache should prevent duplicate Apply with instant strategy too");
  }

  // ==================== PostLifecycle WhenAll RED/GREEN Test ====================

  [Test]
  public async Task Worker_WithCoordinator_NoEventStore_PostLifecycleInline_DoesNotFire_Async() {
    // Without IEventStore, processedEvents is empty → batchProcessedEvents is empty →
    // Phase 5 doesn't run → PostLifecycle correctly doesn't fire (no events to process).
    // This verifies the correct behavior for services without event store.
    var coordinator = new DedupFakeWorkCoordinator();
    var runner = new CountingPerspectiveRunner();
    var registry = new SingleRunnerRegistry(runner);
    var lifecycleCoordinator = new LifecycleCoordinator();
    var postLifecycleSpy = new PostLifecycleInlineSpyInvoker();
    var streamId = Guid.CreateVersion7();

    coordinator.PerspectiveWorkToReturn = [new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = streamId,
      PerspectiveName = "Test.FakePerspective",
      LastProcessedEventId = null,
      PartitionNumber = 1
    }];

    // Wire worker WITH coordinator and spy invoker, but WITHOUT IEventStore
    // This means upcomingEvents will be null → ExpectPerspectiveCompletions never called (bug)
    var databaseReadiness = new DedupFakeDatabaseReadinessCheck { IsReady = true };
    var instanceProvider = new DedupFakeServiceInstanceProvider();
    IPerspectiveCompletionStrategy strategy = new BatchedCompletionStrategy();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IPerspectiveCompletionStrategy>(strategy);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<ILifecycleCoordinator>(lifecycleCoordinator);
    services.AddScoped<IReceptorInvoker>(_ => postLifecycleSpy);
    // NO IEventStore registered — this is the key to reproducing the bug
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      strategy,
      databaseReadiness
    );

    // Act — run one cycle + wait for processing to complete
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForRunCallsAsync(1, TimeSpan.FromSeconds(5));
    await coordinator.WaitForProcessWorkBatchCallsAsync(2, TimeSpan.FromSeconds(5));
    await Task.Delay(200);
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert — Without IEventStore, processedEvents is empty, so PostLifecycle correctly doesn't fire
    await Assert.That(postLifecycleSpy.PostLifecycleInlineCount).IsEqualTo(0)
      .Because("Without IEventStore, no events are loaded for PostLifecycle processing. " +
               "PostLifecycle correctly doesn't fire when there are no processed events.");
  }

  [Test]
  public async Task Worker_WithCoordinator_WithEventStore_PostLifecycleInline_Fires_Async() {
    // Companion test: same scenario but WITH IEventStore — proves the fix works for both paths.
    var coordinator = new DedupFakeWorkCoordinator();
    var runner = new CountingPerspectiveRunner();
    var registry = new SingleRunnerRegistry(runner);
    var lifecycleCoordinator = new LifecycleCoordinator();
    var postLifecycleSpy = new PostLifecycleInlineSpyInvoker();
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var fakeEventStore = new MinimalFakeEventStore(streamId, eventId);
    var fakeEventTypeProvider = new MinimalFakeEventTypeProvider();

    coordinator.PerspectiveWorkToReturn = [new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = streamId,
      PerspectiveName = "Test.FakePerspective",
      LastProcessedEventId = null,
      PartitionNumber = 1
    }];

    var worker = _createWorkerWithFullDI(
      coordinator, registry, lifecycleCoordinator, postLifecycleSpy, fakeEventStore, fakeEventTypeProvider);

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await runner.WaitForRunCallsAsync(1, TimeSpan.FromSeconds(5));
    await coordinator.WaitForProcessWorkBatchCallsAsync(2, TimeSpan.FromSeconds(5));
    await Task.Delay(200);
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(postLifecycleSpy.PostLifecycleInlineCount).IsGreaterThanOrEqualTo(1)
      .Because("LOCK-IN: PostLifecycleInline must fire when IEventStore IS registered too.");
  }

  // ==================== Helpers ====================

  private static PerspectiveWorker _createWorkerWithFullDI(
    DedupFakeWorkCoordinator coordinator,
    IPerspectiveRunnerRegistry registry,
    ILifecycleCoordinator lifecycleCoordinator,
    IReceptorInvoker spyInvoker,
    IEventStore eventStore,
    IEventTypeProvider eventTypeProvider) {
    var databaseReadiness = new DedupFakeDatabaseReadinessCheck { IsReady = true };
    var instanceProvider = new DedupFakeServiceInstanceProvider();
    IPerspectiveCompletionStrategy strategy = new BatchedCompletionStrategy();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IPerspectiveCompletionStrategy>(strategy);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton(lifecycleCoordinator);
    services.AddScoped<IReceptorInvoker>(_ => spyInvoker);
    services.AddScoped<IEventStore>(_ => eventStore);
    services.AddSingleton(eventTypeProvider);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    return new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      strategy,
      databaseReadiness,
      eventTypeProvider: eventTypeProvider
    );
  }

  private static PerspectiveWorker _createWorker(
    DedupFakeWorkCoordinator coordinator,
    IPerspectiveRunnerRegistry registry,
    IProcessedEventCacheObserver? observer = null,
    TimeProvider? timeProvider = null,
    bool useBatchedStrategy = true) {
    var databaseReadiness = new DedupFakeDatabaseReadinessCheck { IsReady = true };
    var instanceProvider = new DedupFakeServiceInstanceProvider();
    IPerspectiveCompletionStrategy strategy = useBatchedStrategy
      ? new BatchedCompletionStrategy()
      : new InstantCompletionStrategy();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IPerspectiveCompletionStrategy>(strategy);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    if (observer is not null) {
      services.AddSingleton(observer);
    }
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    return new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      strategy,
      databaseReadiness,
      processedEventCacheObserver: observer,
      timeProvider: timeProvider
    );
  }

  // ==================== Test Fakes ====================

  private sealed class SpyDedupObserver : IProcessedEventCacheObserver {
    public List<(IReadOnlyList<Guid> EventIds, string PerspectiveName, Guid StreamId)> DedupCalls { get; } = [];
    public List<IReadOnlyList<Guid>> InFlightCalls { get; } = [];
    public List<int> ActivationCounts { get; } = [];
    public List<int> EvictionCounts { get; } = [];
    public List<Guid> RemovedEventIds { get; } = [];

    public void OnEventsDeduped(IReadOnlyList<Guid> dedupedEventIds, string perspectiveName, Guid streamId) =>
      DedupCalls.Add((dedupedEventIds, perspectiveName, streamId));
    public void OnEventsMarkedInFlight(IReadOnlyList<Guid> eventIds) =>
      InFlightCalls.Add(eventIds);
    public void OnRetentionActivated(int count) =>
      ActivationCounts.Add(count);
    public void OnEvicted(int count) =>
      EvictionCounts.Add(count);
    public void OnEventsRemoved(IReadOnlyList<Guid> eventIds) =>
      RemovedEventIds.AddRange(eventIds);
  }

  private sealed class CountingPerspectiveRunner : IPerspectiveRunner {
    private readonly TaskCompletionSource<int>[] _runWaiters = new TaskCompletionSource<int>[10];
    public int RunAsyncCallCount => _callCount;
    private int _callCount;

    public CountingPerspectiveRunner() {
      for (var i = 0; i < _runWaiters.Length; i++) {
        _runWaiters[i] = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
      }
    }

    public async Task WaitForRunCallsAsync(int count, TimeSpan timeout) {
      ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _runWaiters.Length);
      using var cts = new CancellationTokenSource(timeout);
      await _runWaiters[count - 1].Task.WaitAsync(cts.Token);
    }

    public Task<PerspectiveCursorCompletion> RunAsync(
      Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) {
      var current = Interlocked.Increment(ref _callCount);
      for (var i = 0; i < _runWaiters.Length && i < current; i++) {
        _runWaiters[i].TrySetResult(current);
      }
      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.CreateVersion7(),
        Status = PerspectiveProcessingStatus.Completed
      });
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      RunAsync(streamId, perspectiveName, null, cancellationToken);

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
  }

  private sealed class SingleRunnerRegistry : IPerspectiveRunnerRegistry {
    private readonly IPerspectiveRunner _runner;
    public SingleRunnerRegistry(IPerspectiveRunner runner) => _runner = runner;

    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => _runner;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [new PerspectiveRegistrationInfo("Test.FakePerspective", "global::Test.FakePerspective", "global::Test.FakeModel", ["Whizbang.Core.Tests.Workers.PerspectiveWorkerDedupTests+_fakeEvent, Whizbang.Core.Tests"])];
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class DedupFakeWorkCoordinator : IWorkCoordinator {
    private int _callCount;
    private readonly TaskCompletionSource<int>[] _waiters = new TaskCompletionSource<int>[10];

    public DedupFakeWorkCoordinator() {
      for (var i = 0; i < _waiters.Length; i++) {
        _waiters[i] = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
      }
    }

    public List<PerspectiveWork>? PerspectiveWorkToReturn { get; set; }
    public bool ReturnWorkOnEveryCycle { get; set; }
    public PerspectiveWork? PerspectiveWorkTemplate { get; set; }
    public List<List<PerspectiveWork>>? WorkItemsPerCycle { get; set; }

    public async Task WaitForProcessWorkBatchCallsAsync(int count, TimeSpan timeout) {
      ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _waiters.Length);
      using var cts = new CancellationTokenSource(timeout);
      try {
        await _waiters[count - 1].Task.WaitAsync(cts.Token);
      } catch (OperationCanceledException) {
        throw new TimeoutException($"ProcessWorkBatchAsync was not called {count} times within {timeout}. Current count: {_callCount}");
      }
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      var currentCall = Interlocked.Increment(ref _callCount);

      List<PerspectiveWork> work;
      if (WorkItemsPerCycle is not null) {
        var idx = currentCall - 1;
        work = idx < WorkItemsPerCycle.Count ? [.. WorkItemsPerCycle[idx]] : [];
      } else if (ReturnWorkOnEveryCycle && PerspectiveWorkTemplate is not null) {
        work = [PerspectiveWorkTemplate];
      } else if (PerspectiveWorkToReturn is not null) {
        work = [.. PerspectiveWorkToReturn];
        PerspectiveWorkToReturn = null;
      } else {
        work = [];
      }

      // Signal waiters
      for (var i = 0; i < _waiters.Length && i < currentCall; i++) {
        _waiters[i].TrySetResult(currentCall);
      }

      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = work
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class DedupFakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName { get; } = "TestService";
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 12345;
    public Whizbang.Core.Observability.ServiceInstanceInfo ToInfo() =>
      new() { ServiceName = ServiceName, InstanceId = InstanceId, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class DedupFakeDatabaseReadinessCheck : IDatabaseReadinessCheck {
    public bool IsReady { get; set; } = true;
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(IsReady);
  }

  /// <summary>
  /// Spy invoker that counts PostLifecycleInline invocations.
  /// Detects whether PostLifecycle actually fires (not just whether tracking was abandoned).
  /// </summary>
  private sealed class PostLifecycleInlineSpyInvoker : IReceptorInvoker {
    private int _postLifecycleInlineCount;
    public int PostLifecycleInlineCount => _postLifecycleInlineCount;

    public ValueTask InvokeAsync(
      IMessageEnvelope envelope,
      LifecycleStage stage,
      ILifecycleContext? context = null,
      CancellationToken cancellationToken = default) {
      if (stage == LifecycleStage.PostLifecycleInline) {
        Interlocked.Increment(ref _postLifecycleInlineCount);
      }
      return ValueTask.CompletedTask;
    }
  }

  private sealed record _fakeEvent(Guid Id) : IEvent;

  /// <summary>
  /// Minimal fake event store that returns a single event for GetEventsBetweenPolymorphicAsync.
  /// All other methods throw NotImplementedException.
  /// </summary>
  private sealed class MinimalFakeEventStore : IEventStore {
    private readonly Guid _streamId;
    private readonly Guid _eventId;

    public MinimalFakeEventStore(Guid streamId, Guid eventId) {
      _streamId = streamId;
      _eventId = eventId;
    }

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
      Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken = default) {
      if (streamId != _streamId) {
        return Task.FromResult(new List<MessageEnvelope<IEvent>>());
      }

      var envelope = new MessageEnvelope<IEvent> {
        MessageId = MessageId.From(TrackedGuid.FromExternal(_eventId)),
        Payload = new _fakeEvent(_eventId),
        Hops = []
      };
      return Task.FromResult(new List<MessageEnvelope<IEvent>> { envelope });
    }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken ct = default) =>
      throw new NotImplementedException();
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken ct = default) where TMessage : notnull =>
      throw new NotImplementedException();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken ct = default) =>
      throw new NotImplementedException();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken ct = default) =>
      throw new NotImplementedException();
    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken ct = default) =>
      throw new NotImplementedException();
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken ct = default) =>
      throw new NotImplementedException();
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken ct = default) =>
      throw new NotImplementedException();
  }

  /// <summary>
  /// Minimal fake event type provider that returns a single event type.
  /// </summary>
  private sealed class MinimalFakeEventTypeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(_fakeEvent)];
  }
}
