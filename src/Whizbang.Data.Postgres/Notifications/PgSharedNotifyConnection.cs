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
public sealed partial class PgSharedNotifyConnection : BackgroundService, ISharedNotifyConnection, INotifySignalingGate {
  private readonly WhizbangNotificationOptions _options;
  private readonly IConfiguration _configuration;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly INotificationConnectionStringFallback? _connectionStringFallback;
  private readonly ILogger<PgSharedNotifyConnection> _logger;
  private readonly TimeProvider _timeProvider;
  private readonly NotifySubscriptionRegistry _registry = new();
  private readonly Lock _connectionGate = new();
  private readonly Lock _availabilityGate = new();
  private NpgsqlConnection? _connection;
  private bool _isAvailable;
  private DateTimeOffset? _lastVerifiedAt;
  private DateTimeOffset? _lastFailureAt;
  private string? _lastFailureReason;

  /// <summary>Constructor used by DI.</summary>
  public PgSharedNotifyConnection(
    IOptions<WhizbangNotificationOptions> options,
    IConfiguration configuration,
    IServiceInstanceProvider instanceProvider,
    ILogger<PgSharedNotifyConnection>? logger = null,
    INotificationConnectionStringFallback? connectionStringFallback = null,
    TimeProvider? timeProvider = null) {
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _logger = logger ?? NullLogger<PgSharedNotifyConnection>.Instance;
    _connectionStringFallback = connectionStringFallback;
    _timeProvider = timeProvider ?? TimeProvider.System;
  }

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
    if (resolution.ConnectionString is null) {
      _setAvailable(false, "no connection string resolvable");
      return false;
    }
    try {
      await using var conn = new NpgsqlConnection(resolution.ConnectionString);
      await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
      // Pass the original resolved string (not conn.ConnectionString — Npgsql strips the
      // password from that after Open for security, so a second connection built from it
      // can't authenticate).
      var ok = await _runProbeAsync(conn, resolution.ConnectionString, cancellationToken).ConfigureAwait(false);
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
      // password after Open and would fail SASL/SCRAM auth.
      await using (var notifyConn = new NpgsqlConnection(connectionString)) {
        await notifyConn.OpenAsync(ct).ConfigureAwait(false);
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
      _ = _issueListenIfConnectedAsync(subscription.ChannelName);
    }
    return new SubscriptionHandle(this, subscription);
  }

  private async Task _issueListenIfConnectedAsync(string channelName) {
    NpgsqlConnection? conn;
    lock (_connectionGate) {
      conn = _connection;
    }
    if (conn is null || conn.State != System.Data.ConnectionState.Open) {
      // Will be LISTENed during the next ExecuteAsync open cycle. No-op here.
      return;
    }
    try {
      await using var cmd = new NpgsqlCommand($"LISTEN \"{channelName}\"", conn);
      await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
      LogChannelListened(_logger, channelName);
    } catch (Exception ex) {
      LogListenFailed(_logger, channelName, ex);
    }
  }

  private async Task _unlistenIfConnectedAsync(string channelName) {
    NpgsqlConnection? conn;
    lock (_connectionGate) {
      conn = _connection;
    }
    if (conn is null || conn.State != System.Data.ConnectionState.Open) {
      return;
    }
    try {
      await using var cmd = new NpgsqlCommand($"UNLISTEN \"{channelName}\"", conn);
      await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
      LogChannelUnlistened(_logger, channelName);
    } catch (Exception ex) {
      LogUnlistenFailed(_logger, channelName, ex);
    }
  }

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (_options.DisableNotifications || _options.SignalingMode == WorkSignalingMode.Polling) {
      LogDisabledByMode(_logger);
      return;
    }

    var resolution = NotificationConnectionStringResolver.Resolve(
      _options, _configuration, _connectionStringFallback);
    if (resolution.ConnectionString is null) {
      if (_options.SignalingMode == WorkSignalingMode.ListenNotify) {
        throw new InvalidOperationException(
          "WorkSignalingMode.ListenNotify is set but no direct connection string could be resolved. " +
          "Configure WhizbangNotificationOptions.ConnectionStringKey or set DirectConnectionString.");
      }
      LogDisabledNoConnection(_logger);
      return;
    }

    LogResolvedConnection(_logger, resolution.Source);
    var connectionString = resolution.ConnectionString;
    var attempt = 0;

    while (!stoppingToken.IsCancellationRequested) {
      try {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(stoppingToken).ConfigureAwait(false);

        lock (_connectionGate) {
          _connection = conn;
        }

        // LISTEN every registered channel atomically with the connection becoming visible to
        // Subscribe(). Order is "LISTEN first, then publish IsAvailable=true" so any consumer
        // reading IsAvailable inside its OnAvailabilityChanged handler can rely on LISTENs
        // already being live.
        foreach (var channel in _registry.AllChannels()) {
          await using var cmd = new NpgsqlCommand($"LISTEN \"{channel}\"", conn);
          await cmd.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
        }

        // Slice 33.2 — gate IsAvailable behind a real round-trip probe rather than just
        // "connection opened." Probe failure (timeout, error) means the conn is open but
        // NOTIFYs aren't actually flowing — could be pgbouncer in tx-pooling mode, broken
        // producer SQL, or a network partition affecting NOTIFY traffic. Treat as a failure
        // and recycle the conn so the reprobe path runs after PeriodicReprobeInterval.
        var probeOk = await _runProbeAsync(conn, connectionString, stoppingToken).ConfigureAwait(false);
        if (!probeOk) {
          _setAvailable(false, "self-test probe round-trip failed");
          throw new InvalidOperationException(
            "Self-test probe failed: connection opened but pg_notify round-trip did not arrive within SelfTestTimeout.");
        }

        attempt = 0;
        _setAvailable(true, failureReason: null);
        var channelCount = _registry.AllChannels().Count;
        LogConnected(_logger, channelCount);

        // Slice 33.3 will replace this Delay with a Notification + WaitAsync dispatch loop.
        // For 33.1 we just hold the connection open so Subscribe-after-connect can issue
        // LISTEN against a live conn.
        while (!stoppingToken.IsCancellationRequested
            && conn.State == System.Data.ConnectionState.Open) {
          await Task.Delay(_options.ListenKeepaliveInterval, stoppingToken).ConfigureAwait(false);
          // Liveness check — slice 33.2's probe replaces this with a real round-trip.
          await using var ping = new NpgsqlCommand("SELECT 1", conn);
          _ = await ping.ExecuteScalarAsync(stoppingToken).ConfigureAwait(false);
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
        LogReconnect(_logger, ex.Message, delay.TotalSeconds);
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
        _ = owner._unlistenIfConnectedAsync(subscription.ChannelName);
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
    Message = "PgSharedNotifyConnection disconnected ({Reason}); reconnecting in {DelaySeconds}s")]
  static partial void LogReconnect(ILogger logger, string reason, double delaySeconds);
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
}
