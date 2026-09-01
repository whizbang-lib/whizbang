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
  private readonly ClaimChurnFeedback? _churnFeedback;
  private readonly PoisonAdmissionPolicy _poisonPolicy = new(new PoisonAdmissionPolicy.Settings());
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
    ILogger<InboxDrainWorker> logger,
    ClaimChurnFeedback? churnFeedback = null) {
    _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    _churnFeedback = churnFeedback;
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

  // Sizes the per-stream page from observed depth. Lazily built so the options are read after
  // configuration has bound, and null when pinned so the previous fixed-cap path is untouched.
  private AdaptiveStreamBatch? _streamBatchGovernor;

  private AdaptiveStreamBatch? _streamBatch() {
    if (!_options.AdaptivePerStreamEnabled) {
      return null;
    }
    return _streamBatchGovernor ??= new AdaptiveStreamBatch(
      ceiling: Math.Max(_options.MaxPerStream, _options.MaxPerStreamCeiling),
      floor: _options.MaxPerStream,
      additiveStep: _options.MaxPerStream);
  }

  /// <summary>Rows to request per stream on the next fetch.</summary>
  private int _effectivePerStream() => _streamBatch()?.Current ?? _options.MaxPerStream;

  // What each stream was last seen holding. Bounded by pruning to the streams actually being
  // drained: an unbounded map keyed by stream id would grow for the life of the process on a
  // workload that keeps minting new streams.
  private readonly Dictionary<Guid, int> _observedDepth = [];
  private StreamFairShareAllocator? _allocator;

  private StreamFairShareAllocator _fairShare() =>
    _allocator ??= new StreamFairShareAllocator(new StreamFairShareAllocator.Settings {
      MinRowsPerStream = Math.Max(1, _options.MinRowsPerStream),
      MaxRowsPerStream = Math.Max(_options.MaxPerStream, _options.MaxPerStreamCeiling),
    });

  /// <summary>One fetch: a cap, and the streams that share it.</summary>
  /// <param name="Cap">Rows to request per stream in this call.</param>
  /// <param name="Streams">Streams travelling together at that cap.</param>
  internal readonly record struct FetchGroup(int Cap, IReadOnlyList<Guid> Streams);

  /// <summary>
  /// Turns a global row budget into the fetches that will actually be issued.
  /// </summary>
  /// <remarks>
  /// A fetch takes ONE cap for every stream in the call, so per-stream allocations cannot be issued
  /// directly. Each allocation is quantized DOWN to a multiple of the floor and streams sharing a
  /// quantized cap travel together: that bounds the call count to ceiling/floor however many
  /// streams are active, and rounding down keeps the total inside the budget instead of drifting
  /// over it every cycle.
  /// </remarks>
  private List<FetchGroup> _planFetches(IReadOnlyList<Guid> streamIds) {
    if (streamIds.Count == 0) {
      return [];
    }

    var floor = Math.Max(1, _options.MaxPerStream);
    var ceiling = Math.Max(floor, _options.MaxPerStreamCeiling);

    // Default budget is what the previous fixed cap implied, so redistribution never costs total
    // throughput -- it only changes WHERE the rows go.
    // The breadth pass reserves a floor for EVERY stream, so a budget of exactly streams x floor is
    // consumed before depth weighting runs — every stream, one row deep or ten thousand, then gets
    // the same page. That made the adaptation engage only at LOW stream counts, which is backwards:
    // a deep stream among many is precisely the case that serializes.
    //
    // DepthHeadroomFactor funds depth on top of breadth. The ceiling term additionally lets a
    // single deep stream reach full width when few streams are active. Growth stays bounded by the
    // per-stream ceiling and by what a lease window can drain.
    var headroom = Math.Max(1, _options.DepthHeadroomFactor);
    var budget = _options.MaxRowsPerCycle > 0
      ? _options.MaxRowsPerCycle
      : Math.Max(streamIds.Count * floor * headroom, ceiling);

    var demands = new List<StreamDemand>(streamIds.Count);
    for (var i = 0; i < streamIds.Count; i++) {
      // A stream nobody has measured is assumed floor-deep: enough to be admitted and produce the
      // observation that sizes it properly next cycle.
      var depth = _observedDepth.TryGetValue(streamIds[i], out var d) && d > 0 ? d : floor;
      demands.Add(new StreamDemand(streamIds[i], depth));
    }

    var groups = new Dictionary<int, List<Guid>>();
    foreach (var a in _fairShare().Allocate(budget, demands)) {
      // Quantize DOWN to a floor multiple, never below the floor: a thinner slice fetches a
      // uselessly small page and guarantees another round-trip.
      var quantized = Math.Clamp(a.Rows / floor * floor, floor, ceiling);
      if (!groups.TryGetValue(quantized, out var list)) {
        list = [];
        groups[quantized] = list;
      }
      list.Add(a.StreamId);
    }

    var plan = new List<FetchGroup>(groups.Count);
    foreach (var kv in groups) {
      plan.Add(new FetchGroup(kv.Key, kv.Value));
    }
    return plan;
  }

  private void _recordDepth(Guid streamId, int rowsReturned, int capRequested) {
    // A saturated fetch proves only a LOWER bound, so credit the stream with more than it returned
    // or the allocation can never climb past the cap that limited it.
    _observedDepth[streamId] = rowsReturned >= capRequested ? capRequested * 2 : rowsReturned;
  }

  private void _pruneDepth(IReadOnlyList<Guid> keep) {
    if (_observedDepth.Count <= 4096) {
      return;
    }
    var live = new HashSet<Guid>(keep);
    var stale = new List<Guid>();
    foreach (var k in _observedDepth.Keys) {
      if (!live.Contains(k)) {
        stale.Add(k);
      }
    }
    for (var i = 0; i < stale.Count; i++) {
      _observedDepth.Remove(stale[i]);
    }
  }

  /// <summary>Test seam: the fetches that would be issued for these streams.</summary>
  internal IReadOnlyList<FetchGroup> PlanFetchesForTest(IReadOnlyList<Guid> streamIds)
    => _planFetches(streamIds);

  /// <summary>Test seam: seeds what a stream was last seen holding.</summary>
  internal void RecordObservedDepthForTest(Guid streamId, int depth) => _observedDepth[streamId] = depth;

  /// <summary>Test seam for the depth-map prune. The prune only runs mid-cycle past a size
  /// threshold, so reaching it through a real drain would need a coordinator serving thousands
  /// of streams.</summary>
  internal void PruneDepthForTest(IReadOnlyList<Guid> keep) => _pruneDepth(keep);

  /// <summary>Test seam: how many streams the depth map is currently holding.</summary>
  internal int ObservedDepthCountForTest => _observedDepth.Count;

  /// <summary>Test seam: the page size the next fetch would request.</summary>
  internal int EffectivePerStreamForTest() => _effectivePerStream();

  /// <summary>Test seam: folds one fetch outcome in, exactly as the drain path does.</summary>
  internal void ObservePageForTest(int rowsReturned, int capRequested, IReadOnlyList<InboxBatchRow> rows)
    => _observePage(rowsReturned, capRequested, rows);

  /// <summary>
  /// Folds one stream's fetch outcome into the page size.
  /// </summary>
  /// <remarks>
  /// Growth is NOT gated on the outstanding budget's drain sample here, unlike the claim window.
  /// That gate exists because the window infers capacity indirectly and would otherwise ramp blind
  /// at cold start. This control's growth signal is direct evidence -- a page that came back full
  /// means the stream really held that much -- and it starts at the floor and adds one step per
  /// clean saturated cycle, so it cannot jump ahead of what it has actually observed.
  /// </remarks>
  private void _observePage(int rowsReturned, int capRequested, IReadOnlyList<InboxBatchRow> rows) {
    var governor = _streamBatch();
    if (governor is null) {
      return;
    }
    var reclaimed = 0;
    for (var i = 0; i < rows.Count; i++) {
      if (rows[i].Attempts > 1) {
        reclaimed++;
      }
    }
    governor.Observe(rowsReturned, capRequested, reclaimed);
  }

  private async Task _drainStreamBatchAsync(List<Guid> streamIds, CancellationToken ct) {
    foreach (var sid in streamIds) {
      _drainChannel.MarkDraining(sid);
    }
    var batchScopeOk = false;
    try {
      using var scope = _scopeFactory.CreateScope();
      var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

      // One fetch takes a single cap, so the allocation is issued as one fetch per quantized cap:
      // deep streams travel together at a wide page, shallow ones at the floor. Bounded to
      // ceiling/floor calls however many streams are active.
      var plan = _planFetches(streamIds);
      var capByStream = new Dictionary<Guid, int>(streamIds.Count);
      var collected = new List<InboxBatchRow>();
      foreach (var group in plan) {
        if (ct.IsCancellationRequested) {
          break;
        }
        var part = await coordinator.FetchInboxBatchAsync(
          group.Streams, _instanceProvider.InstanceId, group.Cap, _byteBudget(), ct);
        collected.AddRange(part);
        for (var i = 0; i < group.Streams.Count; i++) {
          capByStream[group.Streams[i]] = group.Cap;
        }
      }
      IReadOnlyList<InboxBatchRow> rowsRaw = collected;
      ReportChurnForTest(rowsRaw);
      batchScopeOk = true;

      // Group rows by drain-key (stream_id when set, else message_id — matches the
      // fallback semantics in fetch_inbox_batch's WHERE clause for unscoped/null-stream
      // rows). The drain channel feeds the same key, so dispatch can look it up.
      var perStream = rowsRaw
        .GroupBy(r => DrainKey.For(r.StreamId, r.MessageId))
        .ToDictionary(g => g.Key, g => g.OrderByMessageId().ToList());

      // Observe per STREAM against the cap THAT stream was actually fetched with — a stream in the
      // floor bucket and one in the ceiling bucket saturate at very different widths, so a single
      // shared cap would misreport both.
      foreach (var kv in perStream) {
        var cap = capByStream.TryGetValue(kv.Key, out var c) ? c : _effectivePerStream();
        _observePage(kv.Value.Count, cap, kv.Value);
        // Feeds the NEXT cycle's allocation. Only recorded for streams that returned rows: a stream
        // that came back empty may simply have been drained, and writing zero would drop it from
        // the next plan even though a notify had just said it had work.
        _recordDepth(kv.Key, kv.Value.Count, cap);
      }
      _pruneDepth(streamIds);

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
            // Retried rows yield to fresh work when they already dominate the set. Deferred rows
            // stay unprocessed and are re-fetched later; that is how healthy work gets through a
            // working set otherwise monopolised by rows that cannot succeed.
            if (!_admitRow(row, rowsRaw)) {
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
          if (rows.Count >= capByStream.GetValueOrDefault(sid, _options.MaxPerStream)) {
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
      var singleCap = _effectivePerStream();
      var rowsRaw = await coordinator.FetchInboxBatchAsync(
        [streamId], _instanceProvider.InstanceId, singleCap, _byteBudget(), ct);
      ReportChurnForTest(rowsRaw);
      _observePage(rowsRaw.Count, singleCap, rowsRaw);

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
        if (!_admitRow(row, rowsRaw)) {
          continue;
        }
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

  [LoggerMessage(EventId = 61, Level = LogLevel.Warning,
    Message = "Poison admission gate would have deferred ALL {RowCount} fetched row(s); admitting "
            + "the least-retried (attempts={Attempts}) to keep the cycle moving. A fetch made "
            + "entirely of retried rows means the working set is saturated and healthy work is "
            + "waiting behind it.")]
  static partial void LogPoisonGateForcedProgress(ILogger logger, int rowCount, int attempts);

  [LoggerMessage(EventId = 5, Level = LogLevel.Error,
    Message = "InboxDrainWorker: failed to deserialize envelope for {MessageId}")]
  static partial void LogDeserializeFailed(ILogger logger, Guid messageId, Exception ex);

  /// <summary>
  /// Reports fetched rows' attempt counts to the claim window's feedback seam.
  /// </summary>
  /// <remarks>
  /// This worker is the only place the attempt counts exist on the stream-id path: the claim
  /// returns stream ids and never sees a row. Without this report the adaptive claim window
  /// observes zero churn for the life of the process and never adapts.
  /// </remarks>
  internal void ReportChurnForTest(IReadOnlyList<InboxBatchRow> rows) {
    if (_churnFeedback is null || rows.Count == 0) {
      return;
    }
    var attempts = new int[rows.Count];
    for (var i = 0; i < rows.Count; i++) {
      attempts[i] = rows[i].Attempts;
    }
    _churnFeedback.Report(attempts);
  }


  /// <summary>
  /// Decides which fetched rows may enter the dispatch working set this cycle.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Failing rows are re-claimed when their lease lapses, so they permanently occupy the working
  /// set and the claim never reaches rows behind them. Measured side by side on identical framework
  /// and configuration: a consumer whose set had been retried into the teens held ~10,000 leases
  /// and drained ~29 rows/min with 95% of its inbox never claimed, while a comparison consumer at
  /// first delivery drained the same backlog at ~8,000 rows/min.
  /// </para>
  /// <para>
  /// Deferred rows are simply not written this cycle. They stay unprocessed and are re-fetched
  /// later, which is the point: fresh work gets through while the retried population drains at its
  /// own pace.
  /// </para>
  /// <para>
  /// FORWARD PROGRESS OVERRIDES THE GATE. If every row in a fetch is high-attempt the share is 1.0
  /// and a naive gate would defer all of them, admit nothing, and livelock — turning a starvation
  /// problem into a full stop. At least one row is always admitted.
  /// </para>
  /// </remarks>

  private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, bool[]> _plans = new();

  /// <summary>Applies the admission plan for the row's containing fetch, computing it once.</summary>
  private bool _admitRow(InboxBatchRow row, IReadOnlyList<InboxBatchRow> fetch) {
    var plan = _plans.GetValue(fetch, f => AdmissionPlanForTest((IReadOnlyList<InboxBatchRow>)f));
    for (var i = 0; i < fetch.Count; i++) {
      if (fetch[i].MessageId == row.MessageId) {
        return plan[i];
      }
    }
    return true;
  }

  internal bool[] AdmissionPlanForTest(IReadOnlyList<InboxBatchRow> rows) {
    var plan = new bool[rows.Count];
    if (rows.Count == 0) {
      return plan;
    }

    var settings = new PoisonAdmissionPolicy.Settings();
    var high = 0;
    for (var i = 0; i < rows.Count; i++) {
      if (rows[i].Attempts >= settings.HighAttemptThreshold) {
        high++;
      }
    }
    var share = (double)high / rows.Count;

    var admitted = 0;
    for (var i = 0; i < rows.Count; i++) {
      var d = _poisonPolicy.Evaluate(rows[i].Attempts, rows.Count, share);
      plan[i] = d.Admit;
      if (d.Admit) {
        admitted++;
      }
    }

    if (admitted == 0) {
      // Everything was gated. Admit the least-retried row so the cycle still moves; a gate that can
      // stop all progress is worse than the starvation it prevents.
      var best = 0;
      for (var i = 1; i < rows.Count; i++) {
        if (rows[i].Attempts < rows[best].Attempts) {
          best = i;
        }
      }
      plan[best] = true;
      LogPoisonGateForcedProgress(_logger, rows.Count, rows[best].Attempts);
    }
    return plan;
  }

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
  /// Upper bound the adaptive per-stream page may grow to (default 1000). <see cref="MaxPerStream"/>
  /// becomes the FLOOR it starts from, so existing behavior is the starting point rather than the
  /// fixed value.
  /// </summary>
  /// <remarks>
  /// A fixed page is right for the shape this drain was tuned on -- many streams holding a row or
  /// two -- and pathological for the inverse. A stream holding thousands is otherwise walked one
  /// capped page at a time by a single drainer, each page its own round-trip, so effective
  /// parallelism becomes the stream COUNT and extra replicas idle.
  /// </remarks>
  public int MaxPerStreamCeiling { get; set; } = 1000;

  /// <summary>
  /// Whether the per-stream page adapts to observed stream depth (default true). Set false to pin
  /// it at <see cref="MaxPerStream"/> exactly as before.
  /// </summary>
  public bool AdaptivePerStreamEnabled { get; set; } = true;

  /// <summary>
  /// Rows this drain cycle may fetch in TOTAL across all streams. Zero (default) derives it from
  /// the stream count times <see cref="MaxPerStream"/> -- the same total the previous fixed-cap
  /// behavior implied, so nothing shrinks on upgrade and the allocator only REDISTRIBUTES it.
  /// </summary>
  /// <remarks>
  /// Throughput is a property of total rows moved, so the budget is denominated globally. A
  /// per-stream cap fixes the wrong quantity: the total then swings with however many streams
  /// happen to be active, and no single value suits both a thousand one-row streams and one stream
  /// holding thousands.
  /// </remarks>
  public int MaxRowsPerCycle { get; set; }

  /// <summary>
  /// Rows guaranteed to each admitted stream when the budget is divided (default 100, matching
  /// <see cref="MaxPerStream"/>).
  /// </summary>
  /// <remarks>
  /// This must not sit below the quantization floor. An allocation smaller than the floor cannot be
  /// issued -- a fetch page thinner than the floor is a uselessly small slice -- so it would be
  /// rounded back UP, spending more rows than the budget granted. Keeping the guarantee equal to
  /// the floor means the budget instead seats FEWER streams this cycle, which is the honest
  /// response, and the allocator's rotation gives the rest their turn next cycle.
  /// </remarks>
  public int MinRowsPerStream { get; set; } = 100;

  /// <summary>
  /// Multiplier applied to the derived per-cycle budget so depth weighting has headroom above the
  /// breadth guarantee (default 4). Ignored when <see cref="MaxRowsPerCycle"/> is set explicitly.
  /// </summary>
  /// <remarks>
  /// The breadth pass reserves <see cref="MinRowsPerStream"/> for every admitted stream. Without
  /// headroom above that, the budget is exhausted before depth is weighted and every stream
  /// receives the floor regardless of how much it holds — so a stream holding thousands drains in
  /// ceiling/floor sequential round-trips, by one worker, while the rest of the fleet is idle.
  /// Raising this funds depth; the per-stream ceiling still bounds any single fetch.
  /// </remarks>
  public int DepthHeadroomFactor { get; set; } = 4;

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
