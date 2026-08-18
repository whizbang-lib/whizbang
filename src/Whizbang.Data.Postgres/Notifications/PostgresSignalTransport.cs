using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Postgres <see cref="ISignalTransport"/> (push): publishes control-plane signals as
/// <c>pg_notify(channel, wireName)</c> and receives them by <c>LISTEN</c>ing through the shared
/// notify connection, then routing the wire-name back to typed dispatch via
/// <see cref="SignalTypeRegistry"/>. Payload is <em>doorbell-not-data</em> — only the signal's
/// wire-name crosses the wire; the subscriber fetches authoritative state from the database.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Targeting.</strong> Broadcast signals go to <c>wh_signal_broadcast</c> — every instance
/// LISTENs and receives. Targeted signals go to <c>wh_work_i_&lt;instanceId&gt;</c> — one channel per
/// instance, listened to by that instance only, so only the owner wakes.
/// <see cref="SignalTarget.Streams"/> resolves owners via <c>notify_instance_owners(payload, uuid[])</c>
/// (same helper the SQL store procs use, so the routing rule is unified: pinned owner from
/// <c>wh_active_streams</c>, or deterministic partition-modulo target for unclaimed streams).
/// <see cref="SignalTarget.Instance"/> emits <c>pg_notify</c> directly to that one instance's channel.
/// </para>
/// </remarks>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public sealed partial class PostgresSignalTransport(
  IOptions<WhizbangNotificationOptions> options,
  IConfiguration configuration,
  ISharedNotifyConnection sharedConnection,
  IServiceInstanceProvider instanceProvider,
  ILogger<PostgresSignalTransport> logger,
  INotificationConnectionStringFallback? connectionStringFallback = null,
  INotificationDataSource? notificationDataSource = null,
  SignalBusLivenessState? busLiveness = null,
  TimeProvider? timeProvider = null
) : ISignalTransport {
  /// <summary>Broadcast channel every instance listens on.</summary>
  internal const string BROADCAST_CHANNEL = "wh_signal_broadcast";

  private readonly WhizbangNotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
  private readonly ISharedNotifyConnection _sharedConnection = sharedConnection ?? throw new ArgumentNullException(nameof(sharedConnection));
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly ILogger<PostgresSignalTransport> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly INotificationConnectionStringFallback? _connectionStringFallback = connectionStringFallback;
  private readonly INotificationDataSource? _notificationDataSource = notificationDataSource;
  private readonly SignalBusLivenessState? _busLiveness = busLiveness;
  private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

  private ISignalSink? _sink;
  private IDisposable? _broadcastSubscription;
  private IDisposable? _instanceSubscription;
  private Dictionary<Type, string>? _typeToWireName;
  private Dictionary<string, SignalTypeEntry>? _wireNameToEntry;

  /// <summary>The per-instance channel this transport LISTENs on for targeted signals.</summary>
  internal string InstanceChannel => $"wh_work_i_{_instanceProvider.InstanceId:D}";

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

    // LISTEN on broadcast (all instances) AND this instance's per-owner channel (targeted).
    _broadcastSubscription = _sharedConnection.Subscribe(new BroadcastSubscription(this));
    _instanceSubscription = _sharedConnection.Subscribe(new InstanceSubscription(this));
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public async ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target, CancellationToken cancellationToken = default)
    where TSignal : ISignal {
    if (_typeToWireName is null) {
      // Routing maps are built in StartAsync — this publish beat the hosted start (or the bus was
      // never started at all). Say THAT, not "not in the registry": the misleading version of
      // this message hid a fleet-wide dead doorbell route for weeks (issue #505).
      LogPublishBeforeStart(_logger, typeof(TSignal).FullName ?? typeof(TSignal).Name);
      return;
    }
    if (!_typeToWireName.TryGetValue(typeof(TSignal), out var wireName)) {
      // Genuinely not in the registry — cannot route on the wire (the type must be a discoverable ISignal).
      LogUnregisteredSignal(_logger, typeof(TSignal).FullName ?? typeof(TSignal).Name);
      return;
    }

    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    // Prefer the registered notification data source - the only path that
    // works under UseNpgsql(NpgsqlDataSource), where the resolver's fallback
    // string has had its credentials stripped by Npgsql.
    var plan = NotificationConnectionPlan.Create(_notificationDataSource, resolution);
    if (!plan.IsAvailable) {
      LogPublishSkippedNoConnection(_logger, wireName);
      return;
    }

    await using var conn = await plan.OpenAsync(cancellationToken);

    // Durable signals persist to wh_signals BEFORE the NOTIFY so a tail on any instance
    // can replay them if the NOTIFY was dropped. Best-effort signals go NOTIFY-only.
    if (TSignal.DeliveryClass == SignalDeliveryClass.Durable) {
      await _appendDurableAsync(conn, wireName, target, cancellationToken);
    }

    switch (target.Kind) {
      case SignalTargetKind.Broadcast:
        await _publishBroadcastAsync(conn, wireName, cancellationToken);
        break;
      case SignalTargetKind.Instance:
        await _publishInstanceAsync(conn, wireName, target.InstanceId, cancellationToken);
        break;
      case SignalTargetKind.Streams:
        await _publishStreamsAsync(conn, wireName, target.StreamIds, cancellationToken);
        break;
    }
  }

  private static async Task _appendDurableAsync(NpgsqlConnection conn, string wireName, SignalTarget target, CancellationToken ct) {
    // Instance-targeted signals persist the specific target; broadcast + streams-targeted
    // signals persist with target_instance_id NULL and every instance's tail delivers.
    // (Streams-targeted durable signals are unusual — the wire-side notify_instance_owners
    // routing IS the delivery for the fast path; the durable log's role for streams is
    // "if any tail is behind, deliver again." Broadcasting is the safe fallback.)
    Guid? persistedTarget = target.Kind == SignalTargetKind.Instance ? target.InstanceId : null;
    await using var cmd = new NpgsqlCommand(
      "INSERT INTO wh_signals (wire_name, target_instance_id) VALUES (@wire_name, @target_instance_id)", conn);
    cmd.Parameters.AddWithValue("wire_name", wireName);
    cmd.Parameters.Add(new NpgsqlParameter("target_instance_id", NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = (object?)persistedTarget ?? DBNull.Value,
    });
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private static async Task _publishBroadcastAsync(NpgsqlConnection conn, string wireName, CancellationToken ct) {
    await using var cmd = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", conn);
    cmd.Parameters.AddWithValue("channel", BROADCAST_CHANNEL);
    cmd.Parameters.AddWithValue("payload", wireName);
    _ = await cmd.ExecuteScalarAsync(ct);
  }

  private static async Task _publishInstanceAsync(NpgsqlConnection conn, string wireName, Guid instanceId, CancellationToken ct) {
    await using var cmd = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", conn);
    cmd.Parameters.AddWithValue("channel", $"wh_work_i_{instanceId:D}");
    cmd.Parameters.AddWithValue("payload", wireName);
    _ = await cmd.ExecuteScalarAsync(ct);
  }

  private static async Task _publishStreamsAsync(NpgsqlConnection conn, string wireName, IReadOnlyList<Guid> streamIds, CancellationToken ct) {
    // Reuse the same notify_instance_owners helper the SQL store procs already call — one NOTIFY
    // per unique owner across the input streams, with deterministic partition-modulo fallback for
    // streams not yet pinned in wh_active_streams. See migration 045_NotifyInstanceOwners.sql.
    await using var cmd = new NpgsqlCommand(
      "SELECT notify_instance_owners(@payload, @stream_ids)", conn);
    cmd.Parameters.AddWithValue("payload", wireName);
    var streamArray = new Guid[streamIds.Count];
    for (var i = 0; i < streamIds.Count; i++) {
      streamArray[i] = streamIds[i];
    }
    cmd.Parameters.Add(new NpgsqlParameter("stream_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = streamArray,
    });
    _ = await cmd.ExecuteScalarAsync(ct);
  }

  private void _onNotification(string payload) {
    // Any payload arriving here proves the wire route is alive end-to-end — stamp it for the
    // doorbell-liveness monitor before any routing decision (even unknown wire-names count).
    _busLiveness?.MarkWireSignalReceived(_time.GetUtcNow());
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
    public void OnNotification(string payload) => owner._onNotification(payload);
  }

  /// <summary>Per-transport per-instance-owned LISTEN registration on the shared connection.</summary>
  private sealed class InstanceSubscription(PostgresSignalTransport owner) : INotifySubscription {
    public string ChannelName => owner.InstanceChannel;
    public void OnNotification(string payload) => owner._onNotification(payload);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
    Message = "PostgresSignalTransport.PublishAsync skipped: no connection string resolved (wireName={WireName})")]
  static partial void LogPublishSkippedNoConnection(ILogger logger, string wireName);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "PostgresSignalTransport.PublishAsync: signal type {SignalType} is not in the SignalTypeRegistry; not routed to the wire")]
  static partial void LogUnregisteredSignal(ILogger logger, string signalType);

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
    Message = "PostgresSignalTransport: dispatch for wire-name {WireName} threw; other signals continue")]
  static partial void LogDispatchThrew(ILogger logger, string wireName, Exception ex);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
    Message = "PostgresSignalTransport.PublishAsync: transport not started — StartAsync has not run, so signal {SignalType} cannot be routed to the wire. " +
              "The signal bus is hosted by SignalBusHostedService (AddWhizbangSignalBus); if this repeats after startup, the bus was never started")]
  static partial void LogPublishBeforeStart(ILogger logger, string signalType);
}
