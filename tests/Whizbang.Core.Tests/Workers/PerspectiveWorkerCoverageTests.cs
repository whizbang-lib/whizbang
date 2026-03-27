using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Additional coverage tests for PerspectiveWorker targeting uncovered branches:
/// - Constructor null argument validation
/// - Database readiness warning threshold (>10 consecutive checks)
/// - Idle/active state transitions (OnWorkProcessingStarted/OnWorkProcessingIdle callbacks)
/// - Acknowledgement count extraction fallback paths (outbox first row, inbox first row)
/// - No work claimed path
/// - Startup diagnostics (registry not available, empty registry, perspectives listed)
/// - Sync signaler and sync event tracker invocation
/// - Message tag processor invocation at PostPerspectiveInline
/// - _loadProcessedEventsAsync with null event type provider and empty event types
/// - Error during perspective run (failure reporting path)
/// - Tracing enabled paths (batch span, perspective span, lifecycle span)
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
    await worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(10));
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

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
    var (worker, _, _) = _createWorker();

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(1000); // Let several empty polls complete (generous for CI contention)
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
    await Task.Delay(1000); // Let several polling cycles complete (generous for CI contention)
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(worker.ConsecutiveDatabaseNotReadyChecks).IsGreaterThan(0)
      .Because("Counter should increment when database is not ready");
  }

  [Test]
  public async Task Worker_DatabaseBecomesReady_ResetsConsecutiveCheckCounterAsync() {
    // Arrange - Start not ready, become ready after some polls
    var (worker, _, dbCheck) = _createWorker();
    dbCheck.IsReady = false;

    // Act - Start worker with database not ready
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);

    // Wait for at least 3 not-ready checks via signal (no Task.Delay)
    await dbCheck.WaitForChecksAsync(3, TimeSpan.FromSeconds(10));

    // Now make database ready and wait for at least 2 more checks to ensure the ready path runs
    var checksBeforeReady = dbCheck.CheckCount;
    dbCheck.IsReady = true;
    await dbCheck.WaitForChecksAsync(checksBeforeReady + 2, TimeSpan.FromSeconds(10));

    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - Counter should have been reset to 0
    await Assert.That(worker.ConsecutiveDatabaseNotReadyChecks).IsEqualTo(0)
      .Because("Counter should reset when database becomes ready");
  }

  [Test]
  public async Task Worker_DatabaseNotReadyOnStartup_SkipsInitialProcessingAsync() {
    // Arrange - Database not ready at startup covers the "LogDatabaseNotReadyOnStartup" path
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = false };

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

    // Act - Start and let run briefly, then cancel
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(500); // Generous delay for CI contention
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - ProcessWorkBatch should NOT have been called because database was never ready
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0)
      .Because("No work batch should be processed when database is not ready");
    await Assert.That(worker.ConsecutiveDatabaseNotReadyChecks).IsGreaterThan(0)
      .Because("Database not ready checks should be counted");
  }

  [Test]
  public async Task Worker_DatabaseNotReadyOver10Checks_LogsWarningAsync() {
    // Arrange - Database stays not ready for > 10 consecutive checks to hit warning path
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = false };

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 10 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness
    );

    // Act - Let it run long enough for >10 cycles
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(1000); // Should get >10 cycles at 10ms interval (generous for CI)
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - Should have exceeded the warning threshold
    await Assert.That(worker.ConsecutiveDatabaseNotReadyChecks).IsGreaterThan(10)
      .Because("Counter should exceed 10 to trigger warning log path");
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
  public async Task Worker_MetadataOnOutboxFirstRow_ExtractsAcknowledgementCountsAsync() {
    // Arrange - Metadata on outbox first row (no perspective work)
    var (worker, coordinator, _) = _createWorker();

    coordinator.WorkBatchOverride = new WorkBatch {
      PerspectiveWork = [],
      OutboxWork = [
        new OutboxWork {
          MessageId = Guid.NewGuid(),
          Envelope = new MessageEnvelope<JsonElement> {
            MessageId = MessageId.New(),
            Payload = JsonSerializer.SerializeToElement(new { test = true }),
            Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = new ServiceInstanceInfo { InstanceId = Guid.NewGuid(), ServiceName = "Test", HostName = "test", ProcessId = 1 } }]
          },
          EnvelopeType = "TestType",
          MessageType = "TestType",
          Attempts = 0,
          StreamId = Guid.NewGuid(),
          Metadata = new Dictionary<string, JsonElement> {
            ["perspective_completions_processed"] = JsonSerializer.SerializeToElement(3),
            ["perspective_failures_processed"] = JsonSerializer.SerializeToElement(1)
          }
        }
      ],
      InboxWork = []
    };

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(300);
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - Worker processed without crash (outbox metadata path exercised)
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task Worker_MetadataOnInboxFirstRow_ExtractsAcknowledgementCountsAsync() {
    // Arrange - Metadata on inbox first row (no perspective or outbox work)
    var (worker, coordinator, _) = _createWorker();

    coordinator.WorkBatchOverride = new WorkBatch {
      PerspectiveWork = [],
      OutboxWork = [],
      InboxWork = [
        new InboxWork {
          MessageId = Guid.NewGuid(),
          Envelope = new MessageEnvelope<JsonElement> {
            MessageId = MessageId.New(),
            Payload = JsonSerializer.SerializeToElement(new { test = true }),
            Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = new ServiceInstanceInfo { InstanceId = Guid.NewGuid(), ServiceName = "Test", HostName = "test", ProcessId = 1 } }]
          },
          MessageType = "TestType",
          Metadata = new Dictionary<string, JsonElement> {
            ["perspective_completions_processed"] = JsonSerializer.SerializeToElement(7),
            ["perspective_failures_processed"] = JsonSerializer.SerializeToElement(0)
          }
        }
      ]
    };

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(300);
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - Worker processed without crash (inbox metadata path exercised)
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

  #region Startup Diagnostics Tests

  [Test]
  public async Task Worker_StartupWithNoPerspectiveRegistry_LogsDiagnosticsAsync() {
    // Arrange - No registry registered at startup covers LogPerspectiveRegistryNotAvailableAtStartup
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    // No IPerspectiveRunnerRegistry registered
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
    await Task.Delay(200);
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - Worker started without crash (startup diagnostics exercised)
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task Worker_StartupWithEmptyRegistry_LogsNoPerspectivesAsync() {
    // Arrange - Empty registry covers LogNoPerspectivesRegistered
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };
    var registry = new EmptyRunnerRegistry();

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
    await Task.Delay(200);
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - Worker started without crash (empty registry diagnostics exercised)
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task Worker_StartupWithPopulatedRegistry_LogsRegisteredPerspectivesAsync() {
    // Arrange - Registry with perspectives covers LogRegisteredPerspectivesHeader + LogRegisteredPerspective
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };
    var registry = new FakePerspectiveRunnerRegistry();

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
    await Task.Delay(200);
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - Worker started without crash (perspective listing exercised)
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
  }

  #endregion

  #region Sync Signaler and Sync Event Tracker Tests

  [Test]
  public async Task Worker_WithSyncSignaler_SignalsCheckpointUpdatedAsync() {
    // Arrange - Perspective runner returns PerspectiveType so signaler is called
    var syncSignaler = new FakePerspectiveSyncSignaler();
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };
    var registry = new TypeAwarePerspectiveRunnerRegistry();

    var streamId = Guid.NewGuid();
    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "Test.TypedPerspective",
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
      databaseReadiness,
      syncSignaler: syncSignaler
    );

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));
    // StopAsync cancels the worker AND waits for ExecuteAsync to complete
    // (unlike cts.Cancel() + await workerTask, which is a no-op since StartAsync returns Task.CompletedTask)
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(syncSignaler.SignalCount).IsGreaterThanOrEqualTo(1)
      .Because("Sync signaler should be called when PerspectiveType is set on completion");
    await Assert.That(syncSignaler.LastStreamId).IsEqualTo(streamId);
  }

  [Test]
  public async Task Worker_WithSyncEventTracker_MarksProcessedEventsAsync() {
    // Arrange - Set up event store, event type provider, and sync event tracker
    var syncEventTracker = new FakeSyncEventTracker();
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };
    var registry = new FakePerspectiveRunnerRegistry();

    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "Test.FakePerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    var eventStore = new FakeEventStore();
    eventStore.AddEvent(streamId, eventId, new TestCoverageEvent("test-data"));
    var eventTypeProvider = new FakeEventTypeProvider([typeof(TestCoverageEvent)]);

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddScoped<IReceptorInvoker, NoOpReceptorInvoker>();
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness,
      eventTypeProvider: eventTypeProvider,
      syncEventTracker: syncEventTracker
    );

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(10));
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(syncEventTracker.MarkProcessedByPerspectiveCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Sync event tracker should mark events as processed");
  }

  #endregion

  #region Message Tag Processor Tests

  [Test]
  public async Task Worker_WithMessageTagProcessor_ProcessesTagsAtPostPerspectiveInlineAsync() {
    // Arrange
    var tagProcessor = new FakeMessageTagProcessor();
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };
    var registry = new FakePerspectiveRunnerRegistry();

    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "Test.FakePerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    var eventStore = new FakeEventStore();
    eventStore.AddEvent(streamId, eventId, new TestCoverageEvent("tag-test"));
    var eventTypeProvider = new FakeEventTypeProvider([typeof(TestCoverageEvent)]);

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddSingleton<IMessageTagProcessor>(tagProcessor);
    services.AddScoped<IReceptorInvoker, NoOpReceptorInvoker>();
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness,
      eventTypeProvider: eventTypeProvider
    );

    // Act
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(10));
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(tagProcessor.ProcessTagsCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Tag processor should be invoked at PostPerspectiveInline stage");
  }

  #endregion

  #region PostLifecycle Integration Tests

  [Test]
  public async Task Worker_FiresPostLifecycleAsync_AtBatchEnd_ViaCoordinatorAsync() {
    // Arrange — real PerspectiveWorker with stage-tracking invoker
    var trackingInvoker = new StageTrackingReceptorInvoker();
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };
    var registry = new FakePerspectiveRunnerRegistry();

    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "Test.FakePerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    var eventStore = new FakeEventStore();
    eventStore.AddEvent(streamId, eventId, new TestCoverageEvent("postlifecycle-test"));
    var eventTypeProvider = new FakeEventTypeProvider([typeof(TestCoverageEvent)]);

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddScoped<IReceptorInvoker>(_ => trackingInvoker);
    services.AddSingleton<Whizbang.Core.Lifecycle.ILifecycleCoordinator, Whizbang.Core.Lifecycle.LifecycleCoordinator>();
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness,
      eventTypeProvider: eventTypeProvider
    );

    // Act — run the REAL PerspectiveWorker
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(10));

    // Wait a bit for PostLifecycle to fire (it runs after completion reporting)
    try {
      await trackingInvoker.WaitForPostLifecycleAsync(TimeSpan.FromSeconds(5));
    } catch (TimeoutException) {
      // Expected to timeout if PostLifecycle doesn't fire — that's the bug we're looking for
    }

    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    // Assert — PostLifecycle stages MUST fire through the real worker code path
    await Assert.That(trackingInvoker.HasFired(LifecycleStage.PostLifecycleAsync)).IsTrue()
      .Because("PerspectiveWorker must fire PostLifecycleAsync at batch end");
    await Assert.That(trackingInvoker.HasFired(LifecycleStage.PostLifecycleInline)).IsTrue()
      .Because("PerspectiveWorker must fire PostLifecycleInline at batch end");
  }

  #endregion

  #region Error During Perspective Run Tests

  [Test]
  public async Task Worker_PerspectiveRunThrows_ReportsFailureViaStrategyAsync() {
    // Arrange - suppress unobserved task exceptions from intentional test throw
    void handler(object? s, UnobservedTaskExceptionEventArgs e) {
      if (e.Exception.InnerException is InvalidOperationException ioe &&
          ioe.Message == "Perspective run failed") {
        e.SetObserved();
      }
    }
    TaskScheduler.UnobservedTaskException += handler;

    try {
      var coordinator = new FakeWorkCoordinator();
      var instanceProvider = new FakeServiceInstanceProvider();
      var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };
      var registry = new ThrowingPerspectiveRunnerRegistry();

      var streamId = Guid.NewGuid();
      coordinator.PerspectiveWorkToReturn = [
        new PerspectiveWork {
          StreamId = streamId,
          PerspectiveName = "Test.ThrowingPerspective",
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
      await coordinator.WaitForFailureReportedAsync(timeout: TimeSpan.FromSeconds(10));
      cts.Cancel();

      try {
        await workerTask;
      } catch (OperationCanceledException) {
        // Expected
      } catch (InvalidOperationException) {
        // Expected - the runner throws
      }

      // Assert - Failure should have been reported via the strategy
      await Assert.That(coordinator.ReportFailureCallCount).IsGreaterThanOrEqualTo(1)
        .Because("Failure should be reported via completion strategy when perspective run throws");
    } finally {
      TaskScheduler.UnobservedTaskException -= handler;
    }
  }

  #endregion

  #region Tracing Enabled Paths

  [Test]
  public async Task Worker_WithTracingEnabled_CreatesBatchAndPerspectiveSpansAsync() {
    // Arrange - Enable tracing to cover batch span and perspective span code paths
    var tracingOptions = new TracingOptions {
      EnableWorkerBatchSpans = true,
      Components = TraceComponents.Perspectives | TraceComponents.Lifecycle
    };
    var tracingOptionsMonitor = new FakeOptionsMonitor<TracingOptions>(tracingOptions);

    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };
    var registry = new FakePerspectiveRunnerRegistry();

    var streamId = Guid.NewGuid();
    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "Test.FakePerspective",
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
      tracingOptions: tracingOptionsMonitor,
      new InstantCompletionStrategy(),
      databaseReadiness
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - Worker processed work with tracing enabled (no crash)
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(coordinator.ReportCompletionCallCount).IsGreaterThanOrEqualTo(1);
  }

  #endregion

  #region DebugMode Flag Test

  [Test]
  public async Task Worker_WithDebugMode_SetsDebugFlagOnRequestAsync() {
    // Arrange - Exercise the DebugMode path that sets WorkBatchOptions.DebugMode
    var coordinator = new FakeWorkCoordinator { CaptureRequests = true };
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };
    var registry = new FakePerspectiveRunnerRegistry();

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
        DebugMode = true
      }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(200);
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - Request should have DebugMode flag
    await Assert.That(coordinator.CapturedRequests.Count).IsGreaterThan(0);
    await Assert.That(coordinator.CapturedRequests[0].Flags).IsEqualTo(WorkBatchOptions.DebugMode);
  }

  #endregion

  #region Event Loading Paths

  [Test]
  public async Task Worker_WithEventStoreAndEventTypeProvider_LoadsEventsForTraceContextAsync() {
    // Arrange - Exercises the upcomingEvents loading path (lines 383-407)
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };
    var registry = new FakePerspectiveRunnerRegistry();

    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "Test.FakePerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    var eventStore = new FakeEventStore();
    eventStore.AddEvent(streamId, eventId, new TestCoverageEvent("trace-test"));
    var eventTypeProvider = new FakeEventTypeProvider([typeof(TestCoverageEvent)]);

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness,
      eventTypeProvider: eventTypeProvider
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(coordinator.ReportCompletionCallCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(eventStore.GetEventsBetweenPolymorphicCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Event store should be called to load events for trace context extraction");
  }

  [Test]
  public async Task Worker_WithEventTypeProviderReturningEmpty_SkipsEventLoadingAsync() {
    // Arrange - Empty event types means no events loaded
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };
    var registry = new FakePerspectiveRunnerRegistry();

    var streamId = Guid.NewGuid();
    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "Test.FakePerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    var eventStore = new FakeEventStore();
    var eventTypeProvider = new FakeEventTypeProvider([]); // Empty event types

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness,
      eventTypeProvider: eventTypeProvider
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - Event store should NOT be called because event types are empty
    await Assert.That(eventStore.GetEventsBetweenPolymorphicCallCount).IsEqualTo(0)
      .Because("Event store should not be called when event type provider returns empty list");
  }

  [Test]
  public async Task Worker_WithoutEventTypeProvider_SkipsEventLoadingAsync() {
    // Arrange - No event type provider at all
    var (worker, coordinator, _) = _createWorker();

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

    // Assert - Worker completed without event loading (no event type provider)
    await Assert.That(coordinator.ReportCompletionCallCount).IsGreaterThanOrEqualTo(1);
  }

  #endregion

  #region InstanceMetadata Test

  [Test]
  public async Task Worker_WithInstanceMetadata_PassesMetadataToRequestAsync() {
    // Arrange
    var metadata = new Dictionary<string, JsonElement> {
      ["version"] = JsonSerializer.SerializeToElement("2.0.0"),
      ["env"] = JsonSerializer.SerializeToElement("test")
    };

    var coordinator = new FakeWorkCoordinator { CaptureRequests = true };
    var instanceProvider = new FakeServiceInstanceProvider();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };
    var registry = new FakePerspectiveRunnerRegistry();

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
        InstanceMetadata = metadata
      }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(200);
    cts.Cancel();

    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(coordinator.CapturedRequests.Count).IsGreaterThan(0);
    await Assert.That(coordinator.CapturedRequests[0].Metadata).IsNotNull();
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

  #region Graceful Shutdown Tests

  [Test]
  public async Task Worker_WhenServiceProviderDisposed_ExitsGracefullyAsync() {
    // Arrange - create a worker whose service provider we can dispose mid-flight
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
        PollingIntervalMilliseconds = 50
      }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness
    );

    // Act - start the worker, then dispose the service provider to simulate host shutdown
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);

    // Let the worker process at least one cycle
    await Task.Delay(100);

    // Dispose the service provider (simulates host teardown)
    await serviceProvider.DisposeAsync();

    // Give the worker time to hit the disposed provider
    await Task.Delay(200);

    // Cancel to ensure cleanup
    await cts.CancelAsync();

    // Assert - the worker task should complete without throwing
    // (ObjectDisposedException should be caught internally, not propagated)
    var completedWithinTimeout = workerTask.Wait(TimeSpan.FromSeconds(5));
    await Assert.That(completedWithinTimeout).IsTrue()
      .Because("Worker should exit gracefully when service provider is disposed during shutdown");
  }

  #endregion

  #region Test Types

  private sealed record TestCoverageEvent(string Data) : IEvent;

  #endregion

  #region Test Fakes

  private sealed class FakeWorkCoordinator : IWorkCoordinator {
    private readonly TaskCompletionSource _completionReported = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _failureReported = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<PerspectiveWork> PerspectiveWorkToReturn { get; set; } = [];
    public int ProcessWorkBatchCallCount { get; private set; }
    public int ReportCompletionCallCount { get; private set; }
    public int ReportFailureCallCount { get; private set; }
    public WorkBatch? WorkBatchOverride { get; set; }
    public bool CaptureRequests { get; set; }
    public List<ProcessWorkBatchRequest> CapturedRequests { get; } = [];

    public async Task WaitForCompletionReportedAsync(TimeSpan timeout) {
      using var cts = new CancellationTokenSource(timeout);
      try {
        await _completionReported.Task.WaitAsync(cts.Token);
      } catch (OperationCanceledException) {
        throw new TimeoutException($"Completion was not reported within {timeout}");
      }
    }

    public async Task WaitForFailureReportedAsync(TimeSpan timeout) {
      using var cts = new CancellationTokenSource(timeout);
      try {
        await _failureReported.Task.WaitAsync(cts.Token);
      } catch (OperationCanceledException) {
        throw new TimeoutException($"Failure was not reported within {timeout}");
      }
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(
        ProcessWorkBatchRequest request,
        CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;

      if (CaptureRequests) {
        CapturedRequests.Add(request);
      }

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
        PerspectiveCursorCompletion completion,
        CancellationToken cancellationToken = default) {
      ReportCompletionCallCount++;
      _completionReported.TrySetResult();
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
        PerspectiveCursorFailure failure,
        CancellationToken cancellationToken = default) {
      ReportFailureCallCount++;
      _failureReported.TrySetResult();
      return Task.CompletedTask;
    }

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId,
        string perspectiveName,
        CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCursorInfo?>(null);
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
    private int _checkCount;
    private readonly List<(int MinChecks, TaskCompletionSource Signal)> _waiters = [];

    public int CheckCount => Volatile.Read(ref _checkCount);

    /// <summary>
    /// Returns a task that completes when IsReadyAsync has been called at least the specified number of times.
    /// </summary>
    public Task WaitForChecksAsync(int minChecks, TimeSpan timeout) {
      var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      lock (_waiters) {
        if (Volatile.Read(ref _checkCount) >= minChecks) {
          tcs.TrySetResult();
          return tcs.Task;
        }
        _waiters.Add((minChecks, tcs));
      }
      return tcs.Task.WaitAsync(timeout);
    }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      var count = Interlocked.Increment(ref _checkCount);
      lock (_waiters) {
        for (int i = _waiters.Count - 1; i >= 0; i--) {
          if (count >= _waiters[i].MinChecks) {
            _waiters[i].Signal.TrySetResult();
            _waiters.RemoveAt(i);
          }
        }
      }
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

  /// <summary>
  /// Registry with empty registered perspectives (but still instantiated).
  /// </summary>
  private sealed class EmptyRunnerRegistry : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) {
      return null;
    }

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() {
      return [];
    }

    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  /// <summary>
  /// Registry that returns a runner which provides PerspectiveType on completion.
  /// </summary>
  private sealed class TypeAwarePerspectiveRunnerRegistry : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) {
      return new TypeAwarePerspectiveRunner();
    }

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() {
      return [new PerspectiveRegistrationInfo("Test.TypedPerspective", "global::Test.TypedPerspective", "global::Test.TypedModel", ["global::Test.TypedEvent"])];
    }

    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  /// <summary>
  /// Registry that returns a runner which throws.
  /// </summary>
  private sealed class ThrowingPerspectiveRunnerRegistry : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) {
      return new ThrowingPerspectiveRunner();
    }

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() {
      return [new PerspectiveRegistrationInfo("Test.ThrowingPerspective", "global::Test.ThrowingPerspective", "global::Test.ThrowingModel", ["global::Test.ThrowingEvent"])];
    }

    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class FakePerspectiveRunner : IPerspectiveRunner {
    public Task<PerspectiveCursorCompletion> RunAsync(
        Guid streamId,
        string perspectiveName,
        Guid? lastProcessedEventId,
        CancellationToken cancellationToken) {
      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.NewGuid(),
        Status = PerspectiveProcessingStatus.Completed
      });
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
        RunAsync(streamId, perspectiveName, null, cancellationToken);

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
  }

  private sealed class TypeAwarePerspectiveRunner : IPerspectiveRunner {
    public Task<PerspectiveCursorCompletion> RunAsync(
        Guid streamId,
        string perspectiveName,
        Guid? lastProcessedEventId,
        CancellationToken cancellationToken) {
      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.NewGuid(),
        Status = PerspectiveProcessingStatus.Completed,
        PerspectiveType = typeof(TypeAwarePerspectiveRunner) // Non-null triggers sync signaler
      });
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
        RunAsync(streamId, perspectiveName, null, cancellationToken);

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
  }

  private sealed class ThrowingPerspectiveRunner : IPerspectiveRunner {
    public Task<PerspectiveCursorCompletion> RunAsync(
        Guid streamId,
        string perspectiveName,
        Guid? lastProcessedEventId,
        CancellationToken cancellationToken) {
      throw new InvalidOperationException("Perspective run failed");
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Perspective run failed");

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
  }

  private sealed class FakePerspectiveSyncSignaler : IPerspectiveSyncSignaler {
    public int SignalCount { get; private set; }
    public Guid LastStreamId { get; private set; }
    public Guid LastEventId { get; private set; }

    public void SignalCheckpointUpdated(Type perspectiveType, Guid streamId, Guid lastEventId) {
      SignalCount++;
      LastStreamId = streamId;
      LastEventId = lastEventId;
    }

    public void Dispose() { }

    public IDisposable Subscribe(Type perspectiveType, Action<PerspectiveCursorSignal> onSignal) {
      throw new NotImplementedException();
    }
  }

  private sealed class FakeSyncEventTracker : ISyncEventTracker {
    public int MarkProcessedByPerspectiveCallCount { get; private set; }

    public void TrackEvent(Type eventType, Guid eventId, Guid streamId, string perspectiveName) { }

    public IReadOnlyList<TrackedSyncEvent> GetPendingEvents(Guid streamId, string perspectiveName, Type[]? eventTypes = null) => [];

    public void MarkProcessed(IEnumerable<Guid> eventIds) { }

    public IReadOnlyList<Guid> GetAllTrackedEventIds() => [];

    public Task<bool> WaitForEventsAsync(IReadOnlyList<Guid> eventIds, TimeSpan timeout, Guid? awaiterId = null, CancellationToken cancellationToken = default)
      => Task.FromResult(true);

    public void MarkProcessedByPerspective(IEnumerable<Guid> eventIds, string perspectiveName) {
      MarkProcessedByPerspectiveCallCount++;
    }

    public Task<bool> WaitForPerspectiveEventsAsync(
        IReadOnlyList<Guid> eventIds,
        string perspectiveName,
        TimeSpan timeout,
        Guid? awaiterId = null,
        CancellationToken cancellationToken = default) {
      return Task.FromResult(true);
    }

    public Task<bool> WaitForAllPerspectivesAsync(IReadOnlyList<Guid> eventIds, TimeSpan timeout, Guid? awaiterId = null, CancellationToken cancellationToken = default)
      => Task.FromResult(true);

    public void UnregisterAwaiter(Guid awaiterId) { }
  }

  private sealed class FakeEventStore : IEventStore {
    private readonly Dictionary<Guid, List<MessageEnvelope<IEvent>>> _events = [];
    public int GetEventsBetweenPolymorphicCallCount { get; private set; }

    public void AddEvent(Guid streamId, Guid eventId, IEvent payload, string? userId = null) {
      if (!_events.TryGetValue(streamId, out var list)) {
        list = [];
        _events[streamId] = list;
      }

      list.Add(new MessageEnvelope<IEvent> {
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
            Scope = userId != null
              ? ScopeDelta.FromSecurityContext(new SecurityContext { UserId = userId, TenantId = "test-tenant" })
              : null
          }
        ]
      });
    }

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
        Guid streamId,
        Guid? afterEventId,
        Guid upToEventId,
        IReadOnlyList<Type> eventTypes,
        CancellationToken cancellationToken = default) {
      GetEventsBetweenPolymorphicCallCount++;
      if (_events.TryGetValue(streamId, out var events)) {
        return Task.FromResult(events);
      }
      return Task.FromResult(new List<MessageEnvelope<IEvent>>());
    }

    // IEventStore minimal implementation stubs
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull
      => Task.CompletedTask;
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
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default)
      => Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default)
      => Task.FromResult(-1L);
  }

  private sealed class FakeEventTypeProvider(IReadOnlyList<Type> eventTypes) : IEventTypeProvider {
    private readonly IReadOnlyList<Type> _eventTypes = eventTypes;

    public IReadOnlyList<Type> GetEventTypes() => _eventTypes;
  }

  private sealed class FakeMessageTagProcessor : IMessageTagProcessor {
    public int ProcessTagsCallCount { get; private set; }

    public ValueTask ProcessTagsAsync(
        object message,
        Type messageType,
        LifecycleStage stage,
        IScopeContext? scope = null,
        CancellationToken ct = default) {
      ProcessTagsCallCount++;
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T> {
    public T CurrentValue { get; } = currentValue;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
  }

  private sealed class NoOpReceptorInvoker : IReceptorInvoker {
    public ValueTask InvokeAsync(
        IMessageEnvelope envelope,
        LifecycleStage stage,
        ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Receptor invoker that tracks every stage it's called at.
  /// Used to verify PostLifecycle stages fire through the real worker code path.
  /// </summary>
  private sealed class StageTrackingReceptorInvoker : IReceptorInvoker {
    private readonly Lock _lock = new();
    private readonly List<LifecycleStage> _firedStages = [];
    private readonly TaskCompletionSource _postLifecycleFired = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<LifecycleStage> FiredStages {
      get { lock (_lock) { return [.. _firedStages]; } }
    }

    public bool HasFired(LifecycleStage stage) {
      lock (_lock) { return _firedStages.Contains(stage); }
    }

    public Task WaitForPostLifecycleAsync(TimeSpan timeout) {
      using var cts = new CancellationTokenSource(timeout);
      return _postLifecycleFired.Task.WaitAsync(cts.Token);
    }

    public ValueTask InvokeAsync(
        IMessageEnvelope envelope,
        LifecycleStage stage,
        ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      lock (_lock) { _firedStages.Add(stage); }
      if (stage == LifecycleStage.PostLifecycleInline) {
        _postLifecycleFired.TrySetResult();
      }
      return ValueTask.CompletedTask;
    }
  }

  #endregion
}
