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
/// (<see cref="PgRepackMode.InProcess"/>): largest bloat ratio first, within
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
/// Prerequisites, in order: the strategy is one of the two online ones (otherwise the step does nothing, not even a
/// scan); its capabilities are <see cref="CapabilityState.Ready"/> wherever the capability registry probed them
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
/// raise.
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

  /// <summary>A capability the online strategy needs Ready, with how a log names it and where it is probed.</summary>
  private sealed record Prerequisite(CapabilityId Id, string Label, string ProbedOn);

  /// <summary>An online strategy: the rewriter it runs, the tool a log names, and the capabilities it needs Ready.</summary>
  private sealed record OnlineStrategy(string RewriterStrategy, string Tool, Prerequisite[] Prerequisites);

  private static readonly OnlineStrategy _pgSqueeze = new(TableRewriteStrategies.PG_SQUEEZE, "pg_squeeze", [
    new(new CapabilityId(RequiresPostgresExtensionAttribute.PROVIDER, "pg_squeeze"), "pg_squeeze", "any database"),
  ]);

  private static readonly OnlineStrategy _pgRepack = new(TableRewriteStrategies.PG_REPACK, "pg_repack", [
    new(new CapabilityId(RequiresPostgresExtensionAttribute.PROVIDER, "pg_repack"), "pg_repack", "any database"),
    new(PgRepackClient.Capability, "the pg_repack client (" + PgRepackClient.Capability + ")", "this host"),
  ]);

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
    if (settings.TableRewriteStrategy == TableRewriteStrategy.Online) {
      await _runOnlineAsync(services, settings, cancellationToken).ConfigureAwait(false);
      return;
    }
    if (_onlineStrategy(settings) is not { } online) {
      return;
    }
    var rewriter = _rewriterFor(online);
    if ((_notReadyReason(online) ?? _noRewriterReason(online, rewriter)) is { } reason) {
      LogSkipped(_logger, reason);
      return;
    }
    await _runStrategyAsync(services, online, rewriter!, settings, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// <see cref="TableRewriteStrategy.Online"/> (#2301): pg_squeeze when it can run, else pg_repack in process when it
  /// can, else a blue-green rebuild of each queued perspective table; a table no rebuild can recreate waits.
  /// </summary>
  private async Task _runOnlineAsync(IServiceProvider services, MaintenanceWorkerOptions settings, CancellationToken cancellationToken) {
    var squeezer = _rewriterFor(_pgSqueeze);
    var squeezeWhy = _notReadyReason(_pgSqueeze) ?? _noRewriterReason(_pgSqueeze, squeezer);
    if (squeezeWhy is null) {
      await _runStrategyAsync(services, _pgSqueeze, squeezer!, settings, cancellationToken).ConfigureAwait(false);
      return;
    }
    var repacker = _rewriterFor(_pgRepack);
    var repackWhy = settings.TableRewrite.PgRepack.Mode != PgRepackMode.InProcess
      ? "its client runs as an external job, which Whizbang does not start"
      : _notReadyReason(_pgRepack) ?? _noRewriterReason(_pgRepack, repacker);
    if (repackWhy is null) {
      await _runStrategyAsync(services, _pgRepack, repacker!, settings, cancellationToken).ConfigureAwait(false);
      return;
    }
    if (services.GetService<IWorkCoordinator>() is not { } coordinator) {
      return;
    }
    var queued = await _queuedAsync(coordinator, cancellationToken).ConfigureAwait(false);
    if (queued.Length == 0) {
      return;
    }
    var rebuilder = Array.Find(_rewriters, r => r is BlueGreenTableRewriter) as BlueGreenTableRewriter;
    await _rebuildQueuedAsync(rebuilder, coordinator, queued, settings, $"pg_squeeze: {squeezeWhy}; pg_repack: {repackWhy}", cancellationToken)
      .ConfigureAwait(false);
  }

  /// <summary>One online strategy over the queued tables, within the byte budget.</summary>
  private async Task _runStrategyAsync(
      IServiceProvider services, OnlineStrategy online, ITableRewriter rewriter, MaintenanceWorkerOptions settings,
      CancellationToken cancellationToken) {
    if (services.GetService<IWorkCoordinator>() is not { } coordinator) {
      return;
    }
    var queued = await _queuedAsync(coordinator, cancellationToken).ConfigureAwait(false);
    if (queued.Length == 0) {
      return;
    }

    var approxReady = ApproxBloatMeasurement.IsReady(_capabilities);
    var budget = settings.OnlineRewriteMaxBytesPerCycle;
    var left = budget;
    var rewritten = 0;
    foreach (var candidate in queued) {
      cancellationToken.ThrowIfCancellationRequested();
      if (!_fits(candidate, budget, ref left)) {
        continue;
      }
      if (await _rewriteAsync(rewriter, online.Tool, coordinator, candidate, approxReady, cancellationToken).ConfigureAwait(false)) {
        rewritten++;
      }
    }
    LogCycle(_logger, rewritten, queued.Length, queued.Length - rewritten);
  }

  /// <summary>
  /// The blue-green rebuild over the queued tables (#2301): each one the registry says one perspective writes, largest
  /// bloat first, within the byte budget and at most <see cref="MaintenanceWorkerOptions.BlueGreenRewriteMaxPerCycle"/>
  /// per cycle; a table another rebuild holds is passed over without spending either. The rest wait, and the cycle
  /// says which and why.
  /// </summary>
  private async Task _rebuildQueuedAsync(
      BlueGreenTableRewriter? rebuilder, IWorkCoordinator coordinator, TableRewriteCandidate[] queued,
      MaintenanceWorkerOptions settings, string extensions, CancellationToken cancellationToken) {
    var approxReady = ApproxBloatMeasurement.IsReady(_capabilities);
    var budget = settings.OnlineRewriteMaxBytesPerCycle;
    var left = budget;
    var limit = settings.BlueGreenRewriteMaxPerCycle;
    var started = 0;
    var heldBack = 0;
    var rewritten = 0;
    var waiting = new List<string>();
    foreach (var candidate in queued) {
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
      if (!_fits(candidate, budget, ref left)) {
        continue;
      }
      started++;
      if (await _rewriteAsync(rebuilder, BLUE_GREEN_TOOL, coordinator, candidate, approxReady, cancellationToken).ConfigureAwait(false)) {
        rewritten++;
      }
    }
    if (waiting.Count > 0) {
      LogWaitingForAnExtension(_logger, string.Join(", ", waiting), extensions);
    }
    if (heldBack > 0) {
      LogRebuildLimit(_logger, heldBack, REBUILD_LIMIT_KEY, limit);
    }
    LogCycle(_logger, rewritten, queued.Length, queued.Length - rewritten);
  }

  /// <summary>The queued tables, largest bloat ratio first, ties by name.</summary>
  private static async Task<TableRewriteCandidate[]> _queuedAsync(IWorkCoordinator coordinator, CancellationToken cancellationToken) {
    var candidates = await coordinator.GetTablesNeedingRewriteAsync(cancellationToken).ConfigureAwait(false);
    return [.. candidates
      .Where(c => c.Requested)
      .OrderByDescending(c => c.BloatRatio)
      .ThenBy(c => c.TableName, StringComparer.Ordinal)];
  }

  /// <summary>Whether the table's heap fits what is left of the budget, spending it when it does; logs when it does not.</summary>
  private bool _fits(TableRewriteCandidate candidate, long budget, ref long left) {
    if (candidate.SizeBytes > budget) {
      LogLargerThanBudget(_logger, candidate.TableName, candidate.SizeBytes, BUDGET_KEY, budget);
      return false;
    }
    if (candidate.SizeBytes > left) {
      LogDeferred(_logger, candidate.TableName, candidate.SizeBytes, left, BUDGET_KEY, budget);
      return false;
    }
    left -= candidate.SizeBytes;
    return true;
  }

  private ITableRewriter? _rewriterFor(OnlineStrategy online) =>
    Array.Find(_rewriters, r => string.Equals(r.Strategy, online.RewriterStrategy, StringComparison.Ordinal));

  /// <summary>The online strategy the options select, or null when the cycle rewrites nothing online.</summary>
  private static OnlineStrategy? _onlineStrategy(MaintenanceWorkerOptions settings) {
    if (settings.TableRewriteStrategy == TableRewriteStrategy.PgSqueeze) {
      return _pgSqueeze;
    }
    return PgRepackClient.RunsInProcess(settings) ? _pgRepack : null;
  }

  /// <summary>
  /// Rewrites one table; true when it improved and its request was cleared. While pgstattuple is Ready the re-measure
  /// is pgstattuple_approx, the measurement that queued the table, rather than the rewriter's catalog estimate.
  /// </summary>
  private async Task<bool> _rewriteAsync(
      ITableRewriter rewriter, string tool, IWorkCoordinator coordinator, TableRewriteCandidate candidate, bool approxReady,
      CancellationToken cancellationToken) {
    double? rewritten;
    try {
      rewritten = await rewriter.RewriteAsync(coordinator, candidate.TableName, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogFailed(_logger, candidate.TableName, tool, ex);
      return false;
    }
    var after = await ApproxBloatMeasurement.AfterRewriteAsync(
      coordinator, candidate.TableName, rewritten, approxReady, _logger, cancellationToken).ConfigureAwait(false);
    if (after is not { } measured) {
      LogNotRewritten(_logger, candidate.TableName);
      return false;
    }
    if (candidate.Reason == TableRewriteReason.FillFactorDecrease) {
      // A compact table rewritten at a lower fillfactor grows: the request asked for the free page space, and the
      // rewrite gave it, so it is honored and cleared whatever the re-measure says, and not rewritten again (#2300).
      await coordinator.CompleteTableRewriteAsync(_outcome(rewriter, candidate, measured.Ratio), cancellationToken).ConfigureAwait(false);
      LogFillFactorApplied(_logger, candidate.TableName, tool, measured.Ratio);
      return true;
    }
    var before = candidate.Method.ToValue();
    var afterMethod = measured.Method.ToValue();
    if (measured.Ratio >= candidate.BloatRatio) {
      // The rewrite ran but the table measures no better: the request stays for the next cycle rather than being
      // cleared on a success that did not happen.
      LogNotImproved(_logger, candidate.TableName, candidate.BloatRatio, before, measured.Ratio, afterMethod);
      return false;
    }
    await coordinator.CompleteTableRewriteAsync(_outcome(rewriter, candidate, measured.Ratio), cancellationToken).ConfigureAwait(false);
    LogRewritten(_logger, candidate.TableName, tool, candidate.BloatRatio, before, measured.Ratio, afterMethod);
    return true;
  }

  private static TableRewriteOutcome _outcome(ITableRewriter rewriter, TableRewriteCandidate candidate, double after) =>
    new(candidate.TableName, rewriter.Strategy, candidate.Reason, candidate.BloatRatio, after);

  /// <summary>Why a capability the strategy needs is not Ready everywhere the registry probed it, or null when all are.</summary>
  private string? _notReadyReason(OnlineStrategy online) {
    if (_capabilities is null) {
      return $"no capability registry is registered, so whether {online.Tool} is Ready is not known";
    }
    foreach (var prerequisite in online.Prerequisites) {
      var statuses = _capabilities.Statuses.Where(s => s.Descriptor.Id == prerequisite.Id).ToArray();
      if (statuses.Length == 0) {
        return $"{prerequisite.Label} has not been probed on {prerequisite.ProbedOn}";
      }
      if (Array.Find(statuses, s => s.State != CapabilityState.Ready) is { } notReady) {
        return $"{prerequisite.Label} is {notReady.State} on '{notReady.Scope}', not Ready";
      }
    }
    return null;
  }

  private static string? _noRewriterReason(OnlineStrategy online, ITableRewriter? rewriter) =>
    rewriter is null ? $"no table rewriter is registered for {online.RewriterStrategy}" : null;

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
    Message = "Online rewrite: {Rewritten} of {Queued} queued table(s) rewritten this cycle; {Left} left queued")]
  static partial void LogCycle(ILogger logger, int rewritten, int queued, int left);
}
