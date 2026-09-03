using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;

#pragma warning disable IDE0290  // Allow explicit constructor for optional channel writers

namespace Whizbang.Core.Workers;

/// <summary>
/// The polling worker. The only place that calls <see cref="IWorkCoordinator.ClaimWorkAsync"/>.
/// Adaptive backoff on consecutive empty polls; wake semaphore lets external producers
/// (NOTIFY listener, local channel writes) interrupt the wait so burst latency stays low.
/// Distributes claimed work to the existing channel writers.
/// Phase C of work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/ClaimWorkerAttemptAccountingTests.cs</tests>
public sealed partial class ClaimWorker : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly IWorkNotificationListener _notificationListener;
  private readonly INotifySignalingGate? _signalingGate;
  private readonly ISchemaReadyGate _schemaReadyGate;
  private readonly IWorkChannelWriter? _outboxChannel;
  private readonly IInboxChannelWriter? _inboxChannel;
  private readonly IPerspectiveChannelWriter? _perspectiveChannel;
  private readonly IPerspectiveDrainChannel? _perspectiveDrainChannel;
  private readonly IOutboxDrainChannel? _outboxDrainChannel;
  private readonly IInboxDrainChannel? _inboxDrainChannel;
  private readonly ClaimWorkerOptions _options;
  private readonly AdaptiveClaimWindow _claimWindow;
  private readonly ClaimCycleReport _cycleReport = new(repeatStreakThreshold: 8);
  private readonly ClaimChurnFeedback? _churnFeedback;
  private readonly AdaptiveOutstandingBudget _outstandingBudget;

  /// <summary>Observed inbox rows per claimed stream, smoothed. Converts a row budget into streams.</summary>
  private double _rowsPerStream = 1.0;
  private int _lastOutstanding;
  private long _lastDrainTicks;
  private readonly ILogger<ClaimWorker> _logger;
  private readonly IPinnedConnectionPool _pinnedPool;
  private readonly ISignalBus? _signalBus;
  private readonly SignalBusLivenessState? _busLiveness;
  private readonly WorkCompletionMeter? _completionMeter;
  private int _doorbellSinceLastClaim;
  private bool _lastClaimWasEmpty;
  private ISignalSubscription? _outboxSignalSub;
  private ISignalSubscription? _inboxSignalSub;
  private ISignalSubscription? _perspectiveSignalSub;
  private readonly SemaphoreSlim _wake = new(0, 1);

  // The current repeat-claim spacing nap, when one is in progress. SignalNewWork cancels it so a
  // genuinely NEW row never sits out the nap; completion-feedback wakes (RequestImmediatePoll)
  // deliberately cannot reach it. Null outside the nap window.
  private CancellationTokenSource? _napCts;
  private int _consecutiveEmptyPolls;

  /// <summary>
  /// Identity of the previous claim's work set. A claim that returns exactly what the last one
  /// did is a re-offer, not progress — see the cadence handling in the run loop.
  /// </summary>
  private int _lastWorkSignature;

  /// <summary>True when the most recent claim re-offered the previous claim's work set.</summary>
  private bool _lastClaimWasRepeat;

  /// <summary>
  /// Set once the work coordinator has been asked for outstanding work and answered that it cannot
  /// measure it. Latched rather than re-probed: a backend either implements the count or it does
  /// not, and retrying it every poll would add a round trip per cycle to say the same thing.
  /// </summary>
  private bool _outstandingUnmeasurable;

  /// <summary>
  /// Whether the outstanding bound is doing anything. Every precondition must hold: the operator
  /// enabled it, drain is measurable, and the store can report what this instance holds. Missing any
  /// one of them means the budget would be sized from a number nobody read — worse than no bound,
  /// because it throttles silently and presents as an unexplained performance problem.
  /// </summary>
  private bool _budgetEngaged =>
    _options.AdaptiveOutstandingBudget && _completionMeter is not null && !_outstandingUnmeasurable;

  /// <summary>Constructor.</summary>
#pragma warning disable S107 // ClaimWorker is the central poller — its channel/option dependencies are unavoidable.
  public ClaimWorker(
    IServiceScopeFactory scopeFactory,
    IServiceInstanceProvider instanceProvider,
    IWorkNotificationListener notificationListener,
    ISchemaReadyGate schemaReadyGate,
    IOptions<ClaimWorkerOptions> options,
    ILogger<ClaimWorker> logger,
    IWorkChannelWriter? outboxChannel = null,
    IInboxChannelWriter? inboxChannel = null,
    IPerspectiveChannelWriter? perspectiveChannel = null,
    IPerspectiveDrainChannel? perspectiveDrainChannel = null,
    IOutboxDrainChannel? outboxDrainChannel = null,
    IInboxDrainChannel? inboxDrainChannel = null,
    INotifySignalingGate? signalingGate = null,
    IPinnedConnectionPool? pinnedPool = null,
    ISignalBus? signalBus = null,
    SignalBusLivenessState? busLiveness = null,
    WorkCompletionMeter? completionMeter = null,
    ClaimChurnFeedback? churnFeedback = null) {
#pragma warning restore S107
    _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _notificationListener = notificationListener ?? throw new ArgumentNullException(nameof(notificationListener));
    _signalingGate = signalingGate;
    _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _claimWindow = new AdaptiveClaimWindow(
      ceiling: _options.MaxStreamsPerBatch,
      floor: _options.MinStreamsPerBatch,
      additiveStep: _options.ClaimWindowGrowthStep);
    _outstandingBudget = new AdaptiveOutstandingBudget(
      leaseSeconds: _options.LeaseSeconds,
      ceiling: _options.MaxOutstandingInboxRows,
      floor: _options.MinOutstandingInboxRows,
      safetyFactor: _options.OutstandingBudgetSafetyFactor);
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _outboxChannel = outboxChannel;
    _inboxChannel = inboxChannel;
    _perspectiveChannel = perspectiveChannel;
    _perspectiveDrainChannel = perspectiveDrainChannel;
    _outboxDrainChannel = outboxDrainChannel;
    _inboxDrainChannel = inboxDrainChannel;
    _pinnedPool = pinnedPool ?? NoOpPinnedConnectionPool.Instance;
    _signalBus = signalBus;
    _busLiveness = busLiveness;
    _completionMeter = completionMeter;
    _churnFeedback = churnFeedback;

    // F1 unify-now: bus signals for outbox/inbox/perspective work-available replace the raw
    // WorkSignalCategory subscription for those categories. Push transport (NOTIFY) and pull
    // source (5s DB backstop) both raise the typed signal, so ClaimWorker wakes uniformly on
    // either path. The IWorkNotificationListener.OnSignal subscription is preserved for the
    // orphan + deadletter categories that don't have typed signals yet.
    if (_signalBus is not null) {
      _outboxSignalSub = _signalBus.Subscribe<WorkOutboxAvailableSignal>(_wakeOnSignal);
      _inboxSignalSub = _signalBus.Subscribe<WorkInboxAvailableSignal>(_wakeOnSignal);
      _perspectiveSignalSub = _signalBus.Subscribe<WorkPerspectiveAvailableSignal>(_wakeOnSignal);
    }

    // Subscribe to the listener for orphan+deadletter wake categories (still legacy path);
    // outbox/inbox/perspective now come via the bus above.
    _notificationListener.OnSignal += _onSignal;

    // Slice 33.6 — pick up the gate's availability transitions so a polling-to-NOTIFY-available
    // recovery immediately polls (any work that accumulated during the unavailable window
    // would otherwise wait for the next backoff tick).
    if (_signalingGate is not null) {
      _signalingGate.OnAvailabilityChanged += _onGateAvailabilityChanged;
    }

    // Wake immediately when a strategy persists new outbox/inbox rows — eliminates the
    // ~250 ms poll-tick lag for the legacy synchronous-store-and-publish path that
    // process_work_batch used to provide. Must remain attached for the lifetime of the
    // worker; BackgroundService disposal handles cleanup.
    if (_outboxChannel is not null) {
      _outboxChannel.OnNewWorkAvailable += SignalNewWork;
    }
    if (_inboxChannel is not null) {
      _inboxChannel.OnNewInboxWorkAvailable += SignalNewWork;
    }
  }

  private void _onSignal(WorkSignalCategory category) {
    // OrphanRedistribute has no typed signal yet, so it always wakes via this legacy path.
    // Outbox/Inbox/Perspective: post-unify-now they wake via the bus (see ctor
    // _signalBus.Subscribe calls). When the bus isn't wired (legacy DI / tests), fall through
    // to the legacy wake so the pre-unify-now NOTIFY→ClaimWorker regression tests stay green.
    // Perspective matters here: ClaimWorker is the only claimer of perspective streams, and
    // the post-stamp doorbell (migration 117) rings with the 'perspective' payload — dropping
    // it quantizes fenced perspective visibility to the poll cadence.
    if (category is WorkSignalCategory.OrphanRedistribute) {
      RequestImmediatePoll();
      return;
    }
    if (_signalBus is null && category is WorkSignalCategory.Outbox or WorkSignalCategory.Inbox or WorkSignalCategory.Perspective) {
      SignalNewWork();
    }
  }

  private ValueTask _wakeOnSignal<TSignal>(TSignal signal) where TSignal : ISignal {
    _ = signal;
    SignalNewWork();
    return ValueTask.CompletedTask;
  }

  private void _onGateAvailabilityChanged(bool nowAvailable) {
    // Slice 33.6 — on either transition (available → unavailable OR unavailable → available)
    // wake the poll loop immediately. Unavailable→available: drain any work that accumulated
    // during the unavailable window. Available→unavailable: shorten the next wait cycle so
    // we start tight-polling without waiting out the current adaptive backoff.
    //
    // v0.502 — make the catch-up visible. The unavailable→available transition is the
    // moment work might have accumulated during a NOTIFY outage; we want operators to see
    // the catch-up in logs (and the metric below) so the path is observably exercised, not
    // hidden inside the next regular poll. The actual catch-up SQL runs implicitly via the
    // semaphore-wakeup chain: RequestImmediatePoll → _wake.Release → WaitAsync returns →
    // next loop iteration calls _claimOnceAsync → ClaimWorkAsync → claim_orphaned_*.
    if (nowAvailable) {
      var unavailableSince = Interlocked.Exchange(ref _lastUnavailableAtTicks, 0);
      if (unavailableSince > 0) {
        var elapsedMs = (DateTimeOffset.UtcNow.Ticks - unavailableSince) / TimeSpan.TicksPerMillisecond;
        LogReconnectCatchUp(_logger, elapsedMs);
        Interlocked.Increment(ref _reconnectCatchUpCount);
      }
    } else {
      Interlocked.Exchange(ref _lastUnavailableAtTicks, DateTimeOffset.UtcNow.Ticks);
    }
    RequestImmediatePoll();
  }

  // Tracks the wall-clock ticks at which the gate last flipped to unavailable. Used to
  // compute the "unavailable for N ms" duration that gets logged when we transition back
  // to available. 0 means "currently available" or "never been unavailable yet."
  private long _lastUnavailableAtTicks;

  // Observable counter of NOTIFY-reconnect catch-up triggers. Exposed as a property for
  // test inspection and OTEL bridging (see slice B.6).
  private long _reconnectCatchUpCount;

  /// <summary>
  /// Total number of times this worker has executed a catch-up claim after a NOTIFY-gate
  /// availability transition from unavailable → available. Exposed for observability and
  /// regression testing. Resets on process restart.
  /// </summary>
  public long ReconnectCatchUpCount => Interlocked.Read(ref _reconnectCatchUpCount);

  // Whether the worker has logged its startup catch-up. Incremented once per pod lifetime.
  // Exposed via StartupCatchUpCount for regression testing.
  private long _startupCatchUpCount;

  /// <summary>
  /// Whether this worker has run the startup catch-up claim (always exactly <c>1</c> after
  /// the first <see cref="ExecuteAsync"/> iteration; remains <c>0</c> if the worker is
  /// disabled, schema-gated, or perspective-only). Exposed for tests.
  /// </summary>
  public long StartupCatchUpCount => Interlocked.Read(ref _startupCatchUpCount);

  /// <summary>
  /// Observable: the most recent <see cref="WorkBatch"/> distributed by the worker.
  /// Set whenever a tick produces a non-empty batch. Useful for wiring up downstream
  /// consumers in tests.
  /// </summary>
  public event Action<WorkBatch>? OnBatchClaimed;

  /// <summary>
  /// Wake for NEW work (a store-level doorbell or a strategy persisting fresh rows) — as opposed
  /// to <see cref="RequestImmediatePoll"/>, which the system's own completion-feedback traffic
  /// also pulls. The distinction matters to the repeat-claim spacing: completion feedback must
  /// not defeat the spacing (see ClaimWorkerReemissionBackoffTests), but a genuinely new row
  /// must not sit out the spacing nap either.
  /// </summary>
  public void SignalNewWork() {
    Volatile.Write(ref _doorbellSinceLastClaim, 1);
    RequestImmediatePoll();
    try {
      Volatile.Read(ref _napCts)?.Cancel();
    } catch (ObjectDisposedException) {
      // The nap ended between the read and the cancel — the permit above covers the wake.
    }
  }

  /// <summary>External wake — call from notification listener or local channel writer.</summary>
  public void RequestImmediatePoll() {
    if (_wake.CurrentCount == 0) {
      try { _wake.Release(); } catch (SemaphoreFullException) { /* already pending */ }
    }
  }

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _options.PollingIntervalMilliseconds, _options.PollingMaxIntervalMilliseconds, _instanceProvider.InstanceId);

    // Say at startup whether the bound is on, and if not, exactly which precondition is missing.
    // Store measurability is only knowable after the first claim, so a coordinator that cannot
    // report outstanding work logs its own line then (EventId 14) rather than being guessed at here.
    if (!_options.AdaptiveOutstandingBudget) {
      LogOutstandingBudgetInactive(_logger, "disabled via AdaptiveOutstandingBudget");
    } else if (_completionMeter is null) {
      LogOutstandingBudgetInactive(_logger, "no WorkCompletionMeter registered, so drain is unmeasurable");
    } else {
      LogOutstandingBudgetActive(
        _logger,
        _options.MinOutstandingInboxRows,
        _options.MaxOutstandingInboxRows,
        _options.LeaseSeconds,
        _options.OutstandingBudgetSafetyFactor);
    }

    // Killswitch: ops can disable this worker via ClaimWorkerOptions.Enabled = false
    // (e.g., maintenance window, isolating a misbehaving instance) without removing the
    // registration from DI.
    if (!_options.Enabled) {
      LogDisabled(_logger);
      try {
        await Task.Delay(Timeout.Infinite, stoppingToken);
      } catch (OperationCanceledException) {
        // expected on shutdown
      }
      return;
    }

    // Hold off on any SQL until the schema is provisioned. The driver's initializer
    // (WhizbangDatabaseInitializerService) signals the gate after migrations succeed.
    // This decouples worker startup from hosted-service registration order — even if
    // this worker's StartAsync runs before the initializer, we still wait here.
    try {
      await _schemaReadyGate.WaitForReadyAsync(stoppingToken);
    } catch (OperationCanceledException) {
      return;
    }

    // Register the instance in wh_service_instances before the first claim_work call.
    // HeartbeatWorker performs the UPSERT but ticks on its own cadence, so without this the
    // registry would briefly carry no row for this pod — which skews peers' rank denominators
    // and delays instance-lifecycle signals. This is an optimization, not a correctness
    // requirement: claim_work repairs its own registration before it ranks, so a missed or
    // failed registration here self-heals on the first claim. Failures are non-fatal.
    try {
      await _initialHeartbeatAsync(stoppingToken);
    } catch (OperationCanceledException) {
      return;
    } catch (Exception ex) {
      LogInitialHeartbeatFailed(_logger, ex);
    }

    // PerspectiveOnly mode: legacy WorkCoordinatorPublisherWorker is wired up and is the
    // sole poller. Its process_work_batch handles outbox/inbox/perspective claiming and
    // routes PerspectiveStreamIds onto the drain channel. ClaimWorker doing its own
    // claim_work would race the publisher and break Phase 4.5B's event-store auto-create
    // chain (orphan rows get leased before process_work_batch sees them).
    if (_options.PerspectiveOnly) {
      try {
        await Task.Delay(Timeout.Infinite, stoppingToken);
      } catch (OperationCanceledException) {
        // expected on shutdown
      }
      LogStopped(_logger);
      return;
    }

    // v0.502 slice B.2 — the first claim is implicitly a startup catch-up: any work that
    // was sitting in wh_*box / wh_perspective_events before this pod started (orphaned by
    // a previously-crashed pod, scheduled retries that elapsed during downtime, etc.) gets
    // discovered on the first call. Log + counter for observability so operators can verify
    // the pod actually drained pre-existing state.
    var startupCatchUpFired = false;
    while (!stoppingToken.IsCancellationRequested) {
      try {
        var batch = await _claimOnceAsync(stoppingToken);
        var hadWork = batch.OutboxWork.Count > 0
                   || batch.InboxWork.Count > 0
                   || batch.PerspectiveStreamIds.Count > 0
                   || batch.OutboxStreamIds.Count > 0
                   || batch.InboxStreamIds.Count > 0;

        if (!startupCatchUpFired) {
          startupCatchUpFired = true;
          var picked = batch.OutboxStreamIds.Count + batch.InboxStreamIds.Count
                     + batch.PerspectiveStreamIds.Count + batch.OutboxWork.Count + batch.InboxWork.Count;
          LogStartupCatchUp(_logger, picked);
          Interlocked.Increment(ref _startupCatchUpCount);
        }

        // A claim that returns exactly the previous claim's work set is a RE-OFFER, not
        // progress. claim_work's eligible CTEs match every leased-but-uncompleted row
        // (instance_id = me AND lease_expiry > NOW() AND processed_at IS NULL), so a row that
        // is leased and awaiting its completion flush is re-emitted on every poll — by design,
        // because the in-memory in-flight filter that used to suppress it proved unrecoverable
        // (see _distributeAsync). Treating "non-empty" as "there was work" therefore pins the
        // empty-poll streak at zero forever: the backoff never engages and the loop re-claims as
        // fast as the database can answer, a rate set by query latency rather than by workload.
        //
        // So a repeat counts toward the idle streak for CADENCE purposes only. Emission is
        // deliberately left untouched — every stream_id is still distributed on every cycle, so
        // nothing can wedge waiting on a suppressed emit. Only the wait adapts.
        var signature = _workSignature(batch);
        _lastClaimWasRepeat = hadWork && signature == _lastWorkSignature;

        // A sustained run of repeats means rows are leased to this instance and are NOT completing,
        // so the backlog cannot drain even though the process is healthy and polling. From outside
        // that is indistinguishable from an idle service — modest CPU, no errors, no restarts — so
        // nothing reports it today. See ClaimCycleReport.
        _cycleReport.Record(hadWork, _lastClaimWasRepeat, _logger);
        _lastWorkSignature = signature;

        // Doorbell-liveness accounting (issue #505): on the empty→non-empty edge the store
        // guarantees a doorbell rings, so FRESH work discovered there by a plain poll — while the
        // gate believes NOTIFY is healthy — means doorbells are being dropped somewhere between
        // pg_notify and this worker. The flag is consumed per claim; a doorbell-preceded discovery
        // resets the streak. Startup catch-up never counts: it requires a previously-observed
        // empty claim, and _lastClaimWasEmpty starts false.
        var doorbellRang = Interlocked.Exchange(ref _doorbellSinceLastClaim, 0) == 1;
        // A doorbell that lands DURING an empty claim raced past the claim it was meant to cause:
        // an empty batch cannot have served it. Consuming it here would both charge the NEXT
        // discovery as a missed doorbell and let the idle spacing below nap through a genuinely
        // fresh row (SignalNewWork's cancel only reaches a nap that has already registered its
        // token). Put it back so the next cycle sees it — for the spacing skip and for liveness.
        if (!hadWork && doorbellRang) {
          Volatile.Write(ref _doorbellSinceLastClaim, 1);
        }
        if (_busLiveness is not null && _lastClaimWasEmpty && hadWork && !_lastClaimWasRepeat
            && (_signalingGate?.IsAvailable ?? false)) {
          if (doorbellRang) {
            _busLiveness.RecordDoorbellWake();
          } else {
            _busLiveness.RecordMissedDoorbell();
          }
        }
        _lastClaimWasEmpty = !hadWork;

        if (hadWork) {
          if (_lastClaimWasRepeat) {
            Interlocked.Increment(ref _consecutiveEmptyPolls);
          } else {
            _consecutiveEmptyPolls = 0;
          }
          await _distributeAsync(batch, stoppingToken);
          OnBatchClaimed?.Invoke(batch);
        } else {
          Interlocked.Increment(ref _consecutiveEmptyPolls);
        }
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        LogError(_logger, ex);
        Interlocked.Increment(ref _consecutiveEmptyPolls);  // back off after errors too
      }

      // F1 unify-now: when the signal bus is wired, bus signals + NOTIFY push drive the fast path via
      // RequestImmediatePoll (the semaphore). But signals only cover work that is *owned* or has a
      // targeted channel — ORPHANED/unleased work (e.g. cascade-created perspective rows with
      // instance_id NULL and no wh_active_streams owner) has no signal, and the Postgres pull-source
      // backstop isn't wired on every host (in-process transport). So keep a max-interval backstop poll
      // even when bus-wired, so claim_work/claim_orphaned still run periodically and no work can wedge
      // forever waiting on a signal that never fires. The interval (PollingMaxIntervalMs, ~10 s) is far
      // longer than the bus-wake tests' wait window, so signal-driven behavior is unchanged for them.
      // When the bus isn't wired (legacy DI), fall back to the adaptive backoff.
      try {
        // A pending wake permit short-circuits both waits below, and the system's own completion
        // traffic keeps setting one: publishes complete, completions signal, the permit is
        // released, and the loop re-enters immediately — even when the claim is only re-offering
        // work it already emitted. That feedback path is why the spin rate tracks query latency
        // rather than workload, and why an almost-empty store sustains it as readily as a large
        // one. When the last claim was a pure re-offer, space the next one out BEFORE waiting on
        // the permit. New work stays responsive: it sets the permit during this delay, so the
        // wait below returns immediately and the added latency is bounded by the delay itself.
        // #635: a pure-EMPTY claim spaces out exactly like a re-offer, but only while the
        // signaling gate reports NOTIFY healthy — the doorbell will announce new work, so the
        // permit-per-completion feedback that keeps short-circuiting the wait below must not set
        // the idle cadence. Measured before this: ~27 claim cycles/sec fleet-wide on a deployment
        // with zero application traffic, each cycle a rank + claim + outstanding-count round trip.
        // When the gate is unavailable (or absent), idle polling stays tight, because polling is
        // then the only way work is discovered at all.
        // A doorbell that rang between the claim above and this point must skip the nap outright:
        // SignalNewWork's cancel only reaches a nap that has already registered its token, so
        // without this check a doorbell in that window would wait out the full floor. The flag is
        // not consumed here — the next claim's liveness accounting still reads it.
        var doorbellPending = Volatile.Read(ref _doorbellSinceLastClaim) == 1;
        var spaceOut = !doorbellPending
          && (_lastClaimWasRepeat
            || (Volatile.Read(ref _consecutiveEmptyPolls) > 0 && _signalingGate?.IsAvailable == true));
        if (spaceOut) {
          var floorMs = Math.Min(_computeAdaptivePollWaitMs(), _options.PollingMaxIntervalMilliseconds);
          if (floorMs > 0) {
            // Interruptible nap: a NEW-WORK doorbell (SignalNewWork) cancels it so a fresh row
            // proceeds straight to the wake wait below, where the permit the doorbell released
            // is already pending. Completion-feedback wakes cannot reach this token — letting
            // them would reintroduce the re-offer spin loop this spacing exists to damp.
            using var napCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            Volatile.Write(ref _napCts, napCts);
            try {
              await Task.Delay(floorMs, napCts.Token);
            } catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) {
              // Nap cut short by new work — fall through to the wake wait.
            } finally {
              Volatile.Write(ref _napCts, null);
            }
          }
        }
        if (_signalBus is not null) {
          _ = await _wake.WaitAsync(TimeSpan.FromMilliseconds(_options.PollingMaxIntervalMilliseconds), stoppingToken);
        } else {
          _ = await _wake.WaitAsync(TimeSpan.FromMilliseconds(_computeAdaptivePollWaitMs()), stoppingToken);
        }
      } catch (OperationCanceledException) {
        break;
      }
    }

    LogStopped(_logger);
  }

  private async Task _distributeAsync(WorkBatch batch, CancellationToken ct) {
    // Ordering invariant: write each category in MessageId order so downstream channel readers
    // (which preserve enqueue order) receive same-stream items chronologically. See
    // plans/ordered-stream-invariant.md.
    if (_outboxChannel is not null) {
      foreach (var ow in batch.OutboxWork.OrderByMessageId()) {
        await _outboxChannel.WriteAsync(ow, ct);
      }
    }
    if (_inboxChannel is not null) {
      // The claim already charged an attempt against every row here. If this loop is cut short —
      // shutdown, a full channel, a faulting writer — the rows never handed off have spent an
      // attempt for a dispatch that never happened, and will spend another on every future claim
      // until they dead-letter as MaxAttemptsExceeded having never reached a receptor. Hand them
      // back instead: the refund is only ever taken by a worker that KNOWS it did not dispatch,
      // so a process that dies here still (correctly) leaves its charge standing.
      // Skip rows already in flight. claim_work re-emits every row still leased to this instance and
      // unprocessed on EVERY poll, so without this the same row is queued again each cycle —
      // duplicate copies of work already being dispatched.
      //
      // Safe ONLY because in-flight entries now age out. An earlier IsInFlight write-time filter on
      // this path proved unrecoverable in production: a flag stranded by a hung or canceled task
      // made this worker discard that row's emits forever, and only restarting the process cleared
      // it. With ageing, a stranded flag stops mattering once the lease has lapsed — the row becomes
      // eligible again on its own, so the failure is self-healing rather than permanent.
      var ordered = batch.InboxWork
        .Where(w => !_inboxChannel.IsInFlight(w.MessageId))
        .OrderByMessageId()
        .ToList();
      var handedOff = 0;
      try {
        for (; handedOff < ordered.Count; handedOff++) {
          await _inboxChannel.WriteAsync(ordered[handedOff], ct);
        }
      } finally {
        // Only rows THIS loop failed to deliver are refunded. A row filtered out above was handed
        // off on an earlier poll and is being processed — refunding it would credit an attempt for
        // work that is genuinely in progress.
        if (handedOff < ordered.Count) {
          await _releaseUndispatchedAsync(ordered.Skip(handedOff).Select(w => w.MessageId).ToList());
        }
      }
    }
    if (_perspectiveChannel is not null) {
      foreach (var pw in batch.PerspectiveWork) {
        await _perspectiveChannel.WriteAsync(pw, ct);
      }
    }
    // Per-stream-drain emit: signal the drainer workers with stream_ids. The coordinator
    // populates WorkBatch.OutboxStreamIds / InboxStreamIds / PerspectiveStreamIds for us;
    // we just forward every stream_id every poll. We deliberately do NOT consult IsInFlight
    // here — Phase H step 6 slice 5 / Part B introduced an IsInFlight write-time filter that
    // turned out to be unrecoverable in production: a drain task that hung past its try/finally
    // (or crashed before MarkDrained ran, or got canceled mid-`_drainStreamInnerAsync`) left
    // the in-memory flag stuck forever, and ClaimWorker silently discarded every subsequent
    // claim_work emit for that stream. Observed in production — thousands of inbox rows leased to a
    // healthy instance with zero drain progress; only restart unstuck them. The reconciliation
    // belt is in the SQL: claim_work's eligible_* CTEs filter `instance_id = me AND lease_expiry > NOW()
    // AND processed_at IS NULL`, so they re-emit every leased row on every poll. The drainer's
    // session-local seen-set + idempotent fetch_*_batch (filters processed_at IS NULL) make
    // duplicate writes harmless — a second drain returns zero rows and exits.
    if (_perspectiveDrainChannel is not null) {
      foreach (var streamId in batch.PerspectiveStreamIds) {
        await _perspectiveDrainChannel.WriteAsync(streamId, ct);
      }
    }
    if (_outboxDrainChannel is not null) {
      foreach (var sid in batch.OutboxStreamIds) {
        await _outboxDrainChannel.WriteAsync(sid, ct);
      }
    }
    if (_inboxDrainChannel is not null) {
      foreach (var sid in batch.InboxStreamIds) {
        await _inboxDrainChannel.WriteAsync(sid, ct);
      }
    }
  }

  /// <summary>
  /// Hands claimed-but-undispatched inbox rows back, refunding the attempt the claim charged.
  /// </summary>
  /// <remarks>
  /// Deliberately does NOT take the caller's cancellation token: this runs on the shutdown path,
  /// where that token is already canceled. Using it would skip the release precisely when it
  /// matters most and leave the rows to burn their budget. Failures are swallowed and logged — a
  /// release that does not happen costs an attempt, which is strictly better than a shutdown that
  /// throws.
  /// </remarks>
  private async Task _releaseUndispatchedAsync(List<Guid> messageIds) {
    // No empty-guard here: the sole call site only fires when rows were left undelivered, and
    // ReleaseUnprocessedInboxAsync already returns 0 without opening a connection for an empty list
    // (locked by Coordinator_ReleaseUnprocessedInbox_EmptyList_IsANoOpAsync).
    try {
      using var scope = _scopeFactory.CreateScope();
      var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
      var released = await coordinator.ReleaseUnprocessedInboxAsync(
        _instanceProvider.InstanceId, messageIds, CancellationToken.None);
      LogReleasedUndispatched(_logger, released, messageIds.Count);
    } catch (Exception ex) {
      LogReleaseUndispatchedFailed(_logger, ex, messageIds.Count);
    }
  }

  /// <summary>Feeds the drain rate that sizes the outstanding budget.</summary>
  /// <remarks>
  /// Deliberately conservative: this counts only the NET decrease in outstanding work between
  /// samples, so work arriving in the same interval masks some completions and the measured rate
  /// comes out low. That understates capacity and therefore sizes the budget smaller — the safe
  /// direction to be wrong in, since the failure being prevented is holding too much.
  /// </remarks>
  private void _observeDrain(int outstanding) {
    var now = Stopwatch.GetTimestamp();

    if (_lastDrainTicks != 0 && _completionMeter is not null) {
      // Real completions, not a difference between outstanding readings. Rows arriving inside the
      // same interval would mask completions in a delta, so the measured rate would read low and
      // the budget would shrink for no reason — and a delta-based rate makes the control loop
      // untestable without wall-clock sleeps.
      var completed = (int)Math.Min(int.MaxValue, _completionMeter.ReadAndReset());
      _outstandingBudget.Observe(completed, Stopwatch.GetElapsedTime(_lastDrainTicks, now));
    }

    _lastOutstanding = outstanding;
    _lastDrainTicks = now;
  }

  private async Task<WorkBatch> _claimOnceAsync(CancellationToken ct) {
    await using var pin = await _pinnedPool.TryPinForAsync(typeof(ClaimWorker), ct);
    using var __ctx = PinnedConnectionContext.Push(pin.Connection);
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    // Opting out of the adaptive window means claiming at the operator's configured ceiling, not
    // at whatever value the window happens to hold. Previously this always read the window and
    // relied on it having been constructed AT the ceiling — so "disabled" meant "frozen wherever it
    // started" rather than "bypassed". That was invisible while the window started wide; once it
    // starts at the floor, the distinction is the difference between honouring the opt-out and
    // silently pinning every claim to the minimum.
    var maxStreams = _options.AdaptiveClaimWindow ? _claimWindow.Current : _options.MaxStreamsPerBatch;

    // Bound the TOTAL outstanding, not just this batch — a loop that claims and immediately claims
    // again accumulates held work across cycles regardless of batch size.
    //
    // The outstanding figure comes from the STORE, never from an in-memory flag. That is not a
    // stylistic preference: an earlier in-memory IsInFlight filter on this path proved unrecoverable
    // in production (see the emit loop below) — a flag stranded by a hung or canceled task made
    // this worker silently discard every later emit for that stream, and only a restart cleared it.
    // Any counter we maintain ourselves can be stranded the same way. claim_work's eligible_* CTEs
    // re-emit every row still leased to us and unprocessed on EVERY poll, so the previous claim's
    // counts are the outstanding total according to SQL, and it re-derives itself each cycle. A
    // wrong value cannot persist.
    //
    // No meter means no measured drain. Rather than let the rate read zero forever and pin every
    // deployment at the floor, the bound simply does not engage — an unmeasured budget is worse
    // than none, because it throttles silently and looks like a performance problem.
    if (_budgetEngaged) {
      var headroomRows = _outstandingBudget.Headroom(_lastOutstanding);

      // Convert the row budget into streams: the store claims by stream, and rows-per-stream varies
      // by orders of magnitude, so a fixed assumption would be wrong in one direction or the other.
      var streamsAffordable = (int)Math.Ceiling(headroomRows / Math.Max(1.0, _rowsPerStream));

      // NEVER drop to zero. Skipping the claim entirely is how the previous design deadlocked: the
      // poll is the only thing that observes outstanding work, so a worker that stops polling stops
      // being able to discover that it has recovered. Re-emitting rows we already hold costs no new
      // attempt — they are already leased to us — so polling at the floor is cheap and self-healing.
      maxStreams = Math.Max(1, Math.Min(maxStreams, streamsAffordable));
    }

    var batch = await coordinator.ClaimWorkAsync(new ClaimWorkRequest(
      InstanceId: _instanceProvider.InstanceId,
      ServiceName: _instanceProvider.ServiceName,
      HostName: _instanceProvider.HostName,
      ProcessId: _instanceProvider.ProcessId,
      MaxStreams: maxStreams,
      PartitionCount: _options.PartitionCount,
      LeaseSeconds: _options.LeaseSeconds,
      // #635: the budget reads these counts every cycle; carrying them on the claim's own round
      // trip removes a per-cycle call. Stores that ignore the flag leave batch.Outstanding null
      // and the fallback probe below still runs.
      IncludeOutstanding: _budgetEngaged,
      FreshWorkShare: _options.FreshWorkShare), ct);

    // Feed the claim back into the window. A row arriving with attempts > 1 is work already claimed
    // and not finished, so a high share means the batch outruns what this instance can dispatch
    // inside its lease — and every one of those rows has silently spent a retry attempt it never
    // used. Narrowing here is what stops a backlog consuming its own budget and dead-lettering
    // healthy messages as MaxAttemptsExceeded.
    // Keep the rows-per-stream estimate current. The store claims by stream while the budget is in
    // rows, and the ratio is workload-specific — mostly-singleton streams and a few thousand-row
    // streams both occur, so a fixed assumption would be wrong in one direction or the other.
    if (batch.InboxStreamIds.Count > 0 && batch.InboxWork.Count > 0) {
      var observed = (double)batch.InboxWork.Count / batch.InboxStreamIds.Count;
      _rowsPerStream = (0.2 * observed) + (0.8 * _rowsPerStream);
    }

    // Outstanding, straight from the store. claim_work re-emits everything still leased to this
    // instance and unprocessed, so these counts ARE the current outstanding total — re-derived every
    // poll rather than accumulated, which is what makes it impossible to strand.
    //
    // All three work kinds count. Every one of them is leased and charges an attempt, so bounding
    // only the inbox would leave the identical over-claim arithmetic free to recur in another
    // column — the failure would simply move rather than stop.
    if (_budgetEngaged) {
      // Ask the store what this instance is actually holding. The batch counts CANNOT answer that:
      // claim_work truncates its eligible_* CTEs to the limit computed above, so a figure taken
      // from them can never exceed that limit no matter how much work is held. Sizing the budget
      // from it means reading our own output instead of the system state — the budget stays wide
      // open, more work is claimed each poll, and held work grows without the number ever moving.
      // Prefer the counts the claim itself carried (#635) — same round trip, same snapshot. Null
      // means the store did not measure them there, so probe separately; it never means zero.
      var outstanding = batch.Outstanding
        ?? await coordinator.CountOutstandingWorkAsync(_instanceProvider.InstanceId, ct);
      if (outstanding is null) {
        // Unmeasurable is not zero. Zero would license a full-size claim on the strength of a
        // reading that was never taken, so the bound stands down instead — loudly, once.
        _outstandingUnmeasurable = true;
        LogOutstandingUnmeasurable(_logger);
      } else {
        _observeDrain((int)Math.Min(int.MaxValue, outstanding.Total));
      }
    }

    if (_options.AdaptiveClaimWindow) {
      // Churn is measured across BOTH claim representations. Iterating InboxWork alone reads zero
      // on the stream-id path — where rows arrive as stream ids and are fetched separately — so the
      // window saw "no work, no churn" and Observe() short-circuited on claimedRows <= 0, never
      // adapting for the life of the process. See ClaimChurnSignal.
      var attempts = new int[batch.InboxWork.Count];
      for (var i = 0; i < batch.InboxWork.Count; i++) {
        attempts[i] = batch.InboxWork[i].Attempts;
      }
      // Attempts are not available at claim time on the stream-id path — the claim returns stream
      // ids and never sees a row. The drain worker fetches them and reports what it saw, which is
      // the ONLY place the churn signal exists. Without this the window observes zero churn forever.
      var fed = _churnFeedback?.Take() ?? (0, 0);
      int[]? fetchedAttempts = null;
      if (fed.Item1 > 0) {
        // Reconstructed as attempt counts because that is the shape the signal measures; only the
        // re-claim COUNT is meaningful, not which specific rows churned.
        fetchedAttempts = new int[fed.Item1];
        for (var i = 0; i < fed.Item2 && i < fetchedAttempts.Length; i++) {
          fetchedAttempts[i] = 2;
        }
        for (var i = fed.Item2; i < fetchedAttempts.Length; i++) {
          fetchedAttempts[i] = 1;
        }
      }
      var churn = ClaimChurnSignal.Measure(
        materializedAttempts: attempts,
        streamIdCount: batch.InboxStreamIds.Count,
        fetchedAttempts: fetchedAttempts);
      var reclaimed = churn.Reclaimed;
      var previous = _claimWindow.Current;
      // Gate growth on measured drain ONLY while the budget is the governing control. When the
      // budget is not engaged at all (disabled, or no meter to measure with) it will never produce
      // a sample, and gating on one would freeze the window at its floor forever — turning a
      // cold-start guard into a permanent throughput ceiling for every deployment without a meter.
      // Unmeasured must not silently disable an unrelated control. See AdaptiveClaimWindow.Observe.
      // Unmeasured churn must not read as a clean cycle. Growing on evidence nobody gathered is
      // how a window widens on top of an unobserved thrash, so an unmeasurable cycle blocks growth
      // exactly as an unmeasured drain does. Shrinking stays ungated — backing off is always safe.
      var drainMeasured = (!_budgetEngaged || _outstandingBudget.HasDrainSample) && churn.IsMeasurable;
      _claimWindow.Observe(churn.ClaimedItems, reclaimed, drainMeasured);
      if (_claimWindow.Current != previous) {
        LogClaimWindowResized(_logger, previous, _claimWindow.Current, reclaimed, churn.ClaimedItems);
      }
    }

    return batch;
  }

  private async Task _initialHeartbeatAsync(CancellationToken ct) {
    await using var pin = await _pinnedPool.TryPinForAsync(typeof(ClaimWorker), ct);
    using var __ctx = PinnedConnectionContext.Push(pin.Connection);
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(
      InstanceId: _instanceProvider.InstanceId,
      ServiceName: _instanceProvider.ServiceName,
      HostName: _instanceProvider.HostName,
      ProcessId: _instanceProvider.ProcessId), ct);
  }


  /// <summary>
  /// Cheap identity for a claimed batch: the emitted stream_id sets plus the direct work counts.
  /// Two consecutive claims sharing a signature carried the same work, so the second made no
  /// progress. If claim_work ever returned these in an unstable order the signatures would
  /// differ and the loop would simply fall back to today's cadence — the degradation is toward
  /// the existing behaviour, never toward suppressing work.
  /// </summary>
  private static int _workSignature(WorkBatch batch) {
    var hash = new HashCode();
    hash.Add(batch.OutboxWork.Count);
    hash.Add(batch.InboxWork.Count);
    hash.Add(batch.PerspectiveWork.Count);
    foreach (var id in batch.OutboxStreamIds) { hash.Add(id); }
    foreach (var id in batch.InboxStreamIds) { hash.Add(id); }
    foreach (var id in batch.PerspectiveStreamIds) { hash.Add(id); }
    return hash.ToHashCode();
  }

  private int _computeAdaptivePollWaitMs() {
    var baseMs = _options.PollingIntervalMilliseconds;
    // Slice 33.6 — when the gate has flipped NOTIFY availability to false, the listener
    // won't wake us when work arrives, so we MUST keep polling at the tight base cadence
    // (do not let the adaptive backoff stretch out to PollingMaxIntervalMilliseconds —
    // that would silently increase latency to up to 10 s while NOTIFY is broken).
    if (_signalingGate?.IsAvailable == false) {
      return baseMs;
    }
    if (!_options.EnableSafetyNetPoll && _signalingGate?.IsAvailable == true) {
      return int.MaxValue;
    }
    var notifyHealthyBase = _options.NotifyHealthyPollingIntervalMilliseconds;
    if (_signalingGate?.IsAvailable == true && notifyHealthyBase.HasValue && notifyHealthyBase.Value > baseMs) {
      baseMs = notifyHealthyBase.Value;
    }
    var maxMs = _options.PollingMaxIntervalMilliseconds;
    var empty = Volatile.Read(ref _consecutiveEmptyPolls);
    if (maxMs <= baseMs || empty <= 0) {
      return baseMs;
    }
    var shift = Math.Min(empty - 1, 10);
    var doubled = (long)baseMs << shift;
    return (int)Math.Min(doubled, maxMs);
  }

  /// <inheritdoc />
  public override void Dispose() {
    _outboxSignalSub?.Dispose();
    _inboxSignalSub?.Dispose();
    _perspectiveSignalSub?.Dispose();
    _outboxSignalSub = null;
    _inboxSignalSub = null;
    _perspectiveSignalSub = null;
    base.Dispose();
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "ClaimWorker started: pollMs={PollMs}, maxBackoffMs={MaxMs}, instance={InstanceId}")]
  static partial void LogStarted(ILogger logger, int pollMs, int maxMs, Guid instanceId);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "ClaimWorker tick failed; will back off and retry")]
  static partial void LogError(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "ClaimWorker stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 8, Level = LogLevel.Information,
    Message = "ClaimWorker resized its claim window {Previous} -> {Current} "
            + "(re-claimed {Reclaimed} of {Claimed} inbox rows last cycle)")]
  static partial void LogClaimWindowResized(
    ILogger logger, int previous, int current, int reclaimed, int claimed);

  [LoggerMessage(EventId = 9, Level = LogLevel.Information,
    Message = "ClaimWorker handed back {Released} of {Attempted} undispatched inbox rows, "
            + "refunding the attempt each claim charged")]
  static partial void LogReleasedUndispatched(ILogger logger, int released, int attempted);


  [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
    Message = "ClaimWorker could not hand back {Attempted} undispatched inbox rows; "
            + "they keep the attempt their claim charged and will be re-claimed")]
  static partial void LogReleaseUndispatchedFailed(ILogger logger, Exception ex, int attempted);

  [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "ClaimWorker disabled via options — claim loop skipped")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
    Message = "ClaimWorker initial heartbeat failed; first claim_work calls may raise until HeartbeatWorker registers the instance")]
  static partial void LogInitialHeartbeatFailed(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 6, Level = LogLevel.Information,
    Message = "ClaimWorker NOTIFY gate reconnected after {UnavailableMs}ms — running catch-up claim")]
  static partial void LogReconnectCatchUp(ILogger logger, long unavailableMs);

  [LoggerMessage(EventId = 7, Level = LogLevel.Information,
    Message = "ClaimWorker startup catch-up complete: picked up {ItemsPicked} pre-existing work item(s)")]
  static partial void LogStartupCatchUp(ILogger logger, int itemsPicked);

  // A bound that silently fails to engage is indistinguishable from one that is working, which is
  // how a previous version of this shipped, deployed, and looked correct while holding twelve times
  // the work it permitted. State it plainly at startup, and say WHICH precondition is missing.
  [LoggerMessage(EventId = 12, Level = LogLevel.Information,
    Message = "ClaimWorker outstanding budget ACTIVE: floor={Floor} rows, ceiling={Ceiling} rows, "
            + "leaseSeconds={LeaseSeconds}, safetyFactor={SafetyFactor}")]
  static partial void LogOutstandingBudgetActive(
    ILogger logger, int floor, int ceiling, int leaseSeconds, double safetyFactor);

  [LoggerMessage(EventId = 13, Level = LogLevel.Warning,
    Message = "ClaimWorker outstanding budget INACTIVE ({Reason}) — claimed work is not bounded by "
            + "measured drain; a backlog can be leased faster than it can be dispatched")]
  static partial void LogOutstandingBudgetInactive(ILogger logger, string reason);

  [LoggerMessage(EventId = 14, Level = LogLevel.Warning,
    Message = "ClaimWorker outstanding budget DISENGAGED: the work coordinator does not report "
            + "outstanding work, so the bound has nothing to measure against")]
  static partial void LogOutstandingUnmeasurable(ILogger logger);
}

/// <summary>Configuration for <see cref="ClaimWorker"/>.</summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed class ClaimWorkerOptions {
  /// <summary>
  /// Killswitch. Set to <c>false</c> to disable the claim loop entirely; the worker still
  /// runs as a hosted service but skips its <see cref="ExecuteAsync"/> body. Useful for
  /// maintenance windows or isolating a misbehaving instance without redeploying. Default <c>true</c>.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// When <c>true</c> (default), the claim loop runs a safety-net poll on the
  /// <see cref="NotifyHealthyPollingIntervalMilliseconds"/> cadence (default 30 s) even
  /// when LISTEN/NOTIFY is healthy, to catch any work the listener might have missed.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Set to <c>false</c> for pure NOTIFY-only operation: when the gate reports NOTIFY
  /// healthy, the loop sleeps until an actual signal arrives (Outbox/Inbox/Perspective/
  /// OrphanRedistribute) or a drain channel writes new work. Reduces idle DB load to
  /// essentially zero, at the cost of giving up the "what if we missed a NOTIFY?" backstop.
  /// The orphan-detection NOTIFY (slice B.3) + ScheduledRetryWorker (slice B.4) +
  /// reconnect catch-up (slice B.1) + startup catch-up (slice B.2) together cover every
  /// case the safety-net poll exists for, so disabling it is safe when those workers are
  /// running and NOTIFY is verified solid.
  /// </para>
  /// <para>
  /// When the gate flips unavailable, the safety net automatically re-engages at the
  /// tight <see cref="PollingIntervalMilliseconds"/> cadence regardless of this setting —
  /// a listener outage never causes silent claim-latency degradation.
  /// </para>
  /// </remarks>
  public bool EnableSafetyNetPoll { get; set; } = true;

  /// <summary>
  /// Share of each inbox claim batch reserved for fresh-head streams — streams whose earliest
  /// unprocessed row has never been attempted. Strict oldest-first ordering let a large retry
  /// backlog starve every new arrival (a production 28k-row control-plane backlog put a user's
  /// brand-new stream hours out); the claim is a weighted-fair merge instead. Work-conserving:
  /// when either class is empty the other takes the whole batch. 0.5 balances real-time work
  /// against backlog drain; raise it on services where interactive latency outranks backlog
  /// (1.0 = every fresh stream claims before any retry). Default 0.5.
  /// </summary>
  public double FreshWorkShare { get; set; } = 0.5;

  /// <summary>Base polling cadence in ms. Default 250.</summary>
  public int PollingIntervalMilliseconds { get; set; } = 250;
  /// <summary>Adaptive backoff cap in ms. Default 10 000 (10 s).
  /// Constrained at startup to <c>AbandonStaleInstanceThresholdSeconds × 1000 / 3</c>
  /// to preserve heartbeat-budget freshness.</summary>
  public int PollingMaxIntervalMilliseconds { get; set; } = 10_000;

  /// <summary>
  /// Relaxed baseline polling cadence when LISTEN/NOTIFY is verified healthy. Replaces
  /// <see cref="PollingIntervalMilliseconds"/> as the loop's base wait while the gate
  /// reports <c>IsAvailable=true</c>. Only takes effect when its value is greater than
  /// <see cref="PollingIntervalMilliseconds"/>; otherwise ignored.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Default <c>5_000</c> (5 s) as of v0.684. v0.502 introduced 30_000 to eliminate the
  /// ~4 calls/sec/pod safety-net cost; the v0.683 alpha dropped it to 1_000 to fix a
  /// 30 s+ cold-start latency observed on a production bulk-job import.
  /// 1_000 multiplied claim_work call count ~10× on the import drain path and combined
  /// with the v0.683 emit_chain guard to push claim_work back to 19 % of DB time in production
  /// (the original problem). 5_000 is the compromise: cold-start latency stays under
  /// 5 s, but workers don't poll claim_work every second on a steady-state DB.
  /// </para>
  /// <para>
  /// Root cause of the cold-start latency: <c>notify_instance_owners</c> joins
  /// <c>wh_active_streams</c> on the stream_id and emits <b>zero</b> per-instance
  /// NOTIFYs for streams not yet claimed. The proper architectural fix is a broadcast
  /// NOTIFY on first-touch (before per-instance fan-out); until that lands, this
  /// option's value is the trade-off between cold-start latency and steady-state cost.
  /// </para>
  /// <para>
  /// Falls back to the tight <see cref="PollingIntervalMilliseconds"/> automatically the
  /// moment NOTIFY availability flips to false (so a listener outage doesn't silently
  /// increase claim latency).
  /// </para>
  /// <para>
  /// Set explicitly to <c>null</c> to restore the pre-v0.502 behavior (tight polling always).
  /// </para>
  /// </remarks>
  public int? NotifyHealthyPollingIntervalMilliseconds { get; set; } = 5_000;
  /// <summary>Cap on rows returned per claim_work call. Default 1000.</summary>
  public int MaxStreamsPerBatch { get; set; } = 1000;

  /// <summary>
  /// Narrows the claim batch when work is being re-claimed rather than finished. Default true.
  /// </summary>
  /// <remarks>
  /// A claim charges an attempt per row, so rows claimed but never reached inside the lease window
  /// are re-claimed at another attempt each cycle and eventually dead-lettered as
  /// <see cref="Whizbang.Core.Messaging.MessageFailureReason.MaxAttemptsExceeded"/> having never
  /// reached a receptor. Without this, a backlog larger than one instance's throughput consumes its
  /// own retry budget and destroys healthy messages. Set false to pin the batch at
  /// <see cref="MaxStreamsPerBatch"/> — appropriate only where throughput is known to exceed
  /// arrival rate.
  /// </remarks>
  public bool AdaptiveClaimWindow { get; set; } = true;

  /// <summary>
  /// Floor for the adaptive claim window, so a struggling instance still makes progress. Default 25.
  /// </summary>
  public int MinStreamsPerBatch { get; set; } = 25;

  /// <summary>
  /// Bounds how many claimed-but-unprocessed inbox ROWS this instance may hold at once. Default true.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Distinct from <see cref="AdaptiveClaimWindow"/>, and not substitutable for it. The window bounds
  /// the size of each individual claim; this bounds the <i>total outstanding</i> across claims. A loop
  /// that claims and immediately claims again accumulates outstanding work every cycle no matter how
  /// small each batch is, until the whole backlog is held and its leases lapse together — so a batch
  /// bound alone changes only how long that takes, never whether it happens.
  /// </para>
  /// <para>
  /// Set false to restore the previous unbounded behaviour. Appropriate only where throughput is
  /// known to exceed arrival rate, since the failure mode it prevents is silent: rows dead-letter as
  /// <see cref="Whizbang.Core.Messaging.MessageFailureReason.MaxAttemptsExceeded"/> having never
  /// reached a receptor, with no handler failure to explain it.
  /// </para>
  /// </remarks>
  public bool AdaptiveOutstandingBudget { get; set; } = true;

  /// <summary>
  /// Minimum outstanding inbox rows, retained even when stalled. Default 100.
  /// </summary>
  /// <remarks>
  /// Also the cold-start value: a restarting instance has no drain history, and a restart carrying a
  /// large backlog is exactly when unbounded claiming does its damage, so capacity is earned from
  /// observed completions rather than assumed.
  /// </remarks>
  public int MinOutstandingInboxRows { get; set; } = 100;

  /// <summary>
  /// Hard ceiling on outstanding inbox rows, whatever the measured drain rate suggests. Default 10000.
  /// </summary>
  public int MaxOutstandingInboxRows { get; set; } = 10_000;

  /// <summary>
  /// Fraction of the lease window to plan against when sizing the outstanding budget. Default 0.5.
  /// </summary>
  /// <remarks>
  /// Below 1.0 buys deliberate headroom. Lease expiry is a cliff rather than a gradual degradation —
  /// at the full computed capacity any slowdown tips straight into mass expiry, and every expired row
  /// is re-claimed at another attempt. Raise it only if drain rate is very stable.
  /// </remarks>
  public double OutstandingBudgetSafetyFactor { get; set; } = 0.5;

  /// <summary>
  /// Streams added back per fully clean cycle. Default 25 — additive on purpose, since recovering
  /// multiplicatively would re-enter the overload that caused the shrink.
  /// </summary>
  public int ClaimWindowGrowthStep { get; set; } = 25;

  /// <summary>
  /// When true, ClaimWorker only distributes perspective stream IDs to the drain channel
  /// and ignores any outbox/inbox/per-event-perspective work in the claim batch.
  /// Set this when the legacy <c>WorkCoordinatorPublisherWorker</c> is also registered —
  /// otherwise both workers race to claim inbox/outbox rows and Phase 4.5B's event-store
  /// auto-create chain inside <c>process_work_batch</c> never sees the rows as orphans.
  /// </summary>
  public bool PerspectiveOnly { get; set; }
  /// <summary>Modulo partition count. Default 10000.</summary>
  public int PartitionCount { get; set; } = 10_000;
  /// <summary>Lease duration applied to claimed work. Default 300 s.</summary>
  public int LeaseSeconds { get; set; } = 300;
}
