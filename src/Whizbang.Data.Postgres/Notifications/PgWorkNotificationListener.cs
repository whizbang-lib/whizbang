using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Postgres implementation of <see cref="IWorkNotificationListener"/>. Opens a long-lived
/// direct connection (bypasses pgbouncer), issues <c>LISTEN wh_work</c>, and surfaces
/// each delivered notification as an <see cref="OnSignal"/> event. Reconnects with
/// exponential backoff (capped at <c>ListenReconnectMaxDelay</c>) on disconnect.
/// Phase D of work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public sealed partial class PgWorkNotificationListener(
  IOptions<WhizbangNotificationOptions> options,
  IConfiguration configuration,
  IServiceInstanceProvider instanceProvider,
  ILogger<PgWorkNotificationListener> logger,
  INotificationConnectionStringFallback? connectionStringFallback = null
) : BackgroundService, IWorkNotificationListener {
  private readonly WhizbangNotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly ILogger<PgWorkNotificationListener> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly INotificationConnectionStringFallback? _connectionStringFallback = connectionStringFallback;
  private bool _isHealthy;
  private DateTimeOffset? _lastSignalAt;

  // Slice 27: instance-routed channel. Each instance LISTENs on exactly one channel
  // — its own — so producer-side NOTIFYs from notify_instance_owners target only
  // the owning instance and non-owners never wake.
  private string _channelName => $"wh_work_i_{_instanceProvider.InstanceId}";

  /// <inheritdoc />
  public bool IsHealthy => _isHealthy;
  /// <inheritdoc />
  public DateTimeOffset? LastSignalAt => _lastSignalAt;
  /// <inheritdoc />
  public event Action<WorkSignalCategory>? OnSignal;
  /// <inheritdoc />
  public event Action<bool>? OnHealthChanged;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    // Forced polling-only path. DisableNotifications stays as the legacy synonym for
    // SignalingMode.Polling.
    if (_options.DisableNotifications || _options.SignalingMode == WorkSignalingMode.Polling) {
      LogDisabledByMode(_logger);
      return;
    }

    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback);
    if (resolution.ConnectionString is null) {
      if (_options.SignalingMode == WorkSignalingMode.ListenNotify) {
        // Fail-fast: production expected NOTIFY but config didn't provide a connection.
        throw new InvalidOperationException(
          "WorkSignalingMode.ListenNotify is set but no direct connection string could be resolved. " +
          "Configure WhizbangNotificationOptions.ConnectionStringKey (and provide " +
          "ConnectionStrings:<key>-direct or ConnectionStrings:<key>) or set DirectConnectionString.");
      }
      // Auto mode: fall back to polling-only.
      LogDisabledNoConnection(_logger);
      return;
    }

    LogResolvedConnection(_logger, resolution.Source);
    var connectionString = resolution.ConnectionString;
    LogStarted(_logger);
    var attempt = 0;

    while (!stoppingToken.IsCancellationRequested) {
      try {
        await using var conn = new NpgsqlConnection(connectionString);
        conn.Notification += _onNotification;
        await conn.OpenAsync(stoppingToken);

        // Identifier-quoted so the {GUID} suffix can include hyphens safely.
        await using (var listenCmd = new NpgsqlCommand($"LISTEN \"{_channelName}\"", conn)) {
          await listenCmd.ExecuteNonQueryAsync(stoppingToken);
        }

        _setHealthy(true);
        attempt = 0;
        LogConnected(_logger, _channelName);

        while (!stoppingToken.IsCancellationRequested) {
          using var keepalive = new CancellationTokenSource(_options.ListenKeepaliveInterval);
          using var combined = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, keepalive.Token);
          try {
            await conn.WaitAsync(combined.Token);
            // Notifications fire via _onNotification handler.
          } catch (OperationCanceledException) when (keepalive.IsCancellationRequested && !stoppingToken.IsCancellationRequested) {
            // Keepalive timeout — send SELECT 1 to prove connection liveness.
            await using var pingCmd = new NpgsqlCommand("SELECT 1", conn);
            _ = await pingCmd.ExecuteScalarAsync(stoppingToken);
          }
        }
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        _setHealthy(false);
        attempt++;
        var delay = _computeBackoff(attempt);
        LogReconnect(_logger, ex.Message, delay.TotalSeconds);
        try { await Task.Delay(delay, stoppingToken); } catch (OperationCanceledException) { break; }
      }
    }

    _setHealthy(false);
    LogStopped(_logger);
  }

  private void _onNotification(object? sender, NpgsqlNotificationEventArgs e) {
    _lastSignalAt = DateTimeOffset.UtcNow;
    if (!string.Equals(e.Channel, _channelName, StringComparison.Ordinal)) {
      return;
    }
    var category = e.Payload switch {
      "outbox" => (WorkSignalCategory?)WorkSignalCategory.Outbox,
      "inbox" => WorkSignalCategory.Inbox,
      "perspective" => WorkSignalCategory.Perspective,
      _ => null
    };
    if (category is { } cat) {
      OnSignal?.Invoke(cat);
    }
  }

  private void _setHealthy(bool healthy) {
    if (_isHealthy == healthy) {
      return;
    }
    _isHealthy = healthy;
    OnHealthChanged?.Invoke(healthy);
  }

  private TimeSpan _computeBackoff(int attempt) {
    var ms = _options.ListenReconnectInitialDelay.TotalMilliseconds *
             Math.Pow(_options.ListenReconnectBackoffMultiplier, attempt - 1);
    return TimeSpan.FromMilliseconds(Math.Min(ms, _options.ListenReconnectMaxDelay.TotalMilliseconds));
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "PgWorkNotificationListener disabled by SignalingMode=Polling or DisableNotifications=true — running polling-only")]
  static partial void LogDisabledByMode(ILogger logger);
  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "PgWorkNotificationListener started")]
  static partial void LogStarted(ILogger logger);
  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "PgWorkNotificationListener connected and LISTENing on {Channel}")]
  static partial void LogConnected(ILogger logger, string channel);
  [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "PgWorkNotificationListener disconnected ({Reason}); reconnecting in {DelaySeconds}s")]
  static partial void LogReconnect(ILogger logger, string reason, double delaySeconds);
  [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "PgWorkNotificationListener stopped")]
  static partial void LogStopped(ILogger logger);
  [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "PgWorkNotificationListener disabled — no connection string resolved; running polling-only (set WhizbangNotificationOptions.ConnectionStringKey to enable)")]
  static partial void LogDisabledNoConnection(ILogger logger);
  [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "PgWorkNotificationListener resolved connection string from {Source}")]
  static partial void LogResolvedConnection(ILogger logger, NotificationConnectionStringResolver.ResolutionSource source);
}
