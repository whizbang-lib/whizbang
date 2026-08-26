using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;

namespace Whizbang.Core.Workers;

/// <summary>
/// Drains orphan inbox work from <see cref="IInboxChannelWriter"/>, fires the inbox lifecycle
/// stages (Pre/Post Inbox Detached/Inline + PostAllPerspectives/PostLifecycle for events with no
/// registered perspectives), and packages each item as a <see cref="HandlerCommitRequest"/>
/// routed to <see cref="IInboxHandlerCommitChannel"/> (which the <see cref="InboxHandlerWorker"/>
/// commits via <c>commit_handler_batch</c>).
/// </summary>
/// <remarks>
/// <para>
/// This worker handles ORPHAN inbox messages — rows whose lease expired or whose original
/// processing was interrupted. Live receive-from-transport flow stays in
/// <c>TransportConsumerWorker</c> (which invokes the receptor and stores the inbox row in the
/// same scope). When a row is later picked up by <see cref="ClaimWorker"/> and routed onto
/// <see cref="IInboxChannelWriter"/>, it lands here for re-completion.
/// </para>
/// <para>
/// MaxInboxAttempts purge: when configured, work whose <c>Attempts</c> meets or exceeds the
/// threshold is dead-lettered with a terminal completion (status |= Published) instead of
/// being re-tried.
/// </para>
/// <para>
/// Detached lifecycle bodies (Pre/Post/PostAllPerspectives/PostLifecycle Detached) are
/// scheduled on the ThreadPool via <c>Task.Run</c>. An earlier version routed PostInbox
/// detached through <see cref="BackgroundStageDispatch.StartLongRunning"/> to avoid pool
/// starvation during CI runs, but that path spawned a dedicated OS thread per inbox
/// message and dominated dispatch cost on busy production services. Fix pool contention
/// at the host via <see cref="System.Threading.ThreadPool.SetMinThreads"/> when it
/// recurs, not by trading pool work for unbounded thread creation.
/// </para>
/// <para>
/// Detached bodies do NOT re-establish security context. The outer dispatch scope's
/// <see cref="SecurityContextHelper.EstablishFullContextAsync"/> call sets static
/// AsyncLocal accessors that flow into Task.Run continuations via ExecutionContext.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/inbox-dispatch</docs>
public sealed partial class InboxDispatchWorker : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly IInboxChannelWriter _inboxChannelWriter;
  private readonly WorkCompletionMeter? _completionMeter;
  private readonly IInboxHandlerCommitChannel _handlerCommitChannel;
  private readonly IFailureChannel _failureChannel;
  private readonly ISchemaReadyGate _schemaReadyGate;
  private readonly InboxDispatchWorkerOptions _options;
  private readonly WorkCoordinatorOptions _coordinatorOptions;
  private readonly LeaseHandleOptions _leaseHandleOptions;
  private readonly LeaseRenewalWorkerOptions _leaseRenewalOptions;
  private readonly LeaseRegistry? _leaseRegistry;
  private readonly TimeProvider _timeProvider;
  private readonly ILogger<InboxDispatchWorker> _logger;
  private readonly ILifecycleMessageDeserializer? _lifecycleMessageDeserializer;
  private readonly IReceptorRegistryQuery? _receptorRegistry;
  private readonly IReceptorRegistry? _runtimeReceptorRegistry;
  private readonly InboxDeserializeCache? _deserializeCache;
  private readonly IMessageDiscardPolicy? _discardPolicy;
  // v0.502 slice C.4 — optional DLQ persistence. When wired, the dead-letter branch
  // moves the row into wh_dead_letters via the atomic SQL function instead of just
  // marking it Published. Optional so existing callers (and the legacy fakes used by
  // many tests) continue to compile + run unchanged.
  private readonly IDeadLetterStore? _deadLetterStore;
  private readonly IGenerationProvider? _generationProvider;
  private readonly Whizbang.Core.Observability.DeadLetterMetrics? _dlqMetrics;
  // v0.660 slice 8 — per-message-type dispatch duration histogram.
  private readonly Whizbang.Core.Observability.InboxMetrics? _inboxMetrics;
  // v0.660 slice 3 — gate-derived clamp on internal dispatch fan-out. Optional so
  // existing test fixtures that don't wire a gate continue to compile + behave
  // as they did pre-slice-3.
  private readonly Whizbang.Core.Messaging.WorkCoordinatorGate? _gate;

  /// <summary>Constructor.</summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Worker has many cooperating DI-injected dependencies by design; bundling them into a container type would add indirection without reducing coupling.")]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0290:Use primary constructor", Justification = "Explicit constructor required because dotnet format's IDE0290 rewrite collided with the leading [SuppressMessage] attribute + multi-paragraph XML doc, producing CS1587. Keeping explicit form.")]
  public InboxDispatchWorker(
    IServiceScopeFactory scopeFactory,
    IServiceInstanceProvider instanceProvider,
    IInboxChannelWriter inboxChannelWriter,
    IInboxHandlerCommitChannel handlerCommitChannel,
    IFailureChannel failureChannel,
    ISchemaReadyGate schemaReadyGate,
    IOptions<InboxDispatchWorkerOptions> options,
    IOptions<WorkCoordinatorOptions> coordinatorOptions,
    ILogger<InboxDispatchWorker> logger,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer = null,
    IOptions<LeaseHandleOptions>? leaseHandleOptions = null,
    IOptions<LeaseRenewalWorkerOptions>? leaseRenewalOptions = null,
    LeaseRegistry? leaseRegistry = null,
    TimeProvider? timeProvider = null,
    IReceptorRegistryQuery? receptorRegistry = null,
    InboxDeserializeCache? deserializeCache = null,
    IMessageDiscardPolicy? discardPolicy = null,
    IReceptorRegistry? runtimeReceptorRegistry = null,
    IDeadLetterStore? deadLetterStore = null,
    IGenerationProvider? generationProvider = null,
    Whizbang.Core.Observability.DeadLetterMetrics? dlqMetrics = null,
    Whizbang.Core.Observability.InboxMetrics? inboxMetrics = null,
    Whizbang.Core.Messaging.WorkCoordinatorGate? gate = null,
    WorkCompletionMeter? completionMeter = null) {
    _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _inboxChannelWriter = inboxChannelWriter ?? throw new ArgumentNullException(nameof(inboxChannelWriter));
    _completionMeter = completionMeter;
    _handlerCommitChannel = handlerCommitChannel ?? throw new ArgumentNullException(nameof(handlerCommitChannel));
    _failureChannel = failureChannel ?? throw new ArgumentNullException(nameof(failureChannel));
    _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _coordinatorOptions = coordinatorOptions?.Value ?? throw new ArgumentNullException(nameof(coordinatorOptions));
    _leaseHandleOptions = leaseHandleOptions?.Value ?? new LeaseHandleOptions();
    _leaseRenewalOptions = leaseRenewalOptions?.Value ?? new LeaseRenewalWorkerOptions();
    _leaseRegistry = leaseRegistry;
    _timeProvider = timeProvider ?? TimeProvider.System;
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _lifecycleMessageDeserializer = lifecycleMessageDeserializer;
    _receptorRegistry = receptorRegistry;
    _deserializeCache = deserializeCache;
    _discardPolicy = discardPolicy;
    _runtimeReceptorRegistry = runtimeReceptorRegistry;
    _deadLetterStore = deadLetterStore;
    _generationProvider = generationProvider;
    _dlqMetrics = dlqMetrics;
    _inboxMetrics = inboxMetrics;
    _gate = gate;
  }

  /// <summary>
  /// Clamp the configured internal-dispatch fan-out against the gate's
  /// concurrency cap. Every dispatched message routes through a gated
  /// <c>IWorkCoordinator</c> call upstream, so configuring more dispatch
  /// consumers than the gate has slots just means consumers idle in the
  /// gate's queue — wasted ThreadPool fan-out with no throughput gain.
  /// </summary>
  /// <remarks>
  /// Pure helper extracted as the unit-testable boundary for slice 3 of
  /// plans/throughput-optimization.md. Preserves the pre-slice-3 floor-at-1
  /// invariant. A <paramref name="gateMaxConcurrent"/> of <c>0</c> (the
  /// documented "disabled gate" state) or <c>null</c> means "no upstream cap"
  /// and the configured value passes through (subject to the floor).
  /// </remarks>
  internal static int ClampPartitionCount(int configured, int? gateMaxConcurrent) {
    var floored = Math.Max(1, configured);
    if (gateMaxConcurrent is null or <= 0) {
      return floored;
    }
    return Math.Min(floored, gateMaxConcurrent.Value);
  }

  /// <inheritdoc />
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Worker ExecuteAsync orchestrates the full inbox dispatch lifecycle (claim, deserialize, dispatch, lifecycle stages, commit, retry); splitting would obscure the loop's invariants around lease ownership and cancellation propagation.")]
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _options.MaxInboxAttempts ?? -1);

    if (!_options.Enabled) {
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

    // Slice 14: stream-affinity hash partitioning. Spawn N internal queues + N consumer tasks.
    // The demux loop reads from the main inbox channel and routes each work to the queue indexed
    // by (StreamId.GetHashCode() mod N). Same-stream messages always land in the same queue, so
    // per-stream FIFO is preserved automatically — different streams parallelize across queues.
    // Different streams that hash-collide share a queue and process serially within that queue,
    // which is benign (different-stream order has no FIFO requirement).
    // v0.660 slice 3: clamp internal dispatch fan-out against the gate's cap.
    // See ClampPartitionCount XML doc for rationale.
    var configured = _options.MaxConcurrentDispatch;
    var partitionCount = ClampPartitionCount(configured, _gate?.MaxConcurrent);
    if (partitionCount < Math.Max(1, configured)) {
      LogPartitionCountClamped(_logger, configured, partitionCount, _gate!.MaxConcurrent);
    }
    var partitions = new Channel<InboxWork>[partitionCount];
    var consumers = new Task[partitionCount];
    for (var i = 0; i < partitionCount; i++) {
      partitions[i] = Channel.CreateUnbounded<InboxWork>(new UnboundedChannelOptions {
        SingleReader = true,
        SingleWriter = true,
        AllowSynchronousContinuations = false
      });
      var partitionReader = partitions[i].Reader;
      consumers[i] = Task.Run(async () => {
        try {
          await foreach (var work in partitionReader.ReadAllAsync(stoppingToken)) {
            try {
              await _processOneAsync(work, stoppingToken);
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
              throw;
            } catch (Exception ex) {
              LogDispatchError(_logger, work.MessageId, ex);
              _inboxChannelWriter.RemoveInFlight(work.MessageId);
              await _failureChannel.EnqueueAsync(WorkCategory.Inbox, new MessageFailure {
                MessageId = work.MessageId,
                CompletedStatus = work.Status,
                Error = ex.Message,
                Reason = MessageFailureReason.Unknown
              }, stoppingToken);
            }
          }
        } catch (OperationCanceledException) {
          // shutdown
        }
      }, stoppingToken);
    }

    try {
      await foreach (var work in _inboxChannelWriter.Reader.ReadAllAsync(stoppingToken)) {
        var partitionIndex = (int)((uint)work.StreamId.GetHashCode() % (uint)partitionCount);
        await partitions[partitionIndex].Writer.WriteAsync(work, stoppingToken).ConfigureAwait(false);
      }
    } catch (OperationCanceledException) {
      // expected on shutdown
    } finally {
      foreach (var p in partitions) {
        p.Writer.TryComplete();
      }
      try {
        await Task.WhenAll(consumers).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        // shutdown
      }
    }

    LogStopped(_logger);
  }

  private async Task _processOneAsync(InboxWork work, CancellationToken stoppingToken) {
    // Slice 31 PERF: per-message dispatch wall time. Surfaces whether inbox throughput is
    // bounded by the lifecycle invocation (security context + ReceptorInvoker + receptor body)
    // or by the surrounding plumbing (lease handle + DI scope + handler commit channel).
    // Timed via the injected TimeProvider (not raw Stopwatch) so tests can cross the
    // slow-dispatch threshold with a FakeTimeProvider instead of wall-clock delays.
    var dispatchStartTicks = _timeProvider.GetTimestamp();
    try {
      await ProcessOneInnerAsync(work, stoppingToken);
    } finally {
      // One row has stopped occupying this instance — success and failure alike, since both free
      // the capacity the claim loop budgets against. Recorded in `finally` because that is the only
      // point guaranteed to run exactly once per row: hooking the success path alone would
      // under-report drain on a service working hard on failing messages, and under-reporting is
      // not benign here — it reads as a service that cannot keep up and throttles one that can.
      _completionMeter?.Record();

      var totalMs = _timeProvider.GetElapsedTime(dispatchStartTicks).TotalMilliseconds;
      // v0.660 slice 8: always record into the per-message-type histogram so
      // operators can see distribution by event type without enabling Debug
      // logging. Operator dashboards filter the slow tail; nothing depends on
      // the >100ms gate.
      _inboxMetrics?.RecordDispatch(work.MessageType, totalMs);
      if (_logger.IsEnabled(LogLevel.Debug) && totalMs > 100) {
#pragma warning disable CA1848
        _logger.LogDebug(
          "PERF InboxDispatch message {MessageId} type {MessageType}: total={TotalMs:F0}ms attempts={Attempts}",
          work.MessageId, work.MessageType, totalMs, work.Attempts);
#pragma warning restore CA1848
      }
    }
  }

  /// <summary>
  /// v0.651 inbox forensic-preservation slice — mirror of v0.648's outbox Slice 1.
  /// Prefer the inbox row's existing <c>wh_inbox.error</c> column (the real exception text
  /// from the most recent <c>process_inbox_failures</c> cycle) over the synthetic
  /// <c>"InboxDispatchWorker dead-lettered"</c> meta-message. The meta-message collapses
  /// every DLQ row to a single fingerprint cluster (observed in production: tens of
  /// thousands of rows under one cluster regardless of root cause). Using <c>work.Error</c> restores forensic diversity
  /// so Slice 2's fingerprint algorithm extracts the real exception type + frames and
  /// operators see distinct clusters per failure mode.
  /// </summary>
  private static string _buildPromotionErrorText(InboxWork work, int maxAttempts) =>
    !string.IsNullOrWhiteSpace(work.Error)
      ? work.Error
      : $"InboxDispatchWorker dead-lettered: attempts={work.Attempts} > max={maxAttempts}";

  internal async Task ProcessOneInnerAsync(InboxWork work, CancellationToken stoppingToken) {
    LogDiagProcessOneEntered(_logger, work.MessageId, work.MessageType, work.Attempts);
    var maxAttempts = _options.MaxInboxAttempts;
    // Phase H step 8 slice D: attempts is one-based after the slice D refactor — the row
    // arrives here with attempts=1 on the first attempt, attempts=N on the Nth. Use strict
    // greater-than so MaxInboxAttempts = 5 means "5 total attempts allowed", matching the
    // pre-refactor count (the check used to be >= when attempts was zero-based).
    if (maxAttempts.HasValue && work.Attempts > maxAttempts.Value) {
      LogDeadLettered(_logger, work.MessageId, work.Attempts, maxAttempts.Value);

      // v0.502 slice C.4 — when an IDeadLetterStore is wired, persist the failing row
      // into wh_dead_letters with a forensic snapshot AND have the SQL function DELETE
      // it from wh_inbox in the same transaction. The handler-commit path (legacy
      // mark-Published) is then bypassed since the row no longer exists.
      //
      // When the store isn't wired, fall back to the legacy mark-Published path so
      // existing callers (in-memory tests, etc.) still terminate the row correctly.
      // Control-plane traffic is DROPPED, never stored: a stale checkpoint or repair request has
      // no forensic value (the audit re-issues them on its own cadence), and a stored copy is not
      // inert — the recovery worker re-emits it into the inbox on a later boot, so a burst of
      // failures becomes a backlog every restart replays.
      if (DeadLetterDropPolicy.ShouldDropInsteadOfStore(work.MessageType)) {
        LogControlPlaneDropped(_logger, work.MessageId, work.MessageType, work.Attempts);
        _dlqMetrics?.Added.Add(1,
          new KeyValuePair<string, object?>("source_table", DeadLetterSourceTable.INBOX),
          new KeyValuePair<string, object?>("reason", "ControlPlaneDropped"));
        var droppedRequest = _buildCommitRequest(work, status: (int)(work.Status | MessageProcessingStatus.Published));
        await _handlerCommitChannel.EnqueueAsync(droppedRequest, stoppingToken);
        return;
      }

      if (_deadLetterStore is not null && _generationProvider is not null) {
        try {
          var promotionErrorText = _buildPromotionErrorText(work, maxAttempts.Value);
          await _deadLetterStore.MoveAsync(
            deadLetterId: (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo(),
            sourceTable: DeadLetterSourceTable.INBOX,
            sourceId: work.MessageId,
            failureReason: Whizbang.Core.Messaging.MessageFailureReason.MaxAttemptsExceeded,
            errorText: promotionErrorText,
            instanceId: _instanceProvider.InstanceId,
            generation: _generationProvider.GetGeneration(),
            ct: stoppingToken).ConfigureAwait(false);
          _dlqMetrics?.Added.Add(1,
            new KeyValuePair<string, object?>("source_table", DeadLetterSourceTable.INBOX),
            new KeyValuePair<string, object?>("reason", "MaxAttemptsExceeded"));
          return;
        } catch (Exception ex) {
          // DLQ move is best-effort — if it fails, fall back to the legacy
          // mark-Published path so the row still gets terminal. The retry budget
          // is already exhausted; we never want to keep re-claiming it.
          LogDeadLetterStoreFailed(_logger, work.MessageId, ex);
        }
      }
      var terminalRequest = _buildCommitRequest(work, status: (int)(work.Status | MessageProcessingStatus.Published));
      await _handlerCommitChannel.EnqueueAsync(terminalRequest, stoppingToken);
      return;
    }

    // Slice 4 (discard-policy): early gate before the lease / scope / security-context
    // setup. When the active registry has no consumer for this row's message type,
    // mark the row terminal and short-circuit. Avoids the dispatch machinery firing
    // for messages that wouldn't invoke any receptor anyway — covers the
    // RegistryChanged case where a row was written before a receptor was removed.
    if (ShouldSkipInbox(_discardPolicy, work.MessageType, work.MessageId)) {
      LogDiagSkipBranch(_logger, work.MessageId, work.MessageType);
      var skipRequest = _buildCommitRequest(work, status: (int)(work.Status | MessageProcessingStatus.Published));
      await _handlerCommitChannel.EnqueueAsync(skipRequest, stoppingToken);
      return;
    }

    // Phase H step 9 slice 3: wrap dispatch in a LeaseHandle so a hung handler can't park the
    // dispatch pump forever. Deadline = now + LeaseSeconds - GraceSeconds. LeaseRenewalWorker
    // (slice 6) extends the deadline whenever it renews the SQL lease, up to MaxRenewalsPerWork.
    var deadline = _timeProvider.GetUtcNow()
      + TimeSpan.FromSeconds(Math.Max(1, _leaseRenewalOptions.LeaseSeconds - _leaseHandleOptions.LeaseGraceSeconds));
    using var lease = new LeaseHandle(
      workId: work.MessageId,
      category: WorkCategory.Inbox,
      deadline: deadline,
      maxRenewals: _leaseHandleOptions.MaxRenewalsPerWork,
      timeProvider: _timeProvider,
      linkedTokens: [stoppingToken]);
    _leaseRegistry?.Register(lease);

    await LeaseDispatchExecutor.RunWithLeaseAsync(lease, async ct => {
      LogDiagLambdaEntered(_logger, work.MessageId);
      // Lifecycle stages: scope-once, fire Pre + Post + (optional) PostAllPerspectives/PostLifecycle.
      // The lifecycle invocation no-ops cleanly when ILifecycleMessageDeserializer or IReceptorInvoker
      // is absent — same as the legacy publisher's _invokeInboxLifecycleStagesAsync.
      // Pass `ct` (lease token) for inline awaits + `stoppingToken` for fire-and-forget detached
      // stages so the latter aren't cancelled when the lease disposes on dispatch return.
      await using var scope = _scopeFactory.CreateAsyncScope();
      var secTimeoutSeconds = _options.SecurityContextTimeoutSeconds;
      var secOutcome = await SecurityContextHelper.TryEstablishFullContextWithTimeoutAsync(
        work.Envelope, scope.ServiceProvider, secTimeoutSeconds, ct);
      if (secOutcome == SecurityContextEstablishmentOutcome.TimedOut) {
        LogInboxSecurityContextTimedOut(_logger, work.MessageId, secTimeoutSeconds);
        await SecurityContextHelper.EnqueueSecurityContextTimeoutFailureAsync(
          _failureChannel, WorkCategory.Inbox, work.MessageId,
          work.Status, secTimeoutSeconds, ct);
        return;
      }
      LogDiagSecurityEstablished(_logger, work.MessageId);
      var receptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();

      // Slice 15: deserialize the envelope payload ONCE per message — all four lifecycle stages
      // reuse the same typed envelope. Cache hit on transport redelivery / lease re-claim within
      // the configured TTL. Returns null (typedEnvelope == null) when no deserializer or no
      // payload — lifecycle invocation then no-ops as before.
      var typedEnvelope = _resolveTypedEnvelope(work);

      // Composite fan-out (plans/composite-events-turnkey.md, Phase A): a composite arrives as an
      // ordinary inbox row. At the dispatch seam — inside the retry/DLQ envelope — it expands into N
      // child inbox rows, committed atomically with the composite row's deletion (EventStored bit ⇒
      // process_inbox_completions DELETEs it). The composite is never event-stored, only its children.
      // Returning early skips the composite's own lifecycle stages — those fire per-child instead.
      if (typedEnvelope?.Payload is ICompositeEvent composite) {
        await _fanoutCompositeAsync(work, composite, typedEnvelope, scope.ServiceProvider, receptorInvoker, ct);
        return;
      }

      // Stream-integrity Phase S: a state-only delivery (backfill) builds STATE — the event is
      // stored and projected exactly as normal via the commit below — but its trigger-receptor
      // stages are SKIPPED: backfilled history must never re-run business reactions. The
      // perspective-side lifecycle completion stages still fire (completion accounting, not
      // domain triggers).
      var stateOnly = work.Envelope.StateOnly;

      if (!stateOnly) {
        await InvokeInboxLifecycleStageAsync(
          work, typedEnvelope, scope, receptorInvoker, LifecycleStage.PreInboxDetached, LifecycleStage.PreInboxInline,
          "PreInbox", ct, detachedCancellationToken: stoppingToken);
        LogDiagPreInboxReturned(_logger, work.MessageId);
      }

      // Mark inbox completion via handler-commit channel — InboxHandlerWorker batches these and
      // calls commit_handler_batch.
      var commitRequest = _buildCommitRequest(work, status: (int)MessageProcessingStatus.EventStored);
      await _handlerCommitChannel.EnqueueAsync(commitRequest, ct);
      LogDiagCommitEnqueued(_logger, work.MessageId, commitRequest.InboxCompletion.Status);

      // PostInbox lands AFTER event storage.
      if (!stateOnly) {
        await InvokeInboxLifecycleStageAsync(
          work, typedEnvelope, scope, receptorInvoker, LifecycleStage.PostInboxDetached, LifecycleStage.PostInboxInline,
          "PostInbox", ct, detachedCancellationToken: stoppingToken);
        LogDiagPostInboxReturned(_logger, work.MessageId);
      }

      // For events with no registered perspectives, fire PostAllPerspectives + PostLifecycle here
      // (PerspectiveWorker fires them for events WITH perspectives after processing completes).
      if (!stateOnly && _hasNoPerspectives(work.MessageType, scope.ServiceProvider)) {
        await InvokeInboxLifecycleStageAsync(
          work, typedEnvelope, scope, receptorInvoker, LifecycleStage.PostAllPerspectivesDetached, LifecycleStage.PostAllPerspectivesInline,
          "PostAllPerspectives", ct, detachedCancellationToken: stoppingToken);
        await InvokeInboxLifecycleStageAsync(
          work, typedEnvelope, scope, receptorInvoker, LifecycleStage.PostLifecycleDetached, LifecycleStage.PostLifecycleInline,
          "PostLifecycle", ct, detachedCancellationToken: stoppingToken);
      }
    });
  }

  /// <summary>
  /// Slice 15: deserialize the message payload once per dispatch (or hit the cache on
  /// re-delivery within TTL). Returns null when no deserializer is registered, no envelope
  /// payload exists, or deserialization fails — callers then no-op the lifecycle stage.
  /// </summary>
  private IMessageEnvelope? _resolveTypedEnvelope(InboxWork work) {
    if (_lifecycleMessageDeserializer is null) {
      return null;
    }
    if (_deserializeCache is not null && _deserializeCache.TryGet(work.MessageId, out var cached) && cached is not null) {
      return work.Envelope.ReconstructWithPayload(cached);
    }
    try {
      var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);
      _deserializeCache?.Set(work.MessageId, message);
      return work.Envelope.ReconstructWithPayload(message);
    } catch (Exception ex) {
      // Deserialize is now best-effort at the top of dispatch; per-stage code logs lifecycle
      // errors but a fail here would silently skip ALL stages. Surface it once.
      LogLifecycleError(_logger, work.MessageId, "Deserialize", ex);
      return null;
    }
  }

  /// <summary>
  /// Fans a composite inbox row out into N child inbox rows at the dispatch seam. On success, a single
  /// <see cref="HandlerCommitRequest"/> carries the children and marks the composite row
  /// <see cref="MessageProcessingStatus.EventStored"/> so <c>process_inbox_completions</c> stores the
  /// children and DELETEs the composite atomically. On cap-exceeded / expansion failure, the composite
  /// row is dead-lettered via the same path as max-attempts (forensic snapshot + SQL delete), or — when
  /// no <see cref="IDeadLetterStore"/> is wired — terminated via the legacy mark-Published completion.
  /// </summary>
  private async Task _fanoutCompositeAsync(
      InboxWork work, ICompositeEvent composite, IMessageEnvelope typedEnvelope,
      IServiceProvider scopeProvider, IReceptorInvoker? receptorInvoker, CancellationToken ct) {
    // Pre-fanout hook (plans/composite-events-turnkey.md, Phase B): fire the composite's INLINE
    // receptors (IReceptor<TComposite>) before any child exists — so a receptor can validate the
    // batch, stamp metadata, or emit a durable BatchReceivedEvent. Their emissions are captured by
    // an ambient DispatchOutboxCollector (instead of writing to the outbox in a separate scope) and
    // folded into the SAME HandlerCommitRequest as the fan-out children → pre-fanout side-effects +
    // children commit all-or-nothing. Only inline receptors are collected: detached fire-and-forget
    // receptors outlive this scope and cannot be part of the atomic commit.
    var pre = await _invokePreFanoutHookAsync(work, typedEnvelope, receptorInvoker, ct);
    var preFanoutOutbox = pre.Outbox;

    // Fan-out control (Phase C): an imperative FanoutDirective set by the pre-fanout receptor takes
    // precedence over the composite's declarative FanoutMode.
    //   Skip                      → commit the receptor's emissions + delete the composite, no children.
    //   ReplaceWith(children)     → fan out the receptor-supplied set instead of InnerEvents.
    //   Proceed / none + Auto     → fan out InnerEvents (default).
    //   Proceed / none + Manual   → nothing auto-fans-out (the receptor chose not to drive it).
    var compositeTypeName = composite.GetType().FullName;
    var result = (pre.Directive?.Kind, composite.FanoutMode) switch {
      (FanoutDirectiveKind.Skip, _) =>
        new CompositeInboxFanout.FanoutResult(
          CompositeInboxFanout.FanoutOutcome.Expanded, Array.Empty<InboxMessage>(), null, compositeTypeName),
      (FanoutDirectiveKind.ReplaceWith, _) =>
        CompositeInboxFanout.TryExpand(composite, work.Envelope, scopeProvider, pre.Directive!.Replacement),
      (_, FanoutMode.Manual) =>
        new CompositeInboxFanout.FanoutResult(
          CompositeInboxFanout.FanoutOutcome.Expanded, Array.Empty<InboxMessage>(), null, compositeTypeName),
      _ => CompositeInboxFanout.TryExpand(composite, work.Envelope, scopeProvider),
    };
    if (result.Outcome == CompositeInboxFanout.FanoutOutcome.Expanded) {
      // MaxInnerEventsAllowed is declared by the COMPOSITE, so a composite carrying a hundred
      // thousand inner events simply declares a cap that large and passes. The consumer had no say,
      // and expansion happens after admission control has already accepted the message — so one
      // accepted message can add six figures of inbox rows, invalidating every downstream bound
      // (claim windows, outstanding budgets and lease sizing are all calibrated in rows).
      //
      // Observed: single composites expanding past 200,000 children, and a consumer's inbox
      // climbing for hours against an empty broker with no upstream producer. Producers in the same
      // deployment emitted composites of at most 16 rows, so composites inflate in transit.
      var budget = _options.MaxCompositeChildrenPerExpansion;
      if (budget > 0 && result.Children.Count > budget) {
        var plan = new Whizbang.Core.Messaging.CompositeExpansionBudget(budget)
          .Plan(result.Children.Count);
        LogCompositeExceedsConsumerBudget(
          _logger, compositeTypeName ?? "(unknown)", result.Children.Count, budget, plan.Chunks);

        if (_options.EnforceCompositeExpansionBudget) {
          // Dead-lettered rather than expanded. The DLQ is recoverable, so this defers the work for
          // an operator instead of discarding it — and it keeps one message from burying a consumer.
          var overReason = Whizbang.Core.Messaging.MessageFailureReason.CompositeInnerEventLimitExceeded;
          LogCompositeFanoutFailed(_logger, work.MessageId, overReason.ToString(),
            $"consumer budget {budget} exceeded by {result.Children.Count} children");
          await _deadLetterCompositeAsync(work, overReason,
            $"Composite '{compositeTypeName}' expanded to {result.Children.Count} children, "
            + $"exceeding this consumer's budget of {budget}.", ct).ConfigureAwait(false);
          return;
        }
      }

      var commitRequest = _buildCommitRequest(
        work, status: (int)MessageProcessingStatus.EventStored,
        newInboxMessages: result.Children, newOutboxMessages: preFanoutOutbox);
      await _handlerCommitChannel.EnqueueAsync(commitRequest, ct);
      return;
    }

    // CapExceeded / Failed: the composite is an inbox row that failed → dead-letter it (Phase 3 free).
    var reason = result.Outcome == CompositeInboxFanout.FanoutOutcome.CapExceeded
      ? Whizbang.Core.Messaging.MessageFailureReason.CompositeInnerEventLimitExceeded
      : Whizbang.Core.Messaging.MessageFailureReason.CompositeExpansionFailure;
    LogCompositeFanoutFailed(_logger, work.MessageId, reason.ToString(), result.Detail ?? "(none)");

    if (_deadLetterStore is not null && _generationProvider is not null) {
      try {
        await _deadLetterStore.MoveAsync(
          deadLetterId: (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo(),
          sourceTable: DeadLetterSourceTable.INBOX,
          sourceId: work.MessageId,
          failureReason: reason,
          errorText: result.Detail,
          instanceId: _instanceProvider.InstanceId,
          generation: _generationProvider.GetGeneration(),
          ct: ct).ConfigureAwait(false);
        _dlqMetrics?.Added.Add(1,
          new KeyValuePair<string, object?>("source_table", DeadLetterSourceTable.INBOX),
          new KeyValuePair<string, object?>("reason", reason.ToString()));
        return;
      } catch (Exception ex) {
        // DLQ move is best-effort — fall back to the legacy terminal completion so the row never
        // re-claims. Mirrors the max-attempts branch.
        LogDeadLetterStoreFailed(_logger, work.MessageId, ex);
      }
    }
    var terminalRequest = _buildCommitRequest(work, status: (int)(work.Status | MessageProcessingStatus.Published));
    await _handlerCommitChannel.EnqueueAsync(terminalRequest, ct);
  }

  /// <summary>
  /// Moves a composite to the dead-letter store, falling back to a terminal completion.
  /// </summary>
  /// <remarks>
  /// Mirrors the max-attempts and fan-out-failure branches: the DLQ move is best-effort, and if it
  /// fails the row is still marked terminal so it can never re-claim. Dead-lettering is recoverable,
  /// so this defers oversized work for an operator rather than discarding it.
  /// </remarks>
  private async Task _deadLetterCompositeAsync(
      InboxWork work, Whizbang.Core.Messaging.MessageFailureReason reason, string detail, CancellationToken ct) {
    if (_deadLetterStore is not null && _generationProvider is not null) {
      try {
        await _deadLetterStore.MoveAsync(
          deadLetterId: (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo(),
          sourceTable: DeadLetterSourceTable.INBOX,
          sourceId: work.MessageId,
          failureReason: reason,
          errorText: detail,
          instanceId: _instanceProvider.InstanceId,
          generation: _generationProvider.GetGeneration(),
          ct: ct).ConfigureAwait(false);
        _dlqMetrics?.Added.Add(1,
          new KeyValuePair<string, object?>("source_table", DeadLetterSourceTable.INBOX),
          new KeyValuePair<string, object?>("reason", reason.ToString()));
        return;
      } catch (Exception ex) {
        LogDeadLetterStoreFailed(_logger, work.MessageId, ex);
      }
    }
    var terminal = _buildCommitRequest(work, status: (int)(work.Status | MessageProcessingStatus.Published));
    await _handlerCommitChannel.EnqueueAsync(terminal, ct);
  }

  /// <summary>
  /// Fires the composite's inline receptors under an ambient <see cref="DispatchOutboxCollector"/> and
  /// returns whatever durable events they emitted, so the caller can commit them atomically with the
  /// fan-out children. Returns null when no inline receptor is registered for the composite type (the
  /// common case) — skipping the collector + invocation entirely. Inline only: detached receptors run
  /// fire-and-forget after this scope and cannot be part of the atomic commit.
  /// </summary>
  private async Task<PreFanoutResult> _invokePreFanoutHookAsync(
      InboxWork work, IMessageEnvelope typedEnvelope, IReceptorInvoker? receptorInvoker, CancellationToken ct) {
    if (receptorInvoker is null) {
      return default;
    }
    var runtimeType = typedEnvelope.Payload?.GetType();
    var hasPre = (_receptorRegistry?.HasReceptors(LifecycleStage.PreInboxInline, work.MessageType) ?? false)
      || _runtimeHasReceptors(runtimeType, LifecycleStage.PreInboxInline);
    var hasPost = (_receptorRegistry?.HasReceptors(LifecycleStage.PostInboxInline, work.MessageType) ?? false)
      || _runtimeHasReceptors(runtimeType, LifecycleStage.PostInboxInline);
    if (!hasPre && !hasPost) {
      return default;
    }

    var baseContext = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PreInboxInline,
      EventId = null,
      StreamId = null,
      LastProcessedEventId = null,
      MessageSource = MessageSource.Inbox,
      AttemptNumber = work.Attempts,
    };

    // Open the outbox collector (captures emitted events for the atomic commit) and the fan-out control
    // (lets the receptor impose a FanoutDirective) for the duration of the receptor invocation.
    using var collecting = DispatchOutboxCollector.BeginCollecting();
    using var controlling = DispatchFanoutControl.Begin();
    if (hasPre) {
      await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PreInboxInline,
        baseContext, ct);
    }
    if (hasPost) {
      await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PostInboxInline,
        baseContext with { CurrentStage = LifecycleStage.PostInboxInline }, ct);
    }
    var collected = collecting.Collector.Collected;
    return new PreFanoutResult(
      collected.Count == 0 ? null : collected,
      controlling.Control.Directive);
  }

  /// <summary>Outcome of the composite pre-fanout hook: emitted outbox messages + any imposed directive.</summary>
  private readonly record struct PreFanoutResult(
    IReadOnlyList<OutboxMessage>? Outbox,
    FanoutDirective? Directive);

  private HandlerCommitRequest _buildCommitRequest(
      InboxWork work, int status,
      IReadOnlyList<InboxMessage>? newInboxMessages = null,
      IReadOnlyList<OutboxMessage>? newOutboxMessages = null)
    => new(
      HandlerId: work.MessageId,
      InstanceId: _instanceProvider.InstanceId,
      ServiceName: _instanceProvider.ServiceName,
      HostName: _instanceProvider.HostName,
      ProcessId: _instanceProvider.ProcessId,
      PartitionCount: _options.PartitionCount,
      InboxCompletion: new HandlerInboxCompletion(work.MessageId, status),
      NewOutboxMessages: newOutboxMessages,
      NewInboxMessages: newInboxMessages,
      DebugMode: _coordinatorOptions.DebugMode);

  // ============================================================
  // Lifecycle invocation (ported from legacy
  // WorkCoordinatorPublisherWorker._invokeInboxLifecycleStagesAsync)
  // ============================================================

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Lifecycle stage invocation requires the full set of stage descriptors + ambient context; bundling would obscure call-site intent.")]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Lifecycle invocation handles the full detached-vs-inline branch matrix + receptor resolution + envelope reuse fallback; the branches are interrelated and splitting would force passing the typed envelope through helper boundaries.")]
  internal async Task InvokeInboxLifecycleStageAsync(
      InboxWork work,
      IMessageEnvelope? typedEnvelope,
      AsyncServiceScope scope,
      IReceptorInvoker? receptorInvoker,
      LifecycleStage detachedStage,
      LifecycleStage inlineStage,
      string stageName,
      CancellationToken cancellationToken,
      CancellationToken detachedCancellationToken = default) {
    // Phase H step 9: detached lifecycle stages run fire-and-forget on a separate scheduler
    // and outlive the dispatch. They MUST NOT use the lease token — when the lease disposes
    // on dispatch completion, the still-running detached tasks would throw a first-chance OCE
    // each (production observed thousands of OCEs/sec on a consumer's service). Callers pass the worker's
    // stoppingToken (or CancellationToken.None) here so detached stages run until shutdown.
    // If left default, falls back to <paramref name="cancellationToken"/> for backward-compat.
    var detachedCt = detachedCancellationToken == default ? cancellationToken : detachedCancellationToken;
    if (typedEnvelope is null || receptorInvoker is null) {
      return;
    }

    // Slice 4 of pump-then-process.md: gate by receptor registry. When the service has no
    // receptor registered for either the detached or inline form of this stage, skip the
    // scope creation, security context establishment, and Task.Run spawn entirely. Saves
    // pure overhead for cross-service event types in BFF-style services where many inbox
    // events flow through with no local handler. Null registries → legacy behavior (fire
    // unconditionally) for back-compat in test harnesses.
    //
    // CRITICAL: only gate stages the source generator actually populates — Pre/Post Inbox.
    // The static WhizbangReceptorRegistryQuery.HasReceptors returns false for unknown stages,
    // so a generic gate would silently SKIP PostAllPerspectives + PostLifecycle in production
    // (registry is injected by AddWhizbangWorkers). Tag-notification hooks would never fire
    // for cross-service events. Future generator extension can broaden this gate; for now
    // _isGatedStage explicitly enumerates the safe set.
    //
    // The gate consults BOTH registries:
    //   - <see cref="IReceptorRegistryQuery"/> covers compile-time-declared receptors via
    //     the source-generated WhizbangReceptorRegistryQuery static.
    //   - <see cref="IReceptorRegistry"/> covers runtime-registered receptors — both
    //     integration-test completion waits AND any production dynamic registrations.
    // Per IReceptorRegistry.GetReceptorsFor's contract, the runtime registry already
    // concatenates compile-time + runtime entries, so OR-ing the two sources here is the
    // authoritative "is anyone listening at this stage" check. Without the runtime branch
    // the gate would silently skip runtime-registered receptors (the integration-test
    // PreInboxDetached failure on a consumer's service was this exact bug).
    var runtimeMessageType = typedEnvelope.Payload?.GetType();
    var hasDetached = _receptorRegistry is null || !_isGatedStage(detachedStage)
      || _receptorRegistry.HasReceptors(detachedStage, work.MessageType)
      || _runtimeHasReceptors(runtimeMessageType, detachedStage);
    var hasInline = _receptorRegistry is null || !_isGatedStage(inlineStage)
      || _receptorRegistry.HasReceptors(inlineStage, work.MessageType)
      || _runtimeHasReceptors(runtimeMessageType, inlineStage);
    if (!hasDetached && !hasInline) {
      return;
    }

    try {
      var lifecycleContext = new LifecycleExecutionContext {
        CurrentStage = detachedStage,
        EventId = null,
        StreamId = null,
        LastProcessedEventId = null,
        MessageSource = MessageSource.Inbox,
        AttemptNumber = work.Attempts
      };

      // Slice 16: only spawn the detached fire-and-forget when a receptor is actually registered
      // for the detached stage. Without this guard, every message creates an extra DI scope +
      // Task.Run for a stage where nothing will fire — pure overhead.
      if (hasDetached) {
        // 2026-06: use Task.Run (pooled). TaskCreationOptions.LongRunning was originally
        // added to avoid CI ThreadPool starvation during PerspectiveWorker drain churn,
        // but in production it spawns a dedicated OS thread per inbox message — observed
        // throughput collapse on a consumer's service (10 msg/min vs target 1000s/min) traced here.
        // If pool starvation comes back, fix it at the host via ThreadPool.SetMinThreads.
        _ = Task.Run(async () => {
          try {
            // Fresh DI scope is required (detached body outlives the dispatch scope; needs
            // its own DbContext etc.). But we do NOT re-establish security context: the
            // outer dispatch scope's EstablishFullContextAsync call set the static
            // AsyncLocal accessors (ScopeContextAccessor + MessageContextAccessor),
            // which flow into Task.Run continuations via ExecutionContext by default.
            // Re-establishing wastes a security-extractor round-trip and (with DB-backed
            // extractors) competes for connections with the actual dispatch path.
            await using var detachedScope = _scopeFactory.CreateAsyncScope();
            var detachedInvoker = detachedScope.ServiceProvider.GetService<IReceptorInvoker>();
            if (detachedInvoker is null) {
              return;
            }
            var ctx = lifecycleContext with { CurrentStage = detachedStage };
            await detachedInvoker.InvokeAsync(typedEnvelope, detachedStage, ctx, detachedCt);
            await detachedInvoker.InvokeAsync(typedEnvelope, LifecycleStage.ImmediateDetached,
              ctx with { CurrentStage = LifecycleStage.ImmediateDetached }, detachedCt);
          } catch (OperationCanceledException) when (detachedCt.IsCancellationRequested) {
            // graceful shutdown
          } catch (Exception ex) {
            LogLifecycleError(_logger, work.MessageId, stageName + "Detached", ex);
            // Slice 7 of release/v0.645.0-alpha.1 — mirrors Slice 1's outbox fix:
            // route the lifecycle exception through IFailureChannel so
            // process_inbox_failures populates wh_inbox.error with the full
            // ex.ToString(). Without this, inbox lifecycle faults retried forever
            // silently — identical to a production outbox bug, different worker.
            await _failureChannel.EnqueueAsync(WorkCategory.Inbox, new MessageFailure {
              MessageId = work.MessageId,
              CompletedStatus = work.Status,
              Error = $"Lifecycle stage {stageName}Detached failed: {ex}",
              Reason = MessageFailureReason.Unknown,
            }, detachedCt);
          }
        }, detachedCt);
      }

      // Inline: blocks until complete
      if (hasInline) {
        lifecycleContext = lifecycleContext with { CurrentStage = inlineStage };
        await receptorInvoker.InvokeAsync(typedEnvelope, inlineStage, lifecycleContext, cancellationToken);
        await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.ImmediateDetached,
          lifecycleContext with { CurrentStage = LifecycleStage.ImmediateDetached }, cancellationToken);
      }
    } catch (Exception ex) {
      LogLifecycleError(_logger, work.MessageId, stageName, ex);
      // Slice 7 of release/v0.645.0-alpha.1 — mirrors Slice 1's outbox fix:
      // route the lifecycle exception through IFailureChannel so
      // process_inbox_failures populates wh_inbox.error with the full
      // ex.ToString(). Without this, inbox lifecycle faults retried forever
      // silently — identical to a production outbox bug, different worker.
      await _failureChannel.EnqueueAsync(WorkCategory.Inbox, new MessageFailure {
        MessageId = work.MessageId,
        CompletedStatus = work.Status,
        Error = $"Lifecycle stage {stageName} failed: {ex}",
        Reason = MessageFailureReason.Unknown,
      }, cancellationToken);
    }
  }

  /// <summary>
  /// True when the lifecycle stage is one the source-generated WhizbangReceptorRegistryQuery
  /// actually emits entries for. Stages outside this set return false unconditionally from
  /// HasReceptors, so the gate must NOT consult the registry for them — see the gate site
  /// in <see cref="InvokeInboxLifecycleStageAsync"/> for the failure mode.
  /// </summary>
  private static bool _isGatedStage(LifecycleStage stage) =>
    stage is LifecycleStage.PreInboxDetached
          or LifecycleStage.PreInboxInline
          or LifecycleStage.PostInboxDetached
          or LifecycleStage.PostInboxInline;

  private bool _runtimeHasReceptors(Type? messageType, LifecycleStage stage) {
    if (_runtimeReceptorRegistry is null || messageType is null) {
      return false;
    }
    return _runtimeReceptorRegistry.GetReceptorsFor(messageType, stage).Count > 0;
  }

  private static bool _hasNoPerspectives(string messageType, IServiceProvider serviceProvider) {
    var registry = serviceProvider.GetService<IPerspectiveRunnerRegistry>();
    if (registry is null) {
      return true;
    }
    var normalized = EventTypeMatchingHelper.NormalizeTypeName(messageType);
    foreach (var perspective in registry.GetRegisteredPerspectives()) {
      foreach (var eventType in perspective.EventTypes) {
        if (string.Equals(normalized, EventTypeMatchingHelper.NormalizeTypeName(eventType), StringComparison.Ordinal)) {
          return false;
        }
      }
    }
    return true;
  }

  // ============================================================
  // Logging
  // ============================================================

  [LoggerMessage(EventId = 71, Level = LogLevel.Warning,
    Message = "Composite '{CompositeType}' expanded to {ChildCount} child row(s), exceeding this "
            + "consumer's budget of {Budget} (would need {Chunks} steps). Expansion happens AFTER "
            + "admission control, so one accepted message added this many inbox rows — every "
            + "downstream bound (claim window, outstanding budget, lease sizing) is calibrated in "
            + "rows and cannot see it coming.")]
  static partial void LogCompositeExceedsConsumerBudget(
    ILogger logger, string compositeType, int childCount, int budget, int chunks);

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "InboxDispatchWorker started: maxInboxAttempts={MaxInboxAttempts}")]
  static partial void LogStarted(ILogger logger, int maxInboxAttempts);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "InboxDispatchWorker stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "InboxDispatchWorker disabled via options — dispatch loop skipped")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "InboxDispatchWorker dispatch failed for message {MessageId}; routing to failure channel")]
  static partial void LogDispatchError(ILogger logger, Guid messageId, Exception ex);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "InboxDispatchWorker dead-lettered message {MessageId}: attempts={Attempts} > max={MaxAttempts}")]
  static partial void LogDeadLettered(ILogger logger, Guid messageId, int attempts, int maxAttempts);

  [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "InboxDispatchWorker IDeadLetterStore.MoveAsync failed for {MessageId} — falling back to legacy mark-Published path")]
  static partial void LogDeadLetterStoreFailed(ILogger logger, Guid messageId, Exception ex);

  [LoggerMessage(EventId = 11, Level = LogLevel.Warning,
    Message = "InboxDispatchWorker DROPPED control-plane message {MessageId} ({MessageType}) after {Attempts} attempt(s) — " +
              "control-plane traffic is re-emitted on its own cadence and is never durably dead-lettered")]
  static partial void LogControlPlaneDropped(ILogger logger, Guid messageId, string messageType, int attempts);

  [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "InboxDispatchWorker lifecycle '{Stage}' failed for message {MessageId} (continuing)")]
  static partial void LogLifecycleError(ILogger logger, Guid messageId, string stage, Exception ex);

  [LoggerMessage(EventId = 26, Level = LogLevel.Warning, Message = "InboxDispatchWorker composite fan-out failed for message {MessageId}: {Reason} — {Detail}; dead-lettering composite row")]
  static partial void LogCompositeFanoutFailed(ILogger logger, Guid messageId, string reason, string detail);

  // Dispatch-checkpoint diagnostics for the "inbox rows never advance past
  // status=Stored with zero exception logs" failure class. At Debug so
  // operators can opt in via Serilog override; quiet by default to keep the
  // per-event log volume bounded under load.
  [LoggerMessage(EventId = 19, Level = LogLevel.Debug,
    Message = "DIAG[0] _processOneInnerAsync entered: message={MessageId} type={MessageType} attempts={Attempts}")]
  static partial void LogDiagProcessOneEntered(ILogger logger, Guid messageId, string messageType, int attempts);

  [LoggerMessage(EventId = 25, Level = LogLevel.Debug,
    Message = "DIAG[SKIP] discard policy short-circuited: message={MessageId} type={MessageType}")]
  static partial void LogDiagSkipBranch(ILogger logger, Guid messageId, string messageType);

  [LoggerMessage(EventId = 20, Level = LogLevel.Debug,
    Message = "DIAG[1] dispatch lambda entered: message={MessageId}")]
  static partial void LogDiagLambdaEntered(ILogger logger, Guid messageId);

  [LoggerMessage(EventId = 21, Level = LogLevel.Debug,
    Message = "DIAG[2] security context established: message={MessageId}")]
  static partial void LogDiagSecurityEstablished(ILogger logger, Guid messageId);

  [LoggerMessage(EventId = 22000, Level = LogLevel.Warning,
    Message = "InboxDispatchWorker EstablishFullContextAsync timed out for {MessageId} after {TimeoutSeconds}s — IMessageSecurityContextProvider implementation hung; routing to failure channel with Reason=SecurityContextEstablishmentFailure")]
  static partial void LogInboxSecurityContextTimedOut(ILogger logger, Guid messageId, int timeoutSeconds);

  [LoggerMessage(EventId = 22, Level = LogLevel.Debug,
    Message = "DIAG[3] PreInbox lifecycle returned: message={MessageId}")]
  static partial void LogDiagPreInboxReturned(ILogger logger, Guid messageId);

  [LoggerMessage(EventId = 23, Level = LogLevel.Debug,
    Message = "DIAG[4] commit enqueued: message={MessageId} status={Status}")]
  static partial void LogDiagCommitEnqueued(ILogger logger, Guid messageId, int status);

  [LoggerMessage(EventId = 24, Level = LogLevel.Debug,
    Message = "DIAG[5] PostInbox lifecycle returned: message={MessageId}")]
  static partial void LogDiagPostInboxReturned(ILogger logger, Guid messageId);

  [LoggerMessage(EventId = 25, Level = LogLevel.Warning,
    Message = "InboxDispatchWorker MaxConcurrentDispatch={Configured} exceeds WorkCoordinatorGate.MaxConcurrent={GateMaxConcurrent}; clamped to {EffectivePartitionCount}. Extra dispatch consumers would idle in the gate queue without delivering throughput. Either raise the gate (WorkCoordinatorGate.FromPoolSize, NpgsqlConfig MaxPoolSize) or lower MaxConcurrentDispatch to match.")]
  static partial void LogPartitionCountClamped(ILogger logger, int configured, int effectivePartitionCount, int gateMaxConcurrent);

  /// <summary>
  /// Asks the discard policy whether an inbox row should be short-circuited because
  /// no current consumer exists for its message type. When the policy says discard,
  /// records the skip telemetry (Information log + OTel counter) and returns
  /// <c>true</c>; the caller marks the row terminal and skips the lease/dispatch
  /// machinery. Returns <c>false</c> (legacy behaviour) when no policy is wired.
  /// </summary>
  /// <remarks>
  /// Internal-static for unit testability without spinning up the full worker. Mirrors
  /// the receive-time gates in <c>AzureServiceBusTransport.EmitAckDropTelemetry</c> and
  /// <c>RabbitMQTransport.ShouldSkipReceive</c> — discard reason here is
  /// <see cref="MessageDiscardReason.RegistryChanged"/>, which logs at Information so
  /// rolling-deploy drift is visible without being noisy.
  /// </remarks>
  internal static bool ShouldSkipInbox(
      IMessageDiscardPolicy? discardPolicy,
      string messageType,
      Guid messageId) {
    if (discardPolicy is null || string.IsNullOrEmpty(messageType)) {
      return false;
    }
    var decision = discardPolicy.EvaluateInbox(messageType);
    if (!decision.ShouldDiscard) {
      return false;
    }
    discardPolicy.RecordDiscard(
      gate: MessageDiscardGate.Inbox,
      decision: decision,
      payloadClrType: messageType,
      additionalTags: new Dictionary<string, object?> { ["message_id"] = messageId });
    return true;
  }
}

/// <summary>Configuration for <see cref="InboxDispatchWorker"/>.</summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed class InboxDispatchWorkerOptions {
  /// <summary>
  /// Killswitch. Set to <c>false</c> to disable the dispatch loop entirely. Default <c>true</c>.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Dead-letter threshold. Total number of attempts permitted before terminal commit. When set,
  /// work whose <see cref="InboxWork.Attempts"/> exceeds this value (i.e., we're entering the
  /// (N+1)<sup>th</sup> attempt where N = MaxInboxAttempts) is committed with a terminal status
  /// instead of being re-processed. Attempts are one-based: <c>Attempts == 1</c> on the first
  /// attempt, <c>Attempts == N</c> on the Nth. So <c>MaxInboxAttempts = 3</c> permits 3 attempts
  /// total; the 4th claim's dispatch dead-letters.
  /// </summary>
  /// <remarks>
  /// Default <c>10</c> (v0.502 change). Prior versions defaulted to <c>null</c> = no limit,
  /// which let permanently-failing handlers retry forever and accumulated wh_inbox row counts
  /// indefinitely. Set to <c>null</c> explicitly to restore the prior infinite-retry behavior.
  /// </remarks>
  public int? MaxInboxAttempts { get; set; } = 10;

  /// <summary>
  /// Modulo partition count carried into <see cref="HandlerCommitRequest"/>. Default 10000.
  /// </summary>
  public int PartitionCount { get; set; } = 10_000;

  /// <summary>
  /// Children one composite expansion may add to this consumer's inbox before it is reported
  /// (default 5000). Zero disables the check entirely.
  /// </summary>
  /// <remarks>
  /// Distinct from a composite's own <c>MaxInnerEventsAllowed</c>, which the MESSAGE declares —
  /// a composite carrying a hundred thousand inner events simply declares a cap that large and
  /// passes. This is the CONSUMER's bound on what it will absorb in one step.
  /// </remarks>
  public int MaxCompositeChildrenPerExpansion { get; set; } = 5000;

  /// <summary>
  /// Whether exceeding <see cref="MaxCompositeChildrenPerExpansion"/> dead-letters the composite
  /// (default false — report only).
  /// </summary>
  /// <remarks>
  /// Off by default because enforcing it changes delivery for workloads that function today.
  /// Reporting is always on, since the absence of any signal at expansion is what made an inbox
  /// growing by tens of thousands of rows per minute impossible to attribute from its own logs.
  /// When enabled the composite is dead-lettered, which is recoverable, rather than discarded.
  /// </remarks>
  public bool EnforceCompositeExpansionBudget { get; set; }

  /// <summary>
  /// Slice 14: number of parallel internal dispatch consumers. Same-stream messages always
  /// route to the same consumer (stream-affinity hash partitioning) so per-stream FIFO is
  /// preserved; different-stream messages parallelize across the N consumers. Default 8.
  /// <para>
  /// Sized for typical per-message dispatch cost on a saga consumer (~150-250 ms at the time
  /// slice 14 shipped). Set to 1 to restore the pre-slice-14 single-consumer behavior. Higher
  /// values trade ThreadPool fan-out for less inbox queue depth under burst load.
  /// </para>
  /// </summary>
  public int MaxConcurrentDispatch { get; set; } = 8;

  /// <summary>
  /// Timeout for the per-message <c>SecurityContextHelper.EstablishFullContextAsync</c>
  /// call. When the consumer-side <see cref="Whizbang.Core.Security.IMessageSecurityContextProvider"/>
  /// hangs longer than this, the worker cancels the call, enqueues a
  /// <see cref="MessageFailure"/> with
  /// <see cref="MessageFailureReason.SecurityContextEstablishmentFailure"/>, and
  /// proceeds to the next message. Default 10 s. Set to 0 to disable.
  /// </summary>
  /// <remarks>
  /// Mirror of <see cref="OutboxDrainWorkerOptions"/>.SecurityContextTimeoutSeconds.
  /// See operations/dead-letter-queue/internal-dlq for the production incident that
  /// motivated this safeguard.
  /// </remarks>
  /// <docs>operations/dead-letter-queue/internal-dlq</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/SecurityContextTimeoutTests.cs:InboxDispatchWorker_SecurityContextHangs_TimesOutAndEnqueuesFailureAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/SecurityContextTimeoutTests.cs:OutboxDrainWorker_SecurityContextHangs_TimesOutAndEnqueuesFailureAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/OutboxDrainWorkerGapTests.cs:OutboxDrainWorkerOptions_Defaults_MatchDocumentedValuesAsync</tests>
  public int SecurityContextTimeoutSeconds { get; set; } = 10;
}
