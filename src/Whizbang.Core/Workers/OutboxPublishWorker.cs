using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Workers;

/// <summary>
/// Drains outbox work from <see cref="IWorkChannelWriter"/> and publishes each item to transport via
/// <see cref="IMessagePublishStrategy"/>. On success enqueues to <see cref="IOutboxCompletionChannel"/>;
/// on failure routes to <see cref="IFailureChannel"/>; on transport-not-ready re-buffers to the same
/// channel and queues a lease renewal via <see cref="ILeaseRenewalChannel"/>. Fires Pre/Post Outbox
/// lifecycle stages (PreOutboxDetached/Inline before publish, PostOutboxDetached/Inline +
/// PostLifecycleDetached/Inline after publish) when the lifecycle deps are wired.
/// </summary>
/// <remarks>
/// Replaces the publish-half of the legacy <c>WorkCoordinatorPublisherWorker</c>. The claim-half lives
/// in <see cref="ClaimWorker"/>; the DB-flush half lives in <see cref="OutboxCompletionFlushWorker"/>.
/// Picks bulk vs singular path from <see cref="IMessagePublishStrategy.SupportsBulkPublish"/>.
/// </remarks>
/// <docs>fundamentals/work-coordinator/outbox-publish</docs>
/// <remarks>
/// Constructor. <paramref name="publishStrategy"/> is optional so this worker can be
/// resolved by hosts that don't have a transport registered (e.g., DI-validation tests).
/// When the strategy is null, <see cref="ExecuteAsync"/> logs a warning and exits — equivalent
/// to <see cref="OutboxPublishWorkerOptions.Enabled"/> being false.
/// </remarks>
[method: System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Legacy publisher worker has many cooperating DI-injected dependencies by design; bundling would obscure DI registration intent.")]
public sealed partial class OutboxPublishWorker(
  IServiceScopeFactory scopeFactory,
  IWorkChannelWriter workChannelWriter,
  IOutboxCompletionChannel outboxCompletionChannel,
  IFailureChannel failureChannel,
  ILeaseRenewalChannel leaseRenewalChannel,
  ISchemaReadyGate schemaReadyGate,
  IOptions<OutboxPublishWorkerOptions> options,
  ILogger<OutboxPublishWorker> logger,
  IMessagePublishStrategy? publishStrategy = null,
  ILifecycleMessageDeserializer? lifecycleMessageDeserializer = null,
  IOptionsMonitor<TracingOptions>? tracingOptions = null,
  IOptions<LeaseHandleOptions>? leaseHandleOptions = null,
  IOptions<LeaseRenewalWorkerOptions>? leaseRenewalOptions = null,
  LeaseRegistry? leaseRegistry = null,
  TimeProvider? timeProvider = null,
  IServiceInstanceProvider? instanceProvider = null,
  IDeadLetterStore? deadLetterStore = null,
  IGenerationProvider? generationProvider = null,
  Whizbang.Core.Observability.DeadLetterMetrics? dlqMetrics = null,
  IPinnedConnectionPool? pinnedPool = null,
  IOccurrencePublishGate? occurrenceGate = null) : BackgroundService {
  private readonly IPinnedConnectionPool _pinnedPool = pinnedPool ?? NoOpPinnedConnectionPool.Instance;
  // Defaults to the no-op gate, so hosts that never registered one publish exactly as before.
  private readonly IOccurrencePublishGate _occurrenceGate = occurrenceGate ?? new NoOpOccurrencePublishGate();
  private const string LIFECYCLE_PRE_OUTBOX_ASYNC = "Lifecycle PreOutboxDetached";
  private const string LIFECYCLE_PRE_OUTBOX_INLINE = "Lifecycle PreOutboxInline";
  private const string LIFECYCLE_POST_OUTBOX_ASYNC = "Lifecycle PostOutboxDetached";
  private const string LIFECYCLE_POST_OUTBOX_INLINE = "Lifecycle PostOutboxInline";

  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly IMessagePublishStrategy? _publishStrategy = publishStrategy;
  private readonly IWorkChannelWriter _workChannelWriter = workChannelWriter ?? throw new ArgumentNullException(nameof(workChannelWriter));
  private readonly IOutboxCompletionChannel _outboxCompletionChannel = outboxCompletionChannel ?? throw new ArgumentNullException(nameof(outboxCompletionChannel));
  private readonly IFailureChannel _failureChannel = failureChannel ?? throw new ArgumentNullException(nameof(failureChannel));
  private readonly ILeaseRenewalChannel _leaseRenewalChannel = leaseRenewalChannel ?? throw new ArgumentNullException(nameof(leaseRenewalChannel));
  private readonly ISchemaReadyGate _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
  private readonly OutboxPublishWorkerOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<OutboxPublishWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly ILifecycleMessageDeserializer? _lifecycleMessageDeserializer = lifecycleMessageDeserializer;
  private readonly IOptionsMonitor<TracingOptions>? _tracingOptions = tracingOptions;
  private readonly LeaseHandleOptions _leaseHandleOptions = leaseHandleOptions?.Value ?? new LeaseHandleOptions();
  private readonly LeaseRenewalWorkerOptions _leaseRenewalOptions = leaseRenewalOptions?.Value ?? new LeaseRenewalWorkerOptions();
  private readonly LeaseRegistry? _leaseRegistry = leaseRegistry;
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
  // Slice 3b of release/v0.645.0-alpha.1 — when all three are wired AND
  // MaxOutboxAttempts is set, rows at/over the cap promote to wh_dead_letters
  // before falling through to the failure-channel path. Mirrors the
  // OutboxDrainWorker.cs:287-312 design but in the post-failure catch sites
  // since this worker has no pre-publish gate.
  private readonly IServiceInstanceProvider? _instanceProvider = instanceProvider;
  private readonly IDeadLetterStore? _deadLetterStore = deadLetterStore;
  private readonly IGenerationProvider? _generationProvider = generationProvider;
  private readonly Whizbang.Core.Observability.DeadLetterMetrics? _dlqMetrics = dlqMetrics;

  /// <summary>
  /// Event raised after a message is successfully published to transport. Mirrors the legacy
  /// <c>WorkCoordinatorPublisherWorker.OnOutboxMessagePublished</c> contract — test fixtures use it
  /// for deterministic synchronization.
  /// </summary>
  /// <docs>operations/workers/outbox-publish-worker#processing-hooks</docs>
  public event OutboxMessagePublishedHandler? OnOutboxMessagePublished;

  /// <summary>
  /// Event raised after the publish loop drains a batch and finds the work channel empty.
  /// Mirrors the legacy <c>WorkCoordinatorPublisherWorker.OnWorkProcessingIdle</c> contract —
  /// test fixtures use it as a "this worker has no pending work" signal.
  /// </summary>
  /// <docs>operations/workers/outbox-publish-worker#processing-hooks</docs>
  public event WorkProcessingIdleHandler? OnWorkProcessingIdle;

  /// <summary>
  /// True when the work channel currently has no pending items. Best-effort proxy — a producer
  /// can write between this read and the next loop iteration. Test fixtures wire
  /// <see cref="OnWorkProcessingIdle"/> after sampling <c>IsIdle</c> to close the race window.
  /// </summary>
  public bool IsIdle => _workChannelWriter.Reader.Count == 0;

  private void _maybeFireIdle() {
    if (_workChannelWriter.Reader.Count == 0) {
      OnWorkProcessingIdle?.Invoke();
    }
  }

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _publishStrategy?.SupportsBulkPublish ?? false, _options.MaxBulkPublishBatchSize);

    if (!_options.Enabled || _publishStrategy is null) {
      if (_publishStrategy is null) { LogNoTransportRegistered(_logger); }
      LogDisabled(_logger);
      try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
      LogStopped(_logger);
      return;
    }

    try {
      await _schemaReadyGate.WaitForReadyAsync(stoppingToken);
    } catch (OperationCanceledException) {
      return;
    }

    try {
      if (_publishStrategy!.SupportsBulkPublish) {
        await _bulkLoopAsync(stoppingToken);
      } else {
        await _singularLoopAsync(stoppingToken);
      }
    } catch (OperationCanceledException) {
      // expected on shutdown
    }

    LogStopped(_logger);
  }

  // ============================================================
  // Singular path
  // ============================================================

  private async Task _singularLoopAsync(CancellationToken stoppingToken) {
    await foreach (var work in _workChannelWriter.Reader.ReadAllAsync(stoppingToken)) {
      try {
        if (!await _publishStrategy!.IsReadyAsync(stoppingToken)) {
          await _handleTransportNotReadyAsync(work, stoppingToken);
          continue;
        }

        // Phase H step 9 slice 5: lease-tied cancellation. Wrap the publish + lifecycle stages
        // in a LeaseHandle whose token cancels at lease_expiry - LeaseGraceSeconds. A hung
        // PublishAsync that ignores its CT (e.g., transport library blocking on a TCP read
        // without timeout) gets abandoned by the executor; failure-channel routing runs.
        var leaseDeadline = _timeProvider.GetUtcNow()
          + TimeSpan.FromSeconds(Math.Max(1, _leaseRenewalOptions.LeaseSeconds - _leaseHandleOptions.LeaseGraceSeconds));
        using var lease = new LeaseHandle(
          workId: work.MessageId,
          category: WorkCategory.Outbox,
          deadline: leaseDeadline,
          maxRenewals: _leaseHandleOptions.MaxRenewalsPerWork,
          timeProvider: _timeProvider,
          linkedTokens: [stoppingToken]);
        _leaseRegistry?.Register(lease);

        await LeaseDispatchExecutor.RunWithLeaseAsync(lease, async leaseCt => {
          await using var pin = await _pinnedPool.TryPinForAsync(typeof(OutboxPublishWorker), leaseCt);
          using var __ctx = PinnedConnectionContext.Push(pin.Connection);
          await using var scope = _scopeFactory.CreateAsyncScope();

          // Pre-fire gate (F2): for a schedule occurrence this runs the developer's IScheduleFireHook
          // BEFORE the job executes — check security / refresh the run-as authority / skip / cancel /
          // defer. Non-occurrence messages (and hosts with no hook) short-circuit to Proceed, so the
          // normal publish path is untouched. Runs before the pre-outbox lifecycle so a dropped
          // occurrence does nothing at all.
          var gateDecision = await _occurrenceGate.EvaluateAsync(work, leaseCt);
          if (gateDecision is not OccurrencePublishDecision.Proceed) {
            if (gateDecision is OccurrencePublishDecision.Drop) {
              // Complete it without publishing — the occurrence is dropped, not delivered.
              await _outboxCompletionChannel.EnqueueAsync(work.MessageId, leaseCt);
            } else {
              // Deferred: the gate already rescheduled the message; just let go of it.
              _workChannelWriter.RemoveInFlight(work.MessageId);
            }
            return;
          }

          await SecurityContextHelper.EstablishFullContextAsync(work.Envelope, scope.ServiceProvider, leaseCt);
          var receptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
          var traceContext = _extractTraceContext(work);
          var enableLifecycleSpans = _tracingOptions?.CurrentValue.IsEnabled(TraceComponents.Lifecycle) ?? false;

          var (tracking, typedEnvelope) = await _invokePreOutboxLifecycleAsync(
            work, scope, receptorInvoker, traceContext, enableLifecycleSpans, leaseCt);

          var result = await _publishStrategy!.PublishAsync(work, leaseCt);

          await _invokePostOutboxLifecycleAsync(
            work, scope, tracking, typedEnvelope, traceContext, enableLifecycleSpans, leaseCt);

          await _routeResultAsync(work, result, leaseCt);
        });
      } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
        throw;
      } catch (Exception ex) {
        LogPublishFailed(_logger, work.MessageId, ex);
        _workChannelWriter.RemoveInFlight(work.MessageId);
        // Slice 3b: prefer DLQ promotion on cap-reached; ex.ToString() preserves
        // the full stack so Slice 2's SQL fingerprint sees the type token + frames.
        if (!await _tryPromoteToDlqAsync(work, ex.ToString(), stoppingToken)) {
          await _failureChannel.EnqueueAsync(WorkCategory.Outbox, new MessageFailure {
            MessageId = work.MessageId,
            CompletedStatus = work.Status,
            Error = ex.Message,
            Reason = MessageFailureReason.Unknown
          }, stoppingToken);
        }
      }
      _maybeFireIdle();
    }
  }

  // ============================================================
  // Bulk path
  // ============================================================

  private async Task _bulkLoopAsync(CancellationToken stoppingToken) {
    var maxBatchSize = _options.MaxBulkPublishBatchSize;
    await foreach (var firstWork in _workChannelWriter.Reader.ReadAllAsync(stoppingToken)) {
      var batch = new List<OutboxWork>(maxBatchSize) { firstWork };
      while (batch.Count < maxBatchSize && _workChannelWriter.Reader.TryRead(out var more)) {
        batch.Add(more);
      }

      try {
        if (!await _publishStrategy!.IsReadyAsync(stoppingToken)) {
          foreach (var w in batch) {
            await _handleTransportNotReadyAsync(w, stoppingToken);
          }
          continue;
        }

        // Phase H step 9 slice 5: bulk lease. One LeaseHandle for the whole batch keyed off the
        // first message; deadline = now + LeaseSeconds - GraceSeconds. The whole batch shares a
        // CT, so a hung PublishBatchAsync gets abandoned and ALL items in the batch route to the
        // failure channel via the existing catch. Per-row deadlines on the bulk path are out of
        // scope (see Phase H step 9 § "Out of scope").
        var leaseDeadline = _timeProvider.GetUtcNow()
          + TimeSpan.FromSeconds(Math.Max(1, _leaseRenewalOptions.LeaseSeconds - _leaseHandleOptions.LeaseGraceSeconds));
        using var lease = new LeaseHandle(
          workId: firstWork.MessageId,
          category: WorkCategory.Outbox,
          deadline: leaseDeadline,
          maxRenewals: _leaseHandleOptions.MaxRenewalsPerWork,
          timeProvider: _timeProvider,
          linkedTokens: [stoppingToken]);
        _leaseRegistry?.Register(lease);

        await LeaseDispatchExecutor.RunWithLeaseAsync(lease, async leaseCt => {
          // Per-item scope + Pre lifecycle prep BEFORE bulk publish
          var contexts = new List<BulkBatchContext>(batch.Count);
          foreach (var w in batch) {
            var scope = _scopeFactory.CreateAsyncScope();
            await SecurityContextHelper.EstablishFullContextAsync(w.Envelope, scope.ServiceProvider, leaseCt);
            var receptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
            var traceContext = _extractTraceContext(w);
            var enableLifecycleSpans = _tracingOptions?.CurrentValue.IsEnabled(TraceComponents.Lifecycle) ?? false;
            var (tracking, typedEnvelope) = await _invokePreOutboxLifecycleAsync(
              w, scope, receptorInvoker, traceContext, enableLifecycleSpans, leaseCt);
            contexts.Add(new BulkBatchContext(w, scope, tracking, typedEnvelope, traceContext, enableLifecycleSpans));
          }

          var results = await _publishStrategy!.PublishBatchAsync(batch, leaseCt);

          // Per-item Post lifecycle + result routing
          foreach (var ctx in contexts) {
            var r = results.FirstOrDefault(x => x.MessageId == ctx.Work.MessageId)
              ?? new MessagePublishResult {
                MessageId = ctx.Work.MessageId,
                Success = false,
                CompletedStatus = ctx.Work.Status,
                Error = "No result returned from batch publish",
                Reason = MessageFailureReason.Unknown
              };
            try {
              await _invokePostOutboxLifecycleAsync(
                ctx.Work, ctx.Scope, ctx.Tracking, ctx.TypedEnvelope, ctx.TraceContext, ctx.EnableLifecycleSpans, leaseCt);
              await _routeResultAsync(ctx.Work, r, leaseCt);
            } finally {
              await ctx.Scope.DisposeAsync();
            }
          }
        });
      } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
        throw;
      } catch (Exception ex) {
        LogPublishFailed(_logger, batch[0].MessageId, ex);
        var bulkErrorText = ex.ToString();
        foreach (var w in batch) {
          _workChannelWriter.RemoveInFlight(w.MessageId);
          // Slice 3b: bulk catches affect every row in the batch — promote each one
          // independently against its own attempts count.
          if (await _tryPromoteToDlqAsync(w, bulkErrorText, stoppingToken)) {
            continue;
          }
          await _failureChannel.EnqueueAsync(WorkCategory.Outbox, new MessageFailure {
            MessageId = w.MessageId,
            CompletedStatus = w.Status,
            Error = ex.Message,
            Reason = MessageFailureReason.Unknown
          }, stoppingToken);
        }
      }
      _maybeFireIdle();
    }
  }

  private readonly record struct BulkBatchContext(
    OutboxWork Work,
    AsyncServiceScope Scope,
    ILifecycleTracking? Tracking,
    IMessageEnvelope? TypedEnvelope,
    ActivityContext TraceContext,
    bool EnableLifecycleSpans);

  // ============================================================
  // Common: route results, transport-not-ready
  // ============================================================

  private async Task _routeResultAsync(OutboxWork work, MessagePublishResult result, CancellationToken ct) {
    // Killswitch: drop the batch silently when disabled (defense-in-depth — ExecuteAsync gate
    // also short-circuits, but the inner loop may still drain a few items if Enabled flips
    // mid-run via IOptionsMonitor in a future revision).
    if (!_options.Enabled) {
      return;
    }
    if (result.Success) {
      await _outboxCompletionChannel.EnqueueAsync(work.MessageId, ct);
      OnOutboxMessagePublished?.Invoke(new OutboxMessagePublishedEvent {
        MessageId = work.MessageId,
        Destination = work.Destination
      });
    } else {
      _workChannelWriter.RemoveInFlight(work.MessageId);
      // Slice 3b: promote to DLQ first when attempts reached the cap; fall back to
      // failure-channel routing otherwise.
      if (await _tryPromoteToDlqAsync(work, result.Error ?? "publish failed", ct)) {
        return;
      }
      await _failureChannel.EnqueueAsync(WorkCategory.Outbox, new MessageFailure {
        MessageId = work.MessageId,
        CompletedStatus = result.CompletedStatus,
        Error = result.Error ?? "publish failed",
        Reason = result.Reason
      }, ct);
    }
  }

  /// <summary>
  /// Slice 3b of release/v0.645.0-alpha.1 — atomically promotes a failed outbox row
  /// into wh_dead_letters when <see cref="OutboxPublishWorkerOptions.MaxOutboxAttempts"/>
  /// is configured AND the row's attempts reached the cap AND the DLQ surface
  /// (<see cref="IDeadLetterStore"/>, <see cref="IGenerationProvider"/>,
  /// <see cref="IServiceInstanceProvider"/>) is fully wired. Returns true on a
  /// successful promotion; the caller MUST skip the normal failure-channel enqueue
  /// because move_to_dead_letters has DELETEd the source row.
  ///
  /// <para>Returns false when the gate doesn't fire (cap not configured, below cap,
  /// missing wiring) OR when MoveAsync throws (transient DB issue). On throw, the
  /// caller falls through to the failure-channel path; the next claim will retry
  /// the move via OutboxDrainWorker's pre-publish gate.</para>
  /// </summary>
  private async Task<bool> _tryPromoteToDlqAsync(OutboxWork work, string errorText, CancellationToken ct) {
    if (_deadLetterStore is null
        || _generationProvider is null
        || _instanceProvider is null
        || _options.MaxOutboxAttempts is not int maxAttempts
        || work.Attempts < maxAttempts) {
      return false;
    }
    try {
      await _deadLetterStore.MoveAsync(
        deadLetterId: (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo(),
        sourceTable: DeadLetterSourceTable.OUTBOX,
        sourceId: work.MessageId,
        failureReason: MessageFailureReason.MaxAttemptsExceeded,
        errorText: errorText,
        instanceId: _instanceProvider.InstanceId,
        generation: _generationProvider.GetGeneration(),
        ct: ct).ConfigureAwait(false);
      LogOutboxDlqPromoted(_logger, work.MessageId, work.Attempts, maxAttempts);
      _dlqMetrics?.Added.Add(1,
        new KeyValuePair<string, object?>("source_table", DeadLetterSourceTable.OUTBOX),
        new KeyValuePair<string, object?>("reason", "MaxAttemptsExceeded"));
      return true;
    } catch (Exception ex) {
      // Transient DB failure during MoveAsync — fall through to failure channel so
      // the row's attempts column bumps and the next claim cycle retries the move.
      LogOutboxDlqMoveFailed(_logger, work.MessageId, ex);
      return false;
    }
  }

  private async Task _handleTransportNotReadyAsync(OutboxWork work, CancellationToken ct) {
    await _leaseRenewalChannel.EnqueueAsync(WorkCategory.Outbox, work.MessageId, ct);
    _workChannelWriter.TryWrite(work);
    var delayMs = _options.TransportNotReadyRetryDelayMilliseconds;
    if (delayMs > 0) {
      try {
        await Task.Delay(delayMs, ct);
      } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        throw;
      }
    }
  }

  // ============================================================
  // Lifecycle helpers (ported from legacy WorkCoordinatorPublisherWorker
  // _invokePreOutboxLifecycleAsync / _invokePostOutboxLifecycleAsync)
  // ============================================================

  private static ActivityContext _extractTraceContext(OutboxWork work) {
    var latestHop = work.Envelope.Hops?.LastOrDefault();
    ActivityContext traceContext = default;
    if (latestHop?.TraceParent is not null &&
        ActivityContext.TryParse(latestHop.TraceParent, null, out var parsed)) {
      traceContext = parsed;
    }
    return traceContext;
  }

  private async Task<(ILifecycleTracking? Tracking, IMessageEnvelope? TypedEnvelope)> _invokePreOutboxLifecycleAsync(
      OutboxWork work,
      AsyncServiceScope scope,
      IReceptorInvoker? receptorInvoker,
      ActivityContext traceContext,
      bool enableLifecycleSpans,
      CancellationToken stoppingToken) {
    if (_lifecycleMessageDeserializer is null || receptorInvoker is null) {
      return (null, null);
    }
    // Skip PreOutbox lifecycle for event-store-only messages (null destination).
    if (string.IsNullOrEmpty(work.Destination)) {
      return (null, null);
    }

    var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);
    var typedEnvelope = work.Envelope.ReconstructWithPayload(message);
    var coordinator = scope.ServiceProvider.GetService<ILifecycleCoordinator>();

    if (coordinator is not null) {
      var tracking = coordinator.BeginTracking(
        work.MessageId, typedEnvelope, LifecycleStage.PreOutboxDetached, MessageSource.Outbox);
      using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_PRE_OUTBOX_ASYNC, ActivityKind.Internal, parentContext: traceContext) : null) {
        await tracking.AdvanceToAsync(LifecycleStage.PreOutboxDetached, scope.ServiceProvider, stoppingToken);
      }
      using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_PRE_OUTBOX_INLINE, ActivityKind.Internal, parentContext: traceContext) : null) {
        await tracking.AdvanceToAsync(LifecycleStage.PreOutboxInline, scope.ServiceProvider, stoppingToken);
      }
      return (tracking, typedEnvelope);
    }

    var tracingOptions = new LifecycleTracingOptions(enableLifecycleSpans, traceContext);
    await _invokeLifecycleDirectAsync(receptorInvoker, typedEnvelope, LifecycleStage.PreOutboxDetached, work.Attempts, LIFECYCLE_PRE_OUTBOX_ASYNC, tracingOptions, stoppingToken);
    await _invokeLifecycleDirectAsync(receptorInvoker, typedEnvelope, LifecycleStage.PreOutboxInline, work.Attempts, LIFECYCLE_PRE_OUTBOX_INLINE, tracingOptions, stoppingToken);
    return (null, typedEnvelope);
  }

  private static async Task _invokePostOutboxLifecycleAsync(
      OutboxWork work,
      AsyncServiceScope scope,
      ILifecycleTracking? tracking,
      IMessageEnvelope? typedEnvelope,
      ActivityContext traceContext,
      bool enableLifecycleSpans,
      CancellationToken stoppingToken) {
    if (string.IsNullOrEmpty(work.Destination)) {
      return;
    }

    var coordinator = scope.ServiceProvider.GetService<ILifecycleCoordinator>();

    if (tracking is not null && coordinator is not null) {
      using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_POST_OUTBOX_ASYNC, ActivityKind.Internal, parentContext: traceContext) : null) {
        await tracking.AdvanceToAsync(LifecycleStage.PostOutboxDetached, scope.ServiceProvider, stoppingToken);
      }
      using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_POST_OUTBOX_INLINE, ActivityKind.Internal, parentContext: traceContext) : null) {
        await tracking.AdvanceToAsync(LifecycleStage.PostOutboxInline, scope.ServiceProvider, stoppingToken);
      }
      using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity("Lifecycle PostLifecycleDetached", ActivityKind.Internal, parentContext: traceContext) : null) {
        await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleDetached, scope.ServiceProvider, stoppingToken);
      }
      using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity("Lifecycle PostLifecycleInline", ActivityKind.Internal, parentContext: traceContext) : null) {
        await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, stoppingToken);
      }
      coordinator.AbandonTracking(work.MessageId);
      return;
    }

    if (typedEnvelope is null) {
      return;
    }
    var receptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
    if (receptorInvoker is null) {
      return;
    }
    var tracingOptions = new LifecycleTracingOptions(enableLifecycleSpans, traceContext);
    await _invokeLifecycleDirectAsync(receptorInvoker, typedEnvelope, LifecycleStage.PostOutboxDetached, work.Attempts, LIFECYCLE_POST_OUTBOX_ASYNC, tracingOptions, stoppingToken);
    await _invokeLifecycleDirectAsync(receptorInvoker, typedEnvelope, LifecycleStage.PostOutboxInline, work.Attempts, LIFECYCLE_POST_OUTBOX_INLINE, tracingOptions, stoppingToken);
  }

  private readonly record struct LifecycleTracingOptions(bool EnableLifecycleSpans, ActivityContext TraceContext);

  private static async Task _invokeLifecycleDirectAsync(
      IReceptorInvoker receptorInvoker,
      IMessageEnvelope typedEnvelope,
      LifecycleStage stage,
      int attempts,
      string activityName,
      LifecycleTracingOptions tracingOptions,
      CancellationToken stoppingToken) {
    using (tracingOptions.EnableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(activityName, ActivityKind.Internal, parentContext: tracingOptions.TraceContext) : null) {
      var ctx = new LifecycleExecutionContext {
        CurrentStage = stage,
        MessageSource = MessageSource.Outbox,
        AttemptNumber = attempts
      };
      await receptorInvoker.InvokeAsync(typedEnvelope, stage, ctx, stoppingToken);
      await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.ImmediateDetached,
        ctx with { CurrentStage = LifecycleStage.ImmediateDetached }, stoppingToken);
    }
  }

  // ============================================================
  // Logging
  // ============================================================

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "OutboxPublishWorker started: bulk={SupportsBulk}, maxBulkBatchSize={MaxBulkBatchSize}")]
  static partial void LogStarted(ILogger logger, bool supportsBulk, int maxBulkBatchSize);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "OutboxPublishWorker stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "OutboxPublishWorker disabled via options — publish loop skipped")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "OutboxPublishWorker publish failed for message {MessageId}; routing to failure channel")]
  static partial void LogPublishFailed(ILogger logger, Guid messageId, Exception ex);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "OutboxPublishWorker has no IMessagePublishStrategy registered — publish loop skipped (host did not register a transport)")]
  static partial void LogNoTransportRegistered(ILogger logger);

  [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
    Message = "OutboxPublishWorker dead-lettered {MessageId} at attempts={Attempts} (cap={MaxAttempts}) — moved to wh_dead_letters")]
  static partial void LogOutboxDlqPromoted(ILogger logger, Guid messageId, int attempts, int maxAttempts);

  [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
    Message = "OutboxPublishWorker IDeadLetterStore.MoveAsync failed for {MessageId} — falling through to failure channel")]
  static partial void LogOutboxDlqMoveFailed(ILogger logger, Guid messageId, Exception ex);

  /// <summary>
  /// Asks the discard policy whether an outbox row should be short-circuited before
  /// publish — defends-in-depth against publishing events with no known consumer
  /// anywhere when a future <c>IEventCatalog</c> seam provides that knowledge.
  /// </summary>
  /// <remarks>
  /// Today this is a no-op: <see cref="MessageDiscardPolicy.EvaluateOutbox"/> always
  /// returns <c>ShouldDiscard = false</c> so a misconfiguration can never silently
  /// suppress a publish. The seam exists so a populated catalog can opt-in later
  /// without changing the worker. Mirrors <c>ShouldSkipInbox</c> and
  /// <c>RabbitMQTransport.ShouldSkipReceive</c>.
  /// </remarks>
  internal static bool ShouldSkipOutboxPublish(
      IMessageDiscardPolicy? discardPolicy,
      string messageType,
      Guid messageId) {
    if (discardPolicy is null || string.IsNullOrEmpty(messageType)) {
      return false;
    }
    var decision = discardPolicy.EvaluateOutbox(messageType);
    if (!decision.ShouldDiscard) {
      return false;
    }
    discardPolicy.RecordDiscard(
      gate: MessageDiscardGate.Outbox,
      decision: decision,
      payloadClrType: messageType,
      additionalTags: new Dictionary<string, object?> { ["message_id"] = messageId });
    return true;
  }
}

/// <summary>Configuration for <see cref="OutboxPublishWorker"/>.</summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed class OutboxPublishWorkerOptions {
  /// <summary>
  /// Killswitch.
  /// <para>
  /// <strong>Default <c>false</c></strong> as of Phase H step 4b — the per-stream-id
  /// <see cref="OutboxDrainWorker"/> is now the active outbox publish path. This worker stays
  /// registered for one release as a rollback escape hatch; ops can flip both this and
  /// <see cref="OutboxDrainWorkerOptions.Enabled"/> to revert to the legacy full-body path.
  /// </para>
  /// <para>
  /// Setting both <see cref="OutboxPublishWorkerOptions.Enabled"/> and
  /// <see cref="OutboxDrainWorkerOptions.Enabled"/> to <c>true</c> simultaneously will publish
  /// every outbox row twice — exactly the multi-fire bug the new architecture eliminates. Pick one.
  /// </para>
  /// </summary>
  public bool Enabled { get; set; }

  /// <summary>Maximum batch size per bulk-publish call. Default 100.</summary>
  public int MaxBulkPublishBatchSize { get; set; } = 100;

  /// <summary>
  /// When the transport reports not-ready, the worker re-buffers the message and waits
  /// this long before retrying so it doesn't busy-loop. Default 100 ms; 0 disables (busy-loop).
  /// </summary>
  public int TransportNotReadyRetryDelayMilliseconds { get; set; } = 100;

  /// <summary>
  /// Dead-letter threshold for wh_outbox rows on the OutboxPublishWorker path. Slice 3b
  /// of release/v0.645.0-alpha.1 — mirrors
  /// <see cref="OutboxDrainWorkerOptions.MaxOutboxAttempts"/> so ops who haven't migrated
  /// off the legacy publisher (or who flip both for a rollback window) still get DLQ
  /// promotion. Without this knob, failed outbox rows retried forever — the production
  /// stuck-row pattern. Set to <c>null</c> explicitly to restore the pre-Slice-3b
  /// "retry forever" behavior.
  /// </summary>
  /// <docs>operations/dead-letter-queue/outbox-dlq-promotion</docs>
  public int? MaxOutboxAttempts { get; set; } = 10;
}
