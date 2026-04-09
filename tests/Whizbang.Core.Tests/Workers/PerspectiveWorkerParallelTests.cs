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

    // Queue work items — one per perspective, same stream
    coordinator.PerspectiveWorkToReturn = perspectiveNames
      .Select(name => new PerspectiveWork {
        WorkId = Guid.CreateVersion7(),
        StreamId = streamId,
        PerspectiveName = name
      })
      .ToList();

    var worker = _createWorker(coordinator, registry, maxConcurrentPerspectives: perspectiveCount);

    // Act — start worker, wait for all runners to be concurrently active
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var workerTask = worker.StartAsync(cts.Token);

    // Wait for all 5 runners to enter RunAsync simultaneously.
    // If sequential, only 1 enters at a time → CountdownEvent never reaches 0 → timeout.
    var allEnteredInTime = allEntered.Wait(TimeSpan.FromSeconds(5));

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
    // Arrange — 5 perspective groups but MaxConcurrentPerspectives = 2
    const int perspectiveCount = 5;
    const int maxConcurrency = 2;
    var streamId = Guid.CreateVersion7();

    // CountdownEvent for first wave; gate releases runners immediately (auto-open)
    // so we can observe peak concurrency without blocking runners
    var firstWaveEntered = new CountdownEvent(maxConcurrency);
    var allCompleted = new CountdownEvent(perspectiveCount);
    var gate = new SemaphoreSlim(perspectiveCount, perspectiveCount); // pre-opened — runners don't block
    var runner = new GatedPerspectiveRunner(firstWaveEntered, gate, allCompleted);

    var perspectiveNames = Enumerable.Range(0, perspectiveCount)
      .Select(i => $"Test.Perspective{i}")
      .ToList();

    var registry = new GatedPerspectiveRunnerRegistry(runner, perspectiveNames);
    var coordinator = new ParallelTestWorkCoordinator();

    coordinator.PerspectiveWorkToReturn = perspectiveNames
      .Select(name => new PerspectiveWork {
        WorkId = Guid.CreateVersion7(),
        StreamId = streamId,
        PerspectiveName = name
      })
      .ToList();

    var worker = _createWorker(coordinator, registry, maxConcurrentPerspectives: maxConcurrency);

    // Act
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var workerTask = worker.StartAsync(cts.Token);

    // Wait for all 5 runners to complete
    var allFinished = allCompleted.Wait(TimeSpan.FromSeconds(5));

    await cts.CancelAsync();
    try { await workerTask; } catch (OperationCanceledException) { /* expected */ }

    // Assert — peak concurrency should never exceed 2 even though gate is open
    await Assert.That(allFinished).IsTrue()
      .Because("All 5 perspectives should complete within timeout");
    await Assert.That(runner.PeakConcurrency).IsLessThanOrEqualTo(maxConcurrency)
      .Because($"Peak concurrency should never exceed MaxConcurrentPerspectives={maxConcurrency}");
    await Assert.That(runner.TotalRunCount).IsEqualTo(perspectiveCount)
      .Because("All 5 perspectives should eventually complete");
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
    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork { WorkId = Guid.CreateVersion7(), StreamId = streamId, PerspectiveName = "Test.NormalA" },
      new PerspectiveWork { WorkId = Guid.CreateVersion7(), StreamId = streamId, PerspectiveName = "Test.NormalB" },
      new PerspectiveWork { WorkId = Guid.CreateVersion7(), StreamId = streamId, PerspectiveName = "Test.ThrowingPerspective" },
    ];

    var worker = _createWorker(coordinator, registry, maxConcurrentPerspectives: 3);

    // Act
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

    // Release gate immediately so normal runners can complete
    gate.Release(2);

    // Worker will propagate the exception from the throwing perspective
    var workerTask = worker.StartAsync(cts.Token);

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

  private static PerspectiveWorker _createWorker(
      ParallelTestWorkCoordinator coordinator,
      IPerspectiveRunnerRegistry registry,
      int maxConcurrentPerspectives) {
    var instanceProvider = new TestServiceInstanceProvider();
    var databaseReadiness = new TestDatabaseReadinessCheck();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    return new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions {
        PollingIntervalMilliseconds = 50,
        MaxConcurrentPerspectives = maxConcurrentPerspectives,
        IdleThresholdPolls = 2
      }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness
    );
  }

  #endregion

  #region Test Fakes

  /// <summary>
  /// Perspective runner that gates on entry to measure actual concurrency.
  /// Uses Interlocked for thread-safe concurrency tracking — zero reflection, AOT-safe.
  /// </summary>
  private sealed class GatedPerspectiveRunner : IPerspectiveRunner {
    private readonly CountdownEvent _entrySignal;
    private readonly SemaphoreSlim _gate;
    private readonly CountdownEvent? _completionSignal;
    private int _activeConcurrency;
    private int _peakConcurrency;
    private int _totalRunCount;

    public GatedPerspectiveRunner(CountdownEvent entrySignal, SemaphoreSlim gate, CountdownEvent? completionSignal = null) {
      _entrySignal = entrySignal;
      _gate = gate;
      _completionSignal = completionSignal;
    }

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
      GatedPerspectiveRunner runner,
      List<string> perspectiveNames) : IPerspectiveRunnerRegistry {

    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) =>
      runner;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      perspectiveNames.Select(n =>
        new PerspectiveRegistrationInfo(n, $"global::{n}", "global::Test.FakeModel", ["global::Test.FakeEvent"]))
        .ToList();

    public IReadOnlyList<Type> GetEventTypes() => [];
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
      return all.Select(n =>
        new PerspectiveRegistrationInfo(n, $"global::{n}", "global::Test.FakeModel", ["global::Test.FakeEvent"]))
        .ToList();
    }

    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  /// <summary>
  /// Work coordinator that returns perspective work once, then empty batches.
  /// </summary>
  private sealed class ParallelTestWorkCoordinator : IWorkCoordinator {
    public List<PerspectiveWork> PerspectiveWorkToReturn { get; set; } = [];

    public Task<WorkBatch> ProcessWorkBatchAsync(
        ProcessWorkBatchRequest request,
        CancellationToken cancellationToken = default) {
      var work = new List<PerspectiveWork>(PerspectiveWorkToReturn);
      PerspectiveWorkToReturn.Clear();

      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = work
      });
    }

    public Task ReportPerspectiveCompletionAsync(
        PerspectiveCursorCompletion completion,
        CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
        PerspectiveCursorFailure failure,
        CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

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

  private sealed class TestDatabaseReadinessCheck : IDatabaseReadinessCheck {
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(true);
  }

  #endregion
}
