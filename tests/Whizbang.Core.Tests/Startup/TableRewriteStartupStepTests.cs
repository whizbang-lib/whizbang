// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Messaging;
using Whizbang.Core.Startup;
using Whizbang.Core.Tests.Capabilities;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Increment 8: requested table rewrites are a post-ready step — fleet-exclusive (maintainer
/// duty), non-blocking with respect to Ready, deliberately unbounded — instead of an ACCESS
/// EXCLUSIVE lock taken on the runtime maintenance cadence mid-traffic. Execution stays behind
/// the same operator permission, and a request is cleared only after the rewrite demonstrably
/// worked.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/TableRewriteStartupStep.cs</code-under-test>
[Category("Startup")]
public class TableRewriteStartupStepTests {

  private sealed class RewriteCoordinator : IWorkCoordinator {
    public List<TableRewriteCandidate> Candidates { get; init; } = [];
    public double? RatioAfterRewrite { get; init; } = 1.0;
    public List<string> Rewritten { get; } = [];
    public List<string> Cleared { get; } = [];

    public Task<IReadOnlyList<TableRewriteCandidate>> GetTablesNeedingRewriteAsync(CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<TableRewriteCandidate>>(Candidates);
    public Task<double?> RewriteTableAsync(string tableName, CancellationToken cancellationToken = default) {
      Rewritten.Add(tableName);
      return Task.FromResult(RatioAfterRewrite);
    }
    public Task ClearTableRewriteRequestAsync(string tableName, CancellationToken cancellationToken = default) {
      Cleared.Add(tableName);
      return Task.CompletedTask;
    }

    public List<TableRewriteOutcome> Completed { get; } = [];

    public Task CompleteTableRewriteAsync(TableRewriteOutcome outcome, CancellationToken cancellationToken = default) {
      Completed.Add(outcome);
      Cleared.Add(outcome.TableName);
      return Task.CompletedTask;
    }

    /// <summary>The ratio pgstattuple_approx measures after the rewrite, per table.</summary>
    public Dictionary<string, double?> ApproxAfter { get; init; } = new(StringComparer.Ordinal);
    public List<(string Table, Whizbang.Core.Observability.BloatMeasurementMethod Method)> Measured { get; } = [];

    public Task<double?> MeasureTableBloatAsync(
        string tableName, Whizbang.Core.Observability.BloatMeasurementMethod method, CancellationToken cancellationToken = default) {
      Measured.Add((tableName, method));
      return Task.FromResult(ApproxAfter.GetValueOrDefault(tableName));
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

  private static TableRewriteStartupStep _stepOver(RewriteCoordinator coordinator, bool allow = true) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    var provider = services.BuildServiceProvider();
    var options = Options.Create(new MaintenanceWorkerOptions { AllowTableRewrite = allow });
    return new TableRewriteStartupStep(
      scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      rewriters: [new VacuumFullTableRewriter(options)],
      options: options,
      capabilities: null,
      logger: NullLogger<TableRewriteStartupStep>.Instance);
  }

  /// <summary>A rewriter that records what it was offered, under any strategy name.</summary>
  private sealed class RecordingRewriter(string strategy, double? ratioAfter = 1.0, bool canRun = true) : ITableRewriter {
    public List<string> Rewritten { get; } = [];
    public string Strategy { get; } = strategy;
    public bool CanRun { get; } = canRun;
    public string CannotRunReason => $"{Strategy} is not usable here";
    public Task<double?> RewriteAsync(IWorkCoordinator coordinator, string tableName, CancellationToken cancellationToken) {
      Rewritten.Add(tableName);
      return Task.FromResult(ratioAfter);
    }
  }

  private static TableRewriteStartupStep _stepWith(
      RewriteCoordinator? coordinator, TableRewriteStrategy strategy, params ITableRewriter[] rewriters) =>
    _stepWith(coordinator, strategy, capabilities: null, rewriters);

  private static TableRewriteStartupStep _stepWith(
      RewriteCoordinator? coordinator, TableRewriteStrategy strategy, ICapabilityRegistry? capabilities,
      params ITableRewriter[] rewriters) {
    var services = new ServiceCollection();
    if (coordinator is not null) {
      services.AddSingleton<IWorkCoordinator>(coordinator);
    }
    var provider = services.BuildServiceProvider();
    return new TableRewriteStartupStep(
      scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      rewriters: rewriters,
      options: Options.Create(new MaintenanceWorkerOptions { TableRewriteStrategy = strategy, AllowTableRewrite = true }),
      capabilities: capabilities,
      logger: NullLogger<TableRewriteStartupStep>.Instance);
  }

  /// <summary>A registry whose pg_squeeze reads the given state on each database.</summary>
  private static async Task<CapabilityRegistry> _squeezeAsync(params CapabilityState[] perDatabase) {
    var provider = new FakeCapabilityProvider("postgres", [.. perDatabase.Select((_, i) => $"db-{i}")],
      FakeCapabilityProvider.Describe("postgres", "pg_squeeze"));
    var registry = new CapabilityRegistry([provider], NullLogger<CapabilityRegistry>.Instance);
    await registry.RefreshAsync(CancellationToken.None);
    registry.Record([.. registry.Statuses.Select((status, i) => status with { State = perDatabase[i] })]);
    return registry;
  }

  [Test]
  public async Task Descriptor_DeclaresThePostReadyMaintainerShapeAsync() {
    var descriptor = _stepOver(new RewriteCoordinator()).Descriptor;

    await Assert.That(descriptor.Name).IsEqualTo(FrameworkStartupSteps.REWRITE);
    await Assert.That(descriptor.RequiredCapability).IsEqualTo(StartupDuties.MAINTAINER)
      .Because("one instance rewrites — exclusivity follows the capability the step requires");
    await Assert.That(descriptor.NonHolderBehavior).IsEqualTo(NonHolderBehavior.Skip)
      .Because("nobody blocks on a VACUUM FULL");
    await Assert.That(descriptor.Blocking).IsFalse()
      .Because("deliberately unbounded work must never gate Ready");
    await Assert.That(descriptor.DependsOn).IsEquivalentTo([FrameworkStartupSteps.MIGRATE])
      .Because("without a capability registry there is no Capabilities step to wait for");
  }

  [Test]
  public async Task Descriptor_WithACapabilityRegistry_RunsAfterTheCapabilitiesStepAsync() {
    var descriptor = _stepWith(new RewriteCoordinator(), TableRewriteStrategy.Auto, await _squeezeAsync(CapabilityState.Ready)).Descriptor;

    await Assert.That(descriptor.DependsOn).IsEquivalentTo([FrameworkStartupSteps.MIGRATE, CapabilitiesStartupStep.STEP_NAME])
      .Because("the step reads whether pg_squeeze is usable, which only the Capabilities step has probed");
  }

  [Test]
  public async Task WithoutOperatorPermission_SkipsWithTheStatedReasonAsync() {
    var coordinator = new RewriteCoordinator {
      Candidates = { new TableRewriteCandidate("wh_outbox", 4.0, Requested: true) },
    };
    var report = await _stepOver(coordinator, allow: false).ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(report.Reason)
      .IsEqualTo("Auto detects and reports only: VACUUM FULL is not permitted (Whizbang:Workers:Maintenance:AllowTableRewrite = false)")
      .Because("the operator reads this reason to learn which switch holds the rewrite back");
    await Assert.That(coordinator.Rewritten).IsEmpty()
      .Because("the framework cannot know how large a consumer's table is; taking an ACCESS "
             + "EXCLUSIVE lock unattended must be opted into — same permission, new window");
  }

  [Test]
  public async Task WithNothingOwed_SkipsWithNoRewritesOwedAsync() {
    var report = await _stepOver(new RewriteCoordinator()).ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(report.Reason).IsEqualTo("no rewrites owed")
      .Because("'found nothing to do' is a stated fact, distinct from 'not permitted to look'");
  }

  [Test]
  public async Task WithPermission_RewritesAndClearsOnlyConfirmedRequestsAsync() {
    var coordinator = new RewriteCoordinator {
      Candidates = {
        new TableRewriteCandidate("wh_event_store", 4.2, Requested: true),
        new TableRewriteCandidate("wh_outbox", 3.5, Requested: false),
      },
      RatioAfterRewrite = 1.0,
    };
    var report = await _stepOver(coordinator).ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Completed);
    await Assert.That(coordinator.Rewritten).Contains("wh_event_store");
    await Assert.That(coordinator.Rewritten).Contains("wh_outbox");
    await Assert.That(coordinator.Cleared).Contains("wh_event_store")
      .Because("a migration-recorded request is satisfied once the rewrite is confirmed to have worked");
    await Assert.That(coordinator.Cleared).DoesNotContain("wh_outbox")
      .Because("a detector-found candidate has no recorded request to clear");
    await Assert.That(report.Reason).IsEqualTo("rewrote 2 table(s)");
  }

  [Test]
  public async Task IneffectiveRewrite_LeavesTheRequestQueuedForTheNextBootAsync() {
    var coordinator = new RewriteCoordinator {
      Candidates = { new TableRewriteCandidate("wh_event_store", 4.2, Requested: true) },
      RatioAfterRewrite = 4.2,   // no improvement
    };
    var report = await _stepOver(coordinator).ExecuteAsync(CancellationToken.None);

    await Assert.That(coordinator.Cleared).IsEmpty()
      .Because("clearing a request the rewrite did not satisfy loses the only record that the "
             + "table still owes one");
    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Completed);
    await Assert.That(report.Reason).Contains("left queued");
  }
  [Test]
  public async Task RewriteThatReclaimsNothingMeasurable_LeavesTheRequestQueuedAsync() {
    var coordinator = new RewriteCoordinator {
      Candidates = { new TableRewriteCandidate("wh_event_store", 4.2, Requested: true) },
      RatioAfterRewrite = null,   // not rewritten, for example a table the role does not own
    };
    var report = await _stepOver(coordinator).ExecuteAsync(CancellationToken.None);

    await Assert.That(coordinator.Rewritten).Contains("wh_event_store");
    await Assert.That(coordinator.Cleared).IsEmpty()
      .Because("no measurement after the rewrite is no evidence it worked");
    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Completed);
    await Assert.That(report.Reason).IsEqualTo("rewrote 0 table(s); 1 ineffective, left queued for the next boot");
  }

  [Test]
  public async Task ResolvesTheRewriterByStrategyNameAsync() {
    var coordinator = new RewriteCoordinator {
      Candidates = { new TableRewriteCandidate("wh_outbox", 3.5, Requested: true) },
    };
    var other = new RecordingRewriter("Other");
    var chosen = new RecordingRewriter(TableRewriteStrategies.VACUUM_FULL);

    var report = await _stepWith(coordinator, TableRewriteStrategy.VacuumFull, other, chosen).ExecuteAsync(CancellationToken.None);

    await Assert.That(chosen.Rewritten).IsEquivalentTo(["wh_outbox"])
      .Because("the step hands each owed table to the rewriter of the selected strategy");
    await Assert.That(other.Rewritten).IsEmpty()
      .Because("a registered rewriter of another strategy is never called, wherever it is registered");
    await Assert.That(coordinator.Rewritten).IsEmpty()
      .Because("the rewrite goes through the rewriter, not straight to the coordinator");
    await Assert.That(coordinator.Cleared).IsEquivalentTo(["wh_outbox"]);
    await Assert.That(report.Reason).IsEqualTo("rewrote 1 table(s)");
  }

  [Test]
  public async Task PgSqueeze_IsLeftToTheMaintenanceCycle_NotRunAtBootAsync() {
    var coordinator = new RewriteCoordinator {
      Candidates = { new TableRewriteCandidate("wh_outbox", 3.5, Requested: true) },
    };
    var squeeze = new RecordingRewriter(TableRewriteStrategies.PG_SQUEEZE);
    var vacuum = new RecordingRewriter(TableRewriteStrategies.VACUUM_FULL);

    var report = await _stepWith(coordinator, TableRewriteStrategy.PgSqueeze, await _squeezeAsync(CapabilityState.Ready), squeeze, vacuum)
      .ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(report.Reason).IsEqualTo(
      "PgSqueeze: online rewrite through pg_squeeze; the maintenance cycle performs it, largest bloat first, up to "
      + "Whizbang:Workers:Maintenance:OnlineRewriteMaxBytesPerCycle bytes per cycle");
    await Assert.That(squeeze.Rewritten).IsEmpty()
      .Because("the online rewrite is budgeted per maintenance cycle, so the boot step never runs it unbounded");
    await Assert.That(vacuum.Rewritten).IsEmpty()
      .Because("an explicit online choice is never replaced by a blocking rewrite, though AllowTableRewrite is true");
    await Assert.That(coordinator.Cleared).IsEmpty();
  }

  [Test]
  public async Task PgRepackInProcess_IsLeftToTheMaintenanceCycle_NotRunAtBootAsync() {
    var coordinator = new RewriteCoordinator {
      Candidates = { new TableRewriteCandidate("wh_outbox", 3.5, Requested: true) },
    };
    var repack = new RecordingRewriter(TableRewriteStrategies.PG_REPACK);
    var vacuum = new RecordingRewriter(TableRewriteStrategies.VACUUM_FULL);

    var report = await _stepWith(coordinator, TableRewriteStrategy.PgRepack, repack, vacuum).ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(report.Reason).IsEqualTo(
      "PgRepack: online rewrite through pg_repack; the maintenance cycle performs it, largest bloat first, up to "
      + "Whizbang:Workers:Maintenance:OnlineRewriteMaxBytesPerCycle bytes per cycle");
    await Assert.That(repack.Rewritten).IsEmpty()
      .Because("pg_repack in process is budgeted per maintenance cycle like pg_squeeze, so the boot step never runs it unbounded");
    await Assert.That(vacuum.Rewritten).IsEmpty()
      .Because("an explicit online choice is never replaced by a blocking rewrite, though AllowTableRewrite is true");
    await Assert.That(coordinator.Cleared).IsEmpty();
  }

  [Test]
  public async Task AStrategyWithNoRegisteredRewriter_SaysSo_AndLeavesTheQueueAsync() {
    var coordinator = new RewriteCoordinator {
      Candidates = { new TableRewriteCandidate("wh_outbox", 3.5, Requested: true) },
    };

    var report = await _stepWith(coordinator, TableRewriteStrategy.VacuumFull).ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(report.Reason).EndsWith("; no table rewriter is registered for it, so requested tables stay queued");
    await Assert.That(coordinator.Cleared).IsEmpty();
  }

  [Test]
  public async Task Auto_WithPgSqueezeUsable_NamesTheOptIn_AndStartsNoOnlineRewriteAsync() {
    var coordinator = new RewriteCoordinator {
      Candidates = { new TableRewriteCandidate("wh_outbox", 3.5, Requested: true) },
    };
    var squeeze = new RecordingRewriter(TableRewriteStrategies.PG_SQUEEZE);
    var vacuum = new RecordingRewriter(TableRewriteStrategies.VACUUM_FULL);

    var report = await _stepWith(coordinator, TableRewriteStrategy.Auto,
      await _squeezeAsync(CapabilityState.Ready, CapabilityState.Provisionable), squeeze, vacuum).ExecuteAsync(CancellationToken.None);

    await Assert.That(squeeze.Rewritten).IsEmpty().Because("Auto never starts an online rewrite (decision 1, #1671)");
    await Assert.That(vacuum.Rewritten).IsEquivalentTo(["wh_outbox"]).Because("AllowTableRewrite is true");
    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Completed);
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Auto_ReportsPgSqueezeUsable_OnlyWhenItIsUsableOnEveryDatabaseAsync(bool everyDatabase) {
    var registry = everyDatabase
      ? await _squeezeAsync(CapabilityState.Ready, CapabilityState.Ready)
      : await _squeezeAsync(CapabilityState.Ready, CapabilityState.NotPermitted);
    var step = new TableRewriteStartupStep(
      new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), [],
      Options.Create(new MaintenanceWorkerOptions()), registry, NullLogger<TableRewriteStartupStep>.Instance);

    var report = await step.ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(report.Reason!.Contains("pg_squeeze is usable", StringComparison.Ordinal)).IsEqualTo(everyDatabase);
  }

  [Test]
  public async Task Auto_WithARegistryThatProbesNoPostgres_DoesNotReportPgSqueezeUsableAsync() {
    var registry = new CapabilityRegistry([new FakeCapabilityProvider("broker", ["vhost-a"], FakeCapabilityProvider.Describe("broker", "x"))],
      NullLogger<CapabilityRegistry>.Instance);
    var step = new TableRewriteStartupStep(
      new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), [],
      Options.Create(new MaintenanceWorkerOptions()), registry, NullLogger<TableRewriteStartupStep>.Instance);

    var report = await step.ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Reason).DoesNotContain("pg_squeeze");
  }

  [Test]
  public async Task None_RewritesNothing_WhateverIsPermittedAsync() {
    var coordinator = new RewriteCoordinator {
      Candidates = { new TableRewriteCandidate("wh_outbox", 3.5, Requested: true) },
    };
    var vacuum = new RecordingRewriter(TableRewriteStrategies.VACUUM_FULL);

    var report = await _stepWith(coordinator, TableRewriteStrategy.None, vacuum).ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(report.Reason).StartsWith("None: no table is rewritten");
    await Assert.That(vacuum.Rewritten).IsEmpty();
  }

  [Test]
  public async Task SelectedRewriterThatCannotRun_SkipsWithItsReasonBeforeTouchingTheDatabaseAsync() {
    var cannot = new RecordingRewriter(TableRewriteStrategies.VACUUM_FULL, canRun: false);

    // No coordinator registered: a skip that resolved one first would report the wrong reason.
    var report = await _stepWith(coordinator: null, TableRewriteStrategy.VacuumFull, cannot).ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(report.Reason).IsEqualTo("VacuumFull is not usable here");
    await Assert.That(cannot.Rewritten).IsEmpty();
  }

  [Test]
  public async Task Constructor_RejectsMissingArgumentsAsync() {
    var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    var options = Options.Create(new MaintenanceWorkerOptions());
    var logger = NullLogger<TableRewriteStartupStep>.Instance;

    await Assert.That(() => new TableRewriteStartupStep(null!, [], options, null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new TableRewriteStartupStep(scopeFactory, null!, options, null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new TableRewriteStartupStep(scopeFactory, [], null!, null, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new TableRewriteStartupStep(scopeFactory, [], options, null, null!)).Throws<ArgumentNullException>();
  }

  // ---- the re-measure after a rewrite uses pgstattuple_approx while it is Ready (#1807) ----

  /// <summary>A registry with pgstattuple in <paramref name="state"/> on one database.</summary>
  private static async Task<CapabilityRegistry> _pgstattupleAsync(CapabilityState state) {
    var provider = new FakeCapabilityProvider("postgres", ["db-0"], FakeCapabilityProvider.Describe("postgres", "pgstattuple"));
    provider.States["pgstattuple"] = state;
    var registry = new CapabilityRegistry([provider], NullLogger<CapabilityRegistry>.Instance);
    await registry.RefreshAsync(CancellationToken.None);
    return registry;
  }

  [Test]
  public async Task WhenPgstattupleIsReady_TheReMeasureIsApprox_AndItDecidesTheClearAsync() {
    var coordinator = new RewriteCoordinator {
      Candidates = {
        new TableRewriteCandidate("wh_outbox", 4.0, Requested: true) { Method = Whizbang.Core.Observability.BloatMeasurementMethod.Approx },
        new TableRewriteCandidate("wh_inbox", 4.0, Requested: true) { Method = Whizbang.Core.Observability.BloatMeasurementMethod.Approx },
      },
      ApproxAfter = { ["wh_outbox"] = 1.1, ["wh_inbox"] = 4.2 },
    };
    // The rewriter's own (estimate) re-measure says both improved; the measurement says only one did.
    var vacuum = new RecordingRewriter(TableRewriteStrategies.VACUUM_FULL, ratioAfter: 1.2);
    var step = _stepWith(coordinator, TableRewriteStrategy.VacuumFull, await _pgstattupleAsync(CapabilityState.Ready), vacuum);

    var report = await step.ExecuteAsync(CancellationToken.None);

    await Assert.That(coordinator.Measured).IsEquivalentTo([
      ("wh_outbox", Whizbang.Core.Observability.BloatMeasurementMethod.Approx),
      ("wh_inbox", Whizbang.Core.Observability.BloatMeasurementMethod.Approx),
    ]);
    await Assert.That(coordinator.Cleared).IsEquivalentTo(["wh_outbox"]);
    await Assert.That(report.Reason).IsEqualTo("rewrote 1 table(s); 1 ineffective, left queued for the next boot");
  }

  [Test]
  public async Task WhilePgstattupleIsNotReady_TheRewritersReMeasureDecidesAsync() {
    var coordinator = new RewriteCoordinator {
      Candidates = { new TableRewriteCandidate("wh_outbox", 4.0, Requested: true) },
      ApproxAfter = { ["wh_outbox"] = 4.2 },
    };
    var vacuum = new RecordingRewriter(TableRewriteStrategies.VACUUM_FULL, ratioAfter: 1.2);
    var step = _stepWith(coordinator, TableRewriteStrategy.VacuumFull, await _pgstattupleAsync(CapabilityState.Provisionable), vacuum);

    await step.ExecuteAsync(CancellationToken.None);

    await Assert.That(coordinator.Measured).IsEmpty();
    await Assert.That(coordinator.Cleared).IsEquivalentTo(["wh_outbox"]);
  }

  // ---- #2300: a lowered fillfactor waits for an online strategy ----

  private static TableRewriteCandidate _fillFactorDecrease(string table) =>
    new(table, 1.1, Requested: true) { Reason = TableRewriteReason.FillFactorDecrease };

  private static (TableRewriteStartupStep Step, FakeLogger<TableRewriteStartupStep> Logger) _loggedStep(
      RewriteCoordinator coordinator, TableRewriteStrategy strategy, bool allow, params ITableRewriter[] rewriters) {
    var services = new ServiceCollection().AddSingleton<IWorkCoordinator>(coordinator).BuildServiceProvider();
    var logger = new FakeLogger<TableRewriteStartupStep>();
    var step = new TableRewriteStartupStep(
      scopeFactory: services.GetRequiredService<IServiceScopeFactory>(),
      rewriters: rewriters,
      options: Options.Create(new MaintenanceWorkerOptions { TableRewriteStrategy = strategy, AllowTableRewrite = allow }),
      capabilities: null,
      logger: logger);
    return (step, logger);
  }

  private static string[] _waiting(FakeLogger<TableRewriteStartupStep> logger) =>
    [.. logger.Collector.GetSnapshot().Select(r => r.Message).Where(m => m.Contains("lowered fillfactor", StringComparison.Ordinal))];

  /// <summary>
  /// #2300: VACUUM FULL is permitted for bloat, and still never takes a table offered only because its fillfactor was
  /// lowered: that rewrite is for an online strategy alone. The bloated table beside it is rewritten as before, and the
  /// start says once which tables wait and what would rewrite them.
  /// </summary>
  [Test]
  public async Task VacuumFull_NeverTakesAFillFactorDecrease_AndTheStartSaysItWaitsAsync() {
    var coordinator = new RewriteCoordinator {
      Candidates = { new TableRewriteCandidate("wh_outbox", 4.0, Requested: true), _fillFactorDecrease("wh_per_order"), _fillFactorDecrease("wh_per_item") },
    };
    var vacuum = new RecordingRewriter(TableRewriteStrategies.VACUUM_FULL);
    var (step, logger) = _loggedStep(coordinator, TableRewriteStrategy.Auto, allow: true, vacuum);

    var report = await step.ExecuteAsync(CancellationToken.None);

    await Assert.That(vacuum.Rewritten).IsEquivalentTo(["wh_outbox"])
      .Because("a lowered fillfactor on a compact table is never worth an ACCESS EXCLUSIVE lock taken unattended");
    await Assert.That(report.Reason).IsEqualTo("rewrote 1 table(s)");
    var waiting = _waiting(logger);
    await Assert.That(waiting).Count().IsEqualTo(1).Because("one line per start, naming every table that waits");
    await Assert.That(waiting[0]).Contains("wh_per_order, wh_per_item")
      .And.Contains("Whizbang:Workers:Maintenance:TableRewriteStrategy")
      .And.Contains("PgSqueeze").And.Contains("PgRepack")
      .And.Contains("VACUUM FULL is never used");
  }

  /// <summary>#2300: with no rewrite permitted at all, the request still waits and the start still says so.</summary>
  [Test]
  [Arguments(TableRewriteStrategy.Auto, false)]
  [Arguments(TableRewriteStrategy.None, true)]
  public async Task NoRewritePermitted_AFillFactorDecreaseWaits_AndTheStartSaysSoAsync(TableRewriteStrategy strategy, bool allow) {
    var coordinator = new RewriteCoordinator { Candidates = { _fillFactorDecrease("wh_per_order") } };
    var vacuum = new RecordingRewriter(TableRewriteStrategies.VACUUM_FULL);
    var (step, logger) = _loggedStep(coordinator, strategy, allow, vacuum);

    var report = await step.ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(vacuum.Rewritten).IsEmpty();
    await Assert.That(coordinator.Cleared).IsEmpty();
    await Assert.That(_waiting(logger)).Count().IsEqualTo(1);
  }

  /// <summary>#2300: only fillfactor decreases owed, VACUUM FULL permitted: nothing is rewritten and the start says why.</summary>
  [Test]
  public async Task OnlyFillFactorDecreasesOwed_VacuumFullRewritesNothingAsync() {
    var coordinator = new RewriteCoordinator { Candidates = { _fillFactorDecrease("wh_per_order") } };
    var vacuum = new RecordingRewriter(TableRewriteStrategies.VACUUM_FULL);
    var (step, _) = _loggedStep(coordinator, TableRewriteStrategy.VacuumFull, allow: true, vacuum);

    var report = await step.ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(report.Reason).IsEqualTo("no rewrites owed");
    await Assert.That(vacuum.Rewritten).IsEmpty();
  }

  /// <summary>#2300: under an online strategy the maintenance cycle honors the request, so the start logs no wait.</summary>
  [Test]
  public async Task AnOnlineStrategy_LogsNoWait_ForTheCycleHonorsItAsync() {
    var coordinator = new RewriteCoordinator { Candidates = { _fillFactorDecrease("wh_per_order") } };
    var (step, logger) = _loggedStep(coordinator, TableRewriteStrategy.PgSqueeze, allow: false,
      new RecordingRewriter(TableRewriteStrategies.PG_SQUEEZE));

    await step.ExecuteAsync(CancellationToken.None);

    await Assert.That(_waiting(logger)).IsEmpty();
  }

  /// <summary>#2300: a start with no fillfactor decrease owed logs no wait.</summary>
  [Test]
  public async Task NoFillFactorDecreaseOwed_LogsNoWaitAsync() {
    var coordinator = new RewriteCoordinator { Candidates = { new TableRewriteCandidate("wh_outbox", 4.0, Requested: true) } };
    var (step, logger) = _loggedStep(coordinator, TableRewriteStrategy.Auto, allow: false);

    await step.ExecuteAsync(CancellationToken.None);

    await Assert.That(_waiting(logger)).IsEmpty();
  }

  /// <summary>#2300: a start that rewrites nothing and has no coordinator registered skips with the plan's reason.</summary>
  [Test]
  public async Task NoRewritePermitted_WithNoCoordinator_SkipsWithThePlansReasonAsync() {
    var report = await _stepWith(coordinator: null, TableRewriteStrategy.None).ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(report.Reason).StartsWith("None: no table is rewritten");
  }

  /// <summary>#2301: <c>Online</c> is left to the maintenance cycle, which picks the strategy each cycle; nothing runs at boot.</summary>
  [Test]
  public async Task Online_IsLeftToTheMaintenanceCycle_NotRunAtBootAsync() {
    var coordinator = new RewriteCoordinator { Candidates = { new TableRewriteCandidate("wh_per_order", 4.0, Requested: true), _fillFactorDecrease("wh_per_item") } };
    var vacuum = new RecordingRewriter(TableRewriteStrategies.VACUUM_FULL);
    var (step, logger) = _loggedStep(coordinator, TableRewriteStrategy.Online, allow: true, vacuum);

    var report = await step.ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(report.Reason).StartsWith("Online: ").And.Contains("the maintenance cycle performs it");
    await Assert.That(vacuum.Rewritten).IsEmpty();
    await Assert.That(_waiting(logger)).IsEmpty().Because("the cycle rewrites it, so nothing waits for a strategy");
  }

  /// <summary>#2301: the boot-time rewrite records the strategy that carried the request out, as the request is cleared.</summary>
  [Test]
  public async Task ARewriteAtBoot_RecordsItsStrategyAsync() {
    var coordinator = new RewriteCoordinator { Candidates = { new TableRewriteCandidate("wh_outbox", 4.0, Requested: true) } };

    await _stepOver(coordinator).ExecuteAsync(CancellationToken.None);

    await Assert.That(coordinator.Completed).IsEquivalentTo([
      new TableRewriteOutcome("wh_outbox", TableRewriteStrategies.VACUUM_FULL, TableRewriteReason.Bloat, 4.0, 1.0)]);
  }
}
