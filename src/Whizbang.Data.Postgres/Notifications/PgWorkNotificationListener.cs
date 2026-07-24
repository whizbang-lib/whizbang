using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Listens for Postgres LISTEN/NOTIFY work signals via the per-pod shared connection
/// (<see cref="ISharedNotifyConnection"/>). Slice 33.4 — collapsed from a full
/// <c>BackgroundService</c> + own <see cref="Npgsql.NpgsqlConnection"/> + own reconnect loop
/// into a thin <see cref="INotifySubscription"/> that just parses payloads and surfaces
/// them as <see cref="OnSignal"/> events.
/// </summary>
/// <remarks>
/// <para>
/// Pre-slice-33 design: every per-pod listener inherited <c>BackgroundService</c> and owned
/// its own direct connection. With three listener types (work, commit-order, app-signal)
/// times N pods times M services times E environments, that's a lot of direct connections
/// against the Postgres <c>max_connections</c> budget. Slice 33 collapses them to a single
/// shared direct connection per pod via the <see cref="PgSharedNotifyConnection"/>
/// multiplexer. The killswitch + functionality probe live there too.
/// </para>
/// <para>
/// <strong>What stayed the same:</strong> the public surface — <see cref="IsHealthy"/>,
/// <see cref="LastSignalAt"/>, <see cref="OnSignal"/>, <see cref="OnHealthChanged"/> — so
/// existing consumers (notably <c>ClaimWorker._onSignal</c>) don't have to change.
/// <see cref="IsHealthy"/> now delegates to <see cref="INotifySignalingGate.IsAvailable"/>;
/// <see cref="OnHealthChanged"/> mirrors the gate's <see cref="INotifySignalingGate.OnAvailabilityChanged"/>.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public sealed partial class PgWorkNotificationListener : IWorkNotificationListener, INotifySubscription, IHostedService, IDisposable {
  private readonly ISharedNotifyConnection _sharedConnection;
  private readonly INotifySignalingGate _gate;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly ILogger<PgWorkNotificationListener> _logger;
  private IDisposable? _subscription;
  private DateTimeOffset? _lastSignalAt;

  /// <summary>Constructor used by DI.</summary>
  public PgWorkNotificationListener(
    ISharedNotifyConnection sharedConnection,
    INotifySignalingGate gate,
    IServiceInstanceProvider instanceProvider,
    ILogger<PgWorkNotificationListener>? logger = null) {
    _sharedConnection = sharedConnection ?? throw new ArgumentNullException(nameof(sharedConnection));
    _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _logger = logger ?? NullLogger<PgWorkNotificationListener>.Instance;
    _gate.OnAvailabilityChanged += _forwardAvailability;
  }

  /// <summary>
  /// Instance-routed channel. Producer-side <c>notify_instance_owners</c> emits on this
  /// per-instance channel so only the owning instance wakes — non-owners never receive
  /// the notification.
  /// </summary>
  public string ChannelName => $"wh_work_i_{_instanceProvider.InstanceId:D}";

  /// <inheritdoc cref="INotifySubscription.ChannelName" />
  string INotifySubscription.ChannelName => ChannelName;

  /// <inheritdoc />
  public bool IsHealthy => _gate.IsAvailable;
  /// <inheritdoc />
  public DateTimeOffset? LastSignalAt => _lastSignalAt;
  /// <inheritdoc />
  public event Action<WorkSignalCategory>? OnSignal;
  /// <inheritdoc />
  public event Action<bool>? OnHealthChanged;

  void INotifySubscription.OnNotification(string payload) {
    _lastSignalAt = DateTimeOffset.UtcNow;
    var category = payload switch {
      "outbox" => (WorkSignalCategory?)WorkSignalCategory.Outbox,
      "inbox" => WorkSignalCategory.Inbox,
      "perspective" => WorkSignalCategory.Perspective,
      // v0.502 slice B.3 — emitted by cleanup_stale_instances after releasing leases
      // owned by a dead pod. Live pods react by running claim_orphaned_*.
      "orphan" => WorkSignalCategory.OrphanRedistribute,
      // v0.681 slice 7c — emitted by the AFTER INSERT trigger on wh_dead_letters.
      // DeadLetterRecoveryWorker wakes to scan for rows whose recovery policy elapsed.
      "deadletter" => WorkSignalCategory.DeadLetterReady,
      _ => null,
    };
    if (category is { } cat) {
      OnSignal?.Invoke(cat);
    } else {
      LogUnknownPayload(_logger, payload);
    }
  }

  Task IHostedService.StartAsync(CancellationToken cancellationToken) {
    _subscription = _sharedConnection.Subscribe(this);
    LogSubscribed(_logger, ChannelName);
    return Task.CompletedTask;
  }

  Task IHostedService.StopAsync(CancellationToken cancellationToken) {
    _subscription?.Dispose();
    _subscription = null;
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public void Dispose() {
    _gate.OnAvailabilityChanged -= _forwardAvailability;
    _subscription?.Dispose();
    _subscription = null;
  }

  private void _forwardAvailability(bool available) => OnHealthChanged?.Invoke(available);

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "PgWorkNotificationListener subscribed to channel {Channel}")]
  static partial void LogSubscribed(ILogger logger, string channel);

  [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
    Message = "PgWorkNotificationListener received unknown payload '{Payload}' — ignored")]
  static partial void LogUnknownPayload(ILogger logger, string payload);
}
