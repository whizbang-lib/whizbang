// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// The maintenance step that rewrites queued tables online, through pg_squeeze when
/// <see cref="MaintenanceWorkerOptions.TableRewriteStrategy"/> is <see cref="TableRewriteStrategy.PgSqueeze"/>, or through
/// pg_repack when it is <see cref="TableRewriteStrategy.PgRepack"/> with the client run in process
/// (<see cref="PgRepackMode.InProcess"/>), through the first of the two that can run under
/// <see cref="TableRewriteStrategy.Auto"/> (#2319): largest bloat ratio first, within
/// <see cref="MaintenanceWorkerOptions.OnlineRewriteMaxBytesPerCycle"/> per cycle, and a request cleared only when the
/// re-measure shows the rewrite improved the table.
/// </summary>
/// <remarks>
/// <para>
/// It runs inside the maintenance cycle, so where the cycle runs: on the maintainer duty's holder, and only when the
/// housekeeping gate admits the cycle. The boot-time <c>Rewrite</c> step leaves both online strategies to it, so an
/// online rewrite is always bounded by the budget. pg_repack in <see cref="PgRepackMode.External"/> mode is the
/// operator's job, and this step leaves it alone.
/// </para>
/// <para>
/// Prerequisites, in order: the strategy is one of the online ones, or Auto with an online tool that can run (otherwise
/// the step does nothing, not even a scan); its capabilities are <see cref="CapabilityState.Ready"/> wherever the capability registry probed them
/// (pg_squeeze on every database; for pg_repack the extension on every database and the <c>program/pg_repack</c>
/// client on this host); a rewriter is registered under the strategy's name. Anything unmet is logged and leaves the
/// queue as it is. For pg_squeeze, each table's own prerequisites (logical WAL, a free replication slot, an identity
/// index, the role's rights) are checked by the coordinator before it calls pg_squeeze; for pg_repack, the coordinator
/// chooses the connection and hands it to the client only through its environment.
/// </para>
/// <para>
/// Only tables in the <c>pending_table_rewrites</c> queue are rewritten; the cycle's bloat detection puts a table there
/// before this step runs, and the schema pass puts one there when it lowers the table's fillfactor. A table offered only
/// for that (<see cref="TableRewriteReason.FillFactorDecrease"/>) is rewritten once, whatever its bloat, and its request
/// is cleared once the rewrite ran, since a compact table rewritten at a lower fillfactor measures larger, not smaller
/// (#2300). They are taken largest bloat ratio first, ties by name. A table whose heap does not fit what is
/// left of the budget is deferred to a later cycle and the next smaller one that fits is taken, so one large table does
/// not hold the rest back; a table larger than the whole budget is never rewritten, and the log names the setting to
/// raise. A cycle with nothing to rewrite says so once, at Information, with the queue's length, the threshold and how
/// many tables the post-rewrite floor held back, and each attempt is recorded on the table statistics meter (#2319).
/// </para>
/// </remarks>
/// <param name="rewriters">The registered rewriters; the one under the online strategy's name runs.</param>
/// <param name="options">The maintenance options: the strategy, the pg_repack mode and the byte budget.</param>
/// <param name="capabilities">The capability registry, which says whether the strategy's capabilities are Ready; null when the host registers none.</param>
/// <param name="logger">The step's logger.</param>
/// <docs>operations/infrastructure/storage-maintenance#online-rewrite</docs>
/// <docs>operations/infrastructure/storage-maintenance#fillfactor-decrease</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/OnlineTableRewriteStepTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FillFactorDecreaseRewriteTests.cs</tests>
public sealed partial class OnlineTableRewriteStep(
    IEnumerable<ITableRewriter> rewriters,
    IOptions<MaintenanceWorkerOptions> options,
    ICapabilityRegistry? capabilities,
    ILogger<OnlineTableRewriteStep> logger) : IMaintenanceStep {
  private const string BUDGET_KEY = "Whizbang:Workers:Maintenance:OnlineRewriteMaxBytesPerCycle";
  private const string REBUILD_LIMIT_KEY = "Whizbang:Workers:Maintenance:BlueGreenRewriteMaxPerCycle";
  private const string BLUE_GREEN_TOOL = "a blue-green rebuild";

  private readonly ITableRewriter[] _rewriters = [.. ArgumentGuard.NotNull(rewriters)];
  private readonly IOptions<MaintenanceWorkerOptions> _options = ArgumentGuard.NotNull(options);
  private readonly ICapabilityRegistry? _capabilities = capabilities;
  private readonly ILogger<OnlineTableRewriteStep> _logger = ArgumentGuard.NotNull(logger);

  /// <inheritdoc />
  public string Name => "online-table-rewrite";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var settings = _options.Value;
    switch (settings.TableRewriteStrategy) {
      case TableRewriteStrategy.Online:
        await _runOnlineAsync(services, settings, cancellationToken).ConfigureAwait(false);
        return;
      case TableRewriteStrategy.Auto:
        await _runAutoAsync(services, settings, cancellationToken).ConfigureAwait(false);
        return;
    }
    if (_onlineStrategy(settings) is not { } online) {
      return;
    }
    if (OnlineRewriteTools.CannotRunReason(online, _capabilities, _rewriters) is { } reason) {
      LogSkipped(_logger, reason);
      return;
    }
    await _runStrategyAsync(services, online, OnlineRewriteTools.RewriterFor(online, _rewriters)!, settings, cancellationToken)
      .ConfigureAwait(false);
  }

  /// <summary>
  /// <see cref="TableRewriteStrategy.Auto"/> (#2319): the first online tool that can run (pg_squeeze, else pg_repack in
  /// process), as under <see cref="TableRewriteStrategy.Online"/>, and nothing else. With neither, Auto keeps the
  /// boot-time <c>VACUUM FULL</c> or detection only, so the cycle does nothing and says nothing: no blue-green rebuild.
  /// </summary>
  private async Task _runAutoAsync(IServiceProvider services, MaintenanceWorkerOptions settings, CancellationToken cancellationToken) {
    var choice = OnlineRewriteTools.Choose(_capabilities, settings, _rewriters);
    if (choice.Tool is { } tool) {
      await _runStrategyAsync(services, tool, choice.Rewriter!, settings, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// <see cref="TableRewriteStrategy.Online"/> (#2301): pg_squeeze when it can run, else pg_repack in process when it
  /// can, else a blue-green rebuild of each queued perspective table; a table no rebuild can recreate waits.
  /// </summary>
  private async Task _runOnlineAsync(IServiceProvider services, MaintenanceWorkerOptions settings, CancellationToken cancellationToken) {
    var choice = OnlineRewriteTools.Choose(_capabilities, settings, _rewriters);
    if (choice.Tool is { } tool) {
      await _runStrategyAsync(services, tool, choice.Rewriter!, settings, cancellationToken).ConfigureAwait(false);
      return;
    }
    if (services.GetService<IWorkCoordinator>() is not { } coordinator) {
      return;
    }
    var cycle = await _cycleAsync(services, coordinator, cancellationToken).ConfigureAwait(false);
    if (cycle is null) {
      return;
    }
    var rebuilder = Array.Find(_rewriters, r => r is BlueGreenTableRewriter) as BlueGreenTableRewriter;
    await _rebuildQueuedAsync(rebuilder, cycle, settings, $"pg_squeeze: {choice.SqueezeWhy}; pg_repack: {choice.RepackWhy}", cancellationToken)
      .ConfigureAwait(false);
  }

  /// <summary>
  /// One cycle's view of the queue: the queued tables, the queue status, the metrics to report to and the clock to time
  /// each rewrite by, and the tables passed over for the byte budget.
  /// </summary>
  private sealed class Cycle(
      IWorkCoordinator coordinator, TableRewriteCandidate[] queued, TableRewriteQueueStatus status,
      TableStatisticsMetrics? metrics, TimeProvider time) {
    public IWorkCoordinator Coordinator { get; } = coordinator;
    public TableRewriteCandidate[] Queued { get; } = queued;
    public TableRewriteQueueStatus Status { get; } = status;
    public TableStatisticsMetrics? Metrics { get; } = metrics;
    public TimeProvider Time { get; } = time;
    public int OverBudget { get; set; }
  }

  /// <summary>
  /// Reads the queue and its status; null, having said why once at Information and reported the queue, when nothing is
  /// queued for a rewrite (#2319), so an operator can tell "nothing to do" from "never ran".
  /// </summary>
  private async Task<Cycle?> _cycleAsync(IServiceProvider services, IWorkCoordinator coordinator, CancellationToken cancellationToken) {
    var queued = await _queuedAsync(coordinator, cancellationToken).ConfigureAwait(false);
    var status = await coordinator.GetTableRewriteQueueStatusAsync(cancellationToken).ConfigureAwait(false);
    var cycle = new Cycle(coordinator, queued, status, services.GetService<TableStatisticsMetrics>(),
      services.GetService<TimeProvider>() ?? TimeProvider.System);
    if (queued.Length > 0) {
      return cycle;
    }
    LogNothingToRewrite(_logger, status.Requested, status.Threshold, status.HeldByFloor);
    _reportQueue(cycle);
    return null;
  }

  /// <summary>Ends a cycle: the summary line, and the queue gauge's reading.</summary>
  private void _endCycle(Cycle cycle, int rewritten) {
    LogCycle(_logger, rewritten, cycle.Queued.Length, cycle.Queued.Length - rewritten, cycle.Status.HeldByFloor);
    _reportQueue(cycle);
  }

  private static void _reportQueue(Cycle cycle) =>
    cycle.Metrics?.UpdateRewriteQueue(cycle.Status.Requested, cycle.Queued.Length, cycle.Status.HeldByFloor, cycle.OverBudget);

  /// <summary>One online tool over the queued tables, within the byte budget.</summary>
  private async Task _runStrategyAsync(
      IServiceProvider services, OnlineRewriteTools.Tool online, ITableRewriter rewriter, MaintenanceWorkerOptions settings,
      CancellationToken cancellationToken) {
    if (services.GetService<IWorkCoordinator>() is not { } coordinator
        || await _cycleAsync(services, coordinator, cancellationToken).ConfigureAwait(false) is not { } cycle) {
      return;
    }

    var approxReady = ApproxBloatMeasurement.IsReady(_capabilities);
    var budget = settings.OnlineRewriteMaxBytesPerCycle;
    var left = budget;
    var rewritten = 0;
    foreach (var candidate in cycle.Queued) {
      cancellationToken.ThrowIfCancellationRequested();
      if (!_fits(cycle, candidate, budget, ref left)) {
        continue;
      }
      if (await _rewriteAsync(rewriter, online.Name, cycle, candidate, approxReady, cancellationToken).ConfigureAwait(false)) {
        rewritten++;
      }
    }
    _endCycle(cycle, rewritten);
  }

  /// <summary>
  /// The blue-green rebuild over the queued tables (#2301): each one the registry says one perspective writes, largest
  /// bloat first, within the byte budget and at most <see cref="MaintenanceWorkerOptions.BlueGreenRewriteMaxPerCycle"/>
  /// per cycle; a table another rebuild holds is passed over without spending either. The rest wait, and the cycle
  /// says which and why.
  /// </summary>
  private async Task _rebuildQueuedAsync(
      BlueGreenTableRewriter? rebuilder, Cycle cycle, MaintenanceWorkerOptions settings, string extensions,
      CancellationToken cancellationToken) {
    var approxReady = ApproxBloatMeasurement.IsReady(_capabilities);
    var budget = settings.OnlineRewriteMaxBytesPerCycle;
    var left = budget;
    var limit = settings.BlueGreenRewriteMaxPerCycle;
    var started = 0;
    var heldBack = 0;
    var rewritten = 0;
    var waiting = new List<string>();
    foreach (var candidate in cycle.Queued) {
      cancellationToken.ThrowIfCancellationRequested();
      if (rebuilder is null
          || await rebuilder.PerspectiveOfAsync(candidate.TableName, cancellationToken).ConfigureAwait(false) is not { } perspective) {
        waiting.Add(candidate.TableName);
        continue;
      }
      if (await rebuilder.BusyReasonAsync(candidate.TableName, perspective, cancellationToken).ConfigureAwait(false) is { } busy) {
        LogRebuildInFlight(_logger, candidate.TableName, busy);
        continue;
      }
      if (started >= limit) {
        heldBack++;
        continue;
      }
      if (!_fits(cycle, candidate, budget, ref left)) {
        continue;
      }
      started++;
      if (await _rewriteAsync(rebuilder, BLUE_GREEN_TOOL, cycle, candidate, approxReady, cancellationToken).ConfigureAwait(false)) {
        rewritten++;
      }
    }
    if (waiting.Count > 0) {
      LogWaitingForAnExtension(_logger, string.Join(", ", waiting), extensions);
    }
    if (heldBack > 0) {
      LogRebuildLimit(_logger, heldBack, REBUILD_LIMIT_KEY, limit);
    }
    _endCycle(cycle, rewritten);
  }

  /// <summary>The queued tables, largest bloat ratio first, ties by name.</summary>
  private static async Task<TableRewriteCandidate[]> _queuedAsync(IWorkCoordinator coordinator, CancellationToken cancellationToken) {
    var candidates = await coordinator.GetTablesNeedingRewriteAsync(cancellationToken).ConfigureAwait(false);
    return [.. candidates
      .Where(c => c.Requested)
      .OrderByDescending(c => c.BloatRatio)
      .ThenBy(c => c.TableName, StringComparer.Ordinal)];
  }

  /// <summary>
  /// Whether the table fits what is left of the budget, spending it when it does; logs, and counts it over budget, when
  /// it does not.
  /// </summary>
  private bool _fits(Cycle cycle, TableRewriteCandidate candidate, long budget, ref long left) {
    if (candidate.SizeBytes > budget) {
      cycle.OverBudget++;
      LogLargerThanBudget(_logger, candidate.TableName, candidate.SizeBytes, BUDGET_KEY, budget);
      return false;
    }
    if (candidate.SizeBytes > left) {
      cycle.OverBudget++;
      LogDeferred(_logger, candidate.TableName, candidate.SizeBytes, left, BUDGET_KEY, budget);
      return false;
    }
    left -= candidate.SizeBytes;
    return true;
  }

  /// <summary>The online tool the options select explicitly, or null when the cycle rewrites nothing online.</summary>
  private static OnlineRewriteTools.Tool? _onlineStrategy(MaintenanceWorkerOptions settings) {
    if (settings.TableRewriteStrategy == TableRewriteStrategy.PgSqueeze) {
      return OnlineRewriteTools.PgSqueeze;
    }
    return PgRepackClient.RunsInProcess(settings) ? OnlineRewriteTools.PgRepack : null;
  }

  /// <summary>
  /// Rewrites one table, records the attempt on the rewrite meters (#2319), and returns true when it improved and its
  /// request was cleared.
  /// </summary>
  private async Task<bool> _rewriteAsync(
      ITableRewriter rewriter, string tool, Cycle cycle, TableRewriteCandidate candidate, bool approxReady,
      CancellationToken cancellationToken) {
    var started = cycle.Time.GetTimestamp();
    var before = cycle.Metrics is null
      ? null
      : await cycle.Coordinator.GetTableTotalBytesAsync(candidate.TableName, cancellationToken).ConfigureAwait(false);
    var outcome = await _attemptAsync(rewriter, tool, cycle.Coordinator, candidate, approxReady, cancellationToken).ConfigureAwait(false);
    if (cycle.Metrics is { } metrics) {
      var reclaimed = outcome == TableRewriteMetricOutcome.Rewritten
        ? TableStatisticsMetrics.Reclaimed(before,
            await cycle.Coordinator.GetTableTotalBytesAsync(candidate.TableName, cancellationToken).ConfigureAwait(false))
        : null;
      metrics.RecordRewrite(new TableRewriteAttempt(
        candidate.TableName, rewriter.Strategy, candidate.Reason, outcome, cycle.Time.GetElapsedTime(started), reclaimed));
    }
    return outcome == TableRewriteMetricOutcome.Rewritten;
  }

  /// <summary>
  /// Runs one rewrite and decides its outcome. While pgstattuple is Ready the re-measure is pgstattuple_approx, the
  /// measurement that queued the table, rather than the rewriter's catalog estimate.
  /// </summary>
  private async Task<TableRewriteMetricOutcome> _attemptAsync(
      ITableRewriter rewriter, string tool, IWorkCoordinator coordinator, TableRewriteCandidate candidate, bool approxReady,
      CancellationToken cancellationToken) {
    double? rewritten;
    try {
      rewritten = await rewriter.RewriteAsync(coordinator, candidate.TableName, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogFailed(_logger, candidate.TableName, tool, ex);
      return TableRewriteMetricOutcome.Failed;
    }
    var after = await ApproxBloatMeasurement.AfterRewriteAsync(
      coordinator, candidate.TableName, rewritten, approxReady, _logger, cancellationToken).ConfigureAwait(false);
    if (after is not { } measured) {
      LogNotRewritten(_logger, candidate.TableName);
      return TableRewriteMetricOutcome.Failed;
    }
    if (candidate.Reason == TableRewriteReason.FillFactorDecrease) {
      // A compact table rewritten at a lower fillfactor grows: the request asked for the free page space, and the
      // rewrite gave it, so it is honored and cleared whatever the re-measure says, and not rewritten again (#2300).
      await coordinator.CompleteTableRewriteAsync(_outcome(rewriter, candidate, measured.Ratio), cancellationToken).ConfigureAwait(false);
      LogFillFactorApplied(_logger, candidate.TableName, tool, measured.Ratio);
      return TableRewriteMetricOutcome.Rewritten;
    }
    var before = candidate.Method.ToValue();
    var afterMethod = measured.Method.ToValue();
    if (measured.Ratio >= candidate.BloatRatio) {
      // The rewrite ran but the table measures no better: the request stays for the next cycle rather than being
      // cleared on a success that did not happen.
      LogNotImproved(_logger, candidate.TableName, candidate.BloatRatio, before, measured.Ratio, afterMethod);
      return TableRewriteMetricOutcome.NoGain;
    }
    await coordinator.CompleteTableRewriteAsync(_outcome(rewriter, candidate, measured.Ratio), cancellationToken).ConfigureAwait(false);
    LogRewritten(_logger, candidate.TableName, tool, candidate.BloatRatio, before, measured.Ratio, afterMethod);
    return TableRewriteMetricOutcome.Rewritten;
  }

  private static TableRewriteOutcome _outcome(ITableRewriter rewriter, TableRewriteCandidate candidate, double after) =>
    new(candidate.TableName, rewriter.Strategy, candidate.Reason, candidate.BloatRatio, after);

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Online rewrite skipped: {Reason}; requested tables stay queued")]
  static partial void LogSkipped(ILogger logger, string reason);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "Online rewrite: {Table} ({Bytes} bytes) is larger than {Key} ({Budget}), so no cycle rewrites it until the budget is raised")]
  static partial void LogLargerThanBudget(ILogger logger, string table, long bytes, string key, long budget);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information,
    Message = "Online rewrite: {Table} ({Bytes} bytes) is deferred to a later cycle: it does not fit the {Left} bytes left of {Key} ({Budget})")]
  static partial void LogDeferred(ILogger logger, string table, long bytes, long left, string key, long budget);

  [LoggerMessage(EventId = 4, Level = LogLevel.Information,
    Message = "Online rewrite: {Table} rewritten through {Tool}, bloat {Before}x ({BeforeMethod}) -> {After}x ({AfterMethod})")]
  static partial void LogRewritten(
      ILogger logger, string table, string tool, double before, string beforeMethod, double after, string afterMethod);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
    Message = "Online rewrite: {Table} did not reduce bloat ({Before}x ({BeforeMethod}) -> {After}x ({AfterMethod})); the request stays queued")]
  static partial void LogNotImproved(ILogger logger, string table, double before, string beforeMethod, double after, string afterMethod);

  [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
    Message = "Online rewrite: {Table} was not rewritten (the coordinator logged why); the request stays queued")]
  static partial void LogNotRewritten(ILogger logger, string table);

  [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
    Message = "Online rewrite of {Table} through {Tool} failed; the request stays queued")]
  static partial void LogFailed(ILogger logger, string table, string tool, Exception ex);

  [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
    Message = "Online rewrite: {Tables} wait: no online rewrite extension is usable ({Extensions}), and each is not a perspective table one blue-green rebuild can recreate from its events; they stay queued until pg_squeeze or pg_repack can run")]
  static partial void LogWaitingForAnExtension(ILogger logger, string tables, string extensions);

  [LoggerMessage(EventId = 11, Level = LogLevel.Information,
    Message = "Online rewrite: {Table} is not rebuilt this cycle: {Reason}; the request stays queued")]
  static partial void LogRebuildInFlight(ILogger logger, string table, string reason);

  [LoggerMessage(EventId = 12, Level = LogLevel.Information,
    Message = "Online rewrite: {Count} more perspective table(s) wait for a later cycle: {Key} ({Limit}) rebuilds were started this cycle")]
  static partial void LogRebuildLimit(ILogger logger, int count, string key, int limit);

  [LoggerMessage(EventId = 9, Level = LogLevel.Information,
    Message = "Online rewrite: {Table} rewritten through {Tool} to apply its lowered fillfactor to the rows already stored (bloat now {After}x); the request is cleared")]
  static partial void LogFillFactorApplied(ILogger logger, string table, string tool, double after);

  [LoggerMessage(EventId = 8, Level = LogLevel.Information,
    Message = "Online rewrite: {Rewritten} of {Queued} queued table(s) rewritten this cycle; {Left} left queued; {HeldByFloor} held back by their post-rewrite floor")]
  static partial void LogCycle(ILogger logger, int rewritten, int queued, int left, int heldByFloor);

  [LoggerMessage(EventId = 13, Level = LogLevel.Information,
    Message = "Online rewrite: no table needs a rewrite: {Requested} requested, none at or above {Threshold}x; {HeldByFloor} held back by their post-rewrite floor")]
  static partial void LogNothingToRewrite(ILogger logger, int requested, double threshold, int heldByFloor);
}
