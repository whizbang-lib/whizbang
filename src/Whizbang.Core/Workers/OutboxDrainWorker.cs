using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

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
/// deferred to a follow-up commit; this MVP focuses on the publish-and-complete core.
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
  // Slice 26.6b: cached local service identity from wh_service_config; resolved once
  // on first drain (after schema-ready gate) and reused for envelope publish-time
  // injection. Guid.Empty until resolved.
  private Guid _localServiceId;

  /// <summary>Constructor.</summary>
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
    IMessagePublishStrategy? publishStrategy = null) {
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
  }

  /// <inheritdoc />
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
        // Dedupe within the batch — ClaimWorker may emit the same stream_id multiple times in
        // one window (rapid heartbeats during burst load). Each unique stream is drained once;
        // FetchOutboxBatchAsync returns all pending rows for it in stream-FIFO order.
        var distinctStreams = new HashSet<Guid>(batch);
        foreach (var streamId in distinctStreams) {
          try {
            await _drainStreamAsync(streamId, stoppingToken);
          } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            throw;
          } catch (Exception ex) {
            LogDrainError(_logger, streamId, ex);
          }
        }
      }
    } catch (OperationCanceledException) {
      // expected on shutdown
    }

    LogStopped(_logger);
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
    while (!ct.IsCancellationRequested) {
      fetchCount++;
      var rowsRaw = await coordinator.FetchOutboxBatchAsync(
        [streamId], _instanceProvider.InstanceId, _options.MaxPerStream, ct);

      if (rowsRaw.Count == 0) {
        _logPerfIfInteresting(streamId, publishedCount, fetchCount, totalPublishMs, drainStartTicks);
        return;
      }

      // Ordering invariant: defensive sort by message_id. SQL fetch_outbox_batch already
      // orders by (stream_id, message_id), but the apply boundary trusts only message_id.
      // See plans/ordered-stream-invariant.md.
      var rows = rowsRaw.OrderByMessageId().ToList();

      var newRows = 0;
      foreach (var row in rows) {
        if (!seen.Add(row.MessageId)) {
          // Already published this session — completion flush lagging. Skip.
          continue;
        }
        newRows++;
        var publishStart = System.Diagnostics.Stopwatch.GetTimestamp();
        await _publishOneAsync(row, ct);
        totalPublishMs += (System.Diagnostics.Stopwatch.GetTimestamp() - publishStart)
          * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        publishedCount++;
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
    var totalMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks)
      * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    if (published >= 5 || totalMs > 100) {
#pragma warning disable CA1848
      _logger.LogWarning(
        "PERF OutboxDrain stream {StreamId}: published={Published} fetches={Fetches} total={TotalMs:F0}ms publish={PublishMs:F0}ms fetch+other={OtherMs:F0}ms",
        streamId, published, fetches, totalMs, publishMs, totalMs - publishMs);
#pragma warning restore CA1848
    }
  }

  private async Task _publishOneAsync(OutboxBatchRow row, CancellationToken ct) {
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

    MessagePublishResult result;
    try {
      result = await _publishStrategy!.PublishAsync(work, ct);
    } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
      throw;
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
  /// Sliding-window batching policy for stream_id signals from <see cref="IOutboxDrainChannel"/>.
  /// When the channel emits a stream_id, the drainer waits up to <see cref="SlidingWindowBatcherOptions.SlidingWindow"/>
  /// for additional signals before processing — letting more outbox messages accumulate before
  /// the fetch. Bounded by <see cref="SlidingWindowBatcherOptions.MaxWait"/> and
  /// <see cref="SlidingWindowBatcherOptions.MaxSize"/>.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/per-stream-drain#sliding-window</docs>
  public SlidingWindowBatcherOptions Batcher { get; set; } = new();
}
