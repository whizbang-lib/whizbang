using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;
using Whizbang.Core.Notifications.AppSignals;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Postgres implementation of <see cref="IAppSignalChannel"/>. Uses pg_notify on
/// channels named <c>wh_app_&lt;topic&gt;</c>. Publishing opens a short-lived connection
/// (pooled is fine) and issues a <c>SELECT pg_notify(...)</c> — the notify queues for
/// delivery on the next COMMIT. Subscribing routes through the shared
/// <see cref="PgWorkNotificationListener"/> connection so we don't need a second
/// long-lived bypass-pool connection per subscriber.
/// Phase D of work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/app-signals</docs>
public sealed partial class PgAppSignalChannel(
  IOptions<WhizbangNotificationOptions> options,
  ILogger<PgAppSignalChannel> logger
) : IAppSignalChannel {
  private readonly WhizbangNotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<PgAppSignalChannel> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly ConcurrentDictionary<string, List<Func<string, CancellationToken, Task>>> _subscribers = new();

  /// <inheritdoc />
  public async Task PublishAsync(string topic, string payload, CancellationToken cancellationToken = default) {
    var channel = AppSignalTopicValidator.ToChannelName(topic);
    if (string.IsNullOrWhiteSpace(_options.DirectConnectionString)) {
      LogPublishSkippedNoConnection(_logger, channel);
      return;
    }

    await using var conn = new NpgsqlConnection(_options.DirectConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", conn);
    cmd.Parameters.AddWithValue("channel", channel);
    cmd.Parameters.AddWithValue("payload", payload ?? string.Empty);
    _ = await cmd.ExecuteScalarAsync(cancellationToken);
  }

  /// <inheritdoc />
  public IDisposable Subscribe(string topic, Func<string, CancellationToken, Task> handler) {
    ArgumentNullException.ThrowIfNull(handler);
    _ = AppSignalTopicValidator.Validate(topic);
    var list = _subscribers.GetOrAdd(topic, _ => []);
    lock (list) {
      list.Add(handler);
    }
    return new Subscription(this, topic, handler);
  }

  internal void Dispatch(string topic, string payload, CancellationToken cancellationToken) {
    if (!_subscribers.TryGetValue(topic, out var handlers)) {
      return;
    }
    Func<string, CancellationToken, Task>[] snapshot;
    lock (handlers) {
      snapshot = [.. handlers];
    }
    foreach (var h in snapshot) {
      _ = h(payload, cancellationToken);
    }
  }

  private void _unsubscribe(string topic, Func<string, CancellationToken, Task> handler) {
    if (_subscribers.TryGetValue(topic, out var handlers)) {
      lock (handlers) {
        handlers.Remove(handler);
      }
    }
  }

  private sealed class Subscription(PgAppSignalChannel parent, string topic, Func<string, CancellationToken, Task> handler) : IDisposable {
    private bool _disposed;
    public void Dispose() {
      if (_disposed) {
        return;
      }
      _disposed = true;
      parent._unsubscribe(topic, handler);
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
    Message = "PgAppSignalChannel.PublishAsync skipped: no DirectConnectionString configured (channel={Channel})")]
  static partial void LogPublishSkippedNoConnection(ILogger logger, string channel);
}
