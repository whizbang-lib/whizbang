using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

[NotInParallel("WhizbangBackgroundServiceTests")]
public class MaintenanceWorkerTests {

  private sealed class FakeCoordinator : IWorkCoordinator {
    public TaskCompletionSource FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int CallCount { get; private set; }

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken ct = default) {
      CallCount++;
      FirstCall.TrySetResult();
      return Task.FromResult<IReadOnlyList<MaintenanceResult>>([
        new MaintenanceResult("test_task", RowsAffected: 3, DurationMs: 1.5, Status: "ok")
      ]);
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default) => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  [Test]
  public async Task ExecuteAsync_FirstTick_CallsPerformMaintenanceAsync() {
    var coord = new FakeCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      NullLogger<MaintenanceWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    await coord.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(coord.CallCount).IsGreaterThanOrEqualTo(1);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_DisabledOptions_NoMaintenanceFiresAsync() {
    var coord = new FakeCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();  // gate ready, but Enabled=false should still skip
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions { Enabled = false, IntervalMinutes = 1 }),
      NullLogger<MaintenanceWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    await Task.WhenAny(coord.FirstCall.Task, Task.Delay(500, CancellationToken.None));
    await Assert.That(coord.FirstCall.Task.IsCompleted).IsFalse();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_BlocksOnSchemaGate_UntilMarkedReadyAsync() {
    var coord = new FakeCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();  // not marked ready
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      NullLogger<MaintenanceWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    await Task.WhenAny(coord.FirstCall.Task, Task.Delay(300, CancellationToken.None));
    await Assert.That(coord.FirstCall.Task.IsCompleted).IsFalse();

    gate.MarkReady();
    await coord.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(coord.CallCount).IsGreaterThanOrEqualTo(1);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // --- Audit gap #5 regression locks ---
  //
  // perform_maintenance is what cleans abandoned wh_active_streams + completed-row purges
  // (production mode). If the maintenance worker silently runs at a bad cadence, dead
  // ownership rows accumulate and degrade owner-preferring claim. These tests pin the
  // contract so a future config refactor can't quietly disable cleanup.

  [Test]
  public async Task DefaultIntervalMinutes_IsLessThanOrEqualTo10MinutesForReasonableCleanupCadenceAsync() {
    // Production cleanup of abandoned wh_active_streams runs ONLY through perform_maintenance.
    // If the default interval is hours, dead-instance ownership accumulates for hours after
    // a deploy/scale event. ≤10 min keeps the recovery boundary tight.
    var defaults = new MaintenanceWorkerOptions();
    await Assert.That(defaults.IntervalMinutes).IsLessThanOrEqualTo(10)
      .Because("perform_maintenance is the only path that cleans abandoned wh_active_streams; >10 min lets dead-instance ownership accumulate.");
  }

  [Test]
  public async Task DefaultEnabled_IsTrueAsync() {
    // Maintenance must be on by default. If a future refactor flips this and ops doesn't notice,
    // wh_outbox / wh_inbox / wh_message_deduplication grow without bound (production-mode
    // completion now DELETEs but perform_maintenance still purges anything that slipped through
    // failure paths).
    var defaults = new MaintenanceWorkerOptions();
    await Assert.That(defaults.Enabled).IsTrue();
  }
}
