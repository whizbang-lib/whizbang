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
/// <item><description><strong>Shared-conn LISTEN <c>wh_committed</c></strong> — sub-ms wake from
/// <c>_emit_event_store_chain</c> at commit time. Routed through
/// <see cref="ISharedNotifyConnection"/> (slice 33.5) instead of a dedicated direct connection
/// so all per-pod LISTEN traffic multiplexes onto one direct Postgres connection.</description></item>
/// <item><description><strong>Polling tick</strong> — <see cref="CommitOrderStamperOptions.PollingInterval"/>.
/// Correctness floor; runs unconditionally on the lock-holder, so the system stamps
/// even when LISTEN is unavailable (gate reports IsAvailable=false → wake won't fire from
/// NOTIFY; polling tick keeps stamping anyway).</description></item>
/// </list>
///
/// <para>
/// The advisory lock is still held on a dedicated short-lived connection — not the shared
/// conn — because the lock is session-scoped and would pin the shared conn for the worker's
/// entire leader tenure. That's incompatible with the shared conn's role as the per-pod
/// multiplexer.
/// </para>
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
  ISharedNotifyConnection sharedConnection,
  ILogger<PgCommitOrderStamperWorker> logger,
  INotificationConnectionStringFallback? connectionStringFallback = null
) : BackgroundService {
  private readonly WhizbangNotificationOptions _notificationOptions = notificationOptions?.Value ?? throw new ArgumentNullException(nameof(notificationOptions));
  private readonly CommitOrderStamperOptions _stamperOptions = stamperOptions?.Value ?? throw new ArgumentNullException(nameof(stamperOptions));
  private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
  private readonly ISharedNotifyConnection _sharedConnection = sharedConnection ?? throw new ArgumentNullException(nameof(sharedConnection));
  private readonly ILogger<PgCommitOrderStamperWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly INotificationConnectionStringFallback? _connectionStringFallback = connectionStringFallback;

  private const string CHANNEL_NAME = "wh_committed";
  private readonly SemaphoreSlim _wake = new(initialCount: 1, maxCount: 1);
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
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Stamper drives the full leader-election + NOTIFY-driven wake + back-pressured stamping protocol. Splitting would require sharing the lock-conn lifetime + leader-state semaphore across helpers and lose the visible try/finally structure.")]
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

    // Production triage: Azure SCRAM-SHA-256 failures look identical regardless of which
    // resolution branch produced the password-less string. Log the source + which
    // credential markers we DID get, so operators can tell at a glance whether the
    // problem is:
    //   - config (the configured ConnectionStrings:<key>{-direct} entry lacks a password)
    //   - fallback (DbContext path returned a password-less string — e.g., Whizbang
    //     version pre-dates the RelationalOptionsExtension fix, or the DbContext was
    //     configured with NpgsqlDataSource which doesn't expose the original string)
    //   - explicit option (WhizbangNotificationOptions.DirectConnectionString missing password)
    var summary = _summarizeCredentialMarkers(resolution.ConnectionString);
    LogConnectionDiagnostics(_logger, resolution.Source, summary.HasUsername, summary.HasPassword);

    // Slice 33.5 — subscribe to wh_committed via the shared connection for the entire
    // worker lifetime. Even when this pod is not the leader, the subscription is harmless:
    // the wake semaphore saturates at maxCount of 1 and the stamping loop never starts,
    // so this becomes a no-op. When this pod is the leader, the wake fires sub-millisecond
    // on each committed event.
    var subscription = new CommitNotificationSubscription(this);
    using var subscriptionHandle = _sharedConnection.Subscribe(subscription);

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

        try {
          while (!stoppingToken.IsCancellationRequested) {
            // Wait for NOTIFY-fired wake OR polling-interval timeout. Either path fires
            // the same stamp.
            try {
              _ = await _wake.WaitAsync(_stamperOptions.PollingInterval, stoppingToken);
            } catch (OperationCanceledException) { break; }

            var stamped = await _stampOnceAsync(lockConn, stoppingToken);
            _ = Interlocked.Add(ref _totalStamped, stamped);
            OnStampCompleted?.Invoke(stamped);
          }
        } catch (OperationCanceledException) {
          // shutdown — fall through to finally
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

  /// <summary>
  /// Fires the wake semaphore from the shared-conn dispatch path. Called by
  /// <see cref="CommitNotificationSubscription.OnNotification"/> on every wh_committed
  /// notification. Idempotent saturation — overlapping NOTIFYs collapse to a single
  /// pending wake.
  /// </summary>
  internal void Wake() {
    try {
      _ = _wake.Release();
    } catch (SemaphoreFullException) {
      // Already a wake pending — fine, the loop will pick up both at once.
    }
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

  private sealed class CommitNotificationSubscription(PgCommitOrderStamperWorker owner) : INotifySubscription {
    public string ChannelName => CHANNEL_NAME;
    public void OnNotification(string payload) => owner.Wake();
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

  [LoggerMessage(EventId = 7, Level = LogLevel.Information,
    Message = "PgCommitOrderStamperWorker connection diagnostics — Source={Source}, has Username={HasUsername}, has Password={HasPassword}. If HasPassword=false and Azure rejects with SCRAM-SHA-256, the resolved string is missing a password — check ConnectionStrings:<key>(-direct) config or upgrade Whizbang for the RelationalOptionsExtension fallback fix.")]
  static partial void LogConnectionDiagnostics(
    ILogger logger,
    NotificationConnectionStringResolver.ResolutionSource source,
    bool hasUsername,
    bool hasPassword);

  /// <summary>
  /// Reports which credential markers are present in <paramref name="connectionString"/>
  /// — used purely for the startup diagnostic; never logs the values themselves.
  /// </summary>
  private static (bool HasUsername, bool HasPassword) _summarizeCredentialMarkers(string? connectionString) {
    if (string.IsNullOrEmpty(connectionString)) {
      return (HasUsername: false, HasPassword: false);
    }
    // Crude but adequate for a one-shot diagnostic: case-insensitive substring match for
    // the Npgsql-recognized credential keys (Password / Pwd / Username / User Id).
    // Misses connection-string-builder cases where the key was set programmatically but
    // not via the string itself — but for the a consumer failure mode (resolution returns a
    // bare string), this catches the actual problem.
    var s = connectionString.AsSpan();
    var hasUsername =
      s.Contains("Username=", StringComparison.OrdinalIgnoreCase) ||
      s.Contains("User Id=", StringComparison.OrdinalIgnoreCase) ||
      s.Contains("UserId=", StringComparison.OrdinalIgnoreCase) ||
      s.Contains("User ID=", StringComparison.OrdinalIgnoreCase);
    var hasPassword =
      s.Contains("Password=", StringComparison.OrdinalIgnoreCase) ||
      s.Contains("Pwd=", StringComparison.OrdinalIgnoreCase);
    return (hasUsername, hasPassword);
  }

  [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "PgCommitOrderStamperWorker stopped")]
  static partial void LogStopped(ILogger logger);
}
