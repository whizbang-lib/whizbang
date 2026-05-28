using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;
using Whizbang.Core.Notifications.AppSignals;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Postgres implementation of <see cref="IAppSignalChannel"/>. Uses pg_notify on
/// channels named <c>wh_app_&lt;topic&gt;</c>. Publishing opens a short-lived connection
/// and issues <c>SELECT pg_notify(...)</c>. Subscribing routes through
/// <see cref="ISharedNotifyConnection"/> (slice 33.5) — one shared LISTEN connection
/// across all per-pod consumers, with one <see cref="INotifySubscription"/> per topic
/// fanning out to multiple in-memory handlers.
/// </summary>
/// <docs>fundamentals/work-coordinator/app-signals</docs>
public sealed partial class PgAppSignalChannel(
  IOptions<WhizbangNotificationOptions> options,
  IConfiguration configuration,
  ISharedNotifyConnection sharedConnection,
  ILogger<PgAppSignalChannel> logger,
  INotificationConnectionStringFallback? connectionStringFallback = null
) : IAppSignalChannel {
  private readonly WhizbangNotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
  private readonly ISharedNotifyConnection _sharedConnection = sharedConnection ?? throw new ArgumentNullException(nameof(sharedConnection));
  private readonly ILogger<PgAppSignalChannel> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly INotificationConnectionStringFallback? _connectionStringFallback = connectionStringFallback;
  private readonly ConcurrentDictionary<string, TopicSubscription> _byTopic = new(StringComparer.Ordinal);

  /// <inheritdoc />
  public async Task PublishAsync(string topic, string payload, CancellationToken cancellationToken = default) {
    var channel = AppSignalTopicValidator.ToChannelName(topic);
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback);
    if (resolution.ConnectionString is null) {
      LogPublishSkippedNoConnection(_logger, channel);
      return;
    }

    await using var conn = new NpgsqlConnection(resolution.ConnectionString);
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

    // Lazily create one TopicSubscription per topic (which registers a single
    // INotifySubscription against the shared conn). Subsequent handlers on the same
    // topic share the underlying LISTEN — fan-out happens in memory.
    var topicSub = _byTopic.GetOrAdd(topic, t => new TopicSubscription(this, t));
    topicSub.AddHandler(handler);
    return new HandlerSubscription(this, topic, handler);
  }

  private void _removeHandler(string topic, Func<string, CancellationToken, Task> handler) {
    if (!_byTopic.TryGetValue(topic, out var topicSub)) {
      return;
    }
    var stillHasHandlers = topicSub.RemoveHandler(handler);
    // Last handler for this topic — drop the underlying LISTEN by disposing the
    // shared-conn subscription. The next Subscribe for this topic will re-create
    // a fresh TopicSubscription.
    if (!stillHasHandlers && _byTopic.TryRemove(topic, out var removed)) {
      removed.Dispose();
    }
  }

  /// <summary>
  /// Per-topic INotifySubscription. Registers once with the shared connection on first
  /// handler add; on notification, fans out the payload to every handler in the topic's
  /// list. Disposing it removes the shared-conn LISTEN.
  /// </summary>
  private sealed class TopicSubscription : INotifySubscription, IDisposable {
    private readonly PgAppSignalChannel _owner;
    private readonly string _topic;
    private readonly List<Func<string, CancellationToken, Task>> _handlers = [];
    private readonly Lock _gate = new();
    private IDisposable? _sharedSubscriptionHandle;

    public TopicSubscription(PgAppSignalChannel owner, string topic) {
      _owner = owner;
      _topic = topic;
      ChannelName = AppSignalTopicValidator.ToChannelName(topic);
      _sharedSubscriptionHandle = owner._sharedConnection.Subscribe(this);
    }

    public string ChannelName { get; }

    public void OnNotification(string payload) {
      Func<string, CancellationToken, Task>[] snapshot;
      lock (_gate) {
        snapshot = [.. _handlers];
      }
      foreach (var h in snapshot) {
        try {
          _ = h(payload, CancellationToken.None);
        } catch (Exception ex) {
          _owner._logHandlerThrew(_topic, ex);
        }
      }
    }

    public void AddHandler(Func<string, CancellationToken, Task> handler) {
      lock (_gate) {
        _handlers.Add(handler);
      }
    }

    public bool RemoveHandler(Func<string, CancellationToken, Task> handler) {
      lock (_gate) {
        _handlers.Remove(handler);
        return _handlers.Count > 0;
      }
    }

    public void Dispose() {
      _sharedSubscriptionHandle?.Dispose();
      _sharedSubscriptionHandle = null;
    }
  }

  private sealed class HandlerSubscription(PgAppSignalChannel parent, string topic, Func<string, CancellationToken, Task> handler) : IDisposable {
    private bool _disposed;
    public void Dispose() {
      if (_disposed) {
        return;
      }
      _disposed = true;
      parent._removeHandler(topic, handler);
    }
  }

  private void _logHandlerThrew(string topic, Exception ex) => LogHandlerThrew(_logger, topic, ex);

  [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
    Message = "PgAppSignalChannel.PublishAsync skipped: no DirectConnectionString configured (channel={Channel})")]
  static partial void LogPublishSkippedNoConnection(ILogger logger, string channel);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "PgAppSignalChannel: handler for topic {Topic} threw; other handlers continue")]
  static partial void LogHandlerThrew(ILogger logger, string topic, Exception ex);
}
