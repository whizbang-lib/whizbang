using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests proving that PerspectiveWorker processes perspective groups concurrently
/// when MaxConcurrentPerspectives > 1. Uses gated runners with concurrency tracking
/// to detect actual parallelism without Task.Delay or timing-based assertions.
/// </summary>
[NotInParallel("PerspectiveWorkerParallel")]
public sealed class PerspectiveWorkerParallelTests {

  [Test]
  [Category("Performance")]
  public async Task ProcessWorkBatch_WithMultiplePerspectives_ExecutesConcurrentlyAsync() {
    // Arrange — 5 perspective groups, MaxConcurrentPerspectives = 5
    const int perspectiveCount = 5;
    var streamId = Guid.CreateVersion7();

    var allEntered = new CountdownEvent(perspectiveCount);
    var gate = new SemaphoreSlim(0, perspectiveCount);
    var runner = new GatedPerspectiveRunner(allEntered, gate);

    var perspectiveNames = Enumerable.Range(0, perspectiveCount)
      .Select(i => $"Test.Perspective{i}")
      .ToList();

    var registry = new GatedPerspectiveRunnerRegistry(runner, perspectiveNames);
    var coordinator = new ParallelTestWorkCoordinator();

    var (worker, harness) = _createWorker(coordinator, registry, maxConcurrentPerspectives: perspectiveCount);

    // Act — pre-enqueue ALL work BEFORE starting the worker so its first drain pulls all 5 into
    // ONE batch (the sibling throttle test's lesson: enqueue-after-start races batch composition
    // under load — a partial first batch blocks on the gate and the countdown never reaches 0).
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    foreach (var name in perspectiveNames) {
      await harness.EnqueueWorkAsync(new PerspectiveWork {
        WorkId = Guid.CreateVersion7(),
        StreamId = streamId,
        PerspectiveName = name
      }, cts.Token);
    }
    var workerTask = worker.StartAsync(cts.Token);

    // Wait for all 5 runners to enter RunAsync simultaneously.
    // If sequential, only 1 enters at a time → CountdownEvent never reaches 0 → timeout.
    var allEnteredInTime = allEntered.Wait(TimeSpan.FromSeconds(10));

    // Release gate so runners can complete
    gate.Release(perspectiveCount);

    // Shut down
    await cts.CancelAsync();
    try { await workerTask; } catch (OperationCanceledException) { /* expected */ }

    // Assert
    await Assert.That(allEnteredInTime).IsTrue()
      .Because("All 5 perspectives should enter RunAsync concurrently — sequential would timeout");
    await Assert.That(runner.PeakConcurrency).IsEqualTo(perspectiveCount)
      .Because($"Peak concurrency should be {perspectiveCount} when all perspectives run in parallel");
  }

  [Test]
  [Category("Performance")]
  public async Task ProcessWorkBatch_WithMaxConcurrency2_ThrottlesTo2Async() {
    // 5 perspective groups, MaxConcurrentPerspectives = 2. The worker throttles the batch via
    // Parallel.ForEachAsync(MaxDegreeOfParallelism = 2), and each runner holds its throttle slot for the
    // whole RunAsync call. This proves the bound DETERMINISTICALLY — no peak-concurrency counter, no
    // timing/Task.Delay: runners BLOCK inside RunAsync until the test frees a slot, so
    // (EnteredCount - CompletedCount) is exactly the live slot-holders the throttle permits, and freeing
    // one slot admits EXACTLY one more entrant. A broken throttle (admits 3+ at once, or bursts on a
    // release) is caught by an equality assertion, not a flaky peak read.
    const int perspectiveCount = 5;
    const int maxConcurrency = 2;
    var streamId = Guid.CreateVersion7();

    using var runner = new ThrottleProbeRunner();
    var perspectiveNames = Enumerable.Range(0, perspectiveCount)
      .Select(i => $"Test.Perspective{i}")
      .ToList();

    var registry = new GatedPerspectiveRunnerRegistry(runner, perspectiveNames);
    var coordinator = new ParallelTestWorkCoordinator();

    var (worker, harness) = _createWorker(coordinator, registry, maxConcurrentPerspectives: maxConcurrency);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    // Pre-enqueue ALL work BEFORE starting the worker so its first drain pulls all 5 into ONE batch
    // (MaxStreamsPerBatch = 300 default). Deterministic batch composition — no "did they land together"
    // race, which is exactly what let the old pre-opened-gate version misread peak concurrency.
    foreach (var name in perspectiveNames) {
      await harness.EnqueueWorkAsync(new PerspectiveWork {
        WorkId = Guid.CreateVersion7(),
        StreamId = streamId,
        PerspectiveName = name
      }, cts.Token);
    }
    var workerTask = worker.StartAsync(cts.Token);

    // 1. The throttle admits EXACTLY maxConcurrency before any runner completes — and no more, because
    //    every admitted runner is blocked (holding its slot), so no slot is free for a 3rd to enter.
    await runner.WaitForEnteredAsync(maxConcurrency, cts.Token);
    await Assert.That(runner.EnteredCount).IsEqualTo(maxConcurrency)
      .Because("With all admitted runners blocked, the throttle holds exactly MaxConcurrentPerspectives — no 3rd can enter until a slot frees.");

    // 2. Free one slot at a time; each release admits EXACTLY one more entrant (never a burst), so live
    //    concurrency (entered - completed) never exceeds the throttle.
    for (var expected = maxConcurrency + 1; expected <= perspectiveCount; expected++) {
      runner.ReleaseOne();
      await runner.WaitForEnteredAsync(expected, cts.Token);
      await Assert.That(runner.EnteredCount).IsEqualTo(expected)
        .Because("Freeing one slot admits exactly one more runner — a burst would mean the throttle let live concurrency exceed the limit.");
      await Assert.That(runner.LiveCount).IsLessThanOrEqualTo(maxConcurrency)
        .Because($"Live concurrency (entered - completed) must never exceed MaxConcurrentPerspectives={maxConcurrency}.");
    }

    // 3. Release the rest so every runner completes.
    runner.ReleaseAll(perspectiveCount);
    await runner.WaitForCompletedAsync(perspectiveCount, cts.Token);
    await Assert.That(runner.TotalRunCount).IsEqualTo(perspectiveCount)
      .Because("All 5 perspectives eventually complete.");

    await cts.CancelAsync();
    try { await workerTask; } catch (OperationCanceledException) { /* expected */ }
  }

  [Test]
  [Category("Performance")]
  public async Task ProcessWorkBatch_WhenOneGroupThrows_OtherGroupsCompleteAsync() {
    // Arrange — 3 perspectives: 2 normal + 1 throwing
    var streamId = Guid.CreateVersion7();

    var allNormalEntered = new CountdownEvent(2);
    var gate = new SemaphoreSlim(0, 2);
    var normalRunner = new GatedPerspectiveRunner(allNormalEntered, gate);
    var throwingRunner = new AlwaysThrowingPerspectiveRunner();

    var registry = new MixedPerspectiveRunnerRegistry(
      normalRunner,
      throwingRunner,
      throwingPerspectiveName: "Test.ThrowingPerspective",
      normalPerspectiveNames: ["Test.NormalA", "Test.NormalB"]);

    var coordinator = new ParallelTestWorkCoordinator();
    var (worker, harness) = _createWorker(coordinator, registry, maxConcurrentPerspectives: 3);

    // Act
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

    // Release gate immediately so normal runners can complete
    gate.Release(2);

    // Worker will propagate the exception from the throwing perspective
    var workerTask = worker.StartAsync(cts.Token);

    await harness.EnqueueWorkAsync(new PerspectiveWork { WorkId = Guid.CreateVersion7(), StreamId = streamId, PerspectiveName = "Test.NormalA" }, cts.Token);
    await harness.EnqueueWorkAsync(new PerspectiveWork { WorkId = Guid.CreateVersion7(), StreamId = streamId, PerspectiveName = "Test.NormalB" }, cts.Token);
    await harness.EnqueueWorkAsync(new PerspectiveWork { WorkId = Guid.CreateVersion7(), StreamId = streamId, PerspectiveName = "Test.ThrowingPerspective" }, cts.Token);

    // Give it time to process the batch
    var normalEntered = allNormalEntered.Wait(TimeSpan.FromSeconds(5));

    await cts.CancelAsync();
    try { await workerTask; } catch (OperationCanceledException) { /* expected */ }

    // Assert — normal perspectives should still have run
    await Assert.That(normalEntered).IsTrue()
      .Because("Normal perspectives should execute even when one throws");
    await Assert.That(normalRunner.TotalRunCount).IsGreaterThanOrEqualTo(1)
      .Because("At least some normal perspectives should complete despite the throwing one");
  }

  #region Helper Methods

  private static (PerspectiveWorker Worker, PerspectiveWorkerTestHarness Harness) _createWorker(
      ParallelTestWorkCoordinator coordinator,
      IPerspectiveRunnerRegistry registry,
      int maxConcurrentPerspectives) {
    var instanceProvider = new TestServiceInstanceProvider();
    var harness = new PerspectiveWorkerTestHarness();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions {
        PollingIntervalMilliseconds = 50,
        MaxConcurrentPerspectives = maxConcurrentPerspectives,
        // Pin to ONE consumer loop so MaxConcurrentPerspectives is the sole concurrency ceiling.
        // The worker spawns MaxConcurrentDrainConsumers loops (default 4), EACH running its own
        // Parallel.ForEachAsync(MaxDegreeOfParallelism = MaxConcurrentPerspectives) batch — so the
        // real steady-state ceiling is outer×inner (e.g. 4×2=8), NOT MaxConcurrentPerspectives alone.
        // These tests assert the inner per-perspective throttle in isolation, so the outer must be 1.
        // (This is exactly the conflation that made the old peak-concurrency assertion misfire.)
        MaxConcurrentDrainConsumers = 1,
        IdleThresholdPolls = 2
      }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel
    );
    return (worker, harness);
  }

  #endregion

  #region Test Fakes

  /// <summary>
  /// Perspective runner that gates on entry to measure actual concurrency.
  /// Uses Interlocked for thread-safe concurrency tracking — zero reflection, AOT-safe.
  /// </summary>
  private sealed class GatedPerspectiveRunner(CountdownEvent entrySignal, SemaphoreSlim gate, CountdownEvent? completionSignal = null) : IPerspectiveRunner {
    private readonly CountdownEvent _entrySignal = entrySignal;
    private readonly SemaphoreSlim _gate = gate;
    private readonly CountdownEvent? _completionSignal = completionSignal;
    private int _activeConcurrency;
    private int _peakConcurrency;
    private int _totalRunCount;

    public int PeakConcurrency => Volatile.Read(ref _peakConcurrency);
    public int TotalRunCount => Volatile.Read(ref _totalRunCount);
    public Type PerspectiveType => typeof(object);

    public async Task<PerspectiveCursorCompletion> RunAsync(
        Guid streamId,
        string perspectiveName,
        Guid? lastProcessedEventId,
        CancellationToken cancellationToken) {
      // Track concurrency
      var current = Interlocked.Increment(ref _activeConcurrency);
      _updatePeak(current);

      // Signal that we've entered (safe: count may already be at 0 in throttle tests)
      if (!_entrySignal.IsSet) {
        try { _entrySignal.Signal(); } catch (InvalidOperationException) { /* count already at 0 */ }
      }

      try {
        // Wait on gate — test controls when we can proceed
        await _gate.WaitAsync(cancellationToken);
      } finally {
        Interlocked.Decrement(ref _activeConcurrency);
        Interlocked.Increment(ref _totalRunCount);
        _completionSignal?.Signal();
      }

      return new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.CreateVersion7(),
        Status = PerspectiveProcessingStatus.Completed
      };
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(
        Guid streamId, string perspectiveName, Guid triggeringEventId,
        CancellationToken cancellationToken = default) =>
      RunAsync(streamId, perspectiveName, null, cancellationToken);

    public Task BootstrapSnapshotAsync(
        Guid streamId, string perspectiveName, Guid lastProcessedEventId,
        CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    private void _updatePeak(int current) {
      int peak;
      do {
        peak = Volatile.Read(ref _peakConcurrency);
        if (current <= peak) {
          return;
        }
      } while (Interlocked.CompareExchange(ref _peakConcurrency, current, peak) != peak);
    }
  }

  /// <summary>
  /// Deterministic throttle probe. Each RunAsync increments EnteredCount, then BLOCKS on a gate the
  /// test opens one permit at a time; on release it increments CompletedCount. A runner is inside
  /// RunAsync (having incremented EnteredCount) ONLY while it holds a Parallel.ForEachAsync throttle
  /// slot, so LiveCount = EnteredCount - CompletedCount is exactly the live slot-holders the worker's
  /// MaxDegreeOfParallelism permits — no peak counter, no timing. Milestone awaits let the test
  /// synchronise on exact counts via completion signals (TaskCompletionSource), never Task.Delay.
  /// </summary>
  private sealed class ThrottleProbeRunner : IPerspectiveRunner, IDisposable {
    private readonly SemaphoreSlim _gate = new(0);
    private readonly System.Threading.Lock _sync = new();
    private readonly List<(int Target, TaskCompletionSource Tcs)> _enterWaiters = [];
    private readonly List<(int Target, TaskCompletionSource Tcs)> _completeWaiters = [];
    private int _entered;
    private int _completed;

    public int EnteredCount => Volatile.Read(ref _entered);
    public int CompletedCount => Volatile.Read(ref _completed);
    public int LiveCount => EnteredCount - CompletedCount;
    public int TotalRunCount => CompletedCount;
    public Type PerspectiveType => typeof(object);

    /// <summary>Completes once EnteredCount has reached <paramref name="target"/> — signal-based.</summary>
    public Task WaitForEnteredAsync(int target, CancellationToken ct) =>
      _awaitMilestone(_enterWaiters, () => EnteredCount, target, ct);

    /// <summary>Completes once CompletedCount has reached <paramref name="target"/> — signal-based.</summary>
    public Task WaitForCompletedAsync(int target, CancellationToken ct) =>
      _awaitMilestone(_completeWaiters, () => CompletedCount, target, ct);

    /// <summary>Free one throttle slot — admits exactly one more entrant.</summary>
    public void ReleaseOne() => _gate.Release();

    /// <summary>Free <paramref name="count"/> slots so remaining blocked runners complete.</summary>
    public void ReleaseAll(int count) => _gate.Release(count);

    public async Task<PerspectiveCursorCompletion> RunAsync(
        Guid streamId,
        string perspectiveName,
        Guid? lastProcessedEventId,
        CancellationToken cancellationToken) {
      _signal(_enterWaiters, Interlocked.Increment(ref _entered));
      try {
        await _gate.WaitAsync(cancellationToken);
      } finally {
        _signal(_completeWaiters, Interlocked.Increment(ref _completed));
      }

      return new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.CreateVersion7(),
        Status = PerspectiveProcessingStatus.Completed
      };
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(
        Guid streamId, string perspectiveName, Guid triggeringEventId,
        CancellationToken cancellationToken = default) =>
      RunAsync(streamId, perspectiveName, null, cancellationToken);

    public Task BootstrapSnapshotAsync(
        Guid streamId, string perspectiveName, Guid lastProcessedEventId,
        CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    // Registers (or immediately completes) a waiter for `read() >= target`. The count is read INSIDE
    // the lock that _signal also takes, so there is no lost-wakeup window between the read and the
    // registration. Cancellation (test timeout) faults the waiter instead of hanging.
    private Task _awaitMilestone(
        List<(int Target, TaskCompletionSource Tcs)> waiters, Func<int> read, int target, CancellationToken ct) {
      lock (_sync) {
        if (read() >= target) {
          return Task.CompletedTask;
        }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ct.Register(() => tcs.TrySetCanceled(ct));
        waiters.Add((target, tcs));
        return tcs.Task;
      }
    }

    private void _signal(List<(int Target, TaskCompletionSource Tcs)> waiters, int count) {
      lock (_sync) {
        for (var i = waiters.Count - 1; i >= 0; i--) {
          if (waiters[i].Target <= count) {
            waiters[i].Tcs.TrySetResult();
            waiters.RemoveAt(i);
          }
        }
      }
    }

    public void Dispose() => _gate.Dispose();
  }

  /// <summary>
  /// Runner that always throws — used to test exception handling in parallel execution.
  /// </summary>
  private sealed class AlwaysThrowingPerspectiveRunner : IPerspectiveRunner {
    public Type PerspectiveType => typeof(object);

    public Task<PerspectiveCursorCompletion> RunAsync(
        Guid streamId, string perspectiveName, Guid? lastProcessedEventId,
        CancellationToken cancellationToken) =>
      throw new InvalidOperationException("Intentional test failure");

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(
        Guid streamId, string perspectiveName, Guid triggeringEventId,
        CancellationToken cancellationToken = default) =>
      throw new InvalidOperationException("Intentional test failure");

    public Task BootstrapSnapshotAsync(
        Guid streamId, string perspectiveName, Guid lastProcessedEventId,
        CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
  }

  /// <summary>
  /// Registry returning a shared GatedPerspectiveRunner for all perspective names.
  /// </summary>
  private sealed class GatedPerspectiveRunnerRegistry(
      IPerspectiveRunner runner,
      List<string> perspectiveNames) : IPerspectiveRunnerRegistry {

    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) =>
      runner;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      perspectiveNames.ConvertAll(n =>
        new PerspectiveRegistrationInfo(n, $"global::{n}", "global::Test.FakeModel", ["global::Test.FakeEvent"]));

    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  /// <summary>
  /// Registry that returns a normal runner for most perspectives but a throwing one for a specific name.
  /// </summary>
  private sealed class MixedPerspectiveRunnerRegistry(
      GatedPerspectiveRunner normalRunner,
      AlwaysThrowingPerspectiveRunner throwingRunner,
      string throwingPerspectiveName,
      List<string> normalPerspectiveNames) : IPerspectiveRunnerRegistry {

    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) =>
      perspectiveName == throwingPerspectiveName ? throwingRunner : normalRunner;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() {
      var all = normalPerspectiveNames.Concat([throwingPerspectiveName]);
      return [.. all.Select(n =>
        new PerspectiveRegistrationInfo(n, $"global::{n}", "global::Test.FakeModel", ["global::Test.FakeEvent"]))];
    }

    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  /// <summary>
  /// Work coordinator stub for parallel tests. Perspective work is delivered to the worker
  /// through the perspective channel harness (EnqueueWorkAsync), not through this coordinator.
  /// </summary>
  private sealed class ParallelTestWorkCoordinator : IWorkCoordinator {
    public Task ReportPerspectiveCompletionAsync(
        PerspectiveCursorCompletion completion,
        CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
        PerspectiveCursorFailure failure,
        CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName,
        CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class TestServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName { get; } = "ParallelTestService";
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 99999;

    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }
  #endregion
}
