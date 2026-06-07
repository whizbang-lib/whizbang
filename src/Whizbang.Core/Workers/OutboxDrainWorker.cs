using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;

namespace Whizbang.Core.Workers;

/// <summary>
/// Per-stream-id outbox drainer. Reads a stream_id from <see cref="IOutboxDrainChannel"/>,
/// calls <see cref="IWorkCoordinator.FetchOutboxBatchAsync"/> to pull all leased messages for
/// that stream, publishes each in stream-FIFO order via <see cref="IMessagePublishStrategy"/>,
/// and enqueues completion (or failure) via the per-category channels.
/// </summary>
/// <remarks>
/// <para>
/// Restores the archive-specified poller-vs-drainer split. The poller (<c>ClaimWorker</c>)
/// emits stream_ids only — small payload, cheap empty polls. This drainer fetches the actual
/// message bodies on demand and enforces per-stream FIFO automatically via channel-reader
/// semantics (one drainer task per stream_id at a time).
/// </para>
/// <para>
/// Replaces the body-on-poller path of <c>OutboxPublishWorker</c>. Lifecycle hooks
/// (PreOutboxDetached/Inline, PostOutboxDetached/Inline) and security context propagation are
/// fired around each publish — mirrors the legacy publisher's pattern and the sibling
/// <see cref="InboxDispatchWorker"/>. Optional dependencies (deserializer + receptor registry):
/// when absent, lifecycle invocation no-ops and the worker degrades to publish-and-complete.
/// Skipped entirely for event-store-only messages (null destination) — those rows exist for
/// event store persistence and don't transit transport.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public sealed partial class OutboxDrainWorker : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly IOutboxDrainChannel _drainChannel;
  private readonly IOutboxCompletionChannel _completionChannel;
  private readonly IFailureChannel _failureChannel;
  private readonly ISchemaReadyGate _schemaReadyGate;
  private readonly IMessagePublishStrategy? _publishStrategy;
  private readonly OutboxDrainWorkerOptions _options;
  private readonly JsonSerializerOptions _jsonOptions;
  private readonly ILogger<OutboxDrainWorker> _logger;
  private readonly ILifecycleMessageDeserializer? _lifecycleMessageDeserializer;
  private readonly IReceptorRegistryQuery? _receptorRegistry;
  private readonly IReceptorRegistry? _runtimeReceptorRegistry;
  // v0.502 slice C.4b — optional DLQ persistence + generation tag. When both wired, rows
  // whose Attempts exceed OutboxDrainWorkerOptions.MaxOutboxAttempts get moved into
  // wh_dead_letters via IDeadLetterStore.MoveAsync before any publish attempt.
  private readonly IDeadLetterStore? _deadLetterStore;
  private readonly IGenerationProvider? _generationProvider;
  private readonly Whizbang.Core.Observability.DeadLetterMetrics? _dlqMetrics;
  // Slice 26.6b: cached local service identity from wh_service_config; resolved once
  // on first drain (after schema-ready gate) and reused for envelope publish-time
  // injection. Guid.Empty until resolved.
  private Guid _localServiceId;
  // Idle-state tracking for fixture cleanup-between-tests coordination. Mirrors the
  // OutboxPublishWorker / PerspectiveWorker contract so fixtures can wait deterministically
  // for the drain pipeline to quiesce before truncating tables.
  private volatile bool _isIdle = true;

  /// <summary>
  /// True when the worker is currently between batches with no pending stream_ids to drain.
  /// Mirrors <see cref="OutboxPublishWorker.IsIdle"/> so fixtures that previously polled the
  /// legacy publisher keep working against the drain-worker path.
  /// </summary>
  /// <docs>operations/workers/publisher-worker</docs>
  public bool IsIdle => _isIdle;

  /// <summary>
  /// Fires on the idle → active transition (a non-empty batch starts processing).
  /// </summary>
  public event WorkProcessingStartedHandler? OnWorkProcessingStarted;

  /// <summary>
  /// Fires on the active → idle transition (a batch finishes and no more stream_ids are
  /// immediately pending in the drain channel). Use this in test fixtures to wait
  /// deterministically for the drain pipeline to quiesce.
  /// </summary>
  /// <docs>operations/workers/publisher-worker</docs>
  public event WorkProcessingIdleHandler? OnWorkProcessingIdle;

  /// <summary>
  /// Fired once per successful transport publish (after <see cref="IMessagePublishStrategy.PublishAsync"/>
  /// returns <c>Success = true</c>, before the completion is enqueued). Mirrors
  /// <c>OutboxPublishWorker.OnOutboxMessagePublished</c> so integration test fixtures that
  /// previously listened on the legacy worker keep working when the drain-worker path is
  /// the active publisher. Public hook by design: production code can wire post-publish
  /// observability or test fixtures can wire deterministic completion signals.
  /// </summary>
  /// <docs>operations/workers/publisher-worker</docs>
  public event OutboxMessagePublishedHandler? OnOutboxMessagePublished;

  /// <summary>Constructor.</summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Worker has many cooperating DI-injected dependencies by design; bundling them into a container type would add indirection without reducing coupling.")]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0290:Use primary constructor", Justification = "Explicit constructor required because dotnet format's IDE0290 rewrite collided with the leading [SuppressMessage] attribute + multi-paragraph XML doc, producing CS1587. Keeping explicit form.")]
  public OutboxDrainWorker(
    IServiceScopeFactory scopeFactory,
    IServiceInstanceProvider instanceProvider,
    IOutboxDrainChannel drainChannel,
    IOutboxCompletionChannel completionChannel,
    IFailureChannel failureChannel,
    ISchemaReadyGate schemaReadyGate,
    IOptions<OutboxDrainWorkerOptions> options,
    JsonSerializerOptions jsonOptions,
    ILogger<OutboxDrainWorker> logger,
    IMessagePublishStrategy? publishStrategy = null,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer = null,
    IReceptorRegistryQuery? receptorRegistry = null,
    IReceptorRegistry? runtimeReceptorRegistry = null,
    IDeadLetterStore? deadLetterStore = null,
    IGenerationProvider? generationProvider = null,
    Whizbang.Core.Observability.DeadLetterMetrics? dlqMetrics = null) {
    _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _drainChannel = drainChannel ?? throw new ArgumentNullException(nameof(drainChannel));
    _completionChannel = completionChannel ?? throw new ArgumentNullException(nameof(completionChannel));
    _failureChannel = failureChannel ?? throw new ArgumentNullException(nameof(failureChannel));
    _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _publishStrategy = publishStrategy;
    _lifecycleMessageDeserializer = lifecycleMessageDeserializer;
    _receptorRegistry = receptorRegistry;
    _runtimeReceptorRegistry = runtimeReceptorRegistry;
    _deadLetterStore = deadLetterStore;
    _generationProvider = generationProvider;
    _dlqMetrics = dlqMetrics;
  }

  /// <inheritdoc />
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Worker ExecuteAsync orchestrates schema-ready gate, drain channel polling, per-stream dispatch, and shutdown propagation — the branches are tightly coupled and inlining them keeps the lease/in-flight invariants visible at one site.")]
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _options.MaxPerStream);

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

    // Slice 26.6b: resolve local service identity once for the worker lifetime. Used
    // when injecting envelope SourceServiceId at publish-time; falls back to Guid.Empty
    // for legacy coordinators that don't track service identity.
    try {
      await using var initScope = _scopeFactory.CreateAsyncScope();
      var initCoordinator = initScope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
      _localServiceId = await initCoordinator.GetLocalServiceIdAsync(stoppingToken);
    } catch (OperationCanceledException) {
      return;
    } catch (Exception) {
      // Best-effort: leave _localServiceId at Guid.Empty if the lookup fails. Downstream
      // consumers' SQL trigger then COALESCEs to their own local service.
      _localServiceId = Guid.Empty;
    }

    var batcher = new SlidingWindowBatcher<Guid>(_drainChannel.Reader, _options.Batcher);
    try {
      await foreach (var batch in batcher.ReadBatchesAsync(stoppingToken)) {
        // Idle → active: each non-empty batch represents work.
        _setIdleState(active: true);
        try {
          // Dedupe within the batch — ClaimWorker may emit the same stream_id multiple times in
          // one window (rapid heartbeats during burst load). Each unique stream is drained once;
          // FetchOutboxBatchAsync returns all pending rows for it in stream-FIFO order.
          //
          // Slot-3 throughput fix: cross-stream draining is parallelized via Parallel.ForEachAsync
          // capped by MaxConcurrentStreams. Per-stream FIFO is preserved inside _drainStreamAsync
          // (one task per stream); different streams have no ordering relationship and benefit
          // from N-wide concurrent draining. The pre-fix serial foreach + await collapsed
          // throughput to ~5 msg/sec on JDX dev-slot-3 350-job import (1,578 streams pending).
          var distinctStreams = new HashSet<Guid>(batch);
          var parallelOpts = new ParallelOptions {
            MaxDegreeOfParallelism = Math.Max(1, _options.MaxConcurrentStreams),
            CancellationToken = stoppingToken,
          };
          await Parallel.ForEachAsync(distinctStreams, parallelOpts, async (streamId, ct) => {
            try {
              await _drainStreamAsync(streamId, ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
              throw;
            } catch (Exception ex) {
              LogDrainError(_logger, streamId, ex);
            }
          });
        } finally {
          // Active → idle: batch done. If more stream_ids arrived during processing the
          // next batcher iteration will rapidly flip us back to active — the fixture's
          // race-aware idle-handler subscribe pattern handles that.
          _setIdleState(active: false);
        }
      }
    } catch (OperationCanceledException) {
      // expected on shutdown
    } finally {
      _setIdleState(active: false);
    }

    LogStopped(_logger);
  }

  /// <summary>
  /// Idempotent transition helper. Fires the matching event ONLY on the actual
  /// transition, not on repeated same-state calls — so a test fixture's handler is
  /// invoked exactly once per state change.
  /// </summary>
  private void _setIdleState(bool active) {
    var nextIdle = !active;
    if (_isIdle == nextIdle) {
      return;
    }
    _isIdle = nextIdle;
    if (nextIdle) {
      OnWorkProcessingIdle?.Invoke();
    } else {
      OnWorkProcessingStarted?.Invoke();
    }
  }

  private async Task _drainStreamAsync(Guid streamId, CancellationToken ct) {
    _drainChannel.MarkDraining(streamId);
    try {
      await _drainStreamInnerAsync(streamId, ct);
    } finally {
      _drainChannel.MarkDrained(streamId);
    }
  }

  private async Task _drainStreamInnerAsync(Guid streamId, CancellationToken ct) {
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

    // Phase H step 6 slice 3: loop-until-empty. Drainer keeps fetching for this stream until
    // there are no more new rows. The session-local seen-set dedups against rows whose
    // completion is still in flight (OutboxCompletionFlushWorker coalesces ~10ms — between
    // the drainer's publish and the row's processed_at landing in DB, fetch_outbox_batch
    // would return the same row again because its filter is processed_at IS NULL). Without
    // the set, we'd re-publish the row multiple times — exactly the multi-fire we eliminated.
    // The set is bounded by drain-session size and GC'd on return.
    var seen = new HashSet<Guid>();
    // Slice 31 PERF instrumentation: per-drain wall time + publish breakdown, mirroring
    // the perspective worker pattern. JDX run-23 analysis identified outbox/inbox as the
    // next hot path to characterize.
    var drainStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    var totalPublishMs = 0.0;
    var publishedCount = 0;
    var fetchCount = 0;
    LogDrainStreamEntered(_logger, streamId);
    while (!ct.IsCancellationRequested) {
      fetchCount++;
      LogFetchBatchStart(_logger, streamId, fetchCount);
      var rowsRaw = await coordinator.FetchOutboxBatchAsync(
        [streamId], _instanceProvider.InstanceId, _options.MaxPerStream, ct);
      LogFetchBatchReturned(_logger, streamId, fetchCount, rowsRaw.Count);

      if (rowsRaw.Count == 0) {
        _logPerfIfInteresting(streamId, publishedCount, fetchCount, totalPublishMs, drainStartTicks);
        return;
      }

      // Ordering invariant: defensive sort by message_id. SQL fetch_outbox_batch already
      // orders by (stream_id, message_id), but the apply boundary trusts only message_id.
      // See plans/ordered-stream-invariant.md.
      var rows = rowsRaw.OrderByMessageId().ToList();

      var newRowList = new List<OutboxBatchRow>(rows.Count);
      foreach (var row in rows) {
        if (!seen.Add(row.MessageId)) {
          // Already published this session — completion flush lagging. Skip.
          continue;
        }
        // v0.502 slice C.4b — per-row dead-letter check BEFORE adding to the publish set.
        // Uses strict greater-than to match InboxDispatchWorker semantics: value of 10 means
        // "10 total attempts allowed; the 11th claim drops the row." When the DLQ store +
        // generation provider are wired, an atomic move into wh_dead_letters happens here
        // (the SQL function DELETEs from wh_outbox in the same tx so the row never reaches
        // the publish path). When unwired, this slice is a no-op.
        LogPrePublishGateEval(
          _logger,
          row.MessageId,
          row.Attempts,
          _options.MaxOutboxAttempts ?? -1,
          _deadLetterStore is not null,
          _generationProvider is not null);
        if (_options.MaxOutboxAttempts is int maxAttempts
            && row.Attempts > maxAttempts
            && _deadLetterStore is not null
            && _generationProvider is not null) {
          try {
            LogPrePublishGateFiring(_logger, row.MessageId, row.Attempts, maxAttempts);
            // Slice 1 of release/v0.648.0-alpha.1 — prefer the row's existing
            // wh_outbox.error column (the real exception text from the last
            // process_outbox_failures cycle) over the synthetic meta-message.
            // The meta-message collapses every DLQ row to one fingerprint cluster
            // regardless of root cause (slot-3 Jun-2026: 38k+ rows in a single
            // cluster). Using row.Error restores forensic diversity — Slice 2's
            // fingerprint algorithm extracts the real exception type + frames,
            // operators see distinct clusters per failure mode.
            var promotionErrorText = !string.IsNullOrWhiteSpace(row.Error)
              ? row.Error
              : $"OutboxDrainWorker dead-lettered: attempts={row.Attempts} > max={maxAttempts}";
            await _deadLetterStore.MoveAsync(
              deadLetterId: (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo(),
              sourceTable: DeadLetterSourceTable.OUTBOX,
              sourceId: row.MessageId,
              failureReason: Whizbang.Core.Messaging.MessageFailureReason.MaxAttemptsExceeded,
              errorText: promotionErrorText,
              instanceId: _instanceProvider.InstanceId,
              generation: _generationProvider.GetGeneration(),
              ct: ct).ConfigureAwait(false);
            LogOutboxDeadLettered(_logger, row.MessageId, row.Attempts, maxAttempts);
            _dlqMetrics?.Added.Add(1,
              new KeyValuePair<string, object?>("source_table", DeadLetterSourceTable.OUTBOX),
              new KeyValuePair<string, object?>("reason", "MaxAttemptsExceeded"));
            continue;  // skip publishing — row no longer exists
          } catch (Exception ex) {
            // Move failed (transient DB issue). Drop through to publish — better to attempt
            // delivery than to leave the row stuck in claim_orphaned_outbox forever. Next
            // failure cycle will retry the DLQ move.
            LogOutboxDlqMoveFailed(_logger, row.MessageId, ex);
          }
        }
        newRowList.Add(row);
      }
      var newRows = newRowList.Count;

      // Slot-3 throughput fix: bulk publish path. When the transport reports
      // SupportsBulkPublish, ship the stream's newly-claimed rows as one PublishBatchAsync
      // round-trip instead of looping PublishAsync per row. Azure Service Bus's
      // SenderClient.SendMessagesAsync packs up to ~1 MB / ~100 messages per network call,
      // so a 49-message stream becomes 1 round-trip instead of 49. Per-stream FIFO is
      // preserved by the within-stream ordering above; per-row lifecycle hooks fire inside
      // the bulk helper around the batched publish call.
      if (_publishStrategy!.SupportsBulkPublish && newRowList.Count > 0) {
        var publishStart = System.Diagnostics.Stopwatch.GetTimestamp();
        await PublishBulkAsync(newRowList, ct);
        totalPublishMs += (System.Diagnostics.Stopwatch.GetTimestamp() - publishStart)
          * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        publishedCount += newRowList.Count;
      } else {
        foreach (var row in newRowList) {
          var publishStart = System.Diagnostics.Stopwatch.GetTimestamp();
          await PublishOneAsync(row, ct);
          totalPublishMs += (System.Diagnostics.Stopwatch.GetTimestamp() - publishStart)
            * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
          publishedCount++;
        }
      }

      // If every row in this fetch was a dup of what we already published, completion flush
      // hasn't landed — exit so the next claim_work tick can re-issue the stream once the
      // pending rows clear.
      if (newRows == 0) {
        _logPerfIfInteresting(streamId, publishedCount, fetchCount, totalPublishMs, drainStartTicks);
        return;
      }

      // Slice 32 — skip the confirmation fetch when the previous fetch returned a partial
      // batch (fewer rows than MaxPerStream). Run-24 PERF measured 84% of outbox drain wall
      // time spent on fetch+other vs 16% on actual publish; fetches/drain = 2.0 means EVERY
      // drain pays for a confirmation fetch that almost always returns 0 rows. If a row
      // arrives between the partial fetch and the drain finishing, ClaimWorker's next tick
      // (or LISTEN/NOTIFY signal) re-issues the stream — same recovery path used everywhere.
      if (rowsRaw.Count < _options.MaxPerStream) {
        _logPerfIfInteresting(streamId, publishedCount, fetchCount, totalPublishMs, drainStartTicks);
        return;
      }
    }
    _logPerfIfInteresting(streamId, publishedCount, fetchCount, totalPublishMs, drainStartTicks);
  }

  private void _logPerfIfInteresting(Guid streamId, int published, int fetches, double publishMs, long startTicks) {
    if (!_logger.IsEnabled(LogLevel.Debug)) {
      return;
    }
    var totalMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks)
      * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    if (published >= 5 || totalMs > 100) {
#pragma warning disable CA1848
      _logger.LogDebug(
        "PERF OutboxDrain stream {StreamId}: published={Published} fetches={Fetches} total={TotalMs:F0}ms publish={PublishMs:F0}ms fetch+other={OtherMs:F0}ms",
        streamId, published, fetches, totalMs, publishMs, totalMs - publishMs);
#pragma warning restore CA1848
    }
  }

  /// <summary>
  /// Slot-3 throughput fix: bulk publish path. Serializes each row's <see cref="OutboxWork"/>
  /// up front (deserialization failures are routed to the failure channel and excluded from
  /// the batch), fires Pre-Outbox lifecycle per row, sends the surviving works as one
  /// <see cref="IMessagePublishStrategy.PublishBatchAsync"/> call, then fans the per-row
  /// results out to Post-Outbox lifecycle + the completion / failure channels.
  /// </summary>
  internal async Task PublishBulkAsync(List<OutboxBatchRow> rows, CancellationToken ct) {
    var works = new List<OutboxWork>(rows.Count);
    var rowsByMessageId = new Dictionary<Guid, OutboxBatchRow>(rows.Count);
    var typedEnvelopes = new Dictionary<Guid, IMessageEnvelope?>(rows.Count);
    var receptorInvokers = new Dictionary<Guid, IReceptorInvoker?>(rows.Count);
    var lifecycleScopes = new List<IAsyncDisposable>(rows.Count);
    LogPublishBulkEntered(_logger, rows.Count);
    try {
      foreach (var row in rows) {
        OutboxWork work;
        try {
          work = _toOutboxWork(row);
        } catch (Exception ex) {
          LogDeserializeFailed(_logger, row.MessageId, ex);
          await _failureChannel.EnqueueAsync(WorkCategory.Outbox, new MessageFailure {
            MessageId = row.MessageId,
            CompletedStatus = (MessageProcessingStatus)row.Status,
            Error = ex.Message,
            Reason = MessageFailureReason.Unknown,
          }, ct);
          continue;
        }

        var scope = _scopeFactory.CreateAsyncScope();
        lifecycleScopes.Add(scope);
        var typedEnvelope = _tryResolveTypedEnvelope(work);
        LogTypedEnvelopeResolved(_logger, row.MessageId, typedEnvelope is not null, work.Destination ?? "<null>");
        IReceptorInvoker? receptorInvoker = null;
        if (typedEnvelope is not null && !string.IsNullOrEmpty(work.Destination)) {
          LogSecurityContextStart(_logger, row.MessageId);
          if (!await _trySecurityContextOrEnqueueTimeoutAsync(work, row, scope.ServiceProvider, ct)) {
            LogSecurityContextTimedOutSkipRow(_logger, row.MessageId);
            continue;  // skip this row; lifecycle scope is disposed in finally
          }
          LogSecurityContextOk(_logger, row.MessageId);
          receptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
        }

        LogPreOutboxLifecycleStart(_logger, row.MessageId);
        await InvokeOutboxLifecycleStageAsync(
          work, typedEnvelope, receptorInvoker,
          LifecycleStage.PreOutboxDetached, LifecycleStage.PreOutboxInline,
          "PreOutbox", ct);
        LogPreOutboxLifecycleEnd(_logger, row.MessageId);

        works.Add(work);
        rowsByMessageId[work.MessageId] = row;
        typedEnvelopes[work.MessageId] = typedEnvelope;
        receptorInvokers[work.MessageId] = receptorInvoker;
      }

      if (works.Count == 0) {
        LogPublishBulkNoWork(_logger);
        return;
      }

      IReadOnlyList<MessagePublishResult> results;
      // Slice 4 of release/v0.648.0-alpha.1, hardened in v0.651: wrap PublishBatchAsync
      // in a per-call timeout that fires GUARANTEED — not cooperatively. The v0.648
      // version used CancellationTokenSource.CancelAfter + cooperative catch, which only
      // worked if the transport SDK observed the token. JDX slot-3 forensic (Jun 2026)
      // confirmed the SDK can hang without observing cancellation: the timer fired but
      // the await sat there forever, no OCE thrown, no failure captured, attempts only
      // ticked up via claim_orphaned_outbox at the DB level. WaitAsync(TimeSpan, ct)
      // throws TimeoutException after the deadline regardless of inner cooperation; the
      // still-running publish task is abandoned with an observe-only continuation so any
      // late exception doesn't escape as UnobservedTaskException.
      var publishTimeoutSeconds = _options.PublishTimeoutSeconds;
      Task<IReadOnlyList<MessagePublishResult>>? publishTask = null;
      LogPublishBatchStart(_logger, works.Count, publishTimeoutSeconds);
      try {
        // Capture the Task inside the try so a SYNCHRONOUS throw from PublishBatchAsync —
        // e.g., a strategy that validates inputs and throws before returning — flows into
        // the existing catch (Exception ex) failure path instead of escaping uncaught.
        publishTask = _publishStrategy!.PublishBatchAsync(works, ct);
        results = publishTimeoutSeconds > 0
          ? await publishTask.WaitAsync(TimeSpan.FromSeconds(publishTimeoutSeconds), ct)
          : await publishTask;
        LogPublishBatchReturned(_logger, results.Count);
      } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        throw;
      } catch (TimeoutException) {
        // Publish-specific timeout (not shutdown). Observe the abandoned publish task so
        // a later exception on it surfaces to UnobservedExceptionDiagnostics rather than
        // disappearing at GC time.
        if (publishTask is not null) {
          _ = publishTask.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        }
        LogPublishTimedOut(_logger, works.Count, publishTimeoutSeconds);
        foreach (var work in works) {
          var row = rowsByMessageId[work.MessageId];
          await _failureChannel.EnqueueAsync(WorkCategory.Outbox, new MessageFailure {
            MessageId = work.MessageId,
            CompletedStatus = (MessageProcessingStatus)row.Status,
            Error = $"Publish timed out after {publishTimeoutSeconds}s — SDK call did not return for destination={work.Destination}",
            Reason = MessageFailureReason.TransportException,
          }, ct);
        }
        return;
      } catch (Exception ex) {
        // Whole-batch failure: route every row to the failure channel so the next
        // claim_orphaned_* cycle re-leases them. Lifecycle scopes are disposed in finally.
        foreach (var work in works) {
          LogPublishFailed(_logger, work.MessageId, ex);
          var row = rowsByMessageId[work.MessageId];
          await _failureChannel.EnqueueAsync(WorkCategory.Outbox, new MessageFailure {
            MessageId = work.MessageId,
            CompletedStatus = (MessageProcessingStatus)row.Status,
            Error = ex.Message,
            Reason = MessageFailureReason.Unknown,
          }, ct);
        }
        return;
      }

      foreach (var result in results) {
        var row = rowsByMessageId[result.MessageId];
        if (result.Success) {
          await InvokeOutboxLifecycleStageAsync(
            works.First(w => w.MessageId == result.MessageId),
            typedEnvelopes[result.MessageId],
            receptorInvokers[result.MessageId],
            LifecycleStage.PostOutboxDetached, LifecycleStage.PostOutboxInline,
            "PostOutbox", ct);

          OnOutboxMessagePublished?.Invoke(new OutboxMessagePublishedEvent {
            MessageId = result.MessageId,
            Destination = row.Destination
          });
          await _completionChannel.EnqueueAsync(result.MessageId, ct);
        } else {
          await _failureChannel.EnqueueAsync(WorkCategory.Outbox, new MessageFailure {
            MessageId = result.MessageId,
            CompletedStatus = result.CompletedStatus,
            Error = result.Error ?? "publish failed",
            Reason = result.Reason,
          }, ct);
        }
      }
    } finally {
      foreach (var scope in lifecycleScopes) {
        try { await scope.DisposeAsync(); } catch { /* best-effort scope cleanup */ }
      }
    }
  }

  internal async Task PublishOneAsync(OutboxBatchRow row, CancellationToken ct) {
    OutboxWork work;
    try {
      work = _toOutboxWork(row);
    } catch (Exception ex) {
      LogDeserializeFailed(_logger, row.MessageId, ex);
      await _failureChannel.EnqueueAsync(WorkCategory.Outbox, new MessageFailure {
        MessageId = row.MessageId,
        CompletedStatus = (MessageProcessingStatus)row.Status,
        Error = ex.Message,
        Reason = MessageFailureReason.Unknown,
      }, ct);
      return;
    }
    LogPublishOneEntered(_logger, row.MessageId, work.Destination ?? "<null>");

    // Lifecycle scope: shared across PreOutbox + PostOutbox so the security context is
    // established once and the typed envelope (payload-deserialized) is reused. Skipped
    // for event-store-only messages (null/empty destination) — those rows exist only
    // for event store persistence and shouldn't fire transport-side lifecycle stages.
    await using var scope = _scopeFactory.CreateAsyncScope();
    var typedEnvelope = _tryResolveTypedEnvelope(work);
    IReceptorInvoker? receptorInvoker = null;
    if (typedEnvelope is not null && !string.IsNullOrEmpty(work.Destination)) {
      if (!await _trySecurityContextOrEnqueueTimeoutAsync(work, row, scope.ServiceProvider, ct)) {
        return;
      }
      receptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
    }

    await InvokeOutboxLifecycleStageAsync(
      work, typedEnvelope, receptorInvoker,
      LifecycleStage.PreOutboxDetached, LifecycleStage.PreOutboxInline,
      "PreOutbox", ct);

    MessagePublishResult result;
    // Singular-path Slice 4: same hardening pattern as the bulk path. WaitAsync(TimeSpan, ct)
    // guarantees the timeout fires regardless of whether the transport observes ct, and the
    // abandoned publish task gets an observe-only continuation. Capture the Task inside the
    // try so a synchronous throw from PublishAsync flows into the existing catch path.
    var publishTimeoutSeconds = _options.PublishTimeoutSeconds;
    Task<MessagePublishResult>? publishTask = null;
    LogPublishOneStart(_logger, row.MessageId, publishTimeoutSeconds);
    try {
      publishTask = _publishStrategy!.PublishAsync(work, ct);
      result = publishTimeoutSeconds > 0
        ? await publishTask.WaitAsync(TimeSpan.FromSeconds(publishTimeoutSeconds), ct)
        : await publishTask;
      LogPublishOneReturned(_logger, row.MessageId, result.Success);
    } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
      throw;
    } catch (TimeoutException) {
      if (publishTask is not null) {
        _ = publishTask.ContinueWith(
          static t => { _ = t.Exception; },
          CancellationToken.None,
          TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
          TaskScheduler.Default);
      }
      LogPublishTimedOut(_logger, 1, publishTimeoutSeconds);
      await _failureChannel.EnqueueAsync(WorkCategory.Outbox, new MessageFailure {
        MessageId = row.MessageId,
        CompletedStatus = (MessageProcessingStatus)row.Status,
        Error = $"Publish timed out after {publishTimeoutSeconds}s — SDK call did not return for destination={work.Destination}",
        Reason = MessageFailureReason.TransportException,
      }, ct);
      return;
    } catch (Exception ex) {
      LogPublishFailed(_logger, row.MessageId, ex);
      await _failureChannel.EnqueueAsync(WorkCategory.Outbox, new MessageFailure {
        MessageId = row.MessageId,
        CompletedStatus = (MessageProcessingStatus)row.Status,
        Error = ex.Message,
        Reason = MessageFailureReason.Unknown,
      }, ct);
      return;
    }

    if (result.Success) {
      // Fire PostOutbox lifecycle BEFORE the publish hook + completion enqueue so
      // receptors registered at PostOutbox* observe the published message before any
      // test-side completion signal advances.
      await InvokeOutboxLifecycleStageAsync(
        work, typedEnvelope, receptorInvoker,
        LifecycleStage.PostOutboxDetached, LifecycleStage.PostOutboxInline,
        "PostOutbox", ct);

      // Fire publish hook BEFORE enqueuing the completion. Test fixtures rely on the
      // signal firing per-row; ordering it before completion enqueue means the next
      // observed completion-flush always reflects the published row.
      OnOutboxMessagePublished?.Invoke(new OutboxMessagePublishedEvent {
        MessageId = row.MessageId,
        Destination = row.Destination
      });
      await _completionChannel.EnqueueAsync(row.MessageId, ct);
    } else {
      await _failureChannel.EnqueueAsync(WorkCategory.Outbox, new MessageFailure {
        MessageId = row.MessageId,
        CompletedStatus = result.CompletedStatus,
        Error = result.Error ?? "publish failed",
        Reason = result.Reason,
      }, ct);
    }
  }

  /// <summary>
  /// Deserialize the envelope payload once (Pre and Post stages reuse). Returns null when
  /// the deserializer is absent or deserialize fails — both lifecycle helpers no-op cleanly
  /// in that case so a missing deserializer never blocks the publish path.
  /// </summary>
  private IMessageEnvelope? _tryResolveTypedEnvelope(OutboxWork work) {
    if (_lifecycleMessageDeserializer is null) {
      return null;
    }
    try {
      var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);
      return work.Envelope.ReconstructWithPayload(message);
    } catch (Exception ex) {
      LogLifecycleDeserializeError(_logger, work.MessageId, ex);
      return null;
    }
  }

  /// <summary>
  /// Fire a Pre/PostOutbox lifecycle stage pair (Detached fire-and-forget + Inline awaited).
  /// Mirrors <see cref="InboxDispatchWorker.InvokeInboxLifecycleStageAsync"/>. Wrapped in
  /// try/catch so a misbehaving receptor cannot block publish or completion.
  /// </summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Lifecycle invocation handles the detached-vs-inline branch matrix + receptor resolution + envelope reuse fallback inline so the receptor-isolation try/catch stays at one site.")]
  internal async Task InvokeOutboxLifecycleStageAsync(
      OutboxWork work,
      IMessageEnvelope? typedEnvelope,
      IReceptorInvoker? receptorInvoker,
      LifecycleStage detachedStage,
      LifecycleStage inlineStage,
      string stageName,
      CancellationToken ct) {
    if (typedEnvelope is null || receptorInvoker is null) {
      return;
    }
    // Skip for event-store-only messages (no transport publish).
    if (string.IsNullOrEmpty(work.Destination)) {
      return;
    }

    var runtimeMessageType = typedEnvelope.Payload?.GetType();
    var hasDetached = _receptorRegistry is null || !_isGatedOutboxStage(detachedStage)
      || _receptorRegistry.HasReceptors(detachedStage, work.MessageType)
      || _runtimeHasReceptors(runtimeMessageType, detachedStage);
    var hasInline = _receptorRegistry is null || !_isGatedOutboxStage(inlineStage)
      || _receptorRegistry.HasReceptors(inlineStage, work.MessageType)
      || _runtimeHasReceptors(runtimeMessageType, inlineStage);
    if (!hasDetached && !hasInline) {
      return;
    }

    try {
      var lifecycleContext = new LifecycleExecutionContext {
        CurrentStage = detachedStage,
        EventId = null,
        StreamId = work.StreamId,
        LastProcessedEventId = null,
        MessageSource = MessageSource.Outbox,
        AttemptNumber = work.Attempts
      };

      if (hasDetached) {
        _ = Task.Run(async () => {
          try {
            await using var detachedScope = _scopeFactory.CreateAsyncScope();
            await SecurityContextHelper.EstablishFullContextAsync(typedEnvelope, detachedScope.ServiceProvider, ct);
            var detachedInvoker = detachedScope.ServiceProvider.GetService<IReceptorInvoker>();
            if (detachedInvoker is null) {
              return;
            }
            var ctx = lifecycleContext with { CurrentStage = detachedStage };
            await detachedInvoker.InvokeAsync(typedEnvelope, detachedStage, ctx, ct);
          } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // graceful shutdown
          } catch (Exception ex) {
            LogLifecycleStageError(_logger, work.MessageId, stageName + "Detached", ex);
            // Slice 1 of release/v0.645.0-alpha.1 (outbox-DLQ + dual-hash analysis):
            // route the lifecycle exception through IFailureChannel so
            // process_outbox_failures populates wh_outbox.error with the full
            // ex.ToString(). Without this, slot-3-class faults retried forever with
            // an empty error column and operators had no triage signal.
            await _failureChannel.EnqueueAsync(WorkCategory.Outbox, new MessageFailure {
              MessageId = work.MessageId,
              CompletedStatus = work.Status,
              Error = $"Lifecycle stage {stageName}Detached failed: {ex}",
              Reason = MessageFailureReason.Unknown,
            }, ct);
          }
        }, ct);
      }

      if (hasInline) {
        var inlineCtx = lifecycleContext with { CurrentStage = inlineStage };
        await receptorInvoker.InvokeAsync(typedEnvelope, inlineStage, inlineCtx, ct);
      }
    } catch (Exception ex) {
      LogLifecycleStageError(_logger, work.MessageId, stageName, ex);
      // Slice 1 of release/v0.645.0-alpha.1 (outbox-DLQ + dual-hash analysis):
      // route the lifecycle exception through IFailureChannel so
      // process_outbox_failures populates wh_outbox.error with the full
      // ex.ToString(). Without this, slot-3-class faults retried forever with
      // an empty error column and operators had no triage signal.
      await _failureChannel.EnqueueAsync(WorkCategory.Outbox, new MessageFailure {
        MessageId = work.MessageId,
        CompletedStatus = work.Status,
        Error = $"Lifecycle stage {stageName} failed: {ex}",
        Reason = MessageFailureReason.Unknown,
      }, ct);
    }
  }

  /// <summary>
  /// True when the lifecycle stage is one the source-generated WhizbangReceptorRegistryQuery
  /// actually emits entries for. Stages outside this set return false unconditionally from
  /// HasReceptors, so we fall back to invoking unconditionally for them.
  /// </summary>
  private static bool _isGatedOutboxStage(LifecycleStage stage) =>
    stage is LifecycleStage.PreOutboxDetached
          or LifecycleStage.PreOutboxInline
          or LifecycleStage.PostOutboxDetached
          or LifecycleStage.PostOutboxInline;

  private bool _runtimeHasReceptors(Type? messageType, LifecycleStage stage) {
    if (_runtimeReceptorRegistry is null || messageType is null) {
      return false;
    }
    return _runtimeReceptorRegistry.GetReceptorsFor(messageType, stage).Count > 0;
  }

  /// <summary>
  /// Slice 5a (release/v0.647.0-alpha.1) — single point of truth for the
  /// SecurityContext-establishment-with-timeout pattern across both publish paths
  /// (bulk + singular). Returns true on success (caller continues to lifecycle hooks
  /// and publish), false on timeout (caller's row has already been routed through
  /// the failure channel as SecurityContextEstablishmentFailure; caller should skip
  /// this row via continue/return).
  /// </summary>
  private async ValueTask<bool> _trySecurityContextOrEnqueueTimeoutAsync(
      OutboxWork work, OutboxBatchRow row, IServiceProvider scopedProvider, CancellationToken ct) {
    var timeoutSeconds = _options.SecurityContextTimeoutSeconds;
    var outcome = await SecurityContextHelper.TryEstablishFullContextWithTimeoutAsync(
      work.Envelope, scopedProvider, timeoutSeconds, ct);
    if (outcome == SecurityContextEstablishmentOutcome.TimedOut) {
      LogSecurityContextTimedOut(_logger, work.MessageId, timeoutSeconds);
      await SecurityContextHelper.EnqueueSecurityContextTimeoutFailureAsync(
        _failureChannel, WorkCategory.Outbox, work.MessageId,
        (MessageProcessingStatus)row.Status, timeoutSeconds, ct);
      return false;
    }
    return true;
  }

  private OutboxWork _toOutboxWork(OutboxBatchRow row) {
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
      ?? throw new InvalidOperationException("No JsonTypeInfo for MessageEnvelope<JsonElement>.");
    var envelope = JsonSerializer.Deserialize(row.EventData, typeInfo) as IMessageEnvelope<JsonElement>
      ?? throw new InvalidOperationException($"Failed to deserialize envelope for message {row.MessageId}.");

    // Slice 26.6b: inject source identity into the envelope before publish. The wire
    // version of this envelope must carry SourceServiceId + SourceCommitSequence so
    // the consumer can compare cursors per-source and apply events in the same order
    // the source committed them — deterministic across live and replay paths.
    //
    // COALESCE: when wh_event_store.origin_service_id is non-null this event was 1:1
    // forwarded from another service — preserve the original identity. Otherwise the
    // event was originated locally; populate from the local wh_service_config.service_id.
    if (envelope is MessageEnvelope<JsonElement> concrete) {
      var effectiveSourceId = row.OriginServiceId ?? _localServiceId;
      var effectiveCommitSeq = row.OriginCommitSequence ?? row.CommitSequence ?? 0L;
      envelope = new MessageEnvelope<JsonElement> {
        MessageId = concrete.MessageId,
        Payload = concrete.Payload,
        Hops = concrete.Hops,
        DispatchContext = concrete.DispatchContext,
        Version = concrete.Version,
        ReceptorInvocations = concrete.ReceptorInvocations,
        SourceServiceId = effectiveSourceId,
        SourceCommitSequence = effectiveCommitSeq,
        CausedByServiceId = concrete.CausedByServiceId,
        CausedByCommitSequence = concrete.CausedByCommitSequence,
      };
    }

    return new OutboxWork {
      MessageId = row.MessageId,
      Destination = row.Destination,
      Envelope = envelope,
      EnvelopeType = row.EnvelopeType ?? string.Empty,
      MessageType = row.MessageType,
      StreamId = row.StreamId,
      PartitionNumber = row.PartitionNumber,
      Attempts = row.Attempts,
      Status = (MessageProcessingStatus)row.Status,
      Flags = WorkBatchOptions.None,
    };
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "OutboxDrainWorker started: maxPerStream={MaxPerStream}")]
  static partial void LogStarted(ILogger logger, int maxPerStream);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "OutboxDrainWorker stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "OutboxDrainWorker disabled — idle")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
    Message = "OutboxDrainWorker: no IMessagePublishStrategy registered — drainer disabled")]
  static partial void LogNoTransportRegistered(ILogger logger);

  [LoggerMessage(EventId = 5, Level = LogLevel.Error,
    Message = "OutboxDrainWorker: drain failed for stream {StreamId}")]
  static partial void LogDrainError(ILogger logger, Guid streamId, Exception ex);

  [LoggerMessage(EventId = 6, Level = LogLevel.Error,
    Message = "OutboxDrainWorker: failed to deserialize envelope for {MessageId}")]
  static partial void LogDeserializeFailed(ILogger logger, Guid messageId, Exception ex);

  [LoggerMessage(EventId = 7, Level = LogLevel.Error,
    Message = "OutboxDrainWorker: publish threw for {MessageId}")]
  static partial void LogPublishFailed(ILogger logger, Guid messageId, Exception ex);

  [LoggerMessage(EventId = 8, Level = LogLevel.Warning,
    Message = "OutboxDrainWorker: lifecycle deserialize failed for {MessageId} — Pre/Post Outbox lifecycle skipped for this message")]
  static partial void LogLifecycleDeserializeError(ILogger logger, Guid messageId, Exception ex);

  [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
    Message = "OutboxDrainWorker: lifecycle stage {Stage} failed for {MessageId}; publish path continues")]
  static partial void LogLifecycleStageError(ILogger logger, Guid messageId, string stage, Exception ex);

  [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
    Message = "OutboxDrainWorker dead-lettered message {MessageId}: attempts={Attempts} > max={MaxAttempts}")]
  static partial void LogOutboxDeadLettered(ILogger logger, Guid messageId, int attempts, int maxAttempts);

  [LoggerMessage(EventId = 11, Level = LogLevel.Warning,
    Message = "OutboxDrainWorker IDeadLetterStore.MoveAsync failed for {MessageId} — falling through to publish")]
  static partial void LogOutboxDlqMoveFailed(ILogger logger, Guid messageId, Exception ex);

  [LoggerMessage(EventId = 12, Level = LogLevel.Warning,
    Message = "OutboxDrainWorker EstablishFullContextAsync timed out for {MessageId} after {TimeoutSeconds}s — IMessageSecurityContextProvider implementation hung; routing to failure channel with Reason=SecurityContextEstablishmentFailure")]
  static partial void LogSecurityContextTimedOut(ILogger logger, Guid messageId, int timeoutSeconds);

  [LoggerMessage(EventId = 13, Level = LogLevel.Warning,
    Message = "OutboxDrainWorker transport publish timed out for batch of {RowCount} row(s) after {TimeoutSeconds}s — SDK call did not return; routing each row to failure channel with Reason=TransportException")]
  static partial void LogPublishTimedOut(ILogger logger, int rowCount, int timeoutSeconds);

  // v0.656 forensic Debug instrumentation: slot-3 stuck-row diagnosis.
  // Operator runs slot 3 with `Whizbang.Core.Workers.OutboxDrainWorker` overridden to Debug
  // to surface which silent path is consumed between two-minute claim cycles. Keep at Debug
  // level so production logs aren't flooded; the Whizbang.Core.Workers default stays Information.

  [LoggerMessage(EventId = 30, Level = LogLevel.Debug,
    Message = "OutboxDrainWorker: drain stream {StreamId} entered")]
  static partial void LogDrainStreamEntered(ILogger logger, Guid streamId);

  [LoggerMessage(EventId = 31, Level = LogLevel.Debug,
    Message = "OutboxDrainWorker: stream {StreamId} fetch batch #{FetchCount} starting")]
  static partial void LogFetchBatchStart(ILogger logger, Guid streamId, int fetchCount);

  [LoggerMessage(EventId = 32, Level = LogLevel.Debug,
    Message = "OutboxDrainWorker: stream {StreamId} fetch batch #{FetchCount} returned {RowCount} row(s)")]
  static partial void LogFetchBatchReturned(ILogger logger, Guid streamId, int fetchCount, int rowCount);

  [LoggerMessage(EventId = 33, Level = LogLevel.Debug,
    Message = "OutboxDrainWorker: pre-publish DLQ gate eval msg={MessageId} attempts={Attempts} max={MaxAttempts} dlqStore={HasDlqStore} genProvider={HasGenerationProvider}")]
  static partial void LogPrePublishGateEval(ILogger logger, Guid messageId, int attempts, int maxAttempts, bool hasDlqStore, bool hasGenerationProvider);

  [LoggerMessage(EventId = 34, Level = LogLevel.Debug,
    Message = "OutboxDrainWorker: pre-publish DLQ gate FIRING for {MessageId} (attempts={Attempts} > max={MaxAttempts}) — moving to wh_dead_letters")]
  static partial void LogPrePublishGateFiring(ILogger logger, Guid messageId, int attempts, int maxAttempts);

  [LoggerMessage(EventId = 35, Level = LogLevel.Debug,
    Message = "OutboxDrainWorker.PublishBulkAsync entered with {RowCount} row(s)")]
  static partial void LogPublishBulkEntered(ILogger logger, int rowCount);

  [LoggerMessage(EventId = 36, Level = LogLevel.Debug,
    Message = "OutboxDrainWorker.PublishBulkAsync: msg={MessageId} typedEnvelope={HasEnvelope} destination={Destination}")]
  static partial void LogTypedEnvelopeResolved(ILogger logger, Guid messageId, bool hasEnvelope, string destination);

  [LoggerMessage(EventId = 37, Level = LogLevel.Debug,
    Message = "OutboxDrainWorker.PublishBulkAsync: SecurityContext START msg={MessageId}")]
  static partial void LogSecurityContextStart(ILogger logger, Guid messageId);

  [LoggerMessage(EventId = 38, Level = LogLevel.Debug,
    Message = "OutboxDrainWorker.PublishBulkAsync: SecurityContext OK msg={MessageId}")]
  static partial void LogSecurityContextOk(ILogger logger, Guid messageId);

  [LoggerMessage(EventId = 39, Level = LogLevel.Debug,
    Message = "OutboxDrainWorker.PublishBulkAsync: SecurityContext TIMED OUT msg={MessageId} — skipping row this iteration")]
  static partial void LogSecurityContextTimedOutSkipRow(ILogger logger, Guid messageId);

  [LoggerMessage(EventId = 40, Level = LogLevel.Debug,
    Message = "OutboxDrainWorker.PublishBulkAsync: PreOutbox lifecycle START msg={MessageId}")]
  static partial void LogPreOutboxLifecycleStart(ILogger logger, Guid messageId);

  [LoggerMessage(EventId = 41, Level = LogLevel.Debug,
    Message = "OutboxDrainWorker.PublishBulkAsync: PreOutbox lifecycle END msg={MessageId}")]
  static partial void LogPreOutboxLifecycleEnd(ILogger logger, Guid messageId);

  [LoggerMessage(EventId = 42, Level = LogLevel.Debug,
    Message = "OutboxDrainWorker.PublishBulkAsync: no eligible works after deserialize/lifecycle — returning")]
  static partial void LogPublishBulkNoWork(ILogger logger);

  [LoggerMessage(EventId = 43, Level = LogLevel.Debug,
    Message = "OutboxDrainWorker.PublishBulkAsync: PublishBatchAsync STARTING with {WorkCount} work(s), timeoutSec={TimeoutSeconds}")]
  static partial void LogPublishBatchStart(ILogger logger, int workCount, int timeoutSeconds);

  [LoggerMessage(EventId = 44, Level = LogLevel.Debug,
    Message = "OutboxDrainWorker.PublishBulkAsync: PublishBatchAsync RETURNED with {ResultCount} result(s)")]
  static partial void LogPublishBatchReturned(ILogger logger, int resultCount);

  [LoggerMessage(EventId = 45, Level = LogLevel.Debug,
    Message = "OutboxDrainWorker._publishOneAsync entered msg={MessageId} dest={Destination}")]
  static partial void LogPublishOneEntered(ILogger logger, Guid messageId, string destination);

  [LoggerMessage(EventId = 46, Level = LogLevel.Debug,
    Message = "OutboxDrainWorker._publishOneAsync: PublishOneAsync STARTING msg={MessageId} timeoutSec={TimeoutSeconds}")]
  static partial void LogPublishOneStart(ILogger logger, Guid messageId, int timeoutSeconds);

  [LoggerMessage(EventId = 47, Level = LogLevel.Debug,
    Message = "OutboxDrainWorker._publishOneAsync: PublishOneAsync RETURNED msg={MessageId} success={Success}")]
  static partial void LogPublishOneReturned(ILogger logger, Guid messageId, bool success);
}

/// <summary>Configuration for <see cref="OutboxDrainWorker"/>.</summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public sealed class OutboxDrainWorkerOptions {
  /// <summary>
  /// Killswitch.
  /// <para>
  /// <strong>Default: <c>true</c></strong> as of Phase H step 4b — this is the active outbox
  /// publish path. The legacy <see cref="OutboxPublishWorker"/> defaults to disabled; ops can
  /// flip both for a rollback if needed but should not enable both at once (double publish).
  /// </para>
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>Cap on how many leased outbox rows to drain per stream per iteration. Default 100.</summary>
  public int MaxPerStream { get; set; } = 100;

  /// <summary>
  /// Dead-letter threshold for wh_outbox rows. Total number of publish attempts permitted
  /// before the row is moved into wh_dead_letters (via IDeadLetterStore.MoveAsync).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Default <c>10</c> (v0.502). Prior versions had no max — failed outbox rows accumulated
  /// in wh_outbox indefinitely. Set to <c>null</c> explicitly to restore the prior
  /// no-limit behavior. The check fires per-row before publishing; the same
  /// strict-greater-than semantics as <see cref="InboxDispatchWorkerOptions.MaxInboxAttempts"/>
  /// — value of <c>10</c> permits 10 total attempts; the 11th claim's drain dead-letters.
  /// </para>
  /// </remarks>
  public int? MaxOutboxAttempts { get; set; } = 10;

  /// <summary>
  /// Cap on how many distinct streams to drain concurrently within a single batch from the
  /// drain channel. Per-stream FIFO is preserved (rows within a stream still publish in
  /// order); ONLY cross-stream draining parallelizes. Default 16 — tuned for a single
  /// publisher pod against Azure Service Bus where round-trips dominate per-message cost.
  /// Set to 1 to restore the pre-slot-3 serial behavior.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/per-stream-drain#cross-stream-parallelism</docs>
  public int MaxConcurrentStreams { get; set; } = 16;

  /// <summary>
  /// Sliding-window batching policy for stream_id signals from <see cref="IOutboxDrainChannel"/>.
  /// When the channel emits a stream_id, the drainer waits up to <see cref="SlidingWindowBatcherOptions.SlidingWindow"/>
  /// for additional signals before processing — letting more outbox messages accumulate before
  /// the fetch. Bounded by <see cref="SlidingWindowBatcherOptions.MaxWait"/> and
  /// <see cref="SlidingWindowBatcherOptions.MaxSize"/>.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/per-stream-drain#sliding-window</docs>
  public SlidingWindowBatcherOptions Batcher { get; set; } = new();

  /// <summary>
  /// Timeout for the per-message <c>SecurityContextHelper.EstablishFullContextAsync</c>
  /// call. When the consumer-side <see cref="Whizbang.Core.Security.IMessageSecurityContextProvider"/>
  /// hangs longer than this, the worker cancels the call, enqueues a
  /// <see cref="MessageFailure"/> with
  /// <see cref="MessageFailureReason.SecurityContextEstablishmentFailure"/>, and
  /// proceeds to the next row. Default 10 s.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Introduced in Slice 5a of release/v0.647.0-alpha.1 after the slot-3 BFF
  /// stuck-row pattern was traced to JDX's IMessageSecurityContextProvider
  /// hanging on a test-pattern tenant id (<c>c0ffee00-cafe-f00d-face-feed12345678</c>).
  /// Without this timeout, the publish path waits on the security context
  /// establishment indefinitely; <c>claim_orphaned_outbox</c> keeps re-leasing
  /// the row, attempts increments forever, and no forensic signal surfaces.
  /// </para>
  /// </remarks>
  /// <docs>operations/dead-letter-queue/internal-dlq</docs>
  public int SecurityContextTimeoutSeconds { get; set; } = 10;

  /// <summary>
  /// Timeout for the per-batch (or per-row) transport publish call
  /// (<see cref="IMessagePublishStrategy.PublishBatchAsync"/> or
  /// <see cref="IMessagePublishStrategy.PublishAsync"/>). When the transport SDK
  /// hangs longer than this — i.e. doesn't return AND doesn't throw — the worker
  /// cancels the call, enqueues a <see cref="MessageFailure"/> per row with
  /// <see cref="MessageFailureReason.TransportException"/>, and proceeds. Default
  /// 60 s — matches the Azure Service Bus client's request-timeout convention.
  /// Set to 0 to disable (legacy hang-until-shutdown behavior).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Introduced in Slice 4 of release/v0.648.0-alpha.1 (initially default 120 s,
  /// then briefly default 0 / opt-in). Slice 5a (v0.647) wrapped the
  /// SecurityContext establishment in a timeout; the slot-3-pattern stuck row
  /// still spun because the hang was one stack frame later — the transport
  /// SDK <c>PublishBatchAsync</c> call never returns AND never throws. Without
  /// this timeout, the worker waits for the SDK indefinitely;
  /// <c>claim_orphaned_outbox</c> re-leases the row, attempts increments
  /// forever, the topic's last-access timestamp stays stale, and no forensic
  /// signal surfaces.
  /// </para>
  /// <para>
  /// Default 60 s is on out-of-box because every consumer needs the protection,
  /// not just the one that hit the issue first. The threshold matches the
  /// Azure SDK request-timeout convention — long enough to absorb a normal
  /// publish + worst-case slowdowns under load, short enough to break the
  /// claim-orphan retry cycle within a single operator pager-window. If a
  /// deployment legitimately needs longer publishes, set this option higher
  /// per environment rather than disabling it.
  /// </para>
  /// </remarks>
  /// <docs>operations/dead-letter-queue/internal-dlq</docs>
  public int PublishTimeoutSeconds { get; set; } = 60;
}
