using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Per-pod singleton that owns THE direct Postgres connection used for all LISTEN/NOTIFY
/// signaling. Implements both <see cref="ISharedNotifyConnection"/> (subscribe API for
/// per-channel consumers) and <see cref="INotifySignalingGate"/> (the killswitch read by
/// <c>ClaimWorker</c> + the per-listener subscribers).
/// </summary>
/// <remarks>
/// <para>
/// Slice 33.1 ships the connection lifecycle + subscription registry. The self-test probe
/// (slice 33.2) and notification dispatch loop (slice 33.3) are intentionally NOT in this
/// file yet — that's why <c>IsAvailable</c> at this stage tracks "the shared connection is
/// open" and not "the probe most recently round-tripped." The probe-aware availability lands
/// in 33.2; until then, <c>IsAvailable</c> is the optimistic floor we publish for downstream
/// consumers to begin to build against.
/// </para>
/// <para>
/// Pre-slice-33 design opened one direct conn per listener (work signals, commit-order
/// stamping, app signals → 3 per pod). With horizontal scaling that's a real load on the
/// Postgres <c>max_connections</c> budget on top of pgbouncer-pooled traffic. This singleton
/// is the connection-count collapse: one direct conn per pod regardless of how many
/// <see cref="INotifySubscription"/>s register.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public sealed partial class PgSharedNotifyConnection(
  IOptions<WhizbangNotificationOptions> options,
  IConfiguration configuration,
  IServiceInstanceProvider instanceProvider,
  ILogger<PgSharedNotifyConnection>? logger = null,
  INotificationConnectionStringFallback? connectionStringFallback = null,
  TimeProvider? timeProvider = null,
  INotificationDataSource? notificationDataSource = null
) : BackgroundService, ISharedNotifyConnection, INotifySignalingGate {
  private readonly WhizbangNotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly INotificationConnectionStringFallback? _connectionStringFallback = connectionStringFallback;
  private readonly ILogger<PgSharedNotifyConnection> _logger = logger ?? NullLogger<PgSharedNotifyConnection>.Instance;
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
  // Opt-in via INotificationDataSource (not bare NpgsqlDataSource — that
  // would catch EF Core's own data source and exhaust its small pool).
  // Only path that works under UseNpgsql(NpgsqlDataSource) since Npgsql strips
  // credentials from every public ConnectionString surface.
  private readonly NpgsqlDataSource? _dataSource = notificationDataSource?.DataSource;

  private readonly NotifySubscriptionRegistry _registry = new();
  private readonly Lock _connectionGate = new();
  private readonly Lock _availabilityGate = new();
  private readonly HashSet<string> _listenedChannels = new(StringComparer.Ordinal);
  private NpgsqlConnection? _connection;
  // Slice 33.3 — Subscribe wakes the dispatch loop by cancelling this CTS so the next
  // iteration can issue LISTEN for the newly-registered channel. The dispatch loop holds
  // the shared conn via WaitAsync, so we can't issue LISTEN from Subscribe directly —
  // NpgsqlConnection isn't thread-safe and a concurrent command would throw.
  private CancellationTokenSource? _resyncSignal;
  private bool _isAvailable;
  private DateTimeOffset? _lastVerifiedAt;
  private DateTimeOffset? _lastFailureAt;
  private string? _lastFailureReason;

  /// <inheritdoc />
  public bool IsAvailable => _isAvailable;
  /// <inheritdoc />
  public DateTimeOffset? LastVerifiedAt => _lastVerifiedAt;
  /// <inheritdoc />
  public DateTimeOffset? LastFailureAt => _lastFailureAt;
  /// <inheritdoc />
  public string? LastFailureReason => _lastFailureReason;
  /// <inheritdoc />
  public event Action<bool>? OnAvailabilityChanged;

  /// <inheritdoc />
  /// <remarks>
  /// Slice 33.2 — opens an ephemeral connection, issues <c>LISTEN</c> on a single-use
  /// self-test channel, emits <c>pg_notify</c> via a second ephemeral connection, and waits
  /// up to <see cref="WhizbangNotificationOptions.SelfTestTimeout"/> for the notification.
  /// Updates <see cref="IsAvailable"/> based on the result. Runs INDEPENDENTLY of the
  /// BackgroundService's main loop so ops can force a re-test ("we just fixed the network")
  /// without waiting for the periodic reprobe schedule.
  /// </remarks>
  public async Task<bool> ProbeNowAsync(CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    var resolution = NotificationConnectionStringResolver.Resolve(
      _options, _configuration, _connectionStringFallback);
    if (resolution.ConnectionString is null && _dataSource is null) {
      _setAvailable(false, "no connection string resolvable");
      return false;
    }
    try {
      // Prefer the DI-registered NpgsqlDataSource — only path that survives
      // UseNpgsql(NpgsqlDataSource) configurations where Npgsql has stripped
      // credentials from every public ConnectionString surface.
      await using var conn = _dataSource is not null
        ? await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false)
        : new NpgsqlConnection(resolution.ConnectionString);
      if (_dataSource is null) {
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
      }
      // Pass the original resolved string (not conn.ConnectionString — Npgsql strips the
      // password from that after Open for security, so a second connection built from it
      // can't authenticate). When the data source path is used the probe doesn't open a
      // second connection itself, so this argument is unused.
      var ok = await _runProbeAsync(conn, resolution.ConnectionString ?? string.Empty, cancellationToken).ConfigureAwait(false);
      _setAvailable(ok, ok ? null : "ProbeNowAsync round-trip failed");
      return ok;
    } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
      _setAvailable(false, "ProbeNowAsync timed out");
      return false;
    } catch (Exception ex) {
      _setAvailable(false, ex.Message);
      return false;
    }
  }

  /// <summary>
  /// Performs the LISTEN + NOTIFY-via-second-conn + wait-with-timeout round-trip. Returns
  /// <c>true</c> when the notification arrived within <see cref="WhizbangNotificationOptions.SelfTestTimeout"/>.
  /// Caller is responsible for updating <see cref="IsAvailable"/> based on the result.
  /// </summary>
  /// <param name="conn">The LISTENing connection (kept open across the probe).</param>
  /// <param name="connectionString">Original resolved connection string. The NOTIFY side opens a fresh
  /// connection from this — must be the original (with credentials) because Npgsql strips the
  /// password from <see cref="NpgsqlConnection.ConnectionString"/> after Open for security.</param>
  /// <param name="ct">Caller cancellation.</param>
  private async Task<bool> _runProbeAsync(NpgsqlConnection conn, string connectionString, CancellationToken ct) {
    // Nonce uses 8 hex chars of a fresh UUIDv7 — plenty of entropy for a 2 s self-test
    // window while keeping the channel name short. Per `feedback_use_trackedguid`.
    var nonce = global::Whizbang.Core.ValueObjects.TrackedGuid.NewMedo().Value.ToString("N")[..12];
    var channelName = $"wh_selftest_{_instanceProvider.InstanceId:N}_{nonce}";
    var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    void Handler(object? sender, NpgsqlNotificationEventArgs e) {
      if (string.Equals(e.Channel, channelName, StringComparison.Ordinal)) {
        signal.TrySetResult();
      }
    }

    conn.Notification += Handler;
    try {
      await using (var listenCmd = new NpgsqlCommand($"LISTEN \"{channelName}\"", conn)) {
        await listenCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      // Emit NOTIFY on a separate connection. Postgres's LISTENing backend doesn't observe
      // its own pre-commit NOTIFY on the same backend (would require the emitting tx to
      // commit first, which is a chicken-and-egg for a long-lived LISTEN session). The
      // original connection string carries credentials; conn.ConnectionString strips the
      // password after Open and would fail SASL/SCRAM auth — so use the data source
      // when present (the only path that survives UseNpgsql(NpgsqlDataSource)).
      await using (var notifyConn = _dataSource is not null
          ? await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)
          : new NpgsqlConnection(connectionString)) {
        if (_dataSource is null) {
          await notifyConn.OpenAsync(ct).ConfigureAwait(false);
        }
        await using var notifyCmd = new NpgsqlCommand("SELECT pg_notify(@channel, 'ping')", notifyConn);
        notifyCmd.Parameters.AddWithValue("@channel", channelName);
        _ = await notifyCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
      }

      // Drive WaitAsync so Npgsql delivers the notification to our handler. Timeout via
      // a linked CTS gives the SelfTestTimeout-bounded wait.
      //
      // WaitAsync returns as soon as ANY async message arrives — that includes the
      // notification we expect, but also backend chatter (NoticeResponse, ParameterStatus,
      // etc.) that fires between LISTEN and our NOTIFY. We must loop until either our
      // specific signal arrives (handler set the TCS) or the timeout fires.
      using var timeoutCts = new CancellationTokenSource(_options.SelfTestTimeout, _timeProvider);
      using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
      while (!signal.Task.IsCompletedSuccessfully && !combined.Token.IsCancellationRequested) {
        try {
          await conn.WaitAsync(combined.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (combined.Token.IsCancellationRequested) {
          break;
        }
      }
      return signal.Task.IsCompletedSuccessfully;
    } finally {
      conn.Notification -= Handler;
      try {
        await using var unlistenCmd = new NpgsqlCommand($"UNLISTEN \"{channelName}\"", conn);
        await unlistenCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
      } catch {
        // Best-effort cleanup; if UNLISTEN fails the channel goes away with the conn anyway.
      }
    }
  }

  /// <inheritdoc />
  public IDisposable Subscribe(INotifySubscription subscription) {
    ArgumentNullException.ThrowIfNull(subscription);
    var wasFirst = _registry.Add(subscription);
    if (wasFirst) {
      _signalResync();
    }
    return new SubscriptionHandle(this, subscription);
  }

  /// <summary>
  /// Slice 33.3 — wakes the dispatch loop so it can sync LISTEN/UNLISTEN state with the
  /// registry. NpgsqlConnection isn't thread-safe; issuing LISTEN directly from Subscribe
  /// while the dispatch loop holds the conn via WaitAsync would throw. Instead we cancel
  /// the loop's wait token so it unwinds, calls _syncListensAsync, and resumes WaitAsync.
  /// </summary>
  private void _signalResync() {
    var cts = Volatile.Read(ref _resyncSignal);
    if (cts is null) {
      return;  // Dispatch loop not currently waiting; next iteration will sync naturally.
    }
    try {
      cts.Cancel();
    } catch (ObjectDisposedException) {
      // Loop already moved past this iteration; signal is now stale. The fresh iteration
      // will sync from registry state regardless.
    }
  }

  /// <summary>
  /// Issues LISTEN for every channel in the registry not yet covered, and UNLISTEN for any
  /// channel that's covered but no longer in the registry. Idempotent — safe to call
  /// repeatedly. MUST be called from the dispatch loop's stack so it has exclusive access
  /// to the shared conn (no overlapping WaitAsync).
  /// </summary>
  private async Task _syncListensAsync(NpgsqlConnection conn, CancellationToken ct) {
    var registryChannels = new HashSet<string>(_registry.AllChannels(), StringComparer.Ordinal);
    // Add channels missing from the live set
    foreach (var channel in registryChannels) {
      if (_listenedChannels.Contains(channel)) {
        continue;
      }
      try {
        await using var listenCmd = new NpgsqlCommand($"LISTEN \"{channel}\"", conn);
        await listenCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _listenedChannels.Add(channel);
        LogChannelListened(_logger, channel);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogListenFailed(_logger, channel, ex);
      }
    }
    // Remove channels that are no longer in registry (last subscriber went away).
    // Iterate over a snapshot — _listenedChannels mutates as we go.
    foreach (var channel in _listenedChannels.ToArray()) {
      if (registryChannels.Contains(channel)) {
        continue;
      }
      try {
        await using var unlistenCmd = new NpgsqlCommand($"UNLISTEN \"{channel}\"", conn);
        await unlistenCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _listenedChannels.Remove(channel);
        LogChannelUnlistened(_logger, channel);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogUnlistenFailed(_logger, channel, ex);
      }
    }
  }

  /// <inheritdoc />
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Notify-loop runs the full LISTEN connection lifecycle: resolution, connect, re-listen all topics on reconnect, error-then-backoff, channel-dispatch fanout. The branches are tightly coupled to a single connection lifetime that cannot be split without leaking the connection across helpers.")]
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (_options.DisableNotifications || _options.SignalingMode == WorkSignalingMode.Polling) {
      LogDisabledByMode(_logger);
      return;
    }

    var resolution = NotificationConnectionStringResolver.Resolve(
      _options, _configuration, _connectionStringFallback);
    if (resolution.ConnectionString is null && _dataSource is null) {
      if (_options.SignalingMode == WorkSignalingMode.ListenNotify) {
        throw new InvalidOperationException(
          "WorkSignalingMode.ListenNotify is set but no direct connection string could be resolved. " +
          "Configure WhizbangNotificationOptions.ConnectionStringKey or set DirectConnectionString, " +
          "or register an NpgsqlDataSource singleton in DI.");
      }
      LogDisabledNoConnection(_logger);
      return;
    }

    LogResolvedConnection(_logger, resolution.Source);

    // Production triage: log credential markers so operators can tell at a glance
    // whether an Azure SCRAM-SHA-256 disconnect comes from a missing-password
    // resolution vs. a transient network/auth issue. Same diagnostic as
    // PgCommitOrderStamperWorker.
    var (hasUsername, hasSecret) = ConnectionStringCredentialMarkerSummary.Summarize(resolution.ConnectionString);
    LogConnectionDiagnostics(_logger, resolution.Source, hasUsername, hasSecret);

    var connectionString = resolution.ConnectionString;
    var attempt = 0;

    while (!stoppingToken.IsCancellationRequested) {
      try {
        // Prefer NpgsqlDataSource.OpenConnectionAsync — only path that works
        // with UseNpgsql(NpgsqlDataSource) since Npgsql strips credentials
        // from every public ConnectionString surface in that configuration.
        await using var conn = _dataSource is not null
          ? await _dataSource.OpenConnectionAsync(stoppingToken).ConfigureAwait(false)
          : new NpgsqlConnection(connectionString);
        // Slice 33.3 — attach the persistent dispatch handler BEFORE opening so any
        // notifications that arrive immediately after LISTEN issue are observed. The
        // handler routes by channel name into the subscription registry; the probe's
        // ephemeral handler (slice 33.2) coexists alongside this one — both run, neither
        // interferes (the dispatch handler no-ops on probe channels since they're not
        // in the registry).
        conn.Notification += _dispatchNotification;
        if (_dataSource is null) {
          await conn.OpenAsync(stoppingToken).ConfigureAwait(false);
        }

        lock (_connectionGate) {
          _connection = conn;
        }

        // Slice 33.3 — clear the listened-channels tracking on each new conn. The new
        // conn carries no LISTEN state (server-side LISTENs are per-backend-connection),
        // so _syncListensAsync will issue LISTEN for every channel in the registry.
        _listenedChannels.Clear();

        // LISTEN every registered channel atomically with the connection becoming visible to
        // Subscribe(). Order is "LISTEN first, then publish IsAvailable=true" so any consumer
        // reading IsAvailable inside its OnAvailabilityChanged handler can rely on LISTENs
        // already being live.
        await _syncListensAsync(conn, stoppingToken).ConfigureAwait(false);

        // Slice 33.2 — gate IsAvailable behind a real round-trip probe rather than just
        // "connection opened." Probe failure (timeout, error) means the conn is open but
        // NOTIFYs aren't actually flowing — could be pgbouncer in tx-pooling mode, broken
        // producer SQL, or a network partition affecting NOTIFY traffic. Treat as a failure
        // and recycle the conn so the reprobe path runs after PeriodicReprobeInterval.
        var probeOk = await _runProbeAsync(conn, connectionString ?? string.Empty, stoppingToken).ConfigureAwait(false);
        if (!probeOk) {
          _setAvailable(false, "self-test probe round-trip failed");
          throw new InvalidOperationException(
            "Self-test probe failed: connection opened but pg_notify round-trip did not arrive within SelfTestTimeout.");
        }

        attempt = 0;
        _setAvailable(true, failureReason: null);
        var channelCount = _registry.AllChannels().Count;
        LogConnected(_logger, channelCount);

        // Slice 33.3 — real dispatch loop. WaitAsync blocks on the conn until either a
        // notification arrives (the _dispatchNotification handler runs synchronously and
        // delivers to subscribers), the keepalive timeout fires (issue SELECT 1), or a
        // late Subscribe/Dispose triggers a resync (issue LISTEN/UNLISTEN to match registry).
        // The shared _resyncSignal CTS is what _signalResync() cancels.
        while (!stoppingToken.IsCancellationRequested
            && conn.State == System.Data.ConnectionState.Open) {
          using var keepalive = new CancellationTokenSource(_options.ListenKeepaliveInterval, _timeProvider);
          using var resync = new CancellationTokenSource();
          Volatile.Write(ref _resyncSignal, resync);
          using var combined = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken, keepalive.Token, resync.Token);
          var resyncFired = false;
          var keepaliveFired = false;
          try {
            await conn.WaitAsync(combined.Token).ConfigureAwait(false);
            // Notification arrived (or backend message) — handler ran synchronously inside
            // WaitAsync. Loop again to wait for the next one.
          } catch (OperationCanceledException) when (combined.Token.IsCancellationRequested && !stoppingToken.IsCancellationRequested) {
            resyncFired = resync.IsCancellationRequested;
            keepaliveFired = keepalive.IsCancellationRequested;
          } finally {
            Volatile.Write(ref _resyncSignal, null);
          }

          if (resyncFired) {
            await _syncListensAsync(conn, stoppingToken).ConfigureAwait(false);
          }
          if (keepaliveFired) {
            // Verify the connection is still alive. If SELECT 1 throws, we'll fall into
            // the outer catch and reconnect.
            await using var ping = new NpgsqlCommand("SELECT 1", conn);
            _ = await ping.ExecuteScalarAsync(stoppingToken).ConfigureAwait(false);
          }
        }
      } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
        break;
      } catch (Exception ex) {
        lock (_connectionGate) {
          _connection = null;
        }
        attempt++;
        _setAvailable(false, failureReason: ex.Message);
        var delay = _computeBackoff(attempt);
        LogReconnect(_logger, ex.Message, resolution.Source, _options.ConnectionStringKey ?? "(unset)", delay.TotalSeconds);
        try {
          await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
          break;
        }
      } finally {
        lock (_connectionGate) {
          _connection = null;
        }
      }
    }

    _setAvailable(false, failureReason: "shutdown");
    LogStopped(_logger);
  }

  /// <summary>
  /// Persistent <see cref="NpgsqlConnection.Notification"/> handler. Looks up the subscribers
  /// registered for the incoming channel and invokes their callbacks synchronously. Per-
  /// subscriber exceptions are caught and logged so one bad subscriber can't poison the
  /// dispatch loop. Self-test probe channels won't match any registered subscriber and
  /// silently no-op; the probe's own ephemeral handler picks them up via the TCS.
  /// </summary>
  /// <remarks>
  /// Runs synchronously on Npgsql's WaitAsync stack — subscribers must respect the
  /// <see cref="INotifySubscription.OnNotification"/> contract (fast + non-blocking; enqueue
  /// to a worker channel for real work). A slow subscriber blocks subsequent notifications
  /// on this pod's shared connection.
  /// </remarks>
  private void _dispatchNotification(object? sender, NpgsqlNotificationEventArgs e) {
    var subscribers = _registry.Get(e.Channel);
    if (subscribers.IsEmpty) {
      return;
    }
    foreach (var subscriber in subscribers) {
      try {
        subscriber.OnNotification(e.Payload);
      } catch (Exception ex) {
        LogSubscriberCallbackFailed(_logger, e.Channel, ex);
      }
    }
  }

  private void _setAvailable(bool available, string? failureReason) {
    bool fire;
    lock (_availabilityGate) {
      // ProbeNowAsync can run concurrently with the BackgroundService loop's probe; both
      // call _setAvailable. Guard the transition so OnAvailabilityChanged fires exactly
      // once per actual change.
      fire = _isAvailable != available;
      _isAvailable = available;
      var now = _timeProvider.GetUtcNow();
      if (available) {
        _lastVerifiedAt = now;
      } else {
        _lastFailureAt = now;
        _lastFailureReason = failureReason;
      }
    }
    if (fire) {
      OnAvailabilityChanged?.Invoke(available);
    }
  }

  private TimeSpan _computeBackoff(int attempt) {
    var ms = _options.ListenReconnectInitialDelay.TotalMilliseconds
      * Math.Pow(_options.ListenReconnectBackoffMultiplier, attempt - 1);
    var capped = Math.Min(ms, _options.ListenReconnectMaxDelay.TotalMilliseconds);
    // Slice 33.2 — after FailuresBeforeFallback consecutive failures, stretch the retry
    // cadence to PeriodicReprobeInterval. The shorter ListenReconnectMaxDelay is right for
    // transient network blips; the longer PeriodicReprobeInterval is right for "the system
    // is fundamentally not working right now, check less often."
    if (attempt >= _options.FailuresBeforeFallback) {
      capped = Math.Max(capped, _options.PeriodicReprobeInterval.TotalMilliseconds);
    }
    return TimeSpan.FromMilliseconds(capped);
  }

  // Test hook — internal accessor for the registry so tests can introspect subscription state
  // without going through the public surface.
  internal NotifySubscriptionRegistry RegistryForTesting => _registry;
  internal bool IsConnectionOpenForTesting {
    get {
      lock (_connectionGate) {
        return _connection is not null && _connection.State == System.Data.ConnectionState.Open;
      }
    }
  }

  private sealed class SubscriptionHandle(PgSharedNotifyConnection owner, INotifySubscription subscription) : IDisposable {
    private int _disposed;
    public void Dispose() {
      if (Interlocked.Exchange(ref _disposed, 1) != 0) {
        return;
      }
      var wasLast = owner._registry.Remove(subscription);
      if (wasLast) {
        // Slice 33.3 — signal the dispatch loop to issue UNLISTEN. Same conn-thread-safety
        // reason as Subscribe: we can't run UNLISTEN here while the loop holds the conn.
        owner._signalResync();
      }
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "PgSharedNotifyConnection disabled by SignalingMode=Polling or DisableNotifications=true")]
  static partial void LogDisabledByMode(ILogger logger);
  [LoggerMessage(EventId = 2, Level = LogLevel.Information,
    Message = "PgSharedNotifyConnection connected; LISTENing on {ChannelCount} registered channel(s)")]
  static partial void LogConnected(ILogger logger, int channelCount);
  [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
    Message = "PgSharedNotifyConnection disconnected ({Reason}); reconnecting in {DelaySeconds}s. Resolved connection from {ResolutionSource} for key '{ConnectionStringKey}'. " +
              "If Source=PooledKeyFallback, the connection is routed through pgbouncer and LISTEN/NOTIFY round-trips will fail — configure ConnectionStrings:{ConnectionStringKey}-direct " +
              "with a direct (non-pgbouncer) Postgres connection string.")]
  static partial void LogReconnect(ILogger logger, string reason, NotificationConnectionStringResolver.ResolutionSource resolutionSource, string connectionStringKey, double delaySeconds);
  [LoggerMessage(EventId = 4, Level = LogLevel.Information,
    Message = "PgSharedNotifyConnection stopped")]
  static partial void LogStopped(ILogger logger);
  [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
    Message = "PgSharedNotifyConnection disabled — no connection string resolved; running polling-only (set WhizbangNotificationOptions.ConnectionStringKey to enable)")]
  static partial void LogDisabledNoConnection(ILogger logger);
  [LoggerMessage(EventId = 6, Level = LogLevel.Information,
    Message = "PgSharedNotifyConnection resolved connection string from {Source}")]
  static partial void LogResolvedConnection(ILogger logger, NotificationConnectionStringResolver.ResolutionSource source);
  [LoggerMessage(EventId = 7, Level = LogLevel.Debug,
    Message = "PgSharedNotifyConnection LISTEN issued for {ChannelName}")]
  static partial void LogChannelListened(ILogger logger, string channelName);
  [LoggerMessage(EventId = 8, Level = LogLevel.Debug,
    Message = "PgSharedNotifyConnection UNLISTEN issued for {ChannelName}")]
  static partial void LogChannelUnlistened(ILogger logger, string channelName);
  [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
    Message = "PgSharedNotifyConnection LISTEN failed for {ChannelName}; subscription will be issued on next reconnect")]
  static partial void LogListenFailed(ILogger logger, string channelName, Exception ex);
  [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
    Message = "PgSharedNotifyConnection UNLISTEN failed for {ChannelName}; will be left as a no-op on conn close")]
  static partial void LogUnlistenFailed(ILogger logger, string channelName, Exception ex);
  [LoggerMessage(EventId = 11, Level = LogLevel.Warning,
    Message = "PgSharedNotifyConnection subscriber callback threw on channel {Channel}; other subscribers continue")]
  static partial void LogSubscriberCallbackFailed(ILogger logger, string channel, Exception ex);

  [LoggerMessage(EventId = 12, Level = LogLevel.Information,
    Message = "PgSharedNotifyConnection connection diagnostics — Source={Source}, has user-id marker={HasUsername}, has secret marker={HasSecret}. If HasSecret=false and Azure rejects with SCRAM-SHA-256, the resolved string is missing a credential — check ConnectionStrings:<key>(-direct) config or upgrade Whizbang for the RelationalOptionsExtension fallback fix.")]
  static partial void LogConnectionDiagnostics(
    ILogger logger,
    NotificationConnectionStringResolver.ResolutionSource source,
    bool hasUsername,
    bool hasSecret);
}
