// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Startup;

/// <summary>
/// The <c>Rewrite</c> step: performs the table rewrites migrations and the runtime bloat detector
/// have recorded, through the <see cref="ITableRewriter"/> of the strategy
/// <see cref="TableRewriteStrategyResolver"/> resolves from <see cref="MaintenanceWorkerOptions.TableRewriteStrategy"/>
/// and <see cref="MaintenanceWorkerOptions.AllowTableRewrite"/>. The framework's rewriter is
/// <see cref="VacuumFullTableRewriter"/>: the blocking, space-reclaiming <c>VACUUM FULL</c>
/// that cannot run inside a migration transaction. Post-ready and fleet-exclusive by declaration: it requires the
/// <see cref="StartupDuties.MAINTAINER"/> duty (one instance rewrites), non-holders
/// <see cref="NonHolderBehavior.Skip"/> (nobody blocks on a <c>VACUUM FULL</c> — the entire
/// reason it is non-blocking and deliberately unbounded, because a half-finished rewrite is
/// worse than a slow one).
/// </summary>
/// <remarks>
/// Previously this work ran on the <em>runtime</em> maintenance cadence, taking an ACCESS
/// EXCLUSIVE lock mid-traffic; the pipeline gives it the window it should always have had.
/// The maintenance cycle detects and records. A strategy that resolves to no rewrite, or to a strategy
/// with no registered rewriter, skips with the resolution as its reason, and so does a rewriter that cannot run.
/// <see cref="TableRewriteStrategy.PgSqueeze"/>, <see cref="TableRewriteStrategy.PgRepack"/> with the client run in
/// process, and <see cref="TableRewriteStrategy.Auto"/> when one of those two is Ready on this start (#2319), are left to
/// the maintenance cycle (<see cref="OnlineTableRewriteStep"/>), which rewrites online within a per-cycle byte budget, so
/// such a table is never also taken by <c>VACUUM FULL</c>; before anything else, every start writes the detector's
/// settings (<see cref="TableRewriteSettings"/>) to <c>wh_settings</c>; pg_repack in <see cref="PgRepackMode.External"/> mode is skipped with the reason its rewriter
/// gives, because an operator's job runs it. <c>VACUUM FULL</c> stays behind the operator permission
/// (<see cref="MaintenanceWorkerOptions.AllowTableRewrite"/>), because the framework cannot know how
/// large a consumer's table is, and taking that lock unattended must be opted into.
/// A request is cleared only after the ratio is confirmed to have dropped; an interrupted or
/// ineffective rewrite stays queued for the next boot instead of being silently forgotten.
/// A table offered only because its fillfactor was lowered (<see cref="TableRewriteReason.FillFactorDecrease"/>) is never
/// taken by <c>VACUUM FULL</c>, whatever <see cref="MaintenanceWorkerOptions.AllowTableRewrite"/> says: when no online
/// strategy is chosen, the step logs once which tables wait and what would rewrite them (#2300).
/// </remarks>
/// <docs>operations/startup/capabilities-and-duties</docs>
/// <docs>operations/infrastructure/migrations#table-rewrites-run-post-ready-under-the-maintainer-duty</docs>
/// <docs>operations/infrastructure/storage-maintenance#fillfactor-decrease</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/TableRewriteStartupStepTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FillFactorDecreaseRewriteTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Startup/TableRewriteStartupStepCoverageTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableRewriteJourneyE2ETests.cs</tests>
public sealed partial class TableRewriteStartupStep : IStartupStep {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly ITableRewriter[] _rewriters;
  private readonly IOptions<MaintenanceWorkerOptions> _options;
  private readonly ICapabilityRegistry? _capabilities;
  private readonly ILogger<TableRewriteStartupStep> _logger;

  /// <summary>Creates the step over the scope factory the coordinator resolves from.</summary>
  /// <param name="scopeFactory">The scope factory the work coordinator is resolved from.</param>
  /// <param name="rewriters">The registered rewriters; the one whose <see cref="ITableRewriter.Strategy"/> the strategy resolves to runs.</param>
  /// <param name="options">The maintenance options: <see cref="MaintenanceWorkerOptions.TableRewriteStrategy"/> and <see cref="MaintenanceWorkerOptions.AllowTableRewrite"/>.</param>
  /// <param name="capabilities">
  /// The capability registry, which says whether pg_squeeze is usable, or null when the host registers none. With
  /// one, the step runs after the <c>Capabilities</c> step that probes it.
  /// </param>
  /// <param name="logger">The step's logger.</param>
  public TableRewriteStartupStep(
      IServiceScopeFactory scopeFactory,
      IEnumerable<ITableRewriter> rewriters,
      IOptions<MaintenanceWorkerOptions> options,
      ICapabilityRegistry? capabilities,
      ILogger<TableRewriteStartupStep> logger) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(rewriters);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(logger);
    _scopeFactory = scopeFactory;
    _rewriters = [.. rewriters];
    _options = options;
    _capabilities = capabilities;
    _logger = logger;
    Descriptor = new() {
      Name = FrameworkStartupSteps.REWRITE,
      DependsOn = capabilities is null
        ? [FrameworkStartupSteps.MIGRATE]
        : [FrameworkStartupSteps.MIGRATE, CapabilitiesStartupStep.STEP_NAME],
      RequiredCapability = StartupDuties.MAINTAINER,
      NonHolderBehavior = NonHolderBehavior.Skip,
      Blocking = false,
    };
  }

  /// <inheritdoc />
  public StartupStepDescriptor Descriptor { get; }

  /// <inheritdoc />
  public async ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
    var options = _options.Value;
    var plan = TableRewriteStrategyResolver.Resolve(options.TableRewriteStrategy, options.AllowTableRewrite, _pgSqueezeUsable(),
      OnlineRewriteTools.AutoTool(_capabilities, options, _rewriters));
    await _publishSettingsAsync(options, plan, cancellationToken).ConfigureAwait(false);
    if (plan.Strategy is null) {
      using var waitScope = _scopeFactory.CreateScope();
      if (waitScope.ServiceProvider.GetService<IWorkCoordinator>() is { } waitCoordinator) {
        _reportWaiting(await waitCoordinator.GetTablesNeedingRewriteAsync(cancellationToken).ConfigureAwait(false));
      }
      return new StartupStepReport(StartupStepOutcome.Skipped, plan.Reason);
    }
    if (_runsInTheCycle(plan, options)) {
      // An online rewrite Whizbang runs itself is budgeted per maintenance cycle (OnlineTableRewriteStep); run here it
      // would be unbounded. pg_repack in External mode falls through: its rewriter cannot run, and says why.
      return new StartupStepReport(StartupStepOutcome.Skipped,
        $"{plan.Reason}; the maintenance cycle performs it, largest bloat first, up to {ONLINE_BUDGET_KEY} bytes per cycle");
    }
    var rewriter = Array.Find(_rewriters, r => string.Equals(r.Strategy, plan.Strategy, StringComparison.Ordinal));
    if (rewriter is null) {
      return new StartupStepReport(StartupStepOutcome.Skipped,
        $"{plan.Reason}; no table rewriter is registered for it, so requested tables stay queued");
    }
    if (!rewriter.CanRun) {
      return new StartupStepReport(StartupStepOutcome.Skipped, rewriter.CannotRunReason);
    }

    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
    if (coordinator is null) {
      return new StartupStepReport(StartupStepOutcome.Skipped, "no work coordinator registered");
    }

    // Candidates are re-measured at call time, so a request recorded for an already-rewritten
    // table yields nothing rather than an expensive no-op.
    // A table offered only for a lowered fillfactor is for an online strategy alone: it is compact, and a blocking
    // VACUUM FULL is never taken for it (#2300). It waits, and the start says so.
    var offered = await coordinator.GetTablesNeedingRewriteAsync(cancellationToken).ConfigureAwait(false);
    _reportWaiting(offered);
    var candidates = offered.Where(c => c.Reason != TableRewriteReason.FillFactorDecrease).ToList();
    if (candidates.Count == 0) {
      return new StartupStepReport(StartupStepOutcome.Skipped, "no rewrites owed");
    }

    // While pgstattuple is Ready, "did it shrink" is answered by pgstattuple_approx, the measurement that queued the
    // table, rather than by the rewriter's catalog estimate (#1807).
    var approxReady = ApproxBloatMeasurement.IsReady(_capabilities);
    var meter = new RewriteMeter(scope.ServiceProvider.GetService<TableStatisticsMetrics>(),
      scope.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System);
    var rewritten = 0;
    var leftQueued = 0;
    foreach (var candidate in candidates) {
      cancellationToken.ThrowIfCancellationRequested();
      if (await _rewriteOneAsync(rewriter, coordinator, candidate, approxReady, meter, cancellationToken).ConfigureAwait(false)) {
        rewritten++;
      } else {
        leftQueued++;
      }
    }

    return new StartupStepReport(StartupStepOutcome.Completed,
      leftQueued == 0
        ? $"rewrote {rewritten} table(s)"
        : $"rewrote {rewritten} table(s); {leftQueued} ineffective, left queued for the next boot");
  }

  /// <summary>
  /// Whether the plan's tool is one the maintenance cycle runs within its byte budget: pg_squeeze, the Online choice, or
  /// pg_repack with its client in process, whether chosen explicitly or by <see cref="TableRewriteStrategy.Auto"/>
  /// (#2319), so the boot step never takes such a table with <c>VACUUM FULL</c> as well.
  /// </summary>
  private static bool _runsInTheCycle(TableRewritePlan plan, MaintenanceWorkerOptions options) =>
    plan.Strategy is TableRewriteStrategies.PG_SQUEEZE or TableRewriteStrategies.ONLINE
    || (plan.Strategy == TableRewriteStrategies.PG_REPACK && options.TableRewrite.PgRepack.Mode == PgRepackMode.InProcess);

  /// <summary>
  /// Writes the detector's settings (the threshold, the row floor, the regrowth factor) to <c>wh_settings</c>, each the
  /// configured value or its default for this start's plan, before the candidates are read (#2319). Configuration is the
  /// source of truth, so a hand-edited row is put back. Best-effort: a failed write is logged with the keys it did not
  /// write, and the step goes on with the values already stored. Cancellation is shutdown and propagates.
  /// </summary>
  private async Task _publishSettingsAsync(MaintenanceWorkerOptions options, TableRewritePlan plan, CancellationToken cancellationToken) {
    var settings = TableRewriteSettings.SettingsFor(options, plan);
    using var scope = _scopeFactory.CreateScope();
    if (scope.ServiceProvider.GetService<IWorkCoordinator>() is not { } coordinator) {
      return;
    }
    try {
      await coordinator.SyncRetentionSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogSettingsNotPublished(_logger, string.Join(", ", settings.Select(s => $"{s.Key}={s.Value}")), ex);
    }
  }

  // Rewrites one table and measures it again; true when it shrank. One that did not shrink keeps its request, so the
  // next boot retries it, and says so rather than clearing it and reporting a success that was not achieved.
  // Each attempt is recorded on the rewrite meters (#2319): its outcome, how long it took and what it returned.
  private async Task<bool> _rewriteOneAsync(
      ITableRewriter rewriter, IWorkCoordinator coordinator, TableRewriteCandidate candidate, bool approxReady,
      RewriteMeter meter, CancellationToken cancellationToken) {
    var started = meter.Time.GetTimestamp();
    var size = meter.Metrics is null
      ? null
      : await coordinator.GetTableTotalBytesAsync(candidate.TableName, cancellationToken).ConfigureAwait(false);
    TableRewriteMetricOutcome outcome;
    try {
      outcome = await _attemptOneAsync(rewriter, coordinator, candidate, approxReady, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      meter.Metrics?.RecordRewrite(new TableRewriteAttempt(
        candidate.TableName, rewriter.Strategy, candidate.Reason, TableRewriteMetricOutcome.Failed, meter.Time.GetElapsedTime(started), null));
      throw;
    }
    if (meter.Metrics is { } metrics) {
      var reclaimed = outcome == TableRewriteMetricOutcome.Rewritten
        ? TableStatisticsMetrics.Reclaimed(size,
            await coordinator.GetTableTotalBytesAsync(candidate.TableName, cancellationToken).ConfigureAwait(false))
        : null;
      metrics.RecordRewrite(new TableRewriteAttempt(
        candidate.TableName, rewriter.Strategy, candidate.Reason, outcome, meter.Time.GetElapsedTime(started), reclaimed));
    }
    return outcome == TableRewriteMetricOutcome.Rewritten;
  }

  /// <summary>Where the boot step's rewrites are recorded and timed: the table statistics meter, when registered, and the host's clock.</summary>
  private sealed record RewriteMeter(TableStatisticsMetrics? Metrics, TimeProvider Time);

  private async Task<TableRewriteMetricOutcome> _attemptOneAsync(
      ITableRewriter rewriter, IWorkCoordinator coordinator, TableRewriteCandidate candidate, bool approxReady,
      CancellationToken cancellationToken) {
    var ratio = await rewriter.RewriteAsync(coordinator, candidate.TableName, cancellationToken).ConfigureAwait(false);
    var after = await ApproxBloatMeasurement.AfterRewriteAsync(
      coordinator, candidate.TableName, ratio, approxReady, _logger, cancellationToken).ConfigureAwait(false);
    var before = candidate.Method.ToValue();
    var afterMethod = after?.Method.ToValue() ?? "none";
    if (after is not { } measured || measured.Ratio >= candidate.BloatRatio) {
      LogRewriteIneffective(_logger, candidate.TableName, candidate.BloatRatio, before, after?.Ratio ?? -1, afterMethod);
      return after is null ? TableRewriteMetricOutcome.Failed : TableRewriteMetricOutcome.NoGain;
    }
    LogRewriteDone(_logger, candidate.TableName, candidate.BloatRatio, before, measured.Ratio, afterMethod);
    if (candidate.Requested) {
      await coordinator.CompleteTableRewriteAsync(
        new TableRewriteOutcome(candidate.TableName, rewriter.Strategy, candidate.Reason, candidate.BloatRatio, measured.Ratio),
        cancellationToken).ConfigureAwait(false);
    }
    return TableRewriteMetricOutcome.Rewritten;
  }

  /// <summary>Logs once, naming them, the tables that wait for an online strategy to apply a lowered fillfactor.</summary>
  private void _reportWaiting(IReadOnlyList<TableRewriteCandidate> offered) {
    var waiting = offered.Where(c => c.Reason == TableRewriteReason.FillFactorDecrease).Select(c => c.TableName).ToList();
    if (waiting.Count > 0) {
      LogFillFactorWaiting(_logger, string.Join(", ", waiting), STRATEGY_KEY);
    }
  }

  /// <summary>Whether pg_squeeze is usable (Ready or Provisionable) on every database the registry probes it on.</summary>
  private bool _pgSqueezeUsable() {
    if (_capabilities is null) {
      return false;
    }
    var squeeze = new CapabilityId(RequiresPostgresExtensionAttribute.PROVIDER, PG_SQUEEZE_EXTENSION);
    var statuses = _capabilities.Statuses.Where(s => s.Descriptor.Id == squeeze).ToList();
    return statuses.Count > 0 && statuses.TrueForAll(s => s.IsUsable);
  }

  private const string PG_SQUEEZE_EXTENSION = "pg_squeeze";
  private const string ONLINE_BUDGET_KEY = "Whizbang:Workers:Maintenance:OnlineRewriteMaxBytesPerCycle";
  private const string STRATEGY_KEY = "Whizbang:Workers:Maintenance:TableRewriteStrategy";

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
    Message = "Rewrite step: {Tables} wait for one rewrite to apply their lowered fillfactor to the rows already stored. Only an online rewrite performs it: set {Key} to PgSqueeze, or to PgRepack with its client, and the maintenance cycle rewrites them within its budget. VACUUM FULL is never used for it; the requests stay queued")]
  static partial void LogFillFactorWaiting(ILogger logger, string tables, string key);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
    Message = "Rewrite step: the table-rewrite settings ({Settings}) could not be written to wh_settings; the values already stored stand until the next start")]
  static partial void LogSettingsNotPublished(ILogger logger, string settings, Exception exception);

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Rewrite step: {Table} rewritten, bloat {Before}x ({BeforeMethod}) -> {After}x ({AfterMethod})")]
  static partial void LogRewriteDone(ILogger logger, string table, double before, string beforeMethod, double after, string afterMethod);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "Rewrite step: {Table} rewrite did not reduce bloat ({Before}x ({BeforeMethod}) -> {After}x ({AfterMethod})); request left queued")]
  static partial void LogRewriteIneffective(
      ILogger logger, string table, double before, string beforeMethod, double after, string afterMethod);
}
