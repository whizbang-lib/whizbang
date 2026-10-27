// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// The maintenance step that rewrites queued tables online through pg_squeeze when
/// <see cref="MaintenanceWorkerOptions.TableRewriteStrategy"/> is <see cref="TableRewriteStrategy.PgSqueeze"/>: largest
/// bloat ratio first, within <see cref="MaintenanceWorkerOptions.OnlineRewriteMaxBytesPerCycle"/> per cycle, and a
/// request cleared only when the re-measure shows the rewrite improved the table.
/// </summary>
/// <remarks>
/// <para>
/// It runs inside the maintenance cycle, so where the cycle runs: on the maintainer duty's holder, and only when the
/// housekeeping gate admits the cycle. The boot-time <c>Rewrite</c> step leaves <see cref="TableRewriteStrategy.PgSqueeze"/>
/// to it, so an online rewrite is always bounded by the budget.
/// </para>
/// <para>
/// Prerequisites, in order: the strategy is <see cref="TableRewriteStrategy.PgSqueeze"/> (otherwise the step does nothing,
/// not even a scan); pg_squeeze is <see cref="CapabilityState.Ready"/> on every database the capability registry probed;
/// a rewriter is registered under <see cref="TableRewriteStrategies.PG_SQUEEZE"/>. Anything unmet is logged and leaves
/// the queue as it is. Each table's own prerequisites (logical WAL, a free replication slot, an identity index, the
/// role's rights) are checked by the coordinator before it calls pg_squeeze.
/// </para>
/// <para>
/// Only tables in the <c>pending_table_rewrites</c> queue are rewritten; the cycle's bloat detection puts a table there
/// before this step runs. They are taken largest bloat ratio first, ties by name. A table whose heap does not fit what is
/// left of the budget is deferred to a later cycle and the next smaller one that fits is taken, so one large table does
/// not hold the rest back; a table larger than the whole budget is never rewritten, and the log names the setting to
/// raise.
/// </para>
/// </remarks>
/// <param name="rewriters">The registered rewriters; the one under <see cref="TableRewriteStrategies.PG_SQUEEZE"/> runs.</param>
/// <param name="options">The maintenance options: the strategy and the byte budget.</param>
/// <param name="capabilities">The capability registry, which says whether pg_squeeze is Ready; null when the host registers none.</param>
/// <param name="logger">The step's logger.</param>
/// <docs>operations/infrastructure/storage-maintenance#online-rewrite</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/OnlineTableRewriteStepTests.cs</tests>
public sealed partial class OnlineTableRewriteStep(
    IEnumerable<ITableRewriter> rewriters,
    IOptions<MaintenanceWorkerOptions> options,
    ICapabilityRegistry? capabilities,
    ILogger<OnlineTableRewriteStep> logger) : IMaintenanceStep {
  private const string BUDGET_KEY = "Whizbang:Workers:Maintenance:OnlineRewriteMaxBytesPerCycle";
  private const string PG_SQUEEZE_EXTENSION = "pg_squeeze";

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
    if (settings.TableRewriteStrategy != TableRewriteStrategy.PgSqueeze) {
      return;
    }
    var rewriter = Array.Find(_rewriters, r => string.Equals(r.Strategy, TableRewriteStrategies.PG_SQUEEZE, StringComparison.Ordinal));
    if ((_notReadyReason() ?? _noRewriterReason(rewriter)) is { } reason) {
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
      if (await _rewriteAsync(rewriter!, coordinator, candidate, cancellationToken).ConfigureAwait(false)) {
        rewritten++;
      }
    }
    LogCycle(_logger, rewritten, queued.Length, queued.Length - rewritten);
  }

  /// <summary>Rewrites one table; true when it improved and its request was cleared.</summary>
  private async Task<bool> _rewriteAsync(
      ITableRewriter rewriter, IWorkCoordinator coordinator, TableRewriteCandidate candidate, CancellationToken cancellationToken) {
    double? after;
    try {
      after = await rewriter.RewriteAsync(coordinator, candidate.TableName, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogFailed(_logger, candidate.TableName, ex);
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
    LogRewritten(_logger, candidate.TableName, candidate.BloatRatio, after.Value);
    return true;
  }

  /// <summary>Why pg_squeeze is not Ready everywhere the registry probed it, or null when it is.</summary>
  private string? _notReadyReason() {
    if (_capabilities is null) {
      return "no capability registry is registered, so whether pg_squeeze is Ready is not known";
    }
    var squeeze = new CapabilityId(RequiresPostgresExtensionAttribute.PROVIDER, PG_SQUEEZE_EXTENSION);
    var statuses = _capabilities.Statuses.Where(s => s.Descriptor.Id == squeeze).ToArray();
    if (statuses.Length == 0) {
      return "pg_squeeze has not been probed on any database";
    }
    var notReady = Array.Find(statuses, s => s.State != CapabilityState.Ready);
    return notReady is null ? null : $"pg_squeeze is {notReady.State} on '{notReady.Scope}', not Ready";
  }

  private static string? _noRewriterReason(ITableRewriter? rewriter) =>
    rewriter is null ? $"no table rewriter is registered for {TableRewriteStrategies.PG_SQUEEZE}" : null;

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Online rewrite skipped: {Reason}; requested tables stay queued")]
  static partial void LogSkipped(ILogger logger, string reason);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "Online rewrite: {Table} ({Bytes} bytes) is larger than {Key} ({Budget}), so no cycle rewrites it until the budget is raised")]
  static partial void LogLargerThanBudget(ILogger logger, string table, long bytes, string key, long budget);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information,
    Message = "Online rewrite: {Table} ({Bytes} bytes) is deferred to a later cycle: it does not fit the {Left} bytes left of {Key} ({Budget})")]
  static partial void LogDeferred(ILogger logger, string table, long bytes, long left, string key, long budget);

  [LoggerMessage(EventId = 4, Level = LogLevel.Information,
    Message = "Online rewrite: {Table} rewritten through pg_squeeze, bloat {Before}x -> {After}x")]
  static partial void LogRewritten(ILogger logger, string table, double before, double after);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
    Message = "Online rewrite: {Table} did not reduce bloat ({Before}x -> {After}x); the request stays queued")]
  static partial void LogNotImproved(ILogger logger, string table, double before, double after);

  [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
    Message = "Online rewrite: {Table} was not rewritten (the coordinator logged why); the request stays queued")]
  static partial void LogNotRewritten(ILogger logger, string table);

  [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
    Message = "Online rewrite of {Table} through pg_squeeze failed; the request stays queued")]
  static partial void LogFailed(ILogger logger, string table, Exception ex);

  [LoggerMessage(EventId = 8, Level = LogLevel.Information,
    Message = "Online rewrite: {Rewritten} of {Queued} queued table(s) rewritten this cycle; {Left} left queued")]
  static partial void LogCycle(ILogger logger, int rewritten, int queued, int left);
}
