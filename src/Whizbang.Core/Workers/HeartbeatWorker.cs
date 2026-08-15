using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Workers;

/// <summary>
/// Decoupled heartbeat timer. Fires <see cref="IWorkCoordinator.RecordHeartbeatAsync"/>
/// on a fixed cadence (5 s default) independent of the polling claim worker. Replaces
/// the legacy "heartbeat embedded in <c>process_work_batch</c>" coupling.
/// Phase C of work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public partial class HeartbeatWorker(
  IServiceScopeFactory scopeFactory,
  IServiceInstanceProvider instanceProvider,
  ISchemaReadyGate schemaReadyGate,
  IOptions<HeartbeatWorkerOptions> options,
  ILogger<HeartbeatWorker> logger,
  IPinnedConnectionPool? pinnedPool = null,
  IInstanceAliveLockSource? aliveLockSource = null,
  ISignalBus? signalBus = null
) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly ISchemaReadyGate _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
  private readonly HeartbeatWorkerOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<HeartbeatWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly IPinnedConnectionPool _pinnedPool = pinnedPool ?? NoOpPinnedConnectionPool.Instance;
  private readonly IInstanceAliveLockSource? _aliveLockSource = aliveLockSource;
  private readonly ISignalBus? _signalBus = signalBus;
  private int _joinedAnnounced;

  /// <summary>Internal hook for tests to query the current cadence decision.</summary>
  internal int CurrentCadenceSeconds => _resolveCadenceSeconds();

  private int _resolveCadenceSeconds() {
    if (_options.LivenessSourceMode == HeartbeatLivenessSourceMode.HeartbeatTableOnly) {
      return _options.IntervalSeconds;
    }
    var lockHeld = _aliveLockSource?.IsAliveLockHeld ?? false;
    return lockHeld ? _options.SlowIntervalSeconds : _options.IntervalSeconds;
  }

  /// <summary>
  /// Fires after every successful <c>RecordHeartbeatAsync</c> call. Slice 4 of
  /// zero-idle-polling subscribes <see cref="IIdleActivityTracker.Touch"/> to
  /// this so the <see cref="BackupTickCoordinator"/> never engages POLLING
  /// during a brand-new pod's startup window before any real work has
  /// arrived.
  /// </summary>
  public event Action? OnHeartbeatRecorded;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _options.IntervalSeconds, _instanceProvider.InstanceId);

    // Diagnostic: dump the receptor-registry snapshot at startup so operators can verify
    // the multi-assembly [ModuleInitializer] pattern populated correctly. If the
    // contribution count or receptor-type count is zero, the receive-boundary drop-gate
    // will silently drop every message and chat / cascades will not work.
    var (Contributions, AnyConsumerTypes, InboxHandlerTypes, StageTypeCount) = Whizbang.Core.Generated.WhizbangReceptorRegistryQuery.GetDiagnosticSnapshot();
    LogReceptorRegistrySnapshot(_logger,
      Contributions, AnyConsumerTypes, InboxHandlerTypes, StageTypeCount);

    if (!_options.Enabled) {
      LogDisabled(_logger);
      try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
      return;
    }

    try {
      await _schemaReadyGate.WaitForReadyAsync(stoppingToken);
    } catch (OperationCanceledException) {
      return;
    }

    while (!stoppingToken.IsCancellationRequested) {
      var accepted = true;
      try {
        accepted = await _heartbeatOnceAsync(stoppingToken);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        // Heartbeat failures are non-fatal — peers may flag this instance stale,
        // which is the correct behavior. Log and continue.
        LogError(_logger, ex);
      }

      if (!accepted) {
        // This instance was reaped and tombstoned (migration 106): record_heartbeat refused it.
        // Retrying can never succeed — the tombstone does not expire on this instance's clock —
        // so continuing to call would just be a heartbeat that is guaranteed to be rejected forever.
        LogEvicted(_logger, _instanceProvider.InstanceId);
        break;
      }

      try {
        // Slice 7b — adaptive cadence: when the direct-conn alive-lock is held, use the
        // slow cadence; otherwise (no lock OR LivenessSourceMode=HeartbeatTableOnly) use
        // the fast cadence so the 30-second cleanup_stale_instances recovery guarantee
        // is preserved via the heartbeat-table fallback.
        await Task.Delay(TimeSpan.FromSeconds(_resolveCadenceSeconds()), stoppingToken);
      } catch (OperationCanceledException) {
        break;
      }
    }

    LogStopped(_logger);
  }

  /// <summary>One heartbeat tick. Returns whether it was accepted — <see langword="false"/> means
  /// this instance has been reaped and tombstoned, and the caller must stop retrying.</summary>
  private async Task<bool> _heartbeatOnceAsync(CancellationToken ct) {
    await using var pin = await _pinnedPool.TryPinForAsync(typeof(HeartbeatWorker), ct);
    using var __ctx = PinnedConnectionContext.Push(pin.Connection);
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    var accepted = await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(
      InstanceId: _instanceProvider.InstanceId,
      ServiceName: _instanceProvider.ServiceName,
      HostName: _instanceProvider.HostName,
      ProcessId: _instanceProvider.ProcessId), ct);
    if (!accepted) {
      return false;
    }
    OnHeartbeatRecorded?.Invoke();

    // Announce InstanceJoined once, after the first successful heartbeat (which is the
    // moment wh_service_instances first has a row for this pod). Subscribers use this to
    // warm caches, rebalance topology, etc.
    if (_signalBus is not null && Interlocked.CompareExchange(ref _joinedAnnounced, 1, 0) == 0) {
      try {
        await _signalBus.PublishAsync(new InstanceJoinedSignal(), SignalTarget.Broadcast, ct);
      } catch (OperationCanceledException) { throw; } catch (Exception ex) {
        // Failure to announce is non-fatal — reconciling consumers will pick it up via the
        // heartbeat scan. Reset the flag so a later tick retries.
        Interlocked.Exchange(ref _joinedAnnounced, 0);
        LogJoinedPublishFailed(_logger, ex);
      }
    }

    return true;
  }

  /// <inheritdoc />
  public override async Task StopAsync(CancellationToken cancellationToken) {
    // Best-effort InstanceLeaving on graceful shutdown so peers can rebalance without waiting
    // for the stale-heartbeat threshold. Failures are silent — the InstanceDied monitor will
    // still detect this pod's departure via the lease/heartbeat expiry.
    if (_signalBus is not null) {
      try {
        await _signalBus.PublishAsync(new InstanceLeavingSignal(), SignalTarget.Broadcast, cancellationToken);
      } catch (OperationCanceledException) {
        // shutdown cancellation is fine
      } catch (Exception ex) {
        LogLeavingPublishFailed(_logger, ex);
      }
    }
    await base.StopAsync(cancellationToken);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "HeartbeatWorker started: interval={IntervalSeconds}s, instance={InstanceId}")]
  static partial void LogStarted(ILogger logger, int intervalSeconds, Guid instanceId);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "HeartbeatWorker call failed; will retry on next tick")]
  static partial void LogError(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "HeartbeatWorker stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "HeartbeatWorker disabled via options — heartbeat skipped")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 5, Level = LogLevel.Information,
    Message = "Whizbang receptor registry: {Contributions} assembly contribution(s), {AnyConsumerTypes} any-consumer type(s), {InboxHandlerTypes} inbox-handler type(s), {StageTypeCount} lifecycle-stage receptor type(s) across all stages. Zero values mean the multi-assembly [ModuleInitializer] pattern did not populate — every message will be dropped at the receive boundary.")]
  static partial void LogReceptorRegistrySnapshot(ILogger logger, int contributions, int anyConsumerTypes, int inboxHandlerTypes, int stageTypeCount);

  [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
    Message = "HeartbeatWorker failed to publish InstanceJoinedSignal; will retry on next heartbeat")]
  static partial void LogJoinedPublishFailed(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
    Message = "HeartbeatWorker failed to publish InstanceLeavingSignal on graceful stop; peers will detect via heartbeat expiry")]
  static partial void LogLeavingPublishFailed(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 8, Level = LogLevel.Error,
    Message = "Instance {InstanceId} has been evicted (reaped as stale, then tombstoned) — heartbeat refused. Stopping the heartbeat loop; this instance must not consider itself part of the fleet.")]
  static partial void LogEvicted(ILogger logger, Guid instanceId);
}

/// <summary>
/// Configuration for <see cref="HeartbeatWorker"/>.
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public class HeartbeatWorkerOptions {
  /// <summary>
  /// Killswitch. Set to <c>false</c> to disable the heartbeat loop entirely. The worker
  /// stays registered as a hosted service but skips its <see cref="ExecuteAsync"/> body.
  /// Without heartbeats, peers will eventually flag this instance stale — useful for
  /// gracefully draining an instance ahead of decommission. Default <c>true</c>.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Heartbeat cadence in seconds. Default 30.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Slice 5 of zero-idle-polling raised this from 5 s to 30 s. Safe because
  /// orphan detection now has two independent freshness signals: the
  /// timer-driven <c>wh_service_instances.last_heartbeat_at</c> column AND
  /// the TCP-fresh LISTEN connection in <c>pg_stat_activity</c> joined
  /// via the <c>wh_live_instances</c> view (migration 052). The
  /// <c>claim_orphaned_*</c> functions (migrations 024/025/027 after
  /// Slice 2b) consult both, so a 30 s gap between heartbeat row UPDATEs
  /// doesn't weaken orphan detection while the pod's LISTEN connection
  /// stays alive.
  /// </para>
  /// <para>
  /// Must remain less than
  /// <c>WorkCoordinatorPublisherOptions.AbandonStaleInstanceThresholdSeconds / 3</c>
  /// so peers don't false-flag this instance stale during a transient
  /// gap. With the threshold's default of 600 s and three-renewal margin,
  /// 30 s sits comfortably below.
  /// </para>
  /// </remarks>
  public int IntervalSeconds { get; set; } = 30;

  /// <summary>
  /// Slow heartbeat cadence in seconds, used by the adaptive path when the
  /// session-level alive-lock (see migration 055 and <see cref="IInstanceAliveLockSource"/>)
  /// is held. The lock is the primary liveness signal in that mode — TCP keepalive
  /// detects pod death within ~10-30 s without depending on the heartbeat row's
  /// freshness — so the table write can run at a relaxed cadence. Default 60.
  /// </summary>
  public int SlowIntervalSeconds { get; set; } = 60;

  /// <summary>
  /// Selects between the adaptive (lock-aware) cadence and the legacy (table-only)
  /// cadence. Default <see cref="HeartbeatLivenessSourceMode.AdvisoryLockWhenAvailable"/>.
  /// Setting <see cref="HeartbeatLivenessSourceMode.HeartbeatTableOnly"/> forces the
  /// fast cadence even when a lock source is registered — opt-out for environments
  /// that don't trust the adaptive behaviour.
  /// </summary>
  public HeartbeatLivenessSourceMode LivenessSourceMode { get; set; } = HeartbeatLivenessSourceMode.AdvisoryLockWhenAvailable;
}

/// <summary>
/// Controls how <see cref="HeartbeatWorker"/> decides between fast and slow
/// heartbeat cadence.
/// </summary>
public enum HeartbeatLivenessSourceMode {
  /// <summary>Use the slow cadence when the alive-lock is held; fast otherwise. Default.</summary>
  AdvisoryLockWhenAvailable,
  /// <summary>Always use the fast (<see cref="HeartbeatWorkerOptions.IntervalSeconds"/>) cadence regardless of lock state. Legacy / opt-out behaviour.</summary>
  HeartbeatTableOnly,
}
