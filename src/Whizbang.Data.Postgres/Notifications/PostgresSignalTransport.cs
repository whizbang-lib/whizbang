using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;
using Whizbang.Core.Signals;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Postgres <see cref="ISignalTransport"/> (push): publishes control-plane signals as
/// <c>pg_notify(channel, wireName)</c> and receives them by <c>LISTEN</c>ing through the shared
/// notify connection, then routing the wire-name back to typed dispatch via
/// <see cref="SignalTypeRegistry"/>. Payload is <em>doorbell-not-data</em> — only the signal's
/// wire-name crosses the wire; the subscriber fetches authoritative state from the database.
/// This increment handles <see cref="SignalTargeting.Broadcast"/>; targeted routing (owning-instance
/// channels via <c>notify_instance_owners</c>) is a follow-on.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public sealed partial class PostgresSignalTransport(
  IOptions<WhizbangNotificationOptions> options,
  IConfiguration configuration,
  ISharedNotifyConnection sharedConnection,
  ILogger<PostgresSignalTransport> logger,
  INotificationConnectionStringFallback? connectionStringFallback = null
) : ISignalTransport {
  /// <summary>Broadcast channel every instance listens on.</summary>
  internal const string BROADCAST_CHANNEL = "wh_signal_broadcast";

  private readonly WhizbangNotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
  private readonly ISharedNotifyConnection _sharedConnection = sharedConnection ?? throw new ArgumentNullException(nameof(sharedConnection));
  private readonly ILogger<PostgresSignalTransport> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly INotificationConnectionStringFallback? _connectionStringFallback = connectionStringFallback;

  private ISignalSink? _sink;
  private IDisposable? _broadcastSubscription;
  private Dictionary<Type, string>? _typeToWireName;
  private Dictionary<string, SignalTypeEntry>? _wireNameToEntry;

  /// <inheritdoc />
  public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(sink);
    _sink = sink;

    // Build routing maps from the combined cross-assembly registry (populated by module initializers).
    var all = SignalTypeRegistry.GetAll();
    var typeMap = new Dictionary<Type, string>();
    var wireMap = new Dictionary<string, SignalTypeEntry>(StringComparer.Ordinal);
    foreach (var entry in all) {
      typeMap[entry.SignalType] = entry.WireName;
      wireMap[entry.WireName] = entry;
    }
    _typeToWireName = typeMap;
    _wireNameToEntry = wireMap;

    // LISTEN on the broadcast channel through the one shared per-pod connection.
    _broadcastSubscription = _sharedConnection.Subscribe(new BroadcastSubscription(this));
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public async ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target, CancellationToken cancellationToken = default)
    where TSignal : ISignal {
    if (TSignal.Targeting != SignalTargeting.Broadcast) {
      throw new NotSupportedException(
        $"PostgresSignalTransport currently supports broadcast signals only; '{typeof(TSignal).FullName}' is targeted.");
    }
    _ = target;   // Broadcast path — target has been validated as Broadcast by the bus.
    if (_typeToWireName is null || !_typeToWireName.TryGetValue(typeof(TSignal), out var wireName)) {
      // Not in the registry — cannot route on the wire (the type must be a discoverable ISignal).
      LogUnregisteredSignal(_logger, typeof(TSignal).FullName ?? typeof(TSignal).Name);
      return;
    }

    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback);
    if (resolution.ConnectionString is null) {
      LogPublishSkippedNoConnection(_logger, BROADCAST_CHANNEL);
      return;
    }

    await using var conn = new NpgsqlConnection(resolution.ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", conn);
    cmd.Parameters.AddWithValue("channel", BROADCAST_CHANNEL);
    cmd.Parameters.AddWithValue("payload", wireName);
    _ = await cmd.ExecuteScalarAsync(cancellationToken);
  }

  private void _onBroadcast(string payload) {
    var sink = _sink;
    var map = _wireNameToEntry;
    if (sink is null || map is null) {
      return;
    }
    if (!map.TryGetValue(payload, out var entry)) {
      return;  // unknown wire-name (e.g. a signal type not present in this host) — ignore
    }
    // Enqueue-and-return: dispatch runs on the shared connection's receive loop, so handlers must
    // be non-blocking. Complete synchronously where possible; otherwise observe off the loop.
    try {
      var pending = entry.Dispatch(sink, CancellationToken.None);
      if (!pending.IsCompletedSuccessfully) {
        _ = _observeAsync(pending, payload);
      }
    } catch (Exception ex) {
      LogDispatchThrew(_logger, payload, ex);
    }
  }

  private async Task _observeAsync(ValueTask pending, string wireName) {
    try {
      await pending.ConfigureAwait(false);
    } catch (Exception ex) {
      LogDispatchThrew(_logger, wireName, ex);
    }
  }

  /// <summary>Per-transport broadcast LISTEN registration on the shared connection.</summary>
  private sealed class BroadcastSubscription(PostgresSignalTransport owner) : INotifySubscription {
    public string ChannelName => BROADCAST_CHANNEL;
    public void OnNotification(string payload) => owner._onBroadcast(payload);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
    Message = "PostgresSignalTransport.PublishAsync skipped: no connection string resolved (channel={Channel})")]
  static partial void LogPublishSkippedNoConnection(ILogger logger, string channel);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "PostgresSignalTransport.PublishAsync: signal type {SignalType} is not in the SignalTypeRegistry; not routed to the wire")]
  static partial void LogUnregisteredSignal(ILogger logger, string signalType);

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
    Message = "PostgresSignalTransport: dispatch for wire-name {WireName} threw; other signals continue")]
  static partial void LogDispatchThrew(ILogger logger, string wireName, Exception ex);
}
