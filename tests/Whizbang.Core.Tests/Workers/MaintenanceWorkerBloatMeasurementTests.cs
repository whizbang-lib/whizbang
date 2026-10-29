// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Capabilities;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #1807: the catalog estimate shortlists, and pgstattuple_approx confirms. When pgstattuple is Ready, a table the
/// estimate puts over the rewrite threshold is measured before it enters the rewrite queue, queued with the measured
/// ratio and its method, and not queued at all when the measurement shows the estimate was wrong. Without pgstattuple
/// the cycle records from the estimate exactly as before.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/ApproxBloatMeasurement.cs</code-under-test>
/// <docs>operations/infrastructure/storage-maintenance#how-bloat-is-measured</docs>
public class MaintenanceWorkerBloatMeasurementTests {
  private const double THRESHOLD = 3.0;

  private sealed class MeasuringCoordinator : IWorkCoordinator {
    public List<TableRewriteCandidate> Candidates { get; init; } = [];
    public Dictionary<string, double?> Approx { get; init; } = new(StringComparer.Ordinal);
    public Exception? MeasureThrows { get; init; }
    public List<(string Table, BloatMeasurementMethod Method)> Measured { get; } = [];
    public List<string> Requested { get; } = [];
    public List<(string Table, BloatMeasurement Measurement, double Estimate)> MeasuredRequests { get; } = [];

    public Task<IReadOnlyList<TableRewriteCandidate>> GetTablesNeedingRewriteAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<TableRewriteCandidate>>(Candidates);

    public Task<double?> MeasureTableBloatAsync(string tableName, BloatMeasurementMethod method, CancellationToken cancellationToken = default) {
      Measured.Add((tableName, method));
      return MeasureThrows is not null
        ? Task.FromException<double?>(MeasureThrows)
        : Task.FromResult(Approx.GetValueOrDefault(tableName));
    }

    public Task RequestTableRewriteAsync(string tableName, CancellationToken cancellationToken = default) {
      Requested.Add(tableName);
      return Task.CompletedTask;
    }

    public Task RequestMeasuredTableRewriteAsync(
        string tableName, BloatMeasurement measurement, double estimatedRatio, CancellationToken cancellationToken = default) {
      MeasuredRequests.Add((tableName, measurement, estimatedRatio));
      return Task.CompletedTask;
    }

    // Unused surface for this test.
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
  }

  private static TableRewriteCandidate _candidate(string table, double estimate, bool requested = false) =>
    new(table, estimate, requested) { RewriteThreshold = THRESHOLD };

  /// <summary>A registry whose pgstattuple reads <paramref name="state"/> on one database.</summary>
  private static async Task<CapabilityRegistry> _pgstattupleAsync(CapabilityState state) {
    var provider = new FakeCapabilityProvider("postgres", ["db-0"], FakeCapabilityProvider.Describe("postgres", "pgstattuple"));
    provider.States["pgstattuple"] = state;
    var registry = new CapabilityRegistry([provider], NullLogger<CapabilityRegistry>.Instance);
    await registry.RefreshAsync(CancellationToken.None);
    return registry;
  }

  private sealed record Run(MeasuringCoordinator Coordinator, FakeLogger<MaintenanceWorker> Logger, TableStatisticsMetrics Metrics);

  private static async Task<Run> _runAsync(
      MeasuringCoordinator coordinator, CapabilityState? pgstattuple, CancellationToken cancellationToken = default) =>
    await _runAsync(coordinator, pgstattuple is { } state ? await _pgstattupleAsync(state) : null, cycles: 1, cancellationToken);

  private static async Task<Run> _runAsync(
      MeasuringCoordinator coordinator, ICapabilityRegistry? registry, int cycles, CancellationToken cancellationToken = default) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    if (registry is not null) {
      services.AddSingleton(registry);
    }
    var metrics = new TableStatisticsMetrics(new WhizbangMetrics(new TestMeterFactory()));
    services.AddSingleton(metrics);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var logger = new FakeLogger<MaintenanceWorker>();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1, StuckRowSentinelEnabled = false }),
      logger);
    for (var cycle = 0; cycle < cycles; cycle++) {
      await worker.RunMaintenanceOnceAsync(cancellationToken);
    }
    return new Run(coordinator, logger, metrics);
  }

  private static List<string> _messages(FakeLogger<MaintenanceWorker> logger) =>
    [.. logger.Collector.GetSnapshot().Select(r => r.Message)];

  // --- Without pgstattuple: unchanged ---------------------------------------------------------

  /// <summary>Locks Ready as Ready on every probed database; anything else works from the estimate.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#bloat-pgstattuple-ready</docs>
  [Test]
  public async Task WithoutACapabilityRegistry_TheEstimateIsRecorded_AndNothingIsMeasuredAsync() {
    var run = await _runAsync(new MeasuringCoordinator { Candidates = { _candidate("wh_outbox", 4.2) } }, pgstattuple: null);

    await Assert.That(run.Coordinator.Requested).IsEquivalentTo(["wh_outbox"])
      .Because("without pgstattuple the cycle records from the estimate, exactly as before");
    await Assert.That(run.Coordinator.Measured).IsEmpty()
      .Because("nothing may read a table when whether pgstattuple is there is not known");
    await Assert.That(run.Coordinator.MeasuredRequests).IsEmpty();
  }

  /// <summary>Locks Ready as Ready on every probed database; anything else works from the estimate.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#bloat-pgstattuple-ready</docs>
  [Test]
  [Arguments(CapabilityState.NotPermitted)]
  [Arguments(CapabilityState.NoPrivilege)]
  [Arguments(CapabilityState.Provisionable)]
  public async Task WhilePgstattupleIsNotReady_TheEstimateIsRecorded_AndNothingIsMeasuredAsync(CapabilityState state) {
    var run = await _runAsync(new MeasuringCoordinator { Candidates = { _candidate("wh_outbox", 4.2) } }, state);

    await Assert.That(run.Coordinator.Requested).IsEquivalentTo(["wh_outbox"]);
    await Assert.That(run.Coordinator.Measured).IsEmpty()
      .Because($"pgstattuple is {state}, not Ready, so its functions are not there to call");
  }

  // --- pgstattuple Ready: estimate shortlists, approx confirms --------------------------------

  /// <summary>Locks the exact pgstattuple scan never running.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#bloat-exact-never-runs</docs>
  [Test]
  public async Task WhenReady_ACandidateIsMeasuredWithApprox_AndQueuedWithTheMeasuredRatioAsync() {
    var run = await _runAsync(new MeasuringCoordinator {
      Candidates = { _candidate("wh_outbox", 4.2) },
      Approx = { ["wh_outbox"] = 5.1 },
    }, CapabilityState.Ready);

    await Assert.That(run.Coordinator.Measured).IsEquivalentTo([("wh_outbox", BloatMeasurementMethod.Approx)])
      .Because("the measurement comes before the queue, and it is never the exact full scan");
    await Assert.That(run.Coordinator.MeasuredRequests)
      .IsEquivalentTo([("wh_outbox", new BloatMeasurement(5.1, BloatMeasurementMethod.Approx), 4.2)])
      .Because("the queue row holds the measured ratio and its method, and the estimate it confirmed");
    await Assert.That(run.Coordinator.Requested).IsEmpty()
      .Because("the table is queued once, with its measurement, not also from its estimate");
  }

  /// <summary>Locks a rejected estimate being measured again on the next cycle.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#bloat-rejected-remeasured</docs>
  [Test]
  public async Task WhenReady_AFalsePositiveEstimate_IsNotQueued_AndTheLogGivesBothFiguresAsync() {
    var run = await _runAsync(new MeasuringCoordinator {
      Candidates = { _candidate("wh_inbox", 4.2) },
      Approx = { ["wh_inbox"] = 1.3 },
    }, CapabilityState.Ready);

    await Assert.That(run.Coordinator.Requested).IsEmpty()
      .Because("the measurement says the table is under the threshold, so a rewrite would reclaim nothing");
    await Assert.That(run.Coordinator.MeasuredRequests).IsEmpty();
    await Assert.That(_messages(run.Logger)).Contains(
      "Table wh_inbox was estimated at 4.2x but measures 1.3x by approx, under the rewrite threshold of 3x; it is not queued");
  }

  /// <summary>Locks the 25% disagreement tolerance.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#bloat-disagreement-tolerance</docs>
  [Test]
  public async Task WhenReady_AMeasurementThatDisagreesWithItsEstimate_IsLoggedWithBothFiguresAsync() {
    var run = await _runAsync(new MeasuringCoordinator {
      Candidates = { _candidate("wh_outbox", 4), _candidate("wh_inbox", 4) },
      Approx = { ["wh_outbox"] = 6, ["wh_inbox"] = 4.5 },
    }, CapabilityState.Ready);

    var disagreements = _messages(run.Logger).Where(m => m.Contains("disagree", StringComparison.Ordinal)).ToList();
    await Assert.That(disagreements).IsEquivalentTo([
      "Bloat of wh_outbox: the estimate 4x and the approx measurement 6x disagree by more than 25%",
    ]).Because("50% apart is reported with both figures; 12.5% apart is within the tolerance and is not");
  }

  /// <summary>Locks tables already queued (a migration's requests among them) not being measured again.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#bloat-requested-tables</docs>
  [Test]
  public async Task WhenReady_ATableAlreadyQueued_IsNotMeasuredAgainAsync() {
    var run = await _runAsync(new MeasuringCoordinator {
      Candidates = { _candidate("wh_event_store", 4.2, requested: true) },
      Approx = { ["wh_event_store"] = 5 },
    }, CapabilityState.Ready);

    await Assert.That(run.Coordinator.Measured).IsEmpty()
      .Because("a table enters the queue measured once; the rewrite's own re-measure is what follows it");
    await Assert.That(run.Coordinator.MeasuredRequests).IsEmpty();
    await Assert.That(run.Metrics.GetMeasuredBloatForTest("wh_event_store")).IsNull()
      .Because("a table a migration requested stays on its estimate: nothing measured it, so nothing reports it as measured");
  }

  /// <summary>Locks a failed measurement: a warning, and the estimate stands in.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#bloat-measurement-failure</docs>
  [Test]
  public async Task WhenReady_AMeasurementThatFails_FallsBackToTheEstimate_AndSaysWhyAsync() {
    var run = await _runAsync(new MeasuringCoordinator {
      Candidates = { _candidate("wh_outbox", 4.2) },
      MeasureThrows = new InvalidOperationException("permission denied for function pgstattuple_approx"),
    }, CapabilityState.Ready);

    await Assert.That(run.Coordinator.Requested).IsEquivalentTo(["wh_outbox"])
      .Because("a failed measurement leaves the cycle where it was without pgstattuple: queued from the estimate");
    var failure = run.Logger.Collector.GetSnapshot().Single(r => r.Exception is InvalidOperationException);
    await Assert.That(failure.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(failure.Message).IsEqualTo(
      "Measuring the bloat of wh_outbox with pgstattuple_approx failed; the estimate stands in for it");
  }

  [Test]
  public async Task WhenReady_ATableWithNothingToMeasure_IsQueuedFromItsEstimateAsync() {
    var run = await _runAsync(new MeasuringCoordinator { Candidates = { _candidate("wh_outbox", 4.2) } }, CapabilityState.Ready);

    await Assert.That(run.Coordinator.Measured).Count().IsEqualTo(1);
    await Assert.That(run.Coordinator.Requested).IsEquivalentTo(["wh_outbox"])
      .Because("a measurement with no answer (no rows, or a backend that does not measure) confirms nothing and rejects nothing");
  }

  [Test]
  public async Task WhenReady_ACancellationWhileMeasuring_StopsTheCycleAsync() {
    var coordinator = new MeasuringCoordinator {
      Candidates = { _candidate("wh_outbox", 4.2) },
      MeasureThrows = new OperationCanceledException(),
    };

    await Assert.That(async () => await _runAsync(coordinator, CapabilityState.Ready)).Throws<OperationCanceledException>();
    await Assert.That(coordinator.Requested).IsEmpty()
      .Because("a stopping host is not a failed measurement, so nothing falls back to the estimate");
  }

  // --- The gauge -------------------------------------------------------------------------------

  [Test]
  public async Task WhenReady_EveryMeasurementAndEveryMeasuredQueueRow_IsReportedOnTheGaugeAsync() {
    var run = await _runAsync(new MeasuringCoordinator {
      Candidates = {
        _candidate("wh_outbox", 4.2),
        _candidate("wh_inbox", 4.2),
        new TableRewriteCandidate("wh_event_store", 7.5, Requested: true) { Method = BloatMeasurementMethod.Approx },
        _candidate("wh_settings", 3.5, requested: true),
      },
      Approx = { ["wh_outbox"] = 5.1, ["wh_inbox"] = 1.2 },
    }, CapabilityState.Ready);

    await Assert.That(run.Metrics.GetMeasuredBloatForTest("wh_outbox")).IsEqualTo(new BloatMeasurement(5.1, BloatMeasurementMethod.Approx));
    await Assert.That(run.Metrics.GetMeasuredBloatForTest("wh_inbox")).IsEqualTo(new BloatMeasurement(1.2, BloatMeasurementMethod.Approx))
      .Because("a rejected estimate is still a measurement, and the gauge is where an operator sees the estimate was wrong");
    await Assert.That(run.Metrics.GetMeasuredBloatForTest("wh_event_store")).IsEqualTo(new BloatMeasurement(7.5, BloatMeasurementMethod.Approx))
      .Because("a table stays measured while it waits in the queue");
    await Assert.That(run.Metrics.GetMeasuredBloatForTest("wh_settings")).IsNull()
      .Because("a row queued from its estimate has no measurement; the statistics pass reports its estimate");
  }

  [Test]
  public async Task WithoutPgstattuple_NothingIsReportedAsMeasuredAsync() {
    var run = await _runAsync(new MeasuringCoordinator { Candidates = { _candidate("wh_outbox", 4.2) } }, pgstattuple: null);

    await Assert.That(run.Metrics.GetMeasuredBloatForTest("wh_outbox")).IsNull();
  }

  // --- Design decisions, each locked (#1807) ---------------------------------------------------

  /// <summary>Locks the 25% disagreement tolerance.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#bloat-disagreement-tolerance</docs>
  [Test]
  [Arguments(4.99, false)]
  [Arguments(5.0, false)]
  [Arguments(5.01, true)]
  [Arguments(3.01, false)]
  [Arguments(2.99, true)]
  public async Task TheDisagreementTolerance_IsAQuarterOfTheEstimate_EitherWayAsync(double measured, bool logged) {
    var run = await _runAsync(new MeasuringCoordinator {
      Candidates = { _candidate("wh_outbox", 4.0) },
      Approx = { ["wh_outbox"] = measured },
    }, CapabilityState.Ready);

    var disagreed = _messages(run.Logger).Any(m => m.Contains("disagree", StringComparison.Ordinal));
    await Assert.That(disagreed).IsEqualTo(logged)
      .Because("a measurement more than 25% from its estimate is logged with both figures, and one within 25% is not");
  }

  /// <summary>Locks Ready as Ready on every probed database; anything else works from the estimate.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#bloat-pgstattuple-ready</docs>
  [Test]
  public async Task WhenPgstattupleIsReadyOnOneDatabaseButNotAnother_NothingIsMeasuredAsync() {
    var provider = new FakeCapabilityProvider("postgres", ["db-0", "db-1"], FakeCapabilityProvider.Describe("postgres", "pgstattuple"));
    var registry = new CapabilityRegistry([provider], NullLogger<CapabilityRegistry>.Instance);
    await registry.RefreshAsync(CancellationToken.None);
    registry.Record([.. registry.Statuses.Select(status =>
      status.Scope == "db-1" ? status with { State = CapabilityState.NotPermitted } : status)]);

    var run = await _runAsync(
      new MeasuringCoordinator { Candidates = { _candidate("wh_outbox", 4.2) }, Approx = { ["wh_outbox"] = 1.0 } },
      registry, cycles: 1);

    await Assert.That(run.Coordinator.Measured).IsEmpty()
      .Because("Ready means Ready on every database the registry probed: the coordinator may be on the one without it");
    await Assert.That(run.Coordinator.Requested).IsEquivalentTo(["wh_outbox"])
      .Because("without a measurement the estimate queues the table, exactly as without pgstattuple");
  }

  /// <summary>Locks a rejected estimate being measured again on the next cycle.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#bloat-rejected-remeasured</docs>
  [Test]
  public async Task ARejectedFalsePositive_IsMeasuredAgainOnTheNextCycle_AndStillNotQueuedAsync() {
    var run = await _runAsync(
      new MeasuringCoordinator { Candidates = { _candidate("wh_inbox", 4.2) }, Approx = { ["wh_inbox"] = 1.3 } },
      await _pgstattupleAsync(CapabilityState.Ready), cycles: 2);

    await Assert.That(run.Coordinator.Measured).IsEquivalentTo([
      ("wh_inbox", BloatMeasurementMethod.Approx),
      ("wh_inbox", BloatMeasurementMethod.Approx),
    ]).Because("a rejection is not remembered: while the estimate still shortlists the table, each cycle measures it again, "
             + "so a table that really does bloat later is queued");
    await Assert.That(run.Coordinator.Requested).IsEmpty();
    await Assert.That(run.Coordinator.MeasuredRequests).IsEmpty();
  }
}
