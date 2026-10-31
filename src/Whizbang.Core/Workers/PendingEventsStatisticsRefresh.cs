// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// Refreshes the <c>whizbang.perspective.pending_events</c> gauge from the pending-events statistics query, and reports
/// a refresh whose query failed instead of swallowing it (#1865).
/// </summary>
/// <remarks>
/// <para>
/// The perspective worker runs this about once a minute, off the hot path. A failure never stops the worker: the
/// gauge is non-critical. But the gauge then keeps its last value, and a frozen number reads as real lag or real
/// health, so the failure is made visible three ways. The first failure of a streak is logged at warning with the
/// exception, naming the query and the stale gauge; later failures in the same streak are logged at debug, so an
/// outage does not repeat the warning every minute; and every failure is counted on
/// <c>whizbang.perspective.pending_events_refresh_failures</c>. The first refresh that succeeds after a streak is
/// logged at information with how many refreshes the gauge missed.
/// </para>
/// <para>
/// Cancellation is not a failure and propagates to the worker loop, which handles shutdown.
/// </para>
/// </remarks>
/// <docs>operations/observability/metrics#perspectives</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PendingEventsStatisticsRefreshTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/PerspectiveWorkerStartupAndMaintenanceTests.cs:Maintenance_StatisticsGatherFails_WarnsNamingTheQuery_AndCountsTheFailureAsync</tests>
/// <param name="queryPendingEvents">Runs the pending-events statistics query and returns the pending count.</param>
/// <param name="metrics">The perspective metrics whose gauge and failure counter this updates, if metrics are on.</param>
/// <param name="logger">Where a failed refresh, and the recovery after one, is logged.</param>
internal sealed partial class PendingEventsStatisticsRefresh(
    Func<CancellationToken, Task<long>> queryPendingEvents, PerspectiveMetrics? metrics, ILogger logger) {
#pragma warning disable S4487 // Used by generated [LoggerMessage] partial methods
  private readonly ILogger _logger = logger;
#pragma warning restore S4487
  private int _failureStreak;

  /// <summary>Runs the query once and updates the gauge, or reports why it could not.</summary>
  /// <param name="cancellationToken">Cancels the query.</param>
  /// <tests>tests/Whizbang.Core.Tests/Workers/PendingEventsStatisticsRefreshTests.cs</tests>
  public async Task RefreshAsync(CancellationToken cancellationToken) {
    long pending;
    try {
      pending = await queryPendingEvents(cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      _recordFailure(ex);
      return;
    }

    metrics?.SetPendingEvents(pending);
    if (_failureStreak > 0) {
      LogRefreshRecovered(_failureStreak);
      _failureStreak = 0;
    }
  }

  private void _recordFailure(Exception ex) {
    _failureStreak++;
    metrics?.PendingEventsRefreshFailures.Add(1);
    if (_failureStreak == 1) {
      LogRefreshFailed(ex);
    } else {
      LogRefreshStillFailing(ex, _failureStreak);
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "The pending-events statistics query (IWorkCoordinator.GatherStatisticsAsync) failed, so the whizbang.perspective.pending_events gauge keeps its last value until a refresh succeeds. The worker keeps running; further failures in a row are logged at Debug and counted on whizbang.perspective.pending_events_refresh_failures")]
  private partial void LogRefreshFailed(Exception exception);

  [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
    Message = "The pending-events statistics query (IWorkCoordinator.GatherStatisticsAsync) failed again: {Failures} failed refreshes in a row; the whizbang.perspective.pending_events gauge is still stale")]
  private partial void LogRefreshStillFailing(Exception exception, int failures);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information,
    Message = "The pending-events statistics query (IWorkCoordinator.GatherStatisticsAsync) succeeded after {Failures} failed refreshes; the whizbang.perspective.pending_events gauge is current again")]
  private partial void LogRefreshRecovered(int failures);
}
