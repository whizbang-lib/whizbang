using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;

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
  ILogger<PgWorkNotificationListener> logger
) : BackgroundService, IWorkNotificationListener {
  private readonly WhizbangNotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<PgWorkNotificationListener> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private bool _isHealthy;
  private DateTimeOffset? _lastSignalAt;

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
    if (string.IsNullOrWhiteSpace(_options.DirectConnectionString) || _options.DisableNotifications) {
      LogDisabled(_logger);
      return;
    }

    LogStarted(_logger);
    var attempt = 0;

    while (!stoppingToken.IsCancellationRequested) {
      try {
        await using var conn = new NpgsqlConnection(_options.DirectConnectionString);
        conn.Notification += _onNotification;
        await conn.OpenAsync(stoppingToken);

        await using (var listenCmd = new NpgsqlCommand("LISTEN wh_work", conn)) {
          await listenCmd.ExecuteNonQueryAsync(stoppingToken);
        }

        _setHealthy(true);
        attempt = 0;
        LogConnected(_logger);

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
    if (!string.Equals(e.Channel, "wh_work", StringComparison.Ordinal)) {
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

  [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "PgWorkNotificationListener disabled: no DirectConnectionString or DisableNotifications=true")]
  static partial void LogDisabled(ILogger logger);
  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "PgWorkNotificationListener started")]
  static partial void LogStarted(ILogger logger);
  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "PgWorkNotificationListener connected and LISTENing on wh_work")]
  static partial void LogConnected(ILogger logger);
  [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "PgWorkNotificationListener disconnected ({Reason}); reconnecting in {DelaySeconds}s")]
  static partial void LogReconnect(ILogger logger, string reason, double delaySeconds);
  [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "PgWorkNotificationListener stopped")]
  static partial void LogStopped(ILogger logger);
}
