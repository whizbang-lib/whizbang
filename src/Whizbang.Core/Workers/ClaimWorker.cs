using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;

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
  private readonly ILogger<ClaimWorker> _logger;
  private readonly SemaphoreSlim _wake = new(0, 1);
  private int _consecutiveEmptyPolls;

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
    INotifySignalingGate? signalingGate = null) {
#pragma warning restore S107
    _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _notificationListener = notificationListener ?? throw new ArgumentNullException(nameof(notificationListener));
    _signalingGate = signalingGate;
    _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _outboxChannel = outboxChannel;
    _inboxChannel = inboxChannel;
    _perspectiveChannel = perspectiveChannel;
    _perspectiveDrainChannel = perspectiveDrainChannel;
    _outboxDrainChannel = outboxDrainChannel;
    _inboxDrainChannel = inboxDrainChannel;

    // Subscribe to outbox/inbox signals only — perspective signals route to PerspectiveProcessWorker.
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
      _outboxChannel.OnNewWorkAvailable += RequestImmediatePoll;
    }
    if (_inboxChannel is not null) {
      _inboxChannel.OnNewInboxWorkAvailable += RequestImmediatePoll;
    }
  }

  private void _onSignal(WorkSignalCategory category) {
    if (category is WorkSignalCategory.Outbox or WorkSignalCategory.Inbox) {
      RequestImmediatePoll();
    }
  }

  private void _onGateAvailabilityChanged(bool nowAvailable) {
    // Slice 33.6 — on either transition (available → unavailable OR unavailable → available)
    // wake the poll loop immediately. Unavailable→available: drain any work that accumulated
    // during the unavailable window. Available→unavailable: shorten the next wait cycle so
    // we start tight-polling without waiting out the current adaptive backoff.
    RequestImmediatePoll();
  }

  /// <summary>
  /// Observable: the most recent <see cref="WorkBatch"/> distributed by the worker.
  /// Set whenever a tick produces a non-empty batch. Useful for wiring up downstream
  /// consumers in tests.
  /// </summary>
  public event Action<WorkBatch>? OnBatchClaimed;

  /// <summary>External wake — call from notification listener or local channel writer.</summary>
  public void RequestImmediatePoll() {
    if (_wake.CurrentCount == 0) {
      try { _wake.Release(); } catch (SemaphoreFullException) { /* already pending */ }
    }
  }

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _options.PollingIntervalMilliseconds, _options.PollingMaxIntervalMilliseconds, _instanceProvider.InstanceId);

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
    // claim_work invokes calculate_instance_rank, which raises P0001 when our instance_id
    // is missing from wh_service_instances. HeartbeatWorker performs the UPSERT but ticks
    // on its own cadence — without this initial heartbeat, ClaimWorker can race ahead and
    // hit the raise on startup. We block here so the very first claim cycle has a row to
    // rank against. Failures are non-fatal (log and continue — claim_work will retry/back off).
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

    while (!stoppingToken.IsCancellationRequested) {
      try {
        var batch = await _claimOnceAsync(stoppingToken);
        var hadWork = batch.OutboxWork.Count > 0
                   || batch.InboxWork.Count > 0
                   || batch.PerspectiveStreamIds.Count > 0
                   || batch.OutboxStreamIds.Count > 0
                   || batch.InboxStreamIds.Count > 0;

        if (hadWork) {
          _consecutiveEmptyPolls = 0;
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

      var waitMs = _computeAdaptivePollWaitMs();
      try {
        _ = await _wake.WaitAsync(TimeSpan.FromMilliseconds(waitMs), stoppingToken);
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
      foreach (var iw in batch.InboxWork.OrderByMessageId()) {
        await _inboxChannel.WriteAsync(iw, ct);
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
    // (or crashed before MarkDrained ran, or got cancelled mid-`_drainStreamInnerAsync`) left
    // the in-memory flag stuck forever, and ClaimWorker silently discarded every subsequent
    // claim_work emit for that stream. Observed on a consumer BFF — 3,545 inbox rows leased to a
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

  private async Task<WorkBatch> _claimOnceAsync(CancellationToken ct) {
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    return await coordinator.ClaimWorkAsync(new ClaimWorkRequest(
      InstanceId: _instanceProvider.InstanceId,
      ServiceName: _instanceProvider.ServiceName,
      HostName: _instanceProvider.HostName,
      ProcessId: _instanceProvider.ProcessId,
      MaxStreams: _options.MaxStreamsPerBatch,
      PartitionCount: _options.PartitionCount,
      LeaseSeconds: _options.LeaseSeconds), ct);
  }

  private async Task _initialHeartbeatAsync(CancellationToken ct) {
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(
      InstanceId: _instanceProvider.InstanceId,
      ServiceName: _instanceProvider.ServiceName,
      HostName: _instanceProvider.HostName,
      ProcessId: _instanceProvider.ProcessId), ct);
  }

  private int _computeAdaptivePollWaitMs() {
    var baseMs = _options.PollingIntervalMilliseconds;
    // Slice 33.6 — when the gate has flipped NOTIFY availability to false, the listener
    // won't wake us when work arrives, so we MUST keep polling at the tight base cadence
    // (do not let the adaptive backoff stretch out to PollingMaxIntervalMilliseconds —
    // that would silently increase latency to up to 10 s while NOTIFY is broken).
    // When the gate isn't wired (null), preserve the pre-slice-33 behavior.
    if (_signalingGate?.IsAvailable == false) {
      return baseMs;
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

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "ClaimWorker started: pollMs={PollMs}, maxBackoffMs={MaxMs}, instance={InstanceId}")]
  static partial void LogStarted(ILogger logger, int pollMs, int maxMs, Guid instanceId);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "ClaimWorker tick failed; will back off and retry")]
  static partial void LogError(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "ClaimWorker stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "ClaimWorker disabled via options — claim loop skipped")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
    Message = "ClaimWorker initial heartbeat failed; first claim_work calls may raise until HeartbeatWorker registers the instance")]
  static partial void LogInitialHeartbeatFailed(ILogger logger, Exception ex);
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

  /// <summary>Base polling cadence in ms. Default 250.</summary>
  public int PollingIntervalMilliseconds { get; set; } = 250;
  /// <summary>Adaptive backoff cap in ms. Default 10 000 (10 s).
  /// Constrained at startup to <c>AbandonStaleInstanceThresholdSeconds × 1000 / 3</c>
  /// to preserve heartbeat-budget freshness.</summary>
  public int PollingMaxIntervalMilliseconds { get; set; } = 10_000;
  /// <summary>Cap on rows returned per claim_work call. Default 1000.</summary>
  public int MaxStreamsPerBatch { get; set; } = 1000;

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
