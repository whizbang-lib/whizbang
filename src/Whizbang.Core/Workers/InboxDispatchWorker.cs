using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
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
/// PostInboxDetached uses <see cref="BackgroundStageDispatch.StartLongRunning"/> instead of
/// <c>Task.Run</c> to avoid ThreadPool starvation under load. This mirrors the legacy publisher's
/// guard at line 1311-1313 — when PerspectiveWorker drains its channel concurrently with
/// our PostInbox dispatch, the ThreadPool can be saturated, and pooled <c>Task.Run</c>
/// continuations get queued past the test deadline.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/inbox-dispatch</docs>
public sealed partial class InboxDispatchWorker : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly IInboxChannelWriter _inboxChannelWriter;
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

  /// <summary>Constructor.</summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Worker has many cooperating DI-injected dependencies by design; bundling them into a container type would add indirection without reducing coupling.")]
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
    IReceptorRegistry? runtimeReceptorRegistry = null) {
    _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _inboxChannelWriter = inboxChannelWriter ?? throw new ArgumentNullException(nameof(inboxChannelWriter));
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
    var partitionCount = Math.Max(1, _options.MaxConcurrentDispatch);
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
    var dispatchStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    try {
      await _processOneInnerAsync(work, stoppingToken);
    } finally {
      if (_logger.IsEnabled(LogLevel.Debug)) {
        var totalMs = (System.Diagnostics.Stopwatch.GetTimestamp() - dispatchStartTicks)
          * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (totalMs > 100) {
#pragma warning disable CA1848
          _logger.LogDebug(
            "PERF InboxDispatch message {MessageId} type {MessageType}: total={TotalMs:F0}ms attempts={Attempts}",
            work.MessageId, work.MessageType, totalMs, work.Attempts);
#pragma warning restore CA1848
        }
      }
    }
  }

  private async Task _processOneInnerAsync(InboxWork work, CancellationToken stoppingToken) {
    var maxAttempts = _options.MaxInboxAttempts;
    // Phase H step 8 slice D: attempts is one-based after the slice D refactor — the row
    // arrives here with attempts=1 on the first attempt, attempts=N on the Nth. Use strict
    // greater-than so MaxInboxAttempts = 5 means "5 total attempts allowed", matching the
    // pre-refactor count (the check used to be >= when attempts was zero-based).
    if (maxAttempts.HasValue && work.Attempts > maxAttempts.Value) {
      LogDeadLettered(_logger, work.MessageId, work.Attempts, maxAttempts.Value);
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
      // Lifecycle stages: scope-once, fire Pre + Post + (optional) PostAllPerspectives/PostLifecycle.
      // The lifecycle invocation no-ops cleanly when ILifecycleMessageDeserializer or IReceptorInvoker
      // is absent — same as the legacy publisher's _invokeInboxLifecycleStagesAsync.
      // Pass `ct` (lease token) for inline awaits + `stoppingToken` for fire-and-forget detached
      // stages so the latter aren't cancelled when the lease disposes on dispatch return.
      await using var scope = _scopeFactory.CreateAsyncScope();
      await SecurityContextHelper.EstablishFullContextAsync(work.Envelope, scope.ServiceProvider, ct);
      var receptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();

      // Slice 15: deserialize the envelope payload ONCE per message — all four lifecycle stages
      // reuse the same typed envelope. Cache hit on transport redelivery / lease re-claim within
      // the configured TTL. Returns null (typedEnvelope == null) when no deserializer or no
      // payload — lifecycle invocation then no-ops as before.
      var typedEnvelope = _resolveTypedEnvelope(work);

      await _invokeInboxLifecycleStageAsync(
        work, typedEnvelope, scope, receptorInvoker, LifecycleStage.PreInboxDetached, LifecycleStage.PreInboxInline,
        "PreInbox", useLongRunningForDetached: false, ct, detachedCancellationToken: stoppingToken);

      // Mark inbox completion via handler-commit channel — InboxHandlerWorker batches these and
      // calls commit_handler_batch.
      var commitRequest = _buildCommitRequest(work, status: (int)MessageProcessingStatus.EventStored);
      await _handlerCommitChannel.EnqueueAsync(commitRequest, ct);

      // PostInbox lands AFTER event storage. Use LongRunning for the Detached stage so it can't be
      // starved by PerspectiveWorker drain churn.
      await _invokeInboxLifecycleStageAsync(
        work, typedEnvelope, scope, receptorInvoker, LifecycleStage.PostInboxDetached, LifecycleStage.PostInboxInline,
        "PostInbox", useLongRunningForDetached: true, ct, detachedCancellationToken: stoppingToken);

      // For events with no registered perspectives, fire PostAllPerspectives + PostLifecycle here
      // (PerspectiveWorker fires them for events WITH perspectives after processing completes).
      if (_hasNoPerspectives(work.MessageType, scope.ServiceProvider)) {
        await _invokeInboxLifecycleStageAsync(
          work, typedEnvelope, scope, receptorInvoker, LifecycleStage.PostAllPerspectivesDetached, LifecycleStage.PostAllPerspectivesInline,
          "PostAllPerspectives", useLongRunningForDetached: false, ct, detachedCancellationToken: stoppingToken);
        await _invokeInboxLifecycleStageAsync(
          work, typedEnvelope, scope, receptorInvoker, LifecycleStage.PostLifecycleDetached, LifecycleStage.PostLifecycleInline,
          "PostLifecycle", useLongRunningForDetached: false, ct, detachedCancellationToken: stoppingToken);
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

  private HandlerCommitRequest _buildCommitRequest(InboxWork work, int status)
    => new(
      HandlerId: work.MessageId,
      InstanceId: _instanceProvider.InstanceId,
      ServiceName: _instanceProvider.ServiceName,
      HostName: _instanceProvider.HostName,
      ProcessId: _instanceProvider.ProcessId,
      PartitionCount: _options.PartitionCount,
      InboxCompletion: new HandlerInboxCompletion(work.MessageId, status),
      NewOutboxMessages: null,
      NewInboxMessages: null,
      DebugMode: _coordinatorOptions.DebugMode);

  // ============================================================
  // Lifecycle invocation (ported from legacy
  // WorkCoordinatorPublisherWorker._invokeInboxLifecycleStagesAsync)
  // ============================================================

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Lifecycle stage invocation requires the full set of stage descriptors + ambient context; bundling would obscure call-site intent.")]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Lifecycle invocation handles the full detached-vs-inline branch matrix + receptor resolution + envelope reuse fallback; the branches are interrelated and splitting would force passing the typed envelope through helper boundaries.")]
  private async Task _invokeInboxLifecycleStageAsync(
      InboxWork work,
      IMessageEnvelope? typedEnvelope,
      AsyncServiceScope scope,
      IReceptorInvoker? receptorInvoker,
      LifecycleStage detachedStage,
      LifecycleStage inlineStage,
      string stageName,
      bool useLongRunningForDetached,
      CancellationToken cancellationToken,
      CancellationToken detachedCancellationToken = default) {
    // Phase H step 9: detached lifecycle stages run fire-and-forget on a separate scheduler
    // and outlive the dispatch. They MUST NOT use the lease token — when the lease disposes
    // on dispatch completion, the still-running detached tasks would throw a first-chance OCE
    // each (production observed thousands of OCEs/sec on a consumer BFF). Callers pass the worker's
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
    // PreInboxDetached failure on ECommerce BFF was this exact bug).
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
      // security context + Task.Run for a stage where nothing will fire — pure overhead.
      if (hasDetached) {
        Func<Func<Task>, CancellationToken, Task> scheduler = useLongRunningForDetached
          ? (body, ct) => BackgroundStageDispatch.StartLongRunning(body, ct)
          : (body, ct) => Task.Run(body, ct);

        _ = scheduler(async () => {
          try {
            await using var detachedScope = _scopeFactory.CreateAsyncScope();
            await SecurityContextHelper.EstablishFullContextAsync(typedEnvelope, detachedScope.ServiceProvider, detachedCt);
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
    }
  }

  /// <summary>
  /// True when the lifecycle stage is one the source-generated WhizbangReceptorRegistryQuery
  /// actually emits entries for. Stages outside this set return false unconditionally from
  /// HasReceptors, so the gate must NOT consult the registry for them — see the gate site
  /// in <see cref="_invokeInboxLifecycleStageAsync"/> for the failure mode.
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

  [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "InboxDispatchWorker lifecycle '{Stage}' failed for message {MessageId} (continuing)")]
  static partial void LogLifecycleError(ILogger logger, Guid messageId, string stage, Exception ex);

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
  /// total; the 4th claim's dispatch dead-letters. Null disables. Default <c>null</c>.
  /// </summary>
  public int? MaxInboxAttempts { get; set; }

  /// <summary>
  /// Modulo partition count carried into <see cref="HandlerCommitRequest"/>. Default 10000.
  /// </summary>
  public int PartitionCount { get; set; } = 10_000;

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
}
