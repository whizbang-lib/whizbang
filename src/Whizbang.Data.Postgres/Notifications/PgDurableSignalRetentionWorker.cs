using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Periodically deletes rows from <c>wh_signals</c> older than the configured retention window so
/// the durable-signal log doesn't grow unbounded. Safe because <c>wh_signal_cursors</c> stores an
/// absolute id, not a relative offset — deleting rows &lt;= <c>MIN(last_delivered_signal_id)</c>
/// never breaks any pod's tail.
/// </summary>
/// <remarks>
/// <para>
/// Two guardrails prevent this worker from stranding a slow tail:
/// </para>
/// <para>
/// 1. The DELETE joins against <c>wh_signal_cursors</c> and only removes ids &lt;= the min cursor,
///    so a pod that fell behind still has its needed rows even if they're older than the age window.
/// </para>
/// <para>
/// 2. Rows are also age-bounded — the delete predicate is
///    <c>created_at &lt; NOW() - @retention AND id &lt;= (SELECT MIN(last_delivered_signal_id) FROM wh_signal_cursors)</c>
///    — so an empty cursors table (no pods have ever tailed) does NOT wipe pre-existing signals.
/// </para>
/// </remarks>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public sealed partial class PgDurableSignalRetentionWorker(
  IOptions<WhizbangNotificationOptions> options,
  IConfiguration configuration,
  ILogger<PgDurableSignalRetentionWorker> logger,
  INotificationConnectionStringFallback? connectionStringFallback = null
) : BackgroundService {
  private readonly WhizbangNotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
  private readonly ILogger<PgDurableSignalRetentionWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly INotificationConnectionStringFallback? _connectionStringFallback = connectionStringFallback;

  /// <summary>Sweep interval — daily is plenty; wh_signals rows are small doorbell records.</summary>
  private static readonly TimeSpan _sweepInterval = TimeSpan.FromHours(1);

  /// <summary>Retention window — signals older than this are eligible for deletion.</summary>
  private static readonly TimeSpan _retentionAge = TimeSpan.FromDays(7);

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    // First sweep runs after one interval, not immediately at startup — matches other maintenance
    // workers in the codebase and prevents startup contention on the DB.
    try { await Task.Delay(_sweepInterval, stoppingToken); } catch (OperationCanceledException) { return; }

    while (!stoppingToken.IsCancellationRequested) {
      try {
        await SweepOnceAsync(stoppingToken);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        LogSweepFailed(_logger, ex);
      }

      try {
        await Task.Delay(_sweepInterval, stoppingToken);
      } catch (OperationCanceledException) {
        break;
      }
    }
  }

  /// <summary>Test hook: run one sweep synchronously. Returns the number of rows deleted.</summary>
  public async Task<int> SweepOnceAsync(CancellationToken cancellationToken) {
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    if (resolution.ConnectionString is null) {
      return 0;
    }
    await using var conn = new NpgsqlConnection(resolution.ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand(@"
      DELETE FROM wh_signals
      WHERE created_at < NOW() - @retention
        AND id <= COALESCE((SELECT MIN(last_delivered_signal_id) FROM wh_signal_cursors), 0)", conn);
    cmd.Parameters.AddWithValue("retention", _retentionAge);
    var deleted = await cmd.ExecuteNonQueryAsync(cancellationToken);
    if (deleted > 0) {
      LogSweepDeleted(_logger, deleted);
    }
    return deleted;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "PgDurableSignalRetentionWorker sweep failed; will retry on next interval")]
  static partial void LogSweepFailed(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information,
    Message = "PgDurableSignalRetentionWorker: deleted {DeletedCount} rows from wh_signals")]
  static partial void LogSweepDeleted(ILogger logger, int deletedCount);
}
