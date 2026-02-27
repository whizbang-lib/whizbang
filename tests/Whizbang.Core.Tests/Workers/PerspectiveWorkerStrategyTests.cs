using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for PerspectiveWorker integration with IPerspectiveCompletionStrategy.
/// Verifies that the worker uses the strategy to report perspective completions and failures.
/// </summary>
public class PerspectiveWorkerStrategyTests {
  [Test]
  public async Task PerspectiveWorker_WithBatchedStrategy_CollectsThenReportsOnNextCycle_Async() {
    // Arrange
    var strategy = new BatchedCompletionStrategy();
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var registry = new FakePerspectiveRunnerRegistry();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };

    // Return perspective work on each call
    coordinator.ReturnWorkOnEveryCycle = true;
    var streamId = Guid.NewGuid();
    coordinator.PerspectiveWorkTemplate = new PerspectiveWork {
      StreamId = streamId,
      PerspectiveName = "TestPerspective",
      LastProcessedEventId = null,
      PartitionNumber = 1
    };

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IPerspectiveCompletionStrategy>(strategy);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      strategy,
      databaseReadiness,
      null
    );

    // Act - Run worker for multiple poll cycles
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(500); // Let at least 2 cycles complete (generous for parallel execution)
    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected during shutdown
    }

    // Assert - Batched strategy reports completions via ProcessWorkBatchAsync on the NEXT cycle
    // First cycle: processes work, collects completion in strategy
    // Second cycle: gets pending completions, reports them via ProcessWorkBatchAsync parameters
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(2)
      .Because("Worker should have completed at least 2 poll cycles");
    await Assert.That(coordinator.CompletionsReceivedViaProcessWorkBatch.Count).IsGreaterThanOrEqualTo(1)
      .Because("Batched strategy should report completions via ProcessWorkBatchAsync parameters on next cycle");
    await Assert.That(coordinator.ReportCompletionCallCount).IsEqualTo(0)
      .Because("Batched strategy should NOT use out-of-band reporting");
  }

  [Test]
  public async Task PerspectiveWorker_WithInstantStrategy_ReportsImmediately_Async() {
    // Arrange
    var strategy = new InstantCompletionStrategy();
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var registry = new FakePerspectiveRunnerRegistry();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };

    // Return 1 perspective work item
    var streamId = Guid.NewGuid();
    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "TestPerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IPerspectiveCompletionStrategy>(strategy);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      strategy,
      databaseReadiness,
      null
    );

    // Act - Run worker for one poll cycle
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await Task.Delay(300); // Let first cycle complete (generous for parallel execution)
    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected during shutdown
    }

    // Assert - Strategy should have reported immediately, nothing pending
    await Assert.That(strategy.GetPendingCompletions()).IsEmpty()
      .Because("Instant strategy never collects - reports immediately");
    await Assert.That(coordinator.ReportCompletionCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Instant strategy should report immediately via coordinator");
  }

  [Test]
  public async Task PerspectiveWorker_OnFailure_UsesStrategyToReportFailure_Async() {
    // Arrange
    var strategy = new InstantCompletionStrategy();
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var registry = new FakePerspectiveRunnerRegistry {
      ShouldThrow = true // Force runner to throw exception
    };
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };

    // Return 1 perspective work item
    var streamId = Guid.NewGuid();
    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "TestPerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IPerspectiveCompletionStrategy>(strategy);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      strategy,
      databaseReadiness,
      null
    );

    // Act - Run worker for one poll cycle
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);

    // Wait for the worker to process (which will fail) OR timeout
    // Use ContinueWith to observe any exception immediately to prevent unobserved task exception
    var observerTask = workerTask.ContinueWith(t => {
      // This observes any exception on the task, preventing unobserved task exception
      _ = t.Exception;
    }, TaskContinuationOptions.OnlyOnFaulted);

    await Task.Delay(300); // Let first cycle complete (generous for parallel execution)
    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected during shutdown
    } catch (InvalidOperationException) {
      // Expected - the test runner throws InvalidOperationException("Test exception")
      // This is the exception we're testing gets reported via the failure strategy
    }

    // Assert - Strategy should have reported failure immediately
    await Assert.That(coordinator.ReportFailureCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Instant strategy should report failures immediately via coordinator");
  }

  // ==================== CLR Type Name Registry Lookup Tests ====================

  [Test]
  public async Task PerspectiveWorker_WithClrTypeName_LooksUpRunnerCorrectly_Async() {
    // Arrange - Simulate database returning CLR format name (e.g., "Namespace.Parent+Child")
    // This is the correct format that should match the generated registry
    var strategy = new InstantCompletionStrategy();
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var registry = new ClrTypeNameAwarePerspectiveRunnerRegistry();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };

    // Database returns work with CLR format perspective name
    var streamId = Guid.NewGuid();
    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "TestNamespace.ActiveAccount+Projection", // CLR format with '+'
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IPerspectiveCompletionStrategy>(strategy);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      strategy,
      databaseReadiness,
      null
    );

    // Act - Run worker and wait for completion to be reported
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);

    // Wait for completion to be reported (deterministic, no timers!)
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));

    cts.Cancel();
    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected during shutdown
    }

    // Assert - Registry should have been called with CLR format name and found a runner
    await Assert.That(registry.LastLookedUpName).IsEqualTo("TestNamespace.ActiveAccount+Projection")
      .Because("Registry should be called with exact CLR format name from database");
    await Assert.That(registry.RunnerWasFound).IsTrue()
      .Because("Registry should find runner when CLR format name matches");
    await Assert.That(coordinator.ReportCompletionCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Worker should report completion when runner is found and executed");
  }

  [Test]
  public async Task PerspectiveWorker_WithMismatchedName_FailsToFindRunner_Async() {
    // Arrange - Simulate database returning WRONG format (just "Projection" instead of CLR format)
    // This was the bug: the generator was using GetSimpleName which returned just "Projection"
    var strategy = new InstantCompletionStrategy();
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var registry = new ClrTypeNameAwarePerspectiveRunnerRegistry();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };

    // Database returns work with INCORRECT simple name (the bug!)
    var streamId = Guid.NewGuid();
    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "Projection", // WRONG: Just simple name, not CLR format
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IPerspectiveCompletionStrategy>(strategy);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      strategy,
      databaseReadiness,
      null
    );

    // Act - Run worker and wait for registry lookup to occur
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);

    // Wait for registry to signal that lookup occurred (deterministic, no timers!)
    await registry.WaitForLookupAsync(timeout: TimeSpan.FromSeconds(5));

    cts.Cancel();
    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected during shutdown
    }

    // Assert - Registry lookup failed because name doesn't match CLR format
    await Assert.That(registry.LastLookedUpName).IsEqualTo("Projection")
      .Because("Registry should receive the name as-is from the database");
    await Assert.That(registry.RunnerWasFound).IsFalse()
      .Because("Registry should NOT find runner when simple name doesn't match CLR format");
    await Assert.That(coordinator.ReportCompletionCallCount).IsEqualTo(0)
      .Because("No completion should be reported when runner is not found");
  }

  [Test]
  public async Task PerspectiveWorker_WithDeeplyNestedClrName_LooksUpRunnerCorrectly_Async() {
    // Arrange - Tests deeply nested types: "Namespace.Parent+Child+GrandChild"
    var strategy = new InstantCompletionStrategy();
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var registry = new ClrTypeNameAwarePerspectiveRunnerRegistry();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };

    // Database returns work with deeply nested CLR format name
    var streamId = Guid.NewGuid();
    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "TestNamespace.Sessions+Active+Projection", // Multiple nesting levels
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IPerspectiveCompletionStrategy>(strategy);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      strategy,
      databaseReadiness,
      null
    );

    // Act - Run worker and wait for registry lookup to occur
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);

    // Wait for registry to signal that lookup occurred (deterministic, no timers!)
    await registry.WaitForLookupAsync(timeout: TimeSpan.FromSeconds(5));

    cts.Cancel();
    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected during shutdown
    }

    // Assert - Registry should handle multiple '+' nesting levels correctly
    await Assert.That(registry.LastLookedUpName).IsEqualTo("TestNamespace.Sessions+Active+Projection")
      .Because("Registry should be called with deeply nested CLR format name");
    await Assert.That(registry.RunnerWasFound).IsTrue()
      .Because("Registry should find runner for deeply nested CLR format names");
  }

  #region Test Fakes

  private sealed class FakeWorkCoordinator : IWorkCoordinator {
    private readonly TaskCompletionSource _completionReported = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<PerspectiveWork> PerspectiveWorkToReturn { get; set; } = [];
    public int ProcessWorkBatchCallCount { get; private set; }
    public int ReportCompletionCallCount { get; private set; }
    public int ReportFailureCallCount { get; private set; }
    public List<PerspectiveCheckpointCompletion> CompletionsReceivedViaProcessWorkBatch { get; } = [];
    public List<PerspectiveCheckpointFailure> FailuresReceivedViaProcessWorkBatch { get; } = [];
    public bool ReturnWorkOnEveryCycle { get; set; }
    public PerspectiveWork? PerspectiveWorkTemplate { get; set; }

    /// <summary>
    /// Waits for a completion to be reported via ReportPerspectiveCompletionAsync.
    /// </summary>
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

      // Track completions received via ProcessWorkBatchAsync parameters
      CompletionsReceivedViaProcessWorkBatch.AddRange(request.PerspectiveCompletions);
      FailuresReceivedViaProcessWorkBatch.AddRange(request.PerspectiveFailures);

      // Return work
      List<PerspectiveWork> work;
      if (ReturnWorkOnEveryCycle && PerspectiveWorkTemplate != null) {
        work = [PerspectiveWorkTemplate];
      } else {
        work = [.. PerspectiveWorkToReturn];
        PerspectiveWorkToReturn.Clear();
      }

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
      ReportFailureCallCount++;
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
    public bool ShouldThrow { get; set; }

    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) {
      return new FakePerspectiveRunner { ShouldThrow = ShouldThrow };
    }

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() {
      return [new PerspectiveRegistrationInfo("Test.FakePerspective", "global::Test.FakePerspective", "global::Test.FakeModel", ["global::Test.FakeEvent"])];
    }

    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class FakePerspectiveRunner : IPerspectiveRunner {
    public bool ShouldThrow { get; set; }

    public Task<PerspectiveCheckpointCompletion> RunAsync(
      Guid streamId,
      string perspectiveName,
      Guid? lastProcessedEventId,
      CancellationToken cancellationToken) {
      if (ShouldThrow) {
        throw new InvalidOperationException("Test exception");
      }

      return Task.FromResult(new PerspectiveCheckpointCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.NewGuid(),
        Status = PerspectiveProcessingStatus.Completed
      });
    }
  }

  /// <summary>
  /// A fake registry that simulates the generated PerspectiveRunnerRegistry behavior.
  /// It only returns runners for CLR format names (e.g., "Namespace.Parent+Child")
  /// and tracks lookup attempts for test assertions.
  /// Uses synchronization primitives to signal when lookups occur.
  /// </summary>
  private sealed class ClrTypeNameAwarePerspectiveRunnerRegistry : IPerspectiveRunnerRegistry {
    private readonly TaskCompletionSource _lookupCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Simulates the generated registry's switch statement with CLR format names
    private readonly HashSet<string> _registeredClrNames = [
      "TestNamespace.ActiveAccount+Projection",
      "TestNamespace.Sessions+Active+Projection",
      "TestNamespace.Perspectives.OrderPerspective",
      "Test.FakePerspective"
    ];

    public string? LastLookedUpName { get; private set; }
    public bool RunnerWasFound { get; private set; }

    /// <summary>
    /// Waits deterministically for a registry lookup to occur, no timers!
    /// </summary>
    public async Task WaitForLookupAsync(TimeSpan timeout) {
      using var cts = new CancellationTokenSource(timeout);
      try {
        await _lookupCompleted.Task.WaitAsync(cts.Token);
      } catch (OperationCanceledException) {
        throw new TimeoutException($"Registry lookup did not occur within {timeout}");
      }
    }

    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) {
      LastLookedUpName = perspectiveName;
      RunnerWasFound = _registeredClrNames.Contains(perspectiveName);

      // Signal that lookup has occurred
      _lookupCompleted.TrySetResult();

      if (RunnerWasFound) {
        return new FakePerspectiveRunner();
      }

      return null;
    }

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() {
      return [
        new PerspectiveRegistrationInfo(
          "TestNamespace.ActiveAccount+Projection",
          "global::TestNamespace.ActiveAccount.Projection",
          "global::TestNamespace.ActiveAccount.Model",
          ["global::TestNamespace.AccountCreatedEvent"]
        ),
        new PerspectiveRegistrationInfo(
          "TestNamespace.Sessions+Active+Projection",
          "global::TestNamespace.Sessions.Active.Projection",
          "global::TestNamespace.Sessions.Active.Model",
          ["global::TestNamespace.SessionEvent"]
        ),
        new PerspectiveRegistrationInfo(
          "TestNamespace.Perspectives.OrderPerspective",
          "global::TestNamespace.Perspectives.OrderPerspective",
          "global::TestNamespace.Perspectives.OrderModel",
          ["global::TestNamespace.Perspectives.OrderCreatedEvent"]
        )
      ];
    }

    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  #endregion
}
