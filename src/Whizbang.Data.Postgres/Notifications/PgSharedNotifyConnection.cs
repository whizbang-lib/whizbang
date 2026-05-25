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
  private readonly NotifySubscriptionRegistry _registry = new();
  private readonly Lock _connectionGate = new();
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
    INotificationConnectionStringFallback? connectionStringFallback = null) {
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _logger = logger ?? NullLogger<PgSharedNotifyConnection>.Instance;
    _connectionStringFallback = connectionStringFallback;
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
  /// Slice 33.1 — without the probe, "ProbeNow" is a no-op that reports the current
  /// connection-open state. The real probe arrives in slice 33.2.
  /// </remarks>
  public Task<bool> ProbeNowAsync(CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult(_isAvailable);
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
    if (_isAvailable == available) {
      return;
    }
    _isAvailable = available;
    if (available) {
      _lastVerifiedAt = DateTimeOffset.UtcNow;
    } else {
      _lastFailureAt = DateTimeOffset.UtcNow;
      _lastFailureReason = failureReason;
    }
    OnAvailabilityChanged?.Invoke(available);
  }

  private TimeSpan _computeBackoff(int attempt) {
    var ms = _options.ListenReconnectInitialDelay.TotalMilliseconds
      * Math.Pow(_options.ListenReconnectBackoffMultiplier, attempt - 1);
    return TimeSpan.FromMilliseconds(Math.Min(ms, _options.ListenReconnectMaxDelay.TotalMilliseconds));
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
