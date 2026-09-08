using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Workers;

/// <summary>
/// Decoupled heartbeat timer. Fires <see cref="IWorkCoordinator.RecordHeartbeatAsync"/>
/// on its own cadence independent of the polling claim worker. Replaces
/// the legacy "heartbeat embedded in <c>process_work_batch</c>" coupling.
/// Phase C of work-pump decomposition.
/// </summary>
/// <remarks>
/// <para>
/// The cadence is adaptive: the fast interval while the heartbeat row is the liveness signal, the
/// slow interval while the session alive-lock is (see <see cref="IInstanceAliveLockSource"/>). The
/// readers' stale threshold derives from the same options through
/// <see cref="HeartbeatLivenessThreshold"/>, so the two sides can never disagree.
/// </para>
/// <para>
/// Belt and suspenders on the writer side: the worker remembers when its last beat was accepted
/// and plans each wake from that. If a beat ran late (a stalled tick, a slow round trip, a paused
/// process) and the row's age approaches the threshold minus one fast interval, the next beat is
/// forced immediately rather than at the next cadence boundary, on the dedicated pinned connection
/// when one is available. A watchdog beat is logged and counted so an operator can see how often
/// the regular cadence needed rescuing.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/HeartbeatWorkerTickPlanTests.cs</tests>
public partial class HeartbeatWorker(
  IServiceScopeFactory scopeFactory,
  IServiceInstanceProvider instanceProvider,
  ISchemaReadyGate schemaReadyGate,
  IOptions<HeartbeatWorkerOptions> options,
  ILogger<HeartbeatWorker> logger,
  IWhizbangLifecycleState lifecycleState,
  ILibraryVersionProvider libraryVersion,
  IPinnedConnectionPool? pinnedPool = null,
  IInstanceAliveLockSource? aliveLockSource = null,
  ISignalBus? signalBus = null,
  TimeProvider? timeProvider = null,
  InstanceLivenessMetrics? metrics = null
) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly ISchemaReadyGate _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
  private readonly HeartbeatWorkerOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<HeartbeatWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly IPinnedConnectionPool _pinnedPool = pinnedPool ?? NoOpPinnedConnectionPool.Instance;
  private readonly IInstanceAliveLockSource? _aliveLockSource = aliveLockSource;
  private readonly ISignalBus? _signalBus = signalBus;
  private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
  // Required, not optional: the registry row these feed is what peers and operators read to tell a
  // live instance from a dead one, and an optional dependency is silently null wherever the worker
  // is hand-built. The worker pipeline registers defaults for both.
  private readonly IWhizbangLifecycleState _lifecycleState = lifecycleState ?? throw new ArgumentNullException(nameof(lifecycleState));
  private readonly ILibraryVersionProvider _libraryVersion = libraryVersion ?? throw new ArgumentNullException(nameof(libraryVersion));
  private readonly InstanceLivenessMetrics? _metrics = metrics;
  private int _joinedAnnounced;

  private DateTimeOffset? _lastAcceptedAt;
  private DateTimeOffset? _lastAttemptAt;
  private bool _lastAttemptFailed;

  /// <summary>Test seam: records an accepted beat at <paramref name="at"/> without touching the database.</summary>
  /// <param name="at">When the beat was accepted.</param>
  internal void MarkAcceptedForTests(DateTimeOffset at) {
    _lastAcceptedAt = at;
    _lastAttemptAt = at;
    _lastAttemptFailed = false;
  }

  /// <summary>Test seam: records a failed attempt at <paramref name="at"/> without touching the database.</summary>
  /// <param name="at">When the attempt failed.</param>
  internal void MarkFailedAttemptForTests(DateTimeOffset at) {
    _lastAttemptAt = at;
    _lastAttemptFailed = true;
  }

  /// <summary>Internal hook for tests to query the current cadence decision.</summary>
  internal int CurrentCadenceSeconds => _resolveCadenceSeconds();

  /// <summary>When the most recent beat was accepted, or null before the first.</summary>
  public DateTimeOffset? LastAcceptedAt => _lastAcceptedAt;

  /// <summary>How long the most recent beat's round trip took.</summary>
  public TimeSpan LastBeatDuration { get; private set; }

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

  /// <summary>
  /// Decides, from the clock alone, whether to beat now and otherwise how long to wait before
  /// asking again. Pure: no I/O, so the whole cadence and watchdog contract is testable with a
  /// sequence of timestamps.
  /// </summary>
  /// <remarks>
  /// Rules, in order: a failed attempt is not retried tighter than half the smaller of the fast
  /// interval and the cadence; before any accepted beat the first beat is immediate; a row whose
  /// age has reached the stale threshold minus one fast interval is beaten immediately as a
  /// watchdog; a row older than the cadence is beaten as a regular tick; otherwise wait until the
  /// earlier of the cadence boundary and the watchdog deadline, capped at one fast interval so a
  /// change in lock state is re-planned promptly.
  /// </remarks>
  /// <param name="now">The current time.</param>
  /// <returns>The plan for this wake.</returns>
  public HeartbeatTickPlan PlanNextTick(DateTimeOffset now) {
    var cadence = TimeSpan.FromSeconds(Math.Max(1, _resolveCadenceSeconds()));
    var threshold = HeartbeatLivenessThreshold.StaleThreshold(_options);
    var lead = HeartbeatLivenessThreshold.WatchdogLead(_options);
    var deadline = threshold - lead;

    if (_lastAttemptFailed && _lastAttemptAt is { } attemptedAt) {
      var retrySpacing = TimeSpan.FromTicks(Math.Min(lead.Ticks, cadence.Ticks) / 2);
      var sinceAttempt = now - attemptedAt;
      if (sinceAttempt < retrySpacing) {
        return new HeartbeatTickPlan(false, retrySpacing - sinceAttempt, HeartbeatBeatReason.Regular);
      }
    }

    if (_lastAcceptedAt is not { } acceptedAt) {
      return new HeartbeatTickPlan(true, TimeSpan.Zero, HeartbeatBeatReason.Initial);
    }

    var elapsed = now - acceptedAt;
    if (elapsed >= deadline) {
      return new HeartbeatTickPlan(true, TimeSpan.Zero, HeartbeatBeatReason.Watchdog);
    }
    if (elapsed >= cadence) {
      return new HeartbeatTickPlan(true, TimeSpan.Zero, HeartbeatBeatReason.Regular);
    }

    var untilCadence = cadence - elapsed;
    var untilDeadline = deadline - elapsed;
    var wait = untilCadence < untilDeadline ? untilCadence : untilDeadline;
    if (wait > lead) {
      wait = lead;
    }
    return new HeartbeatTickPlan(false, wait, HeartbeatBeatReason.Regular);
  }

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

    var lead = HeartbeatLivenessThreshold.WatchdogLead(_options);

    while (!stoppingToken.IsCancellationRequested) {
      var plan = PlanNextTick(_time.GetUtcNow());
      if (!plan.BeatNow) {
        try {
          await Task.Delay(plan.Wait, _time, stoppingToken);
        } catch (OperationCanceledException) {
          break;
        }
        continue;
      }

      var accepted = true;
      _lastAttemptAt = _time.GetUtcNow();
      _lastAttemptFailed = false;
      var started = _time.GetTimestamp();
      try {
        accepted = await _heartbeatOnceAsync(stoppingToken);
        LastBeatDuration = _time.GetElapsedTime(started);
        if (accepted) {
          _lastAcceptedAt = _time.GetUtcNow();
          if (plan.Reason == HeartbeatBeatReason.Watchdog) {
            _metrics?.WatchdogBeats.Add(1);
            LogWatchdogBeat(_logger, _instanceProvider.InstanceId, (int)HeartbeatLivenessThreshold.StaleThreshold(_options).TotalSeconds);
          }
          if (LastBeatDuration >= lead) {
            _metrics?.SlowBeats.Add(1);
            LogSlowBeat(_logger, (long)LastBeatDuration.TotalMilliseconds, (int)lead.TotalSeconds);
          }
        }
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        // Heartbeat failures are non-fatal: peers may flag this instance stale,
        // which is the correct behavior. Log and retry on the plan's spacing.
        _lastAttemptFailed = true;
        LogError(_logger, ex);
      }

      if (!accepted) {
        // This instance was reaped and tombstoned (migration 106): record_heartbeat refused it.
        // Retrying can never succeed (the tombstone does not expire on this instance's clock),
        // so continuing to call would just be a heartbeat that is guaranteed to be rejected forever.
        LogEvicted(_logger, _instanceProvider.InstanceId);
        break;
      }
    }

    LogStopped(_logger);
  }

  /// <summary>One heartbeat tick. Returns whether it was accepted; <see langword="false"/> means
  /// this instance has been reaped and tombstoned, and the caller must stop retrying.</summary>
  private async Task<bool> _heartbeatOnceAsync(CancellationToken ct) {
    await using var pin = await _pinnedPool.TryPinForAsync(typeof(HeartbeatWorker), ct);
    using var __ctx = PinnedConnectionContext.Push(pin.Connection);
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    var accepted = await coordinator.RecordHeartbeatAsync(BuildRequest(), ct);
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
        // Failure to announce is non-fatal: reconciling consumers will pick it up via the
        // heartbeat scan. Reset the flag so a later tick retries.
        Interlocked.Exchange(ref _joinedAnnounced, 0);
        LogJoinedPublishFailed(_logger, ex);
      }
    }

    return true;
  }

  /// <summary>
  /// The request one beat sends: identity, the current lifecycle phase and library version (so a
  /// registry row created before the first phase transition is backfilled), and the stale
  /// threshold this writer's cadence implies for the opportunistic peer reap.
  /// </summary>
  /// <returns>The heartbeat request.</returns>
  public HeartbeatRequest BuildRequest() => new(
    InstanceId: _instanceProvider.InstanceId,
    ServiceName: _instanceProvider.ServiceName,
    HostName: _instanceProvider.HostName,
    ProcessId: _instanceProvider.ProcessId,
    LifecyclePhase: _lifecycleState.Phase.ToString(),
    LibraryVersion: _libraryVersion.LibraryVersion,
    StaleThresholdSeconds: HeartbeatLivenessThreshold.StaleThresholdSeconds(_options));

  /// <inheritdoc />
  public override async Task StopAsync(CancellationToken cancellationToken) {
    // Best-effort InstanceLeaving on graceful shutdown so peers can rebalance without waiting
    // for the stale-heartbeat threshold. Failures are silent: the InstanceDied monitor will
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

  [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "HeartbeatWorker disabled via options; heartbeat skipped")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 5, Level = LogLevel.Information,
    Message = "Whizbang receptor registry: {Contributions} assembly contribution(s), {AnyConsumerTypes} any-consumer type(s), {InboxHandlerTypes} inbox-handler type(s), {StageTypeCount} lifecycle-stage receptor type(s) across all stages. Zero values mean the multi-assembly [ModuleInitializer] pattern did not populate; every message will be dropped at the receive boundary.")]
  static partial void LogReceptorRegistrySnapshot(ILogger logger, int contributions, int anyConsumerTypes, int inboxHandlerTypes, int stageTypeCount);

  [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
    Message = "HeartbeatWorker failed to publish InstanceJoinedSignal; will retry on next heartbeat")]
  static partial void LogJoinedPublishFailed(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
    Message = "HeartbeatWorker failed to publish InstanceLeavingSignal on graceful stop; peers will detect via heartbeat expiry")]
  static partial void LogLeavingPublishFailed(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 8, Level = LogLevel.Error,
    Message = "Instance {InstanceId} has been evicted (reaped as stale, then tombstoned); heartbeat refused. Stopping the heartbeat loop; this instance must not consider itself part of the fleet.")]
  static partial void LogEvicted(ILogger logger, Guid instanceId);

  [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
    Message = "HeartbeatWorker forced a watchdog beat for instance {InstanceId}: the regular beat ran late enough to approach the {ThresholdSeconds}s stale threshold. Peers would have read this instance as dead without it; look for the stall that delayed the tick.")]
  static partial void LogWatchdogBeat(ILogger logger, Guid instanceId, int thresholdSeconds);

  [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
    Message = "HeartbeatWorker beat took {DurationMs}ms, at least one fast interval ({LeadSeconds}s). The heartbeat shares the database with the work it reports on; a slow beat means the database or the connection pool is saturated.")]
  static partial void LogSlowBeat(ILogger logger, long durationMs, int leadSeconds);
}

/// <summary>Why a beat is being sent now.</summary>
/// <docs>fundamentals/workers/instance-liveness</docs>
public enum HeartbeatBeatReason {
  /// <summary>No beat has been accepted yet.</summary>
  Initial,
  /// <summary>The cadence boundary was reached.</summary>
  Regular,
  /// <summary>The row's age approached the stale threshold before the cadence boundary.</summary>
  Watchdog,
}

/// <summary>The outcome of <see cref="HeartbeatWorker.PlanNextTick"/>.</summary>
/// <param name="BeatNow">True to beat immediately; false to wait <paramref name="Wait"/> and plan again.</param>
/// <param name="Wait">How long to wait when not beating now.</param>
/// <param name="Reason">Why the beat is due, when it is.</param>
/// <docs>fundamentals/workers/instance-liveness</docs>
public readonly record struct HeartbeatTickPlan(bool BeatNow, TimeSpan Wait, HeartbeatBeatReason Reason);

/// <summary>
/// Configuration for <see cref="HeartbeatWorker"/>.
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public class HeartbeatWorkerOptions {
  /// <summary>
  /// Killswitch. Set to <c>false</c> to disable the heartbeat loop entirely. The worker
  /// stays registered as a hosted service but skips its <see cref="ExecuteAsync"/> body.
  /// Without heartbeats, peers will eventually flag this instance stale; useful for
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
  /// The readers' stale threshold is derived from this and <see cref="SlowIntervalSeconds"/>
  /// by <see cref="HeartbeatLivenessThreshold"/> (two of the slowest cadence plus one fast
  /// interval), so raising the cadence raises the threshold with it.
  /// </para>
  /// </remarks>
  public int IntervalSeconds { get; set; } = 30;

  /// <summary>
  /// Slow heartbeat cadence in seconds, used by the adaptive path when the
  /// session-level alive-lock (see migration 055 and <see cref="IInstanceAliveLockSource"/>)
  /// is held. The lock is the primary liveness signal in that mode; TCP keepalive
  /// detects pod death within ~10-30 s without depending on the heartbeat row's
  /// freshness, so the table write can run at a relaxed cadence. Default 60.
  /// </summary>
  public int SlowIntervalSeconds { get; set; } = 60;

  /// <summary>
  /// Selects between the adaptive (lock-aware) cadence and the legacy (table-only)
  /// cadence. Default <see cref="HeartbeatLivenessSourceMode.AdvisoryLockWhenAvailable"/>.
  /// Setting <see cref="HeartbeatLivenessSourceMode.HeartbeatTableOnly"/> forces the
  /// fast cadence even when a lock source is registered; opt-out for environments
  /// that don't trust the adaptive behavior.
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
  /// <summary>Always use the fast (<see cref="HeartbeatWorkerOptions.IntervalSeconds"/>) cadence regardless of lock state. Legacy / opt-out behavior.</summary>
  HeartbeatTableOnly,
}
