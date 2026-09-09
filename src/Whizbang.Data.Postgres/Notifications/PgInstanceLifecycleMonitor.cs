using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Periodically scans <c>wh_service_instances</c> for pods that are no longer alive and, for each
/// newly-detected death, emits a durable <see cref="InstanceDiedSignal"/> on the bus. The signal
/// drives orphan takeover on live pods. Broadcast + Durable delivery means the fast-path NOTIFY
/// reaches subscribers instantly and the durable log carries the signal across NOTIFY drops so
/// failover is never silently lost.
/// </summary>
/// <remarks>
/// <para>
/// Liveness is the two-signal predicate the SQL reap already uses (<c>is_instance_alive</c>,
/// migration 055): an instance is alive while its session alive-lock is held OR its heartbeat row
/// is within the stale threshold. The threshold is not a constant; it derives from the heartbeat
/// writer's own cadence through <see cref="HeartbeatLivenessThreshold"/>, so a writer on its slow
/// cadence (lock held) or a beat delayed by a stalled database can never read as a death. The
/// earlier form of this monitor compared the bare timestamp against 30 s, the same as the fast
/// cadence, and announced a live, heartbeating instance dead under load.
/// </para>
/// <para>
/// The monitor tracks the deaths it has already announced in-process so it does not republish the
/// same InstanceDied every tick. An announcement is reversible: if a later tick finds the instance
/// alive again, the monitor retracts by publishing <see cref="InstanceJoinedSignal"/>, so peers
/// that reacted to the death re-read topology and stop treating the instance's leases as orphaned.
/// </para>
/// <para>
/// The tick is adaptive: relaxed while every instance is comfortably fresh, fast once any row's age
/// passes half the threshold (a death may be imminent and takeover latency is bounded by the tick).
/// </para>
/// </remarks>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
/// <tests>tests/Whizbang.Core.Tests/Notifications/PgNotificationStackStartupGateTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PgInstanceLifecycleMonitorIntegrationTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PgInstanceLifecycleMonitorUnitTests.cs</tests>
#pragma warning disable S107 // DI-injection constructor: every parameter is a registered service or an optional seam, and a parameter object would only move the list (same reasoning as Dispatcher)
public sealed partial class PgInstanceLifecycleMonitor(
  IOptions<WhizbangNotificationOptions> options,
  IConfiguration configuration,
  ISignalBus signalBus,
  ILogger<PgInstanceLifecycleMonitor> logger,
  INotificationConnectionStringFallback? connectionStringFallback = null,
  INotificationDataSource? notificationDataSource = null,
  Whizbang.Core.Workers.ISchemaReadyGate? schemaReadyGate = null,
  IOptions<HeartbeatWorkerOptions>? heartbeatOptions = null,
  TimeProvider? timeProvider = null,
  InstanceLivenessMetrics? metrics = null,
  ProbeCadenceMetrics? probeMetrics = null
) : BackgroundService {
#pragma warning restore S107
  private readonly WhizbangNotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
  private readonly ISignalBus _signalBus = signalBus ?? throw new ArgumentNullException(nameof(signalBus));
  private readonly ILogger<PgInstanceLifecycleMonitor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly INotificationConnectionStringFallback? _connectionStringFallback = connectionStringFallback;
  private readonly INotificationDataSource? _notificationDataSource = notificationDataSource;
  private readonly Whizbang.Core.Workers.ISchemaReadyGate? _schemaReadyGate = schemaReadyGate;
  private readonly HeartbeatWorkerOptions _heartbeat = heartbeatOptions?.Value ?? new HeartbeatWorkerOptions();
  private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
  private readonly InstanceLivenessMetrics? _metrics = metrics;
  private readonly ProbeCadenceMetrics? _probeMetrics = probeMetrics;

  /// <summary>The fastest the monitor ticks: while some instance is nearing the threshold.</summary>
  internal static readonly TimeSpan FastTick = TimeSpan.FromSeconds(5);

  /// <summary>The slowest the monitor ticks: while every instance is comfortably fresh.</summary>
  internal static readonly TimeSpan RelaxedTickCeiling = TimeSpan.FromSeconds(30);

  private readonly HashSet<Guid> _announcedDeaths = [];

  /// <summary>How stale a heartbeat row may be before the instance counts as dead, derived from the writer's cadence.</summary>
  public TimeSpan StaleThreshold => HeartbeatLivenessThreshold.StaleThreshold(_heartbeat);

  /// <summary>The stale threshold in whole seconds, as passed to <c>is_instance_alive</c>.</summary>
  public int StaleThresholdSeconds => HeartbeatLivenessThreshold.StaleThresholdSeconds(_heartbeat);

  /// <summary>The instances currently announced dead and not yet retracted.</summary>
  public IReadOnlyCollection<Guid> AnnouncedDeaths => _announcedDeaths;

  /// <summary>
  /// The delay before the next tick given the oldest heartbeat age seen: fast once any instance is
  /// past half the threshold, otherwise a sixth of the threshold clamped between the fast tick and
  /// the relaxed ceiling. An empty registry ticks at the relaxed cadence.
  /// </summary>
  /// <param name="oldestAge">The largest heartbeat age observed on the tick, or null when no rows.</param>
  /// <param name="threshold">The stale threshold.</param>
  /// <returns>The delay before the next tick.</returns>
  public static TimeSpan ComputeTickInterval(TimeSpan? oldestAge, TimeSpan threshold) {
    if (threshold <= TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "The threshold must be positive.");
    }
    if (oldestAge is { } age && age >= threshold / 2) {
      return FastTick;
    }
    var relaxed = threshold / 6;
    if (relaxed < FastTick) {
      return FastTick;
    }
    return relaxed > RelaxedTickCeiling ? RelaxedTickCeiling : relaxed;
  }

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    // Death detection reads wh_service_instances; hold at the schema gate so the first
    // tick never scans (or announces takeover from) a table the migration hasn't built yet.
    if (_schemaReadyGate is not null) {
      try {
        await _schemaReadyGate.WaitForReadyAsync(stoppingToken);
      } catch (OperationCanceledException) {
        return;
      }
    }

    while (!stoppingToken.IsCancellationRequested) {
      var next = ComputeTickInterval(null, StaleThreshold);
      try {
        next = await _tickOnceAsync(stoppingToken);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        LogTickFailed(_logger, ex);
      }

      try {
        await Task.Delay(next, _time, stoppingToken);
      } catch (OperationCanceledException) {
        break;
      }
    }
  }

  /// <summary>Test hook: run one detection tick without the loop.</summary>
  public Task TickForTestsAsync(CancellationToken cancellationToken) => _tickOnceAsync(cancellationToken);

  private async Task<TimeSpan> _tickOnceAsync(CancellationToken ct) {
    var threshold = StaleThreshold;
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    // Prefer the registered notification data source, the only path that
    // works under UseNpgsql(NpgsqlDataSource), where the resolver's fallback
    // string has had its credentials stripped by Npgsql.
    var plan = NotificationConnectionPlan.Create(_notificationDataSource, resolution);
    if (!plan.IsAvailable) {
      return ComputeTickInterval(null, threshold);
    }

    var (dead, alive, oldestAge) = await _readLivenessAsync(plan, threshold, ct);
    await _announceDeathsAsync(dead, threshold, ct);
    await _retractRevivalsAsync(alive, ct);

    _probeMetrics?.RecordTick(ProbeCadenceMetrics.PROBE_INSTANCE_LIFECYCLE, foundWork: dead.Count > 0);
    return ComputeTickInterval(oldestAge, threshold);
  }

  /// <summary>One registry scan: the instances found dead, the ones found alive, and the oldest heartbeat age seen.</summary>
  private static async Task<(List<Guid> Dead, List<Guid> Alive, TimeSpan? OldestAge)> _readLivenessAsync(
      NotificationConnectionPlan plan, TimeSpan threshold, CancellationToken ct) {
    var dead = new List<Guid>();
    var alive = new List<Guid>();
    TimeSpan? oldestAge = null;
    await using var conn = await plan.OpenAsync(ct);
    // Two-signal liveness (migration 055): the alive-lock in pg_locks OR a heartbeat row within
    // the derived threshold. The bare timestamp comparison this replaced could not see the lock
    // and used a threshold equal to the fast cadence.
    await using var cmd = new NpgsqlCommand(@"
      SELECT instance_id,
             EXTRACT(EPOCH FROM (NOW() - last_heartbeat_at))::double precision AS age_seconds,
             is_instance_alive(instance_id, @threshold_seconds) AS alive
      FROM wh_service_instances", conn);
    cmd.Parameters.AddWithValue("threshold_seconds", (int)Math.Ceiling(threshold.TotalSeconds));
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct)) {
      var id = reader.GetGuid(0);
      var age = TimeSpan.FromSeconds(Math.Max(0, reader.GetDouble(1)));
      if (oldestAge is null || age > oldestAge) {
        oldestAge = age;
      }
      (reader.GetBoolean(2) ? alive : dead).Add(id);
    }
    return (dead, alive, oldestAge);
  }

  /// <summary>
  /// Announces each instance seen dead for the first time. The durable path INSERTs into wh_signals
  /// and NOTIFY-broadcasts; subscribers on other pods use the signal to trigger orphan takeover for
  /// the dead pod's owned streams. A failed publish is retried on the next tick.
  /// </summary>
  private async Task _announceDeathsAsync(List<Guid> dead, TimeSpan threshold, CancellationToken ct) {
    foreach (var deadId in dead.Where(id => !_announcedDeaths.Contains(id))) {
      _announcedDeaths.Add(deadId);
      try {
        await _signalBus.PublishAsync(new InstanceDiedSignal(), SignalTarget.Broadcast, ct);
        _metrics?.DeathsAnnounced.Add(1);
        LogInstanceDied(_logger, deadId, (int)threshold.TotalSeconds);
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        _announcedDeaths.Remove(deadId);   // retry on next tick
        LogPublishFailed(_logger, deadId, ex);
      }
    }
  }

  /// <summary>
  /// Retracts the announcement for each announced instance found alive again: it heartbeats (or
  /// holds its lock) again, so peers re-read topology; their orphan takeover already re-checks
  /// liveness in SQL, so a fresh row protects the instance's leases from this point on. A failed
  /// publish is retried on the next tick.
  /// </summary>
  private async Task _retractRevivalsAsync(List<Guid> alive, CancellationToken ct) {
    foreach (var aliveId in alive.Where(_announcedDeaths.Contains)) {
      _announcedDeaths.Remove(aliveId);
      try {
        await _signalBus.PublishAsync(new InstanceJoinedSignal(), SignalTarget.Broadcast, ct);
        _metrics?.DeathsRetracted.Add(1);
        LogInstanceRevived(_logger, aliveId);
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        _announcedDeaths.Add(aliveId);   // retract on next tick
        LogRetractFailed(_logger, aliveId, ex);
      }
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "PgInstanceLifecycleMonitor: tick failed; will retry on next interval")]
  static partial void LogTickFailed(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information,
    Message = "PgInstanceLifecycleMonitor: announced InstanceDiedSignal for {InstanceId} (no alive-lock held and heartbeat older than {ThresholdSeconds}s)")]
  static partial void LogInstanceDied(ILogger logger, Guid instanceId, int thresholdSeconds);

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
    Message = "PgInstanceLifecycleMonitor: failed to publish InstanceDiedSignal for {InstanceId}; will retry")]
  static partial void LogPublishFailed(ILogger logger, Guid instanceId, Exception ex);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
    Message = "PgInstanceLifecycleMonitor: instance {InstanceId} is alive again after being announced dead; retracting with InstanceJoinedSignal. A death that does not hold means the heartbeat was late, not absent; look for what stalled it.")]
  static partial void LogInstanceRevived(ILogger logger, Guid instanceId);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
    Message = "PgInstanceLifecycleMonitor: failed to publish the retraction for {InstanceId}; will retry")]
  static partial void LogRetractFailed(ILogger logger, Guid instanceId, Exception ex);
}
