// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Workers;

/// <summary>
/// The maintenance step that rebuilds a bloated index on a queue table that churns to empty with
/// <c>REINDEX INDEX CONCURRENTLY</c>, when it is more than <see cref="MaintenanceWorkerOptions.ReindexBloatThreshold"/>
/// times its expected size (#1746).
/// </summary>
/// <remarks>
/// <para>
/// A queue table (the outbox, the inbox and its work state, perspective events, active streams, the doorbell queue) is
/// filled by a burst and drained back to empty. Autovacuum keeps its heap in check, but a B-tree never gives its pages
/// back to the file: after a burst an index on an empty table can stay the size it was at the peak, and every scan of it
/// walks those empty pages. Only a rebuild returns them.
/// </para>
/// <para>
/// Each run, in order: nothing at all when the threshold is <c>0</c>; then the window's claim, so the fleet runs it once
/// per window (half the maintenance interval) however many instances run maintenance; then the survey. While any index
/// build is running on a queue table the run stops there, because an invalid index may be that build's work in progress.
/// Otherwise every invalid leftover of an interrupted rebuild (an invalid index named <c>..._ccnew</c> or
/// <c>..._ccold</c>) is dropped, and then at most one index is rebuilt: of the valid indexes over the threshold and at
/// least <see cref="MIN_REINDEX_BYTES"/>, the one with the most bytes to give back.
/// </para>
/// <para>
/// The ratio is the catalog estimate (<see cref="BloatMeasurementMethod.Estimate"/>). <c>pgstatindex</c> would measure
/// it, but it reads every page of the index, the same order of reads as the rebuild it would decide, and the library
/// never runs a whole-relation measurement on its own.
/// </para>
/// </remarks>
/// <param name="options">The maintenance options: the threshold and the interval the claim window is derived from.</param>
/// <param name="logger">The step's logger.</param>
/// <param name="timeProvider">The clock the claim window is read from.</param>
/// <docs>operations/infrastructure/storage-maintenance#queue-index-rebuild</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Workers/QueueIndexReindexStepTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueueIndexReindexSqlTests.cs</tests>
public sealed partial class QueueIndexReindexStep(
    IOptions<MaintenanceWorkerOptions> options,
    ILogger<QueueIndexReindexStep> logger,
    TimeProvider? timeProvider = null) : IMaintenanceStep {
  /// <summary>The default threshold: the same line a table is queued for rewrite at (<c>table_rewrite_bloat_threshold</c>).</summary>
  internal const double DEFAULT_THRESHOLD = 3.0;

  /// <summary>The smallest index the step rebuilds, 1 MiB: below it the estimate is a few pages of noise and the gain nothing.</summary>
  internal const long MIN_REINDEX_BYTES = 1L << 20;

  /// <summary>The configuration key of the threshold, as logs and validation name it.</summary>
  internal const string THRESHOLD_KEY = "Whizbang:Workers:Maintenance:ReindexBloatThreshold";

  /// <summary>The configuration key of the lock timeout, as logs and validation name it (#2611).</summary>
  internal const string LOCK_TIMEOUT_KEY = "Whizbang:Workers:Maintenance:ReindexLockTimeout";

  private const string CLAIM_PREFIX = "whizbang:queue-reindex:";

  private readonly IOptions<MaintenanceWorkerOptions> _options = ArgumentGuard.NotNull(options);
  private readonly ILogger<QueueIndexReindexStep> _logger = ArgumentGuard.NotNull(logger);
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  /// <inheritdoc />
  public string Name => "queue-index-reindex";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var settings = _options.Value;
    var threshold = settings.ReindexBloatThreshold;
    if (threshold <= 0 || services.GetService<IWorkCoordinator>() is not { } coordinator) {
      return;
    }
    if (services.GetService<IClaimedEmissionStore>() is not { } claims) {
      LogNoClaimStore(_logger);
      return;
    }
    if (!await claims.TryClaimAsync(_claimKey(settings), (Guid)TrackedGuid.New(), cancellationToken).ConfigureAwait(false)) {
      return;
    }

    var survey = await coordinator.GetQueueIndexBloatAsync(cancellationToken).ConfigureAwait(false);
    if (survey.FirstOrDefault(s => s.BuildInProgress) is { BuildInProgress: true } building) {
      LogBuildInProgress(_logger, building.TableName);
      return;
    }
    var lockTimeout = settings.ReindexLockTimeout;
    foreach (var leftover in survey.Where(s => s.IsReindexLeftover)) {
      await _dropAsync(coordinator, leftover, lockTimeout, cancellationToken).ConfigureAwait(false);
    }
    if (Choose(survey, threshold) is not { } chosen) {
      LogNothingOver(_logger, THRESHOLD_KEY, threshold, MIN_REINDEX_BYTES);
      return;
    }
    if (!await _rebuildAsync(coordinator, chosen, threshold, lockTimeout, services, cancellationToken).ConfigureAwait(false)) {
      // #2611: a rebuild that failed or gave up leaves its copy invalid on the table, written on every insert; drop it now.
      var after = await coordinator.GetQueueIndexBloatAsync(cancellationToken).ConfigureAwait(false);
      foreach (var leftover in after.Where(s => s.IsReindexLeftover && s.TableName == chosen.TableName && !s.BuildInProgress)) {
        await _dropAsync(coordinator, leftover, lockTimeout, cancellationToken).ConfigureAwait(false);
      }
    }
  }

  /// <summary>
  /// The index to rebuild: of the valid indexes with a known ratio over <paramref name="threshold"/> and at least
  /// <see cref="MIN_REINDEX_BYTES"/>, the one with the most bytes to give back, ties by name; null when there is none.
  /// </summary>
  internal static QueueIndexBloat? Choose(IReadOnlyList<QueueIndexBloat> survey, double threshold) =>
    survey
      .Where(index => index.Valid && index.SizeBytes >= MIN_REINDEX_BYTES && index.Ratio > threshold)
      .OrderByDescending(_reclaimable)
      .ThenBy(index => index.IndexName, StringComparer.Ordinal)
      .Select(index => (QueueIndexBloat?)index)
      .FirstOrDefault();

  /// <summary>
  /// Why <paramref name="lockTimeout"/> is not a usable <see cref="MaintenanceWorkerOptions.ReindexLockTimeout"/>, or
  /// nothing: it must be positive, and fit the milliseconds <c>lock_timeout</c> takes (#2611).
  /// </summary>
  internal static List<string> ValidateLockTimeout(TimeSpan lockTimeout) =>
    lockTimeout > TimeSpan.Zero && lockTimeout.TotalMilliseconds <= int.MaxValue
      ? []
      : [$"{LOCK_TIMEOUT_KEY} must be positive and at most {int.MaxValue} milliseconds (was {lockTimeout:c}); zero would let the "
         + "rebuild wait on an old transaction for as long as that transaction stays open."];

  /// <summary>
  /// The sessions a rebuild that gave up was waiting on, as the warning names them: each pid with how long its transaction
  /// has been open, and how many sessions this role could not see.
  /// </summary>
  internal static string DescribeBlockers(QueueIndexRebuild outcome) {
    if (outcome.Blockers.Count == 0 && outcome.HiddenSessions == 0) {
      return "The sessions it waited on had ended by the time it looked.";
    }
    var named = outcome.Blockers.Count == 0
      ? "No session it could see was in its way"
      : "It was waiting on " + string.Join(", ", outcome.Blockers.Select(_describe));
    return outcome.HiddenSessions == 0
      ? named + "."
      : string.Create(CultureInfo.InvariantCulture,
          $"{named}; {outcome.HiddenSessions} other session(s) in the database are not visible to this role (grant pg_monitor to see them).");
  }

  private static string _describe(QueueIndexBlocker blocker) =>
    blocker.TransactionAge is { } age
      ? string.Create(CultureInfo.InvariantCulture,
          $"pid {blocker.Pid} (transaction open {new TimeSpan(age.Ticks - (age.Ticks % TimeSpan.TicksPerSecond)):c})")
      : string.Create(CultureInfo.InvariantCulture, $"pid {blocker.Pid} (transaction age not visible to this role)");

  /// <summary>
  /// Why <paramref name="threshold"/> is not a usable <see cref="MaintenanceWorkerOptions.ReindexBloatThreshold"/>, or
  /// nothing: it must be <c>0</c> or a finite value greater than 1.
  /// </summary>
  internal static List<string> ValidateThreshold(double threshold) =>
    threshold is 0d || (double.IsFinite(threshold) && threshold > 1)
      ? []
      : [$"{THRESHOLD_KEY} must be 0 to turn the rebuild off, or a finite value greater than 1 (was "
         + threshold.ToString(CultureInfo.InvariantCulture)
         + "); a freshly built index measures about 1, so a lower line would rebuild it every cycle."];

  /// <summary>
  /// How long one instance's claim to run the step lasts: half the maintenance interval (at least one minute's worth), as
  /// the integrity audit's claim is, so instances whose cycles fall together share one and an instance's next cycle is
  /// never blocked by its own last claim.
  /// </summary>
  internal static TimeSpan ClaimWindow(MaintenanceWorkerOptions settings) =>
    TimeSpan.FromMinutes(Math.Max(1, settings.IntervalMinutes) / 2.0);

  private string _claimKey(MaintenanceWorkerOptions settings) {
    var now = _timeProvider.GetUtcNow().UtcTicks;
    var window = ClaimWindow(settings).Ticks;
    return string.Create(CultureInfo.InvariantCulture, $"{CLAIM_PREFIX}{now - (now % window)}");
  }

  private static long _reclaimable(QueueIndexBloat index) => index.SizeBytes - index.ExpectedBytes.GetValueOrDefault();

  private async Task _dropAsync(
      IWorkCoordinator coordinator, QueueIndexBloat leftover, TimeSpan lockTimeout, CancellationToken cancellationToken) {
    try {
      if (await coordinator.DropInvalidQueueIndexAsync(leftover.IndexName, lockTimeout, cancellationToken).ConfigureAwait(false)) {
        LogDropped(_logger, leftover.IndexName, leftover.TableName);
      } else {
        LogNotDropped(_logger, leftover.IndexName, leftover.TableName);
      }
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogDropFailed(_logger, leftover.IndexName, leftover.TableName, ex);
    }
  }

  /// <summary>Rebuilds the chosen index; false when the rebuild failed or gave up, so it may have left an invalid copy.</summary>
  private async Task<bool> _rebuildAsync(
      IWorkCoordinator coordinator, QueueIndexBloat chosen, double threshold, TimeSpan lockTimeout,
      IServiceProvider services, CancellationToken cancellationToken) {
    var ratio = Math.Round(chosen.Ratio.GetValueOrDefault(), 2);
    var expected = chosen.ExpectedBytes.GetValueOrDefault();
    var method = chosen.Method.ToValue();
    LogChosen(_logger, chosen.IndexName, chosen.TableName, ratio, expected, method, threshold);
    QueueIndexRebuild outcome;
    try {
      outcome = await coordinator.RebuildQueueIndexAsync(chosen.IndexName, lockTimeout, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogRebuildFailed(_logger, chosen.IndexName, chosen.TableName, ex);
      return false;
    }
    if (outcome.GaveUp) {
      services.GetService<MaintenanceMetrics>()?.QueueIndexRebuildsGaveUp.Add(1,
        new KeyValuePair<string, object?>(MaintenanceMetrics.INDEX_TAG, chosen.IndexName),
        new KeyValuePair<string, object?>(MaintenanceMetrics.TABLE_TAG, chosen.TableName));
      LogGaveUp(_logger, chosen.IndexName, chosen.TableName, LOCK_TIMEOUT_KEY, lockTimeout, DescribeBlockers(outcome));
      return false;
    }
    if (outcome.SizeAfterBytes is not { } afterBytes) {
      LogNotRebuilt(_logger, chosen.IndexName, chosen.TableName);
      return true;
    }
    LogRebuilt(_logger, chosen.IndexName, chosen.TableName, chosen.SizeBytes, afterBytes);
    return true;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
    Message = "Queue index rebuild skipped: no claim store is registered to run it on one instance")]
  static partial void LogNoClaimStore(ILogger logger);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information,
    Message = "Queue index rebuild skipped this cycle: an index build is running on {Table}, so an invalid index there may be its work in progress")]
  static partial void LogBuildInProgress(ILogger logger, string table);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information,
    Message = "Queue index rebuild: dropped {Index} on {Table}, an invalid leftover of an interrupted REINDEX CONCURRENTLY")]
  static partial void LogDropped(ILogger logger, string index, string table);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
    Message = "Queue index rebuild: the invalid leftover {Index} on {Table} was not dropped (the coordinator logged why)")]
  static partial void LogNotDropped(ILogger logger, string index, string table);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
    Message = "Queue index rebuild: dropping the invalid leftover {Index} on {Table} failed")]
  static partial void LogDropFailed(ILogger logger, string index, string table, Exception exception);

  [LoggerMessage(EventId = 6, Level = LogLevel.Information,
    Message = "Queue index rebuild: {Index} on {Table} rebuilt concurrently, {Before} -> {After} bytes")]
  static partial void LogRebuilt(ILogger logger, string index, string table, long before, long after);

  [LoggerMessage(EventId = 10, Level = LogLevel.Information,
    Message = "Queue index rebuild: rebuilding {Index} on {Table}; it measured {Ratio}x its expected {Expected} bytes ({Method}), over "
            + THRESHOLD_KEY + " ({Threshold})")]
  static partial void LogChosen(
      ILogger logger, string index, string table, double ratio, long expected, string method, double threshold);

  [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
    Message = "Queue index rebuild: {Index} on {Table} was not rebuilt (the coordinator logged why)")]
  static partial void LogNotRebuilt(ILogger logger, string index, string table);

  [LoggerMessage(EventId = 8, Level = LogLevel.Warning,
    Message = "Queue index rebuild of {Index} on {Table} failed; its invalid copy, if one was left, is dropped now")]
  static partial void LogRebuildFailed(ILogger logger, string index, string table, Exception exception);

  [LoggerMessage(EventId = 11, Level = LogLevel.Warning,
    Message = "Queue index rebuild of {Index} on {Table} gave up: a lock wait passed {Key} ({LockTimeout}). {Blockers} It is tried again on a later cycle")]
  static partial void LogGaveUp(ILogger logger, string index, string table, string key, TimeSpan lockTimeout, string blockers);

  [LoggerMessage(EventId = 9, Level = LogLevel.Debug,
    Message = "Queue index rebuild: no index on a queue table is over {Key} ({Threshold}) and at least {Floor} bytes")]
  static partial void LogNothingOver(ILogger logger, string key, double threshold, long floor);
}
