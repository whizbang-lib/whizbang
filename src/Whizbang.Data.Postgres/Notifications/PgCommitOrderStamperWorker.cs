using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Slice 26 commit-order stamper. Allocates <c>commit_sequence</c> values via
/// <c>stamp_pending_commit_sequences</c> on every wake. Singleton across the DB —
/// every instance of the service runs the worker but only the one holding the
/// <c>pg_try_advisory_lock</c> stamps. Non-holders sleep on a retry interval.
///
/// <para>
/// Wake sources:
/// </para>
/// <list type="bullet">
/// <item><description><strong>LISTEN <c>wh_committed</c></strong> — sub-ms wake from
/// <c>_emit_event_store_chain</c> at commit time. Activated only when a direct
/// connection is resolved (i.e. NOT pgbouncer-pooled).</description></item>
/// <item><description><strong>Polling tick</strong> — <see cref="CommitOrderStamperOptions.PollingInterval"/>.
/// Correctness floor; runs unconditionally on the lock-holder, so the system stamps
/// even when LISTEN is unavailable.</description></item>
/// </list>
///
/// <para>
/// Restart safety: the advisory lock is session-scoped, so a crash auto-releases.
/// The next instance's retry tick picks it up within <see cref="CommitOrderStamperOptions.LeaderElectionRetry"/>.
/// </para>
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
public sealed partial class PgCommitOrderStamperWorker(
  IOptions<WhizbangNotificationOptions> notificationOptions,
  IOptions<CommitOrderStamperOptions> stamperOptions,
  IConfiguration configuration,
  ILogger<PgCommitOrderStamperWorker> logger,
  INotificationConnectionStringFallback? connectionStringFallback = null
) : BackgroundService {
  private readonly WhizbangNotificationOptions _notificationOptions = notificationOptions?.Value ?? throw new ArgumentNullException(nameof(notificationOptions));
  private readonly CommitOrderStamperOptions _stamperOptions = stamperOptions?.Value ?? throw new ArgumentNullException(nameof(stamperOptions));
  private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
  private readonly ILogger<PgCommitOrderStamperWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly INotificationConnectionStringFallback? _connectionStringFallback = connectionStringFallback;

  private const string CHANNEL_NAME = "wh_committed";
  private bool _isLeader;
  private int _totalStamped;

  /// <summary>True while this instance holds the advisory lock (i.e. is the active stamper).</summary>
  public bool IsLeader => _isLeader;

  /// <summary>Cumulative count of rows this instance has stamped since start.</summary>
  public int TotalStamped => Volatile.Read(ref _totalStamped);

  /// <summary>Fires when this instance acquires the advisory lock and becomes the active stamper.</summary>
  public event Action? OnBecameLeader;

  /// <summary>Fires after each <c>stamp_pending_commit_sequences</c> call with the count stamped this call.</summary>
  public event Action<int>? OnStampCompleted;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (_stamperOptions.DisableStamper) {
      LogDisabled(_logger);
      return;
    }

    var resolution = NotificationConnectionStringResolver.Resolve(_notificationOptions, _configuration, _connectionStringFallback);
    if (resolution.ConnectionString is null) {
      LogDisabledNoConnection(_logger);
      return;
    }

    LogStarted(_logger, resolution.Source);

    while (!stoppingToken.IsCancellationRequested) {
      NpgsqlConnection? lockConn = null;
      try {
        lockConn = new NpgsqlConnection(resolution.ConnectionString);
        await lockConn.OpenAsync(stoppingToken);

        var gotLock = await _tryAcquireLeaderLockAsync(lockConn, stoppingToken);
        if (!gotLock) {
          await lockConn.DisposeAsync();
          lockConn = null;
          try { await Task.Delay(_stamperOptions.LeaderElectionRetry, stoppingToken); } catch (OperationCanceledException) { break; }
          continue;
        }

        _setLeader(true);

        // Wake semaphore: signaled by NOTIFY listener AND polling tick. The loop drains
        // it; if multiple signals arrived between iterations, one stamp call clears them all.
        var wake = new SemaphoreSlim(initialCount: 1, maxCount: 1);
        void onNotification(object? sender, NpgsqlNotificationEventArgs e) {
          if (string.Equals(e.Channel, CHANNEL_NAME, StringComparison.Ordinal)) {
            try { _ = wake.Release(); } catch (SemaphoreFullException) { /* already saturated, fine */ }
          }
        }

        lockConn.Notification += onNotification;
        await using (var listenCmd = new NpgsqlCommand($"LISTEN {CHANNEL_NAME}", lockConn)) {
          await listenCmd.ExecuteNonQueryAsync(stoppingToken);
        }

        try {
          while (!stoppingToken.IsCancellationRequested) {
            // Wait for NOTIFY or polling-interval timeout. Either path fires the same stamp.
            try {
              _ = await wake.WaitAsync(_stamperOptions.PollingInterval, stoppingToken);
            } catch (OperationCanceledException) { break; }

            // Drain any pending notifications so the next wait blocks on fresh signals.
            await _pollPendingNotificationsAsync(lockConn, stoppingToken);

            var stamped = await _stampOnceAsync(lockConn, stoppingToken);
            _ = Interlocked.Add(ref _totalStamped, stamped);
            OnStampCompleted?.Invoke(stamped);
          }
        } finally {
          lockConn.Notification -= onNotification;
        }
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        LogIterationError(_logger, ex.Message);
        // Fall through to retry loop; lockConn finally below will release.
      } finally {
        _setLeader(false);
        if (lockConn is not null) {
          try { await _releaseLeaderLockAsync(lockConn); } catch { /* best effort */ }
          await lockConn.DisposeAsync();
        }
      }

      // Brief pause before re-attempting lock acquisition on next iteration.
      try { await Task.Delay(_stamperOptions.LeaderElectionRetry, stoppingToken); } catch (OperationCanceledException) { break; }
    }

    LogStopped(_logger);
  }

  private async Task<bool> _tryAcquireLeaderLockAsync(NpgsqlConnection conn, CancellationToken ct) {
    await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@k)", conn);
    cmd.Parameters.AddWithValue("k", _stamperOptions.AdvisoryLockKey);
    var result = await cmd.ExecuteScalarAsync(ct);
    return result is bool b && b;
  }

  private async Task _releaseLeaderLockAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conn);
    cmd.Parameters.AddWithValue("k", _stamperOptions.AdvisoryLockKey);
    _ = await cmd.ExecuteScalarAsync();
  }

  private async Task<int> _stampOnceAsync(NpgsqlConnection conn, CancellationToken ct) {
    await using var cmd = new NpgsqlCommand("SELECT stamp_pending_commit_sequences(@bs)", conn);
    cmd.Parameters.AddWithValue("bs", _stamperOptions.BatchSize);
    var result = await cmd.ExecuteScalarAsync(ct);
    return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task _pollPendingNotificationsAsync(NpgsqlConnection conn, CancellationToken ct) {
    // Force a network roundtrip so any pending NOTIFY messages dispatch to the handler
    // before we proceed. Without this, NOTIFYs received during the WaitAsync timeout
    // could remain queued on the connection until the next command — fine in practice
    // but cleaner to drain explicitly.
    try {
      await using var ping = new NpgsqlCommand("SELECT 1", conn);
      _ = await ping.ExecuteScalarAsync(ct);
    } catch (OperationCanceledException) { /* shutdown */ }
  }

  private void _setLeader(bool isLeader) {
    if (_isLeader == isLeader) { return; }
    _isLeader = isLeader;
    if (isLeader) {
      LogBecameLeader(_logger);
      OnBecameLeader?.Invoke();
    } else {
      LogReleasedLeader(_logger);
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "PgCommitOrderStamperWorker disabled by DisableStamper=true")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "PgCommitOrderStamperWorker disabled — no connection string resolved (set WhizbangNotificationOptions.ConnectionStringKey or DirectConnectionString)")]
  static partial void LogDisabledNoConnection(ILogger logger);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "PgCommitOrderStamperWorker started with connection from {Source}")]
  static partial void LogStarted(ILogger logger, NotificationConnectionStringResolver.ResolutionSource source);

  [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "PgCommitOrderStamperWorker became leader — actively stamping")]
  static partial void LogBecameLeader(ILogger logger);

  [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "PgCommitOrderStamperWorker released leader role")]
  static partial void LogReleasedLeader(ILogger logger);

  [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "PgCommitOrderStamperWorker iteration failed: {Reason}")]
  static partial void LogIterationError(ILogger logger, string reason);

  [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "PgCommitOrderStamperWorker stopped")]
  static partial void LogStopped(ILogger logger);
}
