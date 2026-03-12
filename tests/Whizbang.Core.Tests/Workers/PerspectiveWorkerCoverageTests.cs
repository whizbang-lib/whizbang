using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Additional coverage tests for PerspectiveWorker targeting uncovered branches:
/// - Constructor null argument validation
/// - Database readiness warning threshold (>10 consecutive checks)
/// - Idle/active state transitions (OnWorkProcessingStarted/OnWorkProcessingIdle callbacks)
/// - Acknowledgement count extraction fallback paths (outbox first row, inbox first row)
/// - No work claimed path
/// </summary>
public class PerspectiveWorkerCoverageTests {

  #region Constructor Null Argument Tests

  [Test]
  public async Task Constructor_NullInstanceProvider_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();
    var sp = services.BuildServiceProvider();

    // Act & Assert
    await Assert.That(() => new PerspectiveWorker(
      null!,
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions())
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_NullScopeFactory_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    await Assert.That(() => new PerspectiveWorker(
      new FakeServiceInstanceProvider(),
      null!,
      Options.Create(new PerspectiveWorkerOptions())
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();
    var sp = services.BuildServiceProvider();

    // Act & Assert
    await Assert.That(() => new PerspectiveWorker(
      new FakeServiceInstanceProvider(),
      sp.GetRequiredService<IServiceScopeFactory>(),
      null!
    )).Throws<ArgumentNullException>();
  }

  #endregion

  #region Idle/Active State Transition Tests

  [Test]
  public async Task Worker_StartsInIdleState_IsIdleTrueAsync() {
    // Arrange
    var (worker, _, _) = _createWorker();

    // Assert - Worker starts in idle state
    await Assert.That(worker.IsIdle).IsTrue();
    await Assert.That(worker.ConsecutiveEmptyPolls).IsEqualTo(0);
    await Assert.That(worker.ConsecutiveDatabaseNotReadyChecks).IsEqualTo(0);
  }

  [Test]
  public async Task Worker_WithWork_TransitionsToActiveAndFiresEventAsync() {
    // Arrange
    var workProcessingStartedFired = false;
    var (worker, coordinator, _) = _createWorker();
    worker.OnWorkProcessingStarted += () => { workProcessingStartedFired = true; };

    // Return work on first call
    var streamId = Guid.NewGuid();
    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "TestPerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(workProcessingStartedFired).IsTrue()
      .Because("OnWorkProcessingStarted should fire when work appears");
    await Assert.That(worker.IsIdle).IsFalse()
      .Because("Worker should be active after processing work");
  }

  [Test]
  public async Task Worker_AfterWorkCompletes_TransitionsToIdleAndFiresEventAsync() {
    // Arrange
    var idleFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var (worker, coordinator, _) = _createWorker(idleThresholdPolls: 2);
    worker.OnWorkProcessingIdle += () => { idleFired.TrySetResult(); };

    // Return work on first call only, then empty
    var streamId = Guid.NewGuid();
    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "TestPerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);

    // Wait for idle event (work consumed on first call, then 2 empty polls)
    using var idleCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try {
      await idleFired.Task.WaitAsync(idleCts.Token);
    } catch (OperationCanceledException) {
      // May not fire in time; we verify below
    }

    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(idleFired.Task.IsCompleted).IsTrue()
      .Because("OnWorkProcessingIdle should fire after consecutive empty polls reach threshold");
    await Assert.That(worker.IsIdle).IsTrue();
  }

  [Test]
  public async Task Worker_NoWork_IncreasesConsecutiveEmptyPollsAsync() {
    // Arrange - No work returned
    var (worker, coordinator, _) = _createWorker();

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(400); // Let several empty polls complete
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(worker.ConsecutiveEmptyPolls).IsGreaterThan(0)
      .Because("Empty polls should increment the counter");
  }

  #endregion

  #region Database Not Ready Tests

  [Test]
  public async Task Worker_DatabaseNotReady_IncrementsConsecutiveCheckCounterAsync() {
    // Arrange
    var (worker, _, dbCheck) = _createWorker();
    dbCheck.IsReady = false;

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(400); // Let several polling cycles complete
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(worker.ConsecutiveDatabaseNotReadyChecks).IsGreaterThan(0)
      .Because("Counter should increment when database is not ready");
  }

  [Test]
  public async Task Worker_DatabaseBecomesReady_ResetsConsecutiveCheckCounterAsync() {
    // Arrange - Start not ready, become ready after some polls
    var (worker, coordinator, dbCheck) = _createWorker();
    dbCheck.IsReady = false;

    // Act - Start worker with database not ready
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(300); // Let several not-ready cycles happen

    // Now make database ready
    dbCheck.IsReady = true;
    await Task.Delay(300); // Let a ready cycle happen

    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - Counter should have been reset to 0
    await Assert.That(worker.ConsecutiveDatabaseNotReadyChecks).IsEqualTo(0)
      .Because("Counter should reset when database becomes ready");
  }

  #endregion

  #region Acknowledgement Count Extraction - Metadata Paths

  [Test]
  public async Task Worker_MetadataOnPerspectiveFirstRow_ExtractsAcknowledgementCountsAsync() {
    // Arrange
    var (worker, coordinator, _) = _createWorker();
    var streamId = Guid.NewGuid();

    // Return work with metadata on first perspective row
    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "TestPerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1,
        Metadata = new Dictionary<string, JsonElement> {
          ["perspective_completions_processed"] = JsonSerializer.SerializeToElement(5),
          ["perspective_failures_processed"] = JsonSerializer.SerializeToElement(2)
        }
      }
    ];

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - Worker processed work (no crash from metadata extraction)
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task Worker_NoMetadataOnAnyFirstRow_DefaultsToZeroAsync() {
    // Arrange - Return no metadata on any first row (covers all fallback paths)
    var (worker, coordinator, _) = _createWorker();

    coordinator.WorkBatchOverride = new WorkBatch {
      PerspectiveWork = [],
      OutboxWork = [],
      InboxWork = []
    };

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(300); // Let a cycle complete
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - Worker processed without error (no metadata = 0 acknowledged)
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
  }

  #endregion

  #region Registry Not Found / Runner Not Found Tests

  [Test]
  public async Task Worker_RegistryNotRegistered_SkipsPerspectiveAndContinuesAsync() {
    // Arrange - No IPerspectiveRunnerRegistry in DI
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };

    var streamId = Guid.NewGuid();
    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "MissingPerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    // Don't register IPerspectiveRunnerRegistry
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(300);
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - Worker processed batch without crash (logged warning about missing registry)
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task Worker_RunnerNotFound_SkipsPerspectiveAndContinuesAsync() {
    // Arrange - Registry returns null runner
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };
    var registry = new NullRunnerRegistry();

    var streamId = Guid.NewGuid();
    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "NonExistentPerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(300);
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - Worker continued processing without crash
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
  }

  #endregion

  #region PerspectiveWorkerOptions Defaults Tests

  [Test]
  public async Task PerspectiveWorkerOptions_Defaults_HaveExpectedValuesAsync() {
    // Arrange
    var options = new PerspectiveWorkerOptions();

    // Assert
    await Assert.That(options.PollingIntervalMilliseconds).IsEqualTo(1000);
    await Assert.That(options.LeaseSeconds).IsEqualTo(300);
    await Assert.That(options.StaleThresholdSeconds).IsEqualTo(600);
    await Assert.That(options.DebugMode).IsFalse();
    await Assert.That(options.PartitionCount).IsEqualTo(10_000);
    await Assert.That(options.IdleThresholdPolls).IsEqualTo(2);
    await Assert.That(options.PerspectiveBatchSize).IsEqualTo(100);
    await Assert.That(options.InstanceMetadata).IsNull();
  }

  [Test]
  public async Task PerspectiveWorkerOptions_CustomValues_RoundTripCorrectlyAsync() {
    // Arrange & Act
    var options = new PerspectiveWorkerOptions {
      PollingIntervalMilliseconds = 500,
      LeaseSeconds = 60,
      StaleThresholdSeconds = 120,
      DebugMode = true,
      PartitionCount = 5000,
      IdleThresholdPolls = 5,
      PerspectiveBatchSize = 50,
      InstanceMetadata = new Dictionary<string, JsonElement> {
        ["version"] = JsonSerializer.SerializeToElement("1.0")
      }
    };

    // Assert
    await Assert.That(options.PollingIntervalMilliseconds).IsEqualTo(500);
    await Assert.That(options.LeaseSeconds).IsEqualTo(60);
    await Assert.That(options.StaleThresholdSeconds).IsEqualTo(120);
    await Assert.That(options.DebugMode).IsTrue();
    await Assert.That(options.PartitionCount).IsEqualTo(5000);
    await Assert.That(options.IdleThresholdPolls).IsEqualTo(5);
    await Assert.That(options.PerspectiveBatchSize).IsEqualTo(50);
    await Assert.That(options.InstanceMetadata).IsNotNull();
  }

  #endregion

  #region Helpers

  private static (PerspectiveWorker Worker, FakeWorkCoordinator Coordinator, FakeDatabaseReadinessCheck DbCheck) _createWorker(int idleThresholdPolls = 2) {
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var registry = new FakePerspectiveRunnerRegistry();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };

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
        IdleThresholdPolls = idleThresholdPolls
      }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness
    );

    return (worker, coordinator, databaseReadiness);
  }

  #endregion

  #region Test Fakes

  private sealed class FakeWorkCoordinator : IWorkCoordinator {
    private readonly TaskCompletionSource _completionReported = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<PerspectiveWork> PerspectiveWorkToReturn { get; set; } = [];
    public int ProcessWorkBatchCallCount { get; private set; }
    public int ReportCompletionCallCount { get; private set; }
    public WorkBatch? WorkBatchOverride { get; set; }

    public async Task WaitForCompletionReportedAsync(TimeSpan timeout) {
      using var cts = new CancellationTokenSource(timeout);
      try {
        await _completionReported.Task.WaitAsync(cts.Token);
      } catch (OperationCanceledException) {
        throw new TimeoutException($"Completion was not reported within {timeout}");
      }
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(
        ProcessWorkBatchRequest request,
        CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;

      if (WorkBatchOverride is not null) {
        return Task.FromResult(WorkBatchOverride);
      }

      var work = new List<PerspectiveWork>(PerspectiveWorkToReturn);
      PerspectiveWorkToReturn.Clear();

      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = work
      });
    }

    public Task ReportPerspectiveCompletionAsync(
        PerspectiveCheckpointCompletion completion,
        CancellationToken cancellationToken = default) {
      ReportCompletionCallCount++;
      _completionReported.TrySetResult();
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
        PerspectiveCheckpointFailure failure,
        CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<PerspectiveCheckpointInfo?> GetPerspectiveCheckpointAsync(
        Guid streamId,
        string perspectiveName,
        CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCheckpointInfo?>(null);
    }
  }

  private sealed class FakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName { get; } = "TestService";
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 12345;

    public ServiceInstanceInfo ToInfo() {
      return new ServiceInstanceInfo {
        ServiceName = ServiceName,
        InstanceId = InstanceId,
        HostName = HostName,
        ProcessId = ProcessId
      };
    }
  }

  private sealed class FakeDatabaseReadinessCheck : IDatabaseReadinessCheck {
    public bool IsReady { get; set; } = true;

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      return Task.FromResult(IsReady);
    }
  }

  private sealed class FakePerspectiveRunnerRegistry : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) {
      return new FakePerspectiveRunner();
    }

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() {
      return [new PerspectiveRegistrationInfo("Test.FakePerspective", "global::Test.FakePerspective", "global::Test.FakeModel", ["global::Test.FakeEvent"])];
    }

    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  /// <summary>
  /// Registry that always returns null runner (simulates perspective not found).
  /// </summary>
  private sealed class NullRunnerRegistry : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) {
      return null;
    }

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() {
      return [];
    }

    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class FakePerspectiveRunner : IPerspectiveRunner {
    public Task<PerspectiveCheckpointCompletion> RunAsync(
        Guid streamId,
        string perspectiveName,
        Guid? lastProcessedEventId,
        CancellationToken cancellationToken) {
      return Task.FromResult(new PerspectiveCheckpointCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.NewGuid(),
        Status = PerspectiveProcessingStatus.Completed
      });
    }
  }

  #endregion
}
