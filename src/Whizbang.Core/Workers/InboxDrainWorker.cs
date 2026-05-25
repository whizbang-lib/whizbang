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
        // Dedupe within the batch — multiple ClaimWorker ticks during the sliding window can
        // emit the same stream_id repeatedly. Each unique stream is drained once;
        // FetchInboxBatchAsync returns all pending rows for it in stream-FIFO order.
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
        [streamId], _instanceProvider.InstanceId, _options.MaxPerStream, ct);

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
    var totalMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks)
      * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    if (enqueued >= 5 || totalMs > 100) {
#pragma warning disable CA1848
      _logger.LogWarning(
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
  /// Sliding-window batching policy for stream_id signals from <see cref="IInboxDrainChannel"/>.
  /// When the channel emits a stream_id, the drainer waits up to <see cref="SlidingWindowBatcherOptions.SlidingWindow"/>
  /// for additional signals before processing — letting more inbox messages accumulate before
  /// the fetch. Bounded by <see cref="SlidingWindowBatcherOptions.MaxWait"/> and
  /// <see cref="SlidingWindowBatcherOptions.MaxSize"/>.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/per-stream-drain#sliding-window</docs>
  public SlidingWindowBatcherOptions Batcher { get; set; } = new();
}
