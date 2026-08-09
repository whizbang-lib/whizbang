using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Postgres-backed <see cref="BasePollSignalSource{TSignal}"/> for the work-available doorbell
/// signals. Concrete subclasses supply a detection query against the corresponding work table
/// (<c>wh_outbox</c>, <c>wh_inbox</c>, <c>wh_perspective_events</c>) and this base handles the
/// connection resolution, parameter binding, and safety-net logging.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Doorbell semantics.</strong> The subclass's SQL is an <c>EXISTS</c>-style detection
/// probe scoped to this pod's <see cref="IServiceInstanceProvider.InstanceId"/> — cheap, indexed,
/// and idempotent. It returns <c>true</c> when the target table has any unclaimed row assigned to
/// this instance; the subscriber (e.g. <c>ClaimWorker</c>) then runs the authoritative claim path.
/// A duplicate signal is harmless — the subscriber always fetches state from the DB.
/// </para>
/// <para>
/// <strong>NOTIFY-down backstop.</strong> When the push transport is healthy the poll runs at a
/// relaxed interval (correctness backstop); if NOTIFY is unavailable the pull source carries the
/// full wake load without any code change on the subscriber side.
/// </para>
/// </remarks>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public abstract partial class PgWorkAvailablePollSourceBase<TSignal> : BasePollSignalSource<TSignal>
  where TSignal : ISignal, new() {
  private readonly WhizbangNotificationOptions _options;
  private readonly IConfiguration _configuration;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly ILogger _logger;
  private readonly INotificationConnectionStringFallback? _connectionStringFallback;
  private readonly INotificationDataSource? _notificationDataSource;
  private readonly INotifySignalingGate? _signalingGate;
  private readonly TimeSpan _relaxedInterval;

  /// <summary>Tight cadence used while the NOTIFY push transport is unavailable.</summary>
  private static readonly TimeSpan _tightInterval = TimeSpan.FromMilliseconds(500);

  /// <summary>Constructor.</summary>
  protected PgWorkAvailablePollSourceBase(
    TimeProvider clock,
    TimeSpan interval,
    IOptions<WhizbangNotificationOptions> options,
    IConfiguration configuration,
    IServiceInstanceProvider instanceProvider,
    ILogger logger,
    INotificationConnectionStringFallback? connectionStringFallback = null,
    INotifySignalingGate? signalingGate = null,
    INotificationDataSource? notificationDataSource = null
  ) : base(clock, interval) {
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _connectionStringFallback = connectionStringFallback;
    _notificationDataSource = notificationDataSource;
    _signalingGate = signalingGate;
    _relaxedInterval = interval;

    // Adaptive: when the NOTIFY push transport goes unavailable, tighten the poll cadence so
    // this source can carry the wake load until push recovers. When push comes back, relax to
    // the ctor interval (5s default) — NOTIFY carries latency and we don't need to be tight.
    if (_signalingGate is not null) {
      _signalingGate.OnAvailabilityChanged += _onGateAvailabilityChanged;
    }
  }

  private void _onGateAvailabilityChanged(bool nowAvailable) {
    Reschedule(nowAvailable ? _relaxedInterval : _tightInterval);
  }

  /// <summary>
  /// SQL that returns a single boolean — <c>true</c> if any unclaimed work exists for this
  /// instance, <c>false</c> otherwise. The subclass parameter <c>@instance_id</c> is bound to
  /// <see cref="IServiceInstanceProvider.InstanceId"/> by the base class.
  /// </summary>
  protected abstract string DetectSql { get; }

  /// <inheritdoc />
  protected sealed override async ValueTask<bool> DetectAsync(CancellationToken cancellationToken) {
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    // Prefer the registered notification data source - the only path that
    // works under UseNpgsql(NpgsqlDataSource), where the resolver's fallback
    // string has had its credentials stripped by Npgsql.
    var plan = NotificationConnectionPlan.Create(_notificationDataSource, resolution);
    if (!plan.IsAvailable) {
      return false;   // no connection available — nothing to detect; NOTIFY path will drive us when the DB comes back
    }
    await using var conn = await plan.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand(DetectSql, conn);
    cmd.Parameters.AddWithValue("instance_id", _instanceProvider.InstanceId);
    var result = await cmd.ExecuteScalarAsync(cancellationToken);
    return result is bool b && b;
  }

  /// <inheritdoc />
  protected override void OnTickError(Exception ex) {
    // Log at Warning — a single failed poll is not fatal (the NOTIFY push path or the next tick
    // will retry), but sustained failures should be visible in observability.
    LogDetectFailed(_logger, typeof(TSignal).FullName ?? typeof(TSignal).Name, ex);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Work-available poll source {SignalType} detection failed; will retry on next tick")]
  static partial void LogDetectFailed(ILogger logger, string signalType, Exception ex);
}
