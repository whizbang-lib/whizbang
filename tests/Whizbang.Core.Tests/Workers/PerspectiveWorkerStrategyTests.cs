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
    await Task.Delay(150); // Let at least 2 cycles complete
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
    await Task.Delay(100); // Let first cycle complete
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
    await Task.Delay(100); // Let first cycle complete
    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected during shutdown
    }

    // Assert - Strategy should have reported failure immediately
    await Assert.That(coordinator.ReportFailureCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Instant strategy should report failures immediately via coordinator");
  }

  #region Test Fakes

  private sealed class FakeWorkCoordinator : IWorkCoordinator {
    public List<PerspectiveWork> PerspectiveWorkToReturn { get; set; } = [];
    public int ProcessWorkBatchCallCount { get; private set; }
    public int ReportCompletionCallCount { get; private set; }
    public int ReportFailureCallCount { get; private set; }
    public List<PerspectiveCheckpointCompletion> CompletionsReceivedViaProcessWorkBatch { get; } = [];
    public List<PerspectiveCheckpointFailure> FailuresReceivedViaProcessWorkBatch { get; } = [];
    public bool ReturnWorkOnEveryCycle { get; set; }
    public PerspectiveWork? PerspectiveWorkTemplate { get; set; }

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
        work = new List<PerspectiveWork>(PerspectiveWorkToReturn);
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

  #endregion
}
