// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2354: the operational retention sweep runs inside the maintenance cycle (and so behind its idle gate), in bounded
/// batches, with each category's resting period, and reports what it removed and what is left per category.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
public class MaintenanceWorkerOperationalRetentionTests {

  private sealed class SweepCoordinator : IWorkCoordinator {
    public List<(IReadOnlyDictionary<OperationalCategory, TimeSpan> Retention, int BatchSize)> Calls { get; } = [];

    /// <summary>What each call returns, in order; the last repeats.</summary>
    public List<IReadOnlyList<OperationalRetentionSweepResult>> Results { get; init; } = [[]];

    public Task<IReadOnlyList<OperationalRetentionSweepResult>> SweepOperationalRetentionAsync(
        IReadOnlyDictionary<OperationalCategory, TimeSpan> retention, int batchSize, CancellationToken cancellationToken = default) {
      Calls.Add((retention, batchSize));
      return Task.FromResult(Results[Math.Min(Calls.Count, Results.Count) - 1]);
    }

    /// <summary>#2363: every carried-detail roll-off call, and what each returns, in order; the last repeats.</summary>
    public List<(TimeSpan OlderThan, int BatchSize)> RollOffs { get; } = [];
    public List<int> Cleared { get; init; } = [0];

    public Task<int> RollOffCarriedDetailAsync(TimeSpan olderThan, int batchSize, CancellationToken cancellationToken = default) {
      RollOffs.Add((olderThan, batchSize));
      return Task.FromResult(Cleared[Math.Min(RollOffs.Count, Cleared.Count) - 1]);
    }

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private static OperationalRetentionSweepResult _result(OperationalCategory category, string table, int expired, long remaining,
      TimeSpan? oldest = null) => new(category, table, expired, remaining, oldest);

  private static MaintenanceWorker _build(SweepCoordinator coordinator, OperationalRetentionOptions? retention,
      MaintenanceMetrics? metrics = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    if (retention is not null) {
      services.AddSingleton(Options.Create(retention));
    }
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    var sp = services.BuildServiceProvider();
    return new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1, StuckRowSentinelEnabled = false }),
      NullLogger<MaintenanceWorker>.Instance);
  }

  [Test]
  public async Task TheCycle_SweepsWithEachCategorysRetention_AndTheBatchSizeAsync() {
    var coordinator = new SweepCoordinator();
    var worker = _build(coordinator, new OperationalRetentionOptions { RewriteLog = TimeSpan.FromDays(30), BatchSize = 50 });

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    var (retention, batch) = coordinator.Calls.Single();
    await Assert.That(batch).IsEqualTo(50);
    await Assert.That(retention[OperationalCategory.RewriteLog]).IsEqualTo(TimeSpan.FromDays(30));
    await Assert.That(retention[OperationalCategory.Queues]).IsEqualTo(TimeSpan.FromDays(14))
      .Because("a category with no override rests for the default");
    await Assert.That(retention.Count).IsEqualTo(Enum.GetValues<OperationalCategory>().Length)
      .Because("the sweep is told every category, so a table registered under any of them is swept");
  }

  [Test]
  public async Task TheCycle_RepeatsWhileABatchCameBackFull_AndStopsWhenOneIsShortAsync() {
    var coordinator = new SweepCoordinator {
      Results = [
        [_result(OperationalCategory.RewriteLog, "wh_integrity_origin_rewrites", 10, 100)],
        [_result(OperationalCategory.RewriteLog, "wh_integrity_origin_rewrites", 10, 90)],
        [_result(OperationalCategory.RewriteLog, "wh_integrity_origin_rewrites", 3, 87)],
      ],
    };
    var worker = _build(coordinator, new OperationalRetentionOptions { BatchSize = 10, MaxBatchesPerCycle = 5 });

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Calls.Count).IsEqualTo(3)
      .Because("a full batch means more may be due; a short one means the backlog is drained");
  }

  [Test]
  public async Task TheCycle_NeverRunsMoreBatchesThanItsCapAsync() {
    var coordinator = new SweepCoordinator {
      Results = [[_result(OperationalCategory.Queues, "wh_archive_restore_requests", 10, 1_000_000)]],
    };
    var worker = _build(coordinator, new OperationalRetentionOptions { BatchSize = 10, MaxBatchesPerCycle = 2 });

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Calls.Count).IsEqualTo(2)
      .Because("a backlog drains over several cycles instead of holding one cycle, and the live work, behind it");
  }

  [Test]
  public async Task Disabled_SweepsNothingAsync() {
    var coordinator = new SweepCoordinator();
    var worker = _build(coordinator, new OperationalRetentionOptions { Enabled = false });

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Calls).IsEmpty();
  }

  [Test]
  public async Task NoOptionsRegistered_SweepsWithTheDefaultsAsync() {
    var coordinator = new SweepCoordinator();
    var worker = _build(coordinator, retention: null);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Calls.Single().Retention[OperationalCategory.Reports]).IsEqualTo(TimeSpan.FromDays(14))
      .Because("turn-key: a host that configures nothing still has its bookkeeping roll off");
  }

  [Test]
  [NotInParallel("Metrics")]
  public async Task TheCycle_ReportsRowsRemovedAndLeft_AndTheOldestAge_PerCategoryAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new MaintenanceMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    var coordinator = new SweepCoordinator {
      Results = [
        [
          _result(OperationalCategory.RewriteLog, "wh_integrity_origin_rewrites", 4, 20, TimeSpan.FromDays(9)),
          _result(OperationalCategory.Queues, "wh_archive_restore_requests", 0, 0),
          _result(OperationalCategory.Queues, "wh_other_queue", 2, 5, TimeSpan.FromDays(3)),
        ],
      ],
    };
    var worker = _build(coordinator, new OperationalRetentionOptions(), metrics);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    double Sum(string name, string category) => helper.GetByName(name)
      .Where(m => m.Tags.TryGetValue("category", out var c) && Equals(c, category)).Sum(m => m.Value);
    await Assert.That(Sum("whizbang.maintenance.operational_rows_expired", "RewriteLog")).IsEqualTo(4d);
    await Assert.That(Sum("whizbang.maintenance.operational_rows_expired", "Queues")).IsEqualTo(2d);
    await Assert.That(Sum("whizbang.maintenance.operational_rows", "RewriteLog")).IsEqualTo(20d);
    await Assert.That(Sum("whizbang.maintenance.operational_rows", "Queues")).IsEqualTo(5d)
      .Because("a category's rows are the sum over its tables");
    await Assert.That(Sum("whizbang.maintenance.operational_oldest_age_seconds", "Queues")).IsEqualTo(TimeSpan.FromDays(3).TotalSeconds)
      .Because("a category's oldest age is its oldest table's, and an empty table has none to report");
    await Assert.That(helper.GetByName("whizbang.maintenance.operational_rows").All(m => m.Tags.Keys.All(k => k == "category")))
      .IsTrue().Because("the category is the only attribute: no table, stream or tenant");
  }

  [Test]
  [NotInParallel("Metrics")]
  public async Task TheCycle_RollsOffCarriedDetail_WithItsOwnRest_InBoundedBatches_AndCountsItAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new MaintenanceMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    var coordinator = new SweepCoordinator { Cleared = [5, 5, 2] };
    var worker = _build(coordinator,
      new OperationalRetentionOptions { CarriedDetail = TimeSpan.FromDays(3), BatchSize = 5, MaxBatchesPerCycle = 10 }, metrics);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.RollOffs.Count).IsEqualTo(3)
      .Because("#2363: a full batch means more may be due; a short one means the carried detail past its rest is cleared");
    await Assert.That(coordinator.RollOffs.All(r => r.OlderThan == TimeSpan.FromDays(3) && r.BatchSize == 5)).IsTrue()
      .Because("the carried detail rests for its own category's period");
    await Assert.That(helper.GetByName("whizbang.maintenance.operational_rows_expired")
      .Where(m => m.Tags.TryGetValue("category", out var c) && c == "CarriedDetail").Sum(m => m.Value)).IsEqualTo(12d);
  }

  [Test]
  public async Task TheCarriedDetailRollOff_NeverRunsMoreBatchesThanTheCapAsync() {
    var coordinator = new SweepCoordinator { Cleared = [10] };
    var worker = _build(coordinator, new OperationalRetentionOptions { BatchSize = 10, MaxBatchesPerCycle = 2 });

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.RollOffs.Count).IsEqualTo(2);
    await Assert.That(coordinator.RollOffs[0].OlderThan).IsEqualTo(TimeSpan.FromDays(14))
      .Because("with no override the carried detail rests for the default period");
  }
}
