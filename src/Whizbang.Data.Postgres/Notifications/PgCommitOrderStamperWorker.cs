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
  INotificationConnectionStringFallback? connectionStringFallback = null,
  INotificationDataSource? notificationDataSource = null,
  INotifySignalingGate? notifySignalingGate = null
) : BackgroundService {
  private readonly WhizbangNotificationOptions _notificationOptions = notificationOptions?.Value ?? throw new ArgumentNullException(nameof(notificationOptions));
  private readonly CommitOrderStamperOptions _stamperOptions = stamperOptions?.Value ?? throw new ArgumentNullException(nameof(stamperOptions));
  private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
  private readonly ISharedNotifyConnection _sharedConnection = sharedConnection ?? throw new ArgumentNullException(nameof(sharedConnection));
  private readonly ILogger<PgCommitOrderStamperWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly INotificationConnectionStringFallback? _connectionStringFallback = connectionStringFallback;
  private readonly INotifySignalingGate? _notifySignalingGate = notifySignalingGate;
  // Opt-in: register an INotificationDataSource via DI when the DbContext is
  // configured via UseNpgsql(NpgsqlDataSource) — that's the only path that
  // works because Npgsql strips credentials from both NpgsqlConnection.
  // ConnectionString and NpgsqlDataSource.ConnectionString. Marker-interface
  // wrap so we don't accidentally grab EF Core's own NpgsqlDataSource and
  // exhaust its small connection pool.
  private readonly NpgsqlDataSource? _dataSource = notificationDataSource?.DataSource;

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

    var resolution = NotificationConnectionStringResolver.Resolve(_notificationOptions, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    // Prefer a DI-registered NpgsqlDataSource: when the DbContext is configured
    // via UseNpgsql(NpgsqlDataSource), neither the connection string nor the
    // data source's public ConnectionString carry the password (Npgsql strips
    // them eagerly), so the only way to authenticate is to ask the data source
    // to open the connection itself.
    if (resolution.ConnectionString is null && _dataSource is null) {
      LogDisabledNoConnection(_logger);
      return;
    }

    var keyForDiagnostics = _notificationOptions.ConnectionStringKey ?? "(unset)";
    var effectiveSource = _dataSource is not null
      ? NotificationConnectionStringResolver.ResolutionSource.DbContextFallback
      : resolution.Source;
    LogStarted(_logger, effectiveSource, keyForDiagnostics);

    // Production triage: Azure SCRAM-SHA-256 failures look identical regardless of which
    // resolution branch produced the password-less string. Log the source + which
    // credential markers we DID get, so operators can tell at a glance whether the
    // problem is:
    //   - config (the configured ConnectionStrings:<key>{-direct} entry lacks a password)
    //   - fallback (DbContext path returned a password-less string — e.g., Whizbang
    //     version pre-dates the RelationalOptionsExtension fix, or the DbContext was
    //     configured with NpgsqlDataSource which doesn't expose the original string)
    //   - explicit option (WhizbangNotificationOptions.DirectConnectionString missing password)
    var summary = ConnectionStringCredentialMarkerSummary.Summarize(resolution.ConnectionString);
    LogConnectionDiagnostics(_logger, resolution.Source, keyForDiagnostics, summary.HasUsername, summary.HasSecret);

    // Loud-and-early warning mirroring PgSharedNotifyConnection: PooledKeyFallback
    // means the leader-lock connection routes through pgbouncer in tx-pooling mode.
    // pg_try_advisory_lock is session-scoped, so a tx-pool front-end will not preserve
    // the lock across the leader's tenure — operators MUST switch to `-direct` for the
    // stamper to function.
    if (effectiveSource == NotificationConnectionStringResolver.ResolutionSource.PooledKeyFallback) {
      LogPooledFallbackWarning(_logger, keyForDiagnostics);
    }

    // Slice 33.5 — subscribe to wh_committed via the shared connection for the entire
    // worker lifetime. Even when this pod is not the leader, the subscription is harmless:
    // the wake semaphore saturates at maxCount of 1 and the stamping loop never starts,
    // so this becomes a no-op. When this pod is the leader, the wake fires sub-millisecond
    // on each committed event.
    var subscription = new CommitNotificationSubscription(this);
    using var subscriptionHandle = _sharedConnection.Subscribe(subscription);

    // Snap-to-floor on gate-flip: when the gate transitions to false, the relaxed
    // cadence is no longer safe (NOTIFY may not arrive), so wake immediately and
    // let the next loop iteration recompute the effective interval. When the gate
    // flips back to true, wake too — if we were mid-sleep in floor cadence, the
    // next iteration picks up the relaxed cadence right away.
    Action<bool> handleGateChange = _ => Wake();
    if (_notifySignalingGate is not null) {
      _notifySignalingGate.OnAvailabilityChanged += handleGateChange;
    }

    try {
      while (!stoppingToken.IsCancellationRequested) {
        NpgsqlConnection? lockConn = null;
        try {
          // Prefer NpgsqlDataSource.OpenConnectionAsync — it carries the
          // credentials internally and is the only path that works with the
          // UseNpgsql(NpgsqlDataSource) DbContext configuration. Falls back to
          // the resolved connection string when no data source is registered.
          lockConn = _dataSource is not null
            ? await _dataSource.OpenConnectionAsync(stoppingToken)
            : new NpgsqlConnection(resolution.ConnectionString);
          if (_dataSource is null) {
            await lockConn.OpenAsync(stoppingToken);
          }

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
                var effectiveInterval = ComputeEffectivePollingInterval(
                  _stamperOptions,
                  _notifySignalingGate?.IsAvailable);
                _ = await _wake.WaitAsync(effectiveInterval, stoppingToken);
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
          LogIterationError(_logger, ex.Message, resolution.Source, _notificationOptions.ConnectionStringKey ?? "(unset)");
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
    } finally {
      if (_notifySignalingGate is not null) {
        _notifySignalingGate.OnAvailabilityChanged -= handleGateChange;
      }
    }

    LogStopped(_logger);
  }

  /// <summary>
  /// Computes the effective polling interval the wait-loop should use on the next tick.
  /// Returns <see cref="CommitOrderStamperOptions.PollingInterval"/> when the NOTIFY gate
  /// is unavailable (or not wired) — that's the "correctness floor" cadence. Returns
  /// <see cref="CommitOrderStamperOptions.NotifyHealthyPollingInterval"/> when the gate
  /// reports healthy AND the relaxed value is strictly greater than the floor; otherwise
  /// falls back to the floor.
  /// </summary>
  /// <remarks>
  /// Pure function so it's trivially unit-testable without spinning up the worker.
  /// Extracted as <c>internal static</c> so test assemblies can call it directly via
  /// <c>InternalsVisibleTo</c>.
  /// </remarks>
  internal static TimeSpan ComputeEffectivePollingInterval(
      CommitOrderStamperOptions options,
      bool? gateIsAvailable) {
    ArgumentNullException.ThrowIfNull(options);
    // Gate broken (false) or not wired (null) → floor cadence. Missed NOTIFY recovery
    // is the dominant failure mode; the poll must stay tight.
    if (gateIsAvailable != true) {
      return options.PollingInterval;
    }
    // Gate healthy AND relaxed value strictly greater than floor → use relaxed.
    // The "strictly greater" guard handles operator misconfiguration where
    // NotifyHealthyPollingInterval is set below the floor — the knob only relaxes,
    // never tightens.
#pragma warning disable CS0618 // Honoring the Slice 1 knob for backward compat until the stamper backstop loop retires in a follow-up slice.
    var relaxed = options.NotifyHealthyPollingInterval;
#pragma warning restore CS0618
    if (relaxed.HasValue && relaxed.Value > options.PollingInterval) {
      return relaxed.Value;
    }
    return options.PollingInterval;
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

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "PgCommitOrderStamperWorker started with connection from {Source} for key '{ConnectionStringKey}'")]
  static partial void LogStarted(ILogger logger, NotificationConnectionStringResolver.ResolutionSource source, string connectionStringKey);

  [LoggerMessage(EventId = 13, Level = LogLevel.Warning,
    Message = "PgCommitOrderStamperWorker resolved the POOLED connection (key '{ConnectionStringKey}') — pg_try_advisory_lock cannot be held " +
              "through pgbouncer in transaction-pooling mode. Add ConnectionStrings:{ConnectionStringKey}-direct pointing to a direct " +
              "(non-pgbouncer) Postgres connection. Until then, leader election fails and commit-order stamping stalls.")]
  static partial void LogPooledFallbackWarning(ILogger logger, string connectionStringKey);

  [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "PgCommitOrderStamperWorker became leader — actively stamping")]
  static partial void LogBecameLeader(ILogger logger);

  [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "PgCommitOrderStamperWorker released leader role")]
  static partial void LogReleasedLeader(ILogger logger);

  [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "PgCommitOrderStamperWorker iteration failed: {Reason}. Resolved connection from {ResolutionSource} for key '{ConnectionStringKey}'. " +
    "If Source=PooledKeyFallback, the connection routes through pgbouncer — set ConnectionStrings:{ConnectionStringKey}-direct with a direct (non-pgbouncer) Postgres string.")]
  static partial void LogIterationError(ILogger logger, string reason, NotificationConnectionStringResolver.ResolutionSource resolutionSource, string connectionStringKey);

  [LoggerMessage(EventId = 7, Level = LogLevel.Information,
    Message = "PgCommitOrderStamperWorker connection diagnostics — Source={Source}, key='{ConnectionStringKey}', has user-id marker={HasUsername}, has secret marker={HasSecret}. If HasSecret=false and Azure rejects with SCRAM-SHA-256, the resolved string is missing a credential — check ConnectionStrings:{ConnectionStringKey}(-direct) config or upgrade Whizbang for the RelationalOptionsExtension fallback fix.")]
  static partial void LogConnectionDiagnostics(
    ILogger logger,
    NotificationConnectionStringResolver.ResolutionSource source,
    string connectionStringKey,
    bool hasUsername,
    bool hasSecret);

  [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "PgCommitOrderStamperWorker stopped")]
  static partial void LogStopped(ILogger logger);
}
