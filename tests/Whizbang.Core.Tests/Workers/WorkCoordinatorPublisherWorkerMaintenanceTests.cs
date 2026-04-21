using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for periodic maintenance in WorkCoordinatorPublisherWorker.
/// Verifies that PerformMaintenanceAsync is called at the configured interval.
/// </summary>
/// <tests>src/Whizbang.Core/Workers/WorkCoordinatorPublisherWorker.cs</tests>
public class WorkCoordinatorPublisherWorkerMaintenanceTests {

  // ================================================================
  // Test infrastructure
  // ================================================================

  private sealed class MaintenanceTrackingWorkCoordinator : IWorkCoordinator {
    public int MaintenanceCallCount;
    public readonly TaskCompletionSource MaintenanceCalledSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref MaintenanceCallCount);
      MaintenanceCalledSignal.TrySetResult();
      return Task.FromResult<IReadOnlyList<MaintenanceResult>>([
        new MaintenanceResult("purge_old_deduplication", 5, 1.2, "ok")
      ]);
    }
  }

  private sealed class FailingMaintenanceWorkCoordinator : IWorkCoordinator {
    public int MaintenanceCallCount;
    public readonly TaskCompletionSource MaintenanceCalledSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref MaintenanceCallCount);
      MaintenanceCalledSignal.TrySetResult();
      throw new InvalidOperationException("Maintenance failed deliberately for testing");
    }
  }

  private sealed class TestPublishStrategy : IMessagePublishStrategy {
    public bool SupportsBulkPublish => true;
    public Task<bool> IsReadyAsync(CancellationToken ct) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) =>
      Task.FromResult(new MessagePublishResult { MessageId = work.MessageId, Success = true, CompletedStatus = MessageProcessingStatus.Published });
    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> work, CancellationToken ct) =>
      Task.FromResult<IReadOnlyList<MessagePublishResult>>(work.Select(w => new MessagePublishResult { MessageId = w.MessageId, Success = true, CompletedStatus = MessageProcessingStatus.Published }).ToList());
  }

  private sealed class TestChannelWriter : IWorkChannelWriter {
    private readonly Channel<OutboxWork> _channel = Channel.CreateUnbounded<OutboxWork>();
    public ChannelReader<OutboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(OutboxWork work) => _channel.Writer.TryWrite(work);
    public void Complete() => _channel.Writer.Complete();
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public void ClearInFlight() { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public event Action? OnNewWorkAvailable;
    public void SignalNewWorkAvailable() => OnNewWorkAvailable?.Invoke();
    public event Action? OnNewPerspectiveWorkAvailable;
    public void SignalNewPerspectiveWorkAvailable() => OnNewPerspectiveWorkAvailable?.Invoke();
  }

  private static ServiceProvider _buildServices(
    IWorkCoordinator workCoordinator,
    TimeSpan maintenanceInterval) {
    var services = new ServiceCollection();
    services.AddSingleton(workCoordinator);
    services.AddSingleton<IMessagePublishStrategy>(new TestPublishStrategy());
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(Guid.NewGuid(), "TestService", "TestHost", Environment.ProcessId));
    services.AddSingleton<IWorkChannelWriter>(new TestChannelWriter());
    services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions {
      PollingIntervalMilliseconds = 50,
      MaintenanceInterval = maintenanceInterval
    }));
    services.AddLogging();
    services.AddHostedService<WorkCoordinatorPublisherWorker>();
    return services.BuildServiceProvider();
  }

  // ================================================================
  // Tests
  // ================================================================

  [Test]
  public async Task PeriodicMaintenance_IntervalElapsed_CallsPerformMaintenanceAsync() {
    // Arrange — use a very short interval so maintenance fires quickly
    var workCoordinator = new MaintenanceTrackingWorkCoordinator();
    var services = _buildServices(workCoordinator, maintenanceInterval: TimeSpan.FromMilliseconds(100));

    // Act — start the worker, which sets _lastMaintenanceRun = now
    // We need to wait for the interval to elapse
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await worker.StartAsync(cts.Token);

    // Wait for maintenance to be called
    await workCoordinator.MaintenanceCalledSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(workCoordinator.MaintenanceCallCount).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task PeriodicMaintenance_DisabledViaZeroInterval_NeverCallsMaintenanceAsync() {
    // Arrange — use TimeSpan.Zero to disable periodic maintenance
    var workCoordinator = new MaintenanceTrackingWorkCoordinator();
    var services = _buildServices(workCoordinator, maintenanceInterval: TimeSpan.Zero);

    // Act — run the worker for a bit
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await worker.StartAsync(cts.Token);

    // Wait enough time that maintenance would have fired if enabled
    await Task.Delay(500, cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert — maintenance should NEVER have been called
    await Assert.That(workCoordinator.MaintenanceCallCount).IsEqualTo(0);
  }

  [Test]
  public async Task PeriodicMaintenance_ExceptionThrown_WorkerContinuesNormallyAsync() {
    // Arrange — use a coordinator that throws on maintenance
    var workCoordinator = new FailingMaintenanceWorkCoordinator();
    var services = _buildServices(workCoordinator, maintenanceInterval: TimeSpan.FromMilliseconds(100));

    // Act — start the worker
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await worker.StartAsync(cts.Token);

    // Wait for maintenance to be called (and fail)
    await workCoordinator.MaintenanceCalledSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));

    // Wait a bit more — worker should continue running despite the exception
    await Task.Delay(300, cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert — maintenance was attempted, and the worker survived the exception
    await Assert.That(workCoordinator.MaintenanceCallCount).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task PeriodicMaintenance_DefaultInterval_IsSixHoursAsync() {
    // Arrange
    var options = new WorkCoordinatorPublisherOptions();

    // Assert
    await Assert.That(options.MaintenanceInterval).IsEqualTo(TimeSpan.FromHours(6));
  }
}
