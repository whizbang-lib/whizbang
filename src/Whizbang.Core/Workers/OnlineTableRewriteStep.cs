// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Messaging;

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
/// before this step runs. They are taken largest bloat ratio first, ties by name. A table whose heap does not fit what is
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
/// <tests>tests/Whizbang.Core.Tests/Workers/OnlineTableRewriteStepTests.cs</tests>
public sealed partial class OnlineTableRewriteStep(
    IEnumerable<ITableRewriter> rewriters,
    IOptions<MaintenanceWorkerOptions> options,
    ICapabilityRegistry? capabilities,
    ILogger<OnlineTableRewriteStep> logger) : IMaintenanceStep {
  private const string BUDGET_KEY = "Whizbang:Workers:Maintenance:OnlineRewriteMaxBytesPerCycle";

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
    if (_onlineStrategy(settings) is not { } online) {
      return;
    }
    var rewriter = Array.Find(_rewriters, r => string.Equals(r.Strategy, online.RewriterStrategy, StringComparison.Ordinal));
    if ((_notReadyReason(online) ?? _noRewriterReason(online, rewriter)) is { } reason) {
      LogSkipped(_logger, reason);
      return;
    }
    if (services.GetService<IWorkCoordinator>() is not { } coordinator) {
      return;
    }

    var candidates = await coordinator.GetTablesNeedingRewriteAsync(cancellationToken).ConfigureAwait(false);
    var queued = candidates
      .Where(c => c.Requested)
      .OrderByDescending(c => c.BloatRatio)
      .ThenBy(c => c.TableName, StringComparer.Ordinal)
      .ToArray();
    if (queued.Length == 0) {
      return;
    }

    var budget = settings.OnlineRewriteMaxBytesPerCycle;
    var left = budget;
    var rewritten = 0;
    foreach (var candidate in queued) {
      cancellationToken.ThrowIfCancellationRequested();
      if (candidate.SizeBytes > budget) {
        LogLargerThanBudget(_logger, candidate.TableName, candidate.SizeBytes, BUDGET_KEY, budget);
        continue;
      }
      if (candidate.SizeBytes > left) {
        LogDeferred(_logger, candidate.TableName, candidate.SizeBytes, left, BUDGET_KEY, budget);
        continue;
      }
      left -= candidate.SizeBytes;
      if (await _rewriteAsync(rewriter!, online.Tool, coordinator, candidate, cancellationToken).ConfigureAwait(false)) {
        rewritten++;
      }
    }
    LogCycle(_logger, rewritten, queued.Length, queued.Length - rewritten);
  }

  /// <summary>The online strategy the options select, or null when the cycle rewrites nothing online.</summary>
  private static OnlineStrategy? _onlineStrategy(MaintenanceWorkerOptions settings) {
    if (settings.TableRewriteStrategy == TableRewriteStrategy.PgSqueeze) {
      return _pgSqueeze;
    }
    return PgRepackClient.RunsInProcess(settings) ? _pgRepack : null;
  }

  /// <summary>Rewrites one table; true when it improved and its request was cleared.</summary>
  private async Task<bool> _rewriteAsync(
      ITableRewriter rewriter, string tool, IWorkCoordinator coordinator, TableRewriteCandidate candidate, CancellationToken cancellationToken) {
    double? after;
    try {
      after = await rewriter.RewriteAsync(coordinator, candidate.TableName, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogFailed(_logger, candidate.TableName, tool, ex);
      return false;
    }
    if (after is null) {
      LogNotRewritten(_logger, candidate.TableName);
      return false;
    }
    if (after.Value >= candidate.BloatRatio) {
      // The rewrite ran but the table measures no better: the request stays for the next cycle rather than being
      // cleared on a success that did not happen.
      LogNotImproved(_logger, candidate.TableName, candidate.BloatRatio, after.Value);
      return false;
    }
    await coordinator.ClearTableRewriteRequestAsync(candidate.TableName, cancellationToken).ConfigureAwait(false);
    LogRewritten(_logger, candidate.TableName, tool, candidate.BloatRatio, after.Value);
    return true;
  }

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
    Message = "Online rewrite: {Table} rewritten through {Tool}, bloat {Before}x -> {After}x")]
  static partial void LogRewritten(ILogger logger, string table, string tool, double before, double after);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
    Message = "Online rewrite: {Table} did not reduce bloat ({Before}x -> {After}x); the request stays queued")]
  static partial void LogNotImproved(ILogger logger, string table, double before, double after);

  [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
    Message = "Online rewrite: {Table} was not rewritten (the coordinator logged why); the request stays queued")]
  static partial void LogNotRewritten(ILogger logger, string table);

  [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
    Message = "Online rewrite of {Table} through {Tool} failed; the request stays queued")]
  static partial void LogFailed(ILogger logger, string table, string tool, Exception ex);

  [LoggerMessage(EventId = 8, Level = LogLevel.Information,
    Message = "Online rewrite: {Rewritten} of {Queued} queued table(s) rewritten this cycle; {Left} left queued")]
  static partial void LogCycle(ILogger logger, int rewritten, int queued, int left);
}
