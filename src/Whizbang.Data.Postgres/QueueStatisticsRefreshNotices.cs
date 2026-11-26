// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The warning a claim's statistics refresh raises when it could not analyze a stale queue table (#2611), and how a
/// coordinator surfaces it: counted by table and reason, and logged. The refresh raises it once per back-off (only the
/// claimant holding the table's turn writes the back-off), so each one is a stretch of claims planned on stale
/// statistics, never a claim that waited. Both PostgreSQL drivers' coordinators read it through here.
/// </summary>
/// <docs>fundamentals/work-coordinator/claim-loop#the-statistics-refresh-never-waits</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:ClaimWork_WhileTheRefreshCannotAnalyze_NeverWaits_CountsTheSkip_AndWarnsOncePerBackoffAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueueStatisticsRefreshNoticesTests.cs</tests>
public static partial class QueueStatisticsRefreshNotices {
  /// <summary>The message of the warning; its detail is <c>table=reason</c> and its hint says what happened.</summary>
  public const string SKIPPED = "whizbang.queue_statistics_refresh_skipped";

  /// <summary>
  /// Collects the refresh-skipped warnings a claim raises on <paramref name="connection"/> for as long as the returned
  /// scope lives, and on dispose counts each on <see cref="WorkCoordinatorMetrics.QueueStatisticsRefreshSkips"/> and logs it
  /// as a warning.
  /// </summary>
  /// <param name="connection">The connection the claim runs on.</param>
  /// <param name="metrics">The coordinator's metrics, when registered.</param>
  /// <param name="logger">The coordinator's logger, when it has one.</param>
  /// <returns>The scope; dispose it once the claim has run.</returns>
  public static SkipScope Watch(NpgsqlConnection connection, WorkCoordinatorMetrics? metrics, ILogger? logger) {
    ArgumentNullException.ThrowIfNull(connection);
    return new SkipScope(connection, metrics, logger);
  }

  /// <summary>Whether a notice is the refresh-skipped warning; its table and reason, when it is.</summary>
  /// <param name="messageText">The notice's message.</param>
  /// <param name="detail">The notice's detail, <c>table=reason</c>.</param>
  /// <param name="table">The queue table that was not refreshed.</param>
  /// <param name="reason">Why: <c>lock_not_available</c>, <c>lock_timeout</c> or <c>failed</c>.</param>
  /// <returns>True for a well-formed refresh-skipped notice.</returns>
  public static bool TryRead(string? messageText, string? detail, out string table, out string reason) {
    table = "";
    reason = "";
    if (!string.Equals(messageText, SKIPPED, StringComparison.Ordinal) || detail is null) {
      return false;
    }
    var split = detail.IndexOf('=', StringComparison.Ordinal);
    if (split <= 0 || split == detail.Length - 1) {
      return false;
    }
    table = detail[..split];
    reason = detail[(split + 1)..];
    return true;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "The planner statistics of {Table} are stale and could not be refreshed ({Reason}); claims go on with the statistics they have. {Hint}")]
  private static partial void LogSkipped(ILogger logger, string table, string reason, string hint);

  /// <summary>Collects the refresh-skipped warnings a claim raises; counts and logs them on dispose.</summary>
  public sealed class SkipScope : IDisposable {
    private readonly NpgsqlConnection _connection;
    private readonly WorkCoordinatorMetrics? _metrics;
    private readonly ILogger? _logger;
    private readonly List<(string Table, string Reason, string Hint)> _skips = [];

    internal SkipScope(NpgsqlConnection connection, WorkCoordinatorMetrics? metrics, ILogger? logger) {
      _connection = connection;
      _metrics = metrics;
      _logger = logger;
      _connection.Notice += _onNotice;
    }

    /// <inheritdoc />
    public void Dispose() {
      _connection.Notice -= _onNotice;
      lock (_skips) {
        foreach (var (table, reason, hint) in _skips) {
          _metrics?.QueueStatisticsRefreshSkips.Add(1,
            new KeyValuePair<string, object?>(WorkCoordinatorMetrics.TABLE_TAG, table),
            new KeyValuePair<string, object?>(WorkCoordinatorMetrics.REASON_TAG, reason));
          if (_logger is not null) {
            LogSkipped(_logger, table, reason, hint);
          }
        }
        _skips.Clear();
      }
    }

    private void _onNotice(object? sender, NpgsqlNoticeEventArgs e) {
      if (TryRead(e.Notice.MessageText, e.Notice.Detail, out var table, out var reason)) {
        lock (_skips) {
          _skips.Add((table, reason, e.Notice.Hint ?? ""));
        }
      }
    }
  }
}
