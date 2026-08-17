using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// Per-stream-id inbox drainer. Reads stream_ids from <see cref="IInboxDrainChannel"/>, calls
/// <see cref="IWorkCoordinator.FetchInboxBatchAsync"/> to pull all leased rows for that stream,
/// reconstructs <see cref="InboxWork"/> per row, and writes each to the existing
/// <see cref="IInboxChannelWriter"/> in stream-FIFO order.
/// </summary>
/// <remarks>
/// <para>
/// Adapter design: the actual handler dispatch + lifecycle hooks live in
/// <see cref="InboxDispatchWorker"/>. This worker only does the payload fetch, then re-feeds
/// the existing inbox channel. That keeps the dispatch path unchanged and minimizes risk.
/// Once <c>claim_work</c> SQL drops the inbox body projection (analogous to the outbox step 5b),
/// this adapter becomes the only source of <see cref="InboxWork"/> records.
/// </para>
/// <para>
/// Per-stream FIFO is preserved: channel-reader semantics give one drainer task per stream_id;
/// within a stream the fetch returns rows in <c>received_at</c> order; we write them to the
/// inbox channel in that order. <see cref="InboxDispatchWorker"/> reads sequentially.
/// </para>
/// <para>
/// <strong>Default disabled.</strong> The legacy <c>ClaimWorker</c> still populates
/// <see cref="IInboxChannelWriter"/> directly from <see cref="WorkBatch.InboxWork"/>; if this
/// drainer is also enabled while <c>claim_work</c> still projects inbox bodies, every inbox row
/// would be enqueued twice (double dispatch). Step 5d will flip the default and drop the inbox
/// projection together.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public sealed partial class InboxDrainWorker : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly IInboxDrainChannel _drainChannel;
  private readonly IInboxChannelWriter _inboxChannelWriter;
  private readonly ISchemaReadyGate _schemaReadyGate;
  private readonly InboxDrainWorkerOptions _options;
  private readonly JsonSerializerOptions _jsonOptions;
  private readonly ILogger<InboxDrainWorker> _logger;
  // Idle-state tracking for fixture cleanup-between-tests coordination. Mirrors the
  // sibling OutboxDrainWorker contract so fixtures can wait deterministically for the
  // inbox drain pipeline to quiesce before truncating tables.
  private volatile bool _isIdle = true;

  /// <summary>
  /// True when the worker is currently between batches with no pending stream_ids to drain.
  /// </summary>
  /// <docs>operations/workers/inbox-dispatch-worker</docs>
  public bool IsIdle => _isIdle;

  /// <summary>
  /// Fires on the idle → active transition (a non-empty batch starts processing).
  /// </summary>
  public event WorkProcessingStartedHandler? OnWorkProcessingStarted;

  /// <summary>
  /// Fires on the active → idle transition (a batch finishes). Use this in test
  /// fixtures to wait deterministically for the inbox drain pipeline to quiesce.
  /// </summary>
  /// <docs>operations/workers/inbox-dispatch-worker</docs>
  public event WorkProcessingIdleHandler? OnWorkProcessingIdle;

  /// <summary>Constructor.</summary>
  public InboxDrainWorker(
    IServiceScopeFactory scopeFactory,
    IServiceInstanceProvider instanceProvider,
    IInboxDrainChannel drainChannel,
    IInboxChannelWriter inboxChannelWriter,
    ISchemaReadyGate schemaReadyGate,
    IOptions<InboxDrainWorkerOptions> options,
    JsonSerializerOptions jsonOptions,
    ILogger<InboxDrainWorker> logger) {
    _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _drainChannel = drainChannel ?? throw new ArgumentNullException(nameof(drainChannel));
    _inboxChannelWriter = inboxChannelWriter ?? throw new ArgumentNullException(nameof(inboxChannelWriter));
    _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _options.MaxPerStream);

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

    var batcher = new SlidingWindowBatcher<Guid>(_drainChannel.Reader, _options.Batcher);
    try {
      await foreach (var batch in batcher.ReadBatchesAsync(stoppingToken)) {
        _setIdleState(active: true);
        try {
          // Dedupe within the batch — multiple ClaimWorker ticks during the sliding window can
          // emit the same stream_id repeatedly. v0.685 — drain the whole deduped batch with a
          // SINGLE multi-stream FetchInboxBatchAsync call, then dispatch per-stream in C#.
          // A production measurement showed ~1.9 fetch calls per event with the prior
          // per-stream loop because each stream had only 1-2 rows; the fetch CTE's per-call
          // setup (parse + plan + window-sort) dominated. Batching streams amortizes that.
          var distinctStreams = new HashSet<Guid>(batch);
          if (distinctStreams.Count > 0) {
            await _drainStreamBatchAsync(distinctStreams.ToList(), stoppingToken);
          }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
          throw;
        } catch (Exception ex) {
          // A transient infrastructure failure (pool exhaustion, a DB blip) must never fault
          // this worker: the host default (StopHost) would turn it into a full service outage.
          // Inbox rows are durable and the claim backstop re-offers the streams — log and
          // continue loses nothing.
          LogBatchDrainFailed(_logger, ex);
        } finally {
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
  /// Idempotent transition helper. Fires the matching event only on actual state changes.
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

  /// <summary>
  /// v0.685 — batched-fetch drain for a set of stream_ids. One multi-stream
  /// <see cref="IWorkCoordinator.FetchInboxBatchAsync"/> call amortizes the
  /// CTE setup cost (parse + plan + window-sort) across the whole batch.
  /// Streams that filled their per-stream cap fall back to the existing
  /// per-stream loop-until-empty path so they don't get short-changed on the
  /// tail. Per-stream error isolation matches the prior foreach pattern.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/inbox-drain</docs>
  /// <summary>
  /// The configured byte budget, or null when disabled. Non-positive is treated as "off" so a
  /// misconfigured zero cannot silently starve every fetch down to one row per stream.
  /// </summary>
  private long? _byteBudget() =>
    _options.MaxBytesPerStream is > 0 ? _options.MaxBytesPerStream : null;

  private async Task _drainStreamBatchAsync(IReadOnlyList<Guid> streamIds, CancellationToken ct) {
    foreach (var sid in streamIds) {
      _drainChannel.MarkDraining(sid);
    }
    var batchScopeOk = false;
    try {
      using var scope = _scopeFactory.CreateScope();
      var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

      var rowsRaw = await coordinator.FetchInboxBatchAsync(
        streamIds, _instanceProvider.InstanceId, _options.MaxPerStream, _byteBudget(), ct);
      batchScopeOk = true;

      // Group rows by drain-key (stream_id when set, else message_id — matches the
      // fallback semantics in fetch_inbox_batch's WHERE clause for unscoped/null-stream
      // rows). The drain channel feeds the same key, so dispatch can look it up.
      var perStream = rowsRaw
        .GroupBy(r => DrainKey.For(r.StreamId, r.MessageId))
        .ToDictionary(g => g.Key, g => g.OrderByMessageId().ToList());

      foreach (var sid in streamIds) {
        if (ct.IsCancellationRequested) {
          break;
        }
        try {
          var hasRows = perStream.TryGetValue(sid, out var rows) && rows is { Count: > 0 };
          if (!hasRows) {
            continue;
          }

          // Dispatch all rows from the batched fetch first — they're already in hand and
          // were consumed at the SQL level, so they won't reappear in the inner-loop fetch.
          var seen = new HashSet<Guid>();
          var hadAnyNew = false;
          foreach (var row in rows!) {
            if (!seen.Add(row.MessageId)) {
              continue;
            }
            InboxWork work;
            try {
              work = _toInboxWork(row);
            } catch (Exception ex) {
              LogDeserializeFailed(_logger, row.MessageId, ex);
              continue;
            }
            await _inboxChannelWriter.WriteAsync(work, ct);
            hadAnyNew = true;
          }

          // Cap-filling streams may have more rows pending — fall back to the legacy
          // per-stream loop-until-empty path to drain the tail. The inner loop fetches
          // afresh from where we left off (the batched fetch consumed up to MaxPerStream
          // rows from this stream); its seen-set is independent but redundant fetches
          // can only happen on real races, dedupped downstream by wh_message_deduplication.
          if (rows.Count >= _options.MaxPerStream) {
            await _drainStreamInnerAsync(sid, ct);
          } else if (hadAnyNew) {
            _inboxChannelWriter.SignalNewInboxWorkAvailable();
          }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
          throw;
        } catch (Exception ex) {
          LogDrainError(_logger, sid, ex);
        }
      }
    } finally {
      // Always release the draining marker, even if the batched fetch threw before
      // dispatch — otherwise the channel thinks these streams are stuck draining.
      _ = batchScopeOk;  // kept for future diagnostics; intentionally unused
      foreach (var sid in streamIds) {
        _drainChannel.MarkDrained(sid);
      }
    }
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Per-stream drain loop coordinates fetch + sort + serialize + dispatch + completion enqueue; the loop's invariants depend on the branches staying inline (skip-empty short-circuit, FIFO ordering guarantee, scope lifetime).")]
  private async Task _drainStreamInnerAsync(Guid streamId, CancellationToken ct) {
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

    // Phase H step 6 slice 3: loop-until-empty with session-local seen-set dedup.
    // See OutboxDrainWorker._drainStreamInnerAsync for the rationale — same race shape.
    var seen = new HashSet<Guid>();
    var hadAnyNew = false;
    // Slice 31 PERF instrumentation: per-drain wall time + deserialize/enqueue split.
    var drainStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    var totalDeserMs = 0.0;
    var totalWriteMs = 0.0;
    var enqueued = 0;
    var fetchCount = 0;
    while (!ct.IsCancellationRequested) {
      fetchCount++;
      var rowsRaw = await coordinator.FetchInboxBatchAsync(
        [streamId], _instanceProvider.InstanceId, _options.MaxPerStream, _byteBudget(), ct);

      if (rowsRaw.Count == 0) {
        if (hadAnyNew) {
          _inboxChannelWriter.SignalNewInboxWorkAvailable();
        }
        _logPerfIfInteresting(streamId, enqueued, fetchCount, totalDeserMs, totalWriteMs, drainStartTicks);
        return;
      }

      // Ordering invariant: defensive sort by message_id. SQL fetch_inbox_batch already
      // orders by (stream_id, message_id), but the apply boundary trusts only message_id.
      // See plans/ordered-stream-invariant.md.
      var rows = rowsRaw.OrderByMessageId().ToList();

      var newRows = 0;
      foreach (var row in rows) {
        if (!seen.Add(row.MessageId)) {
          continue;
        }
        newRows++;
        InboxWork work;
        var deserStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try {
          work = _toInboxWork(row);
        } catch (Exception ex) {
          LogDeserializeFailed(_logger, row.MessageId, ex);
          continue;
        }
        totalDeserMs += (System.Diagnostics.Stopwatch.GetTimestamp() - deserStart)
          * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var writeStart = System.Diagnostics.Stopwatch.GetTimestamp();
        await _inboxChannelWriter.WriteAsync(work, ct);
        totalWriteMs += (System.Diagnostics.Stopwatch.GetTimestamp() - writeStart)
          * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        enqueued++;
        hadAnyNew = true;
      }

      if (newRows == 0) {
        if (hadAnyNew) {
          _inboxChannelWriter.SignalNewInboxWorkAvailable();
        }
        _logPerfIfInteresting(streamId, enqueued, fetchCount, totalDeserMs, totalWriteMs, drainStartTicks);
        return;
      }

      // Slice 32 — partial-batch early exit. See OutboxDrainWorker for rationale: run-24
      // measured 91% of inbox drain wall time on fetch+other, with fetches/drain = 2.0
      // (every drain pays for a confirmation fetch that almost always returns 0).
      if (rowsRaw.Count < _options.MaxPerStream) {
        if (hadAnyNew) {
          _inboxChannelWriter.SignalNewInboxWorkAvailable();
        }
        _logPerfIfInteresting(streamId, enqueued, fetchCount, totalDeserMs, totalWriteMs, drainStartTicks);
        return;
      }
    }
    _logPerfIfInteresting(streamId, enqueued, fetchCount, totalDeserMs, totalWriteMs, drainStartTicks);
  }

  private void _logPerfIfInteresting(Guid streamId, int enqueued, int fetches, double deserMs, double writeMs, long startTicks) {
    if (!_logger.IsEnabled(LogLevel.Debug)) {
      return;
    }
    var totalMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks)
      * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    if (enqueued >= 5 || totalMs > 100) {
#pragma warning disable CA1848
      _logger.LogDebug(
        "PERF InboxDrain stream {StreamId}: enqueued={Enqueued} fetches={Fetches} total={TotalMs:F0}ms deser={DeserMs:F0}ms write={WriteMs:F0}ms other={OtherMs:F0}ms",
        streamId, enqueued, fetches, totalMs, deserMs, writeMs, totalMs - deserMs - writeMs);
#pragma warning restore CA1848
    }
  }

  private InboxWork _toInboxWork(InboxBatchRow row) {
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
      ?? throw new InvalidOperationException("No JsonTypeInfo for MessageEnvelope<JsonElement>.");
    var envelope = JsonSerializer.Deserialize(row.EventData, typeInfo) as IMessageEnvelope<JsonElement>
      ?? throw new InvalidOperationException($"Failed to deserialize envelope for inbox message {row.MessageId}.");

    return new InboxWork {
      MessageId = row.MessageId,
      Envelope = envelope,
      MessageType = row.MessageType,
      StreamId = row.StreamId,
      PartitionNumber = row.PartitionNumber,
      Attempts = row.Attempts,
      Status = (MessageProcessingStatus)row.Status,
      Flags = WorkBatchOptions.None,
      Error = row.Error,
    };
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "InboxDrainWorker started: maxPerStream={MaxPerStream}")]
  static partial void LogStarted(ILogger logger, int maxPerStream);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "InboxDrainWorker stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "InboxDrainWorker disabled — idle")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 4, Level = LogLevel.Error,
    Message = "InboxDrainWorker: drain failed for stream {StreamId}")]
  static partial void LogDrainError(ILogger logger, Guid streamId, Exception ex);

  [LoggerMessage(EventId = 6, Level = LogLevel.Error,
    Message = "Inbox drain batch failed on a transient error; the streams re-offer via the claim backstop")]
  static partial void LogBatchDrainFailed(ILogger logger, Exception exception);

  [LoggerMessage(EventId = 5, Level = LogLevel.Error,
    Message = "InboxDrainWorker: failed to deserialize envelope for {MessageId}")]
  static partial void LogDeserializeFailed(ILogger logger, Guid messageId, Exception ex);
}

/// <summary>Configuration for <see cref="InboxDrainWorker"/>.</summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public sealed class InboxDrainWorkerOptions {
  /// <summary>
  /// Killswitch.
  /// <para>
  /// <strong>Default: <c>true</c></strong> as of Phase H step 5d-flip. <c>claim_work</c>
  /// projects stream_ids only for inbox; this drainer is the only source of <see cref="InboxWork"/>
  /// for <c>InboxDispatchWorker</c>. Disabling it stops inbox dispatch entirely.
  /// </para>
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>Cap on how many leased inbox rows to drain per stream per iteration. Default 100.</summary>
  public int MaxPerStream { get; set; } = 100;

  /// <summary>
  /// Cap on the PAYLOAD BYTES a single fetch may return per stream. Default 4 MB;
  /// <c>null</c> or non-positive disables it, restoring the count-only bound.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <see cref="MaxPerStream"/> assumes rows are roughly the same size. Control-plane traffic
  /// breaks that badly: an integrity manifest carries up to MaxDigestsPerManifest digests, so one
  /// row can dwarf an ordinary command by orders of magnitude. Fetching 100 of those is tens of
  /// megabytes of JSON in one round trip, several times that once deserialized, per concurrent
  /// drain consumer — enough to OOM a service whose ordinary working set is comfortable.
  /// </para>
  /// <para>
  /// Observed live before this existed: services holding 540-700 queued manifests (83-105 MB)
  /// were OOMKilled on every start and never drained the backlog, because the fetch meant to make
  /// progress was what killed the process. The redelivery pump already had the equivalent bound
  /// (<c>MaxBytesPerComposite</c>); the drain fetch did not.
  /// </para>
  /// <para>
  /// The budget trims the TAIL of a slice: at least one row per stream is always returned, so an
  /// oversized message is still delivered rather than stalling its stream forever. Ordinary
  /// traffic never reaches the budget, so this changes nothing for it — 100 rows at 2 KB is
  /// 200 KB against a 4 MB ceiling.
  /// </para>
  /// </remarks>
  public long? MaxBytesPerStream { get; set; } = 4L * 1024 * 1024;

  /// <summary>
  /// Sliding-window batching policy for stream_id signals from <see cref="IInboxDrainChannel"/>.
  /// When the channel emits a stream_id, the drainer waits up to <see cref="SlidingWindowBatcherOptions.SlidingWindow"/>
  /// for additional signals before processing — letting more inbox messages accumulate before
  /// the fetch. Bounded by <see cref="SlidingWindowBatcherOptions.MaxWait"/> and
  /// <see cref="SlidingWindowBatcherOptions.MaxSize"/>.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/per-stream-drain#sliding-window</docs>
  public SlidingWindowBatcherOptions Batcher { get; set; } = new();
}
