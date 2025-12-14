using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Messaging;

/// <summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/OrderedStreamProcessorTests.cs:ProcessInboxWorkAsync_SingleStream_ProcessesInOrderAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/OrderedStreamProcessorTests.cs:ProcessInboxWorkAsync_MultipleStreams_ProcessesConcurrentlyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/OrderedStreamProcessorTests.cs:ProcessInboxWorkAsync_StreamWithError_ContinuesOtherStreamsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/OrderedStreamProcessorTests.cs:ProcessInboxWorkAsync_PartialFailure_ReportsCorrectStatusAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/OrderedStreamProcessorTests.cs:ProcessOutboxWorkAsync_SameStreamSameOrder_ProcessesSequentiallyAsync</tests>
/// Processes work batches while maintaining strict ordering per stream.
/// Events from the same stream are processed sequentially to preserve order.
/// Events from different streams CAN be processed in parallel (configurable).
/// </summary>
public class OrderedStreamProcessor {
  private readonly bool _parallelizeStreams;
  private readonly ILogger<OrderedStreamProcessor>? _logger;

  /// <summary>
  /// Creates a new OrderedStreamProcessor.
  /// </summary>
  /// <param name="parallelizeStreams">
  /// When true, different streams can be processed concurrently.
  /// When false, all streams processed sequentially (safer, simpler debugging).
  /// </param>
  /// <param name="logger">Optional logger</param>
  public OrderedStreamProcessor(bool parallelizeStreams = false, ILogger<OrderedStreamProcessor>? logger = null) {
    _parallelizeStreams = parallelizeStreams;
    _logger = logger;
  }

  /// <summary>
  /// Processes inbox work maintaining stream order.
  /// Groups messages by stream_id and processes each stream sequentially.
  /// Optionally processes different streams in parallel.
  /// </summary>
  /// <param name="inboxWork">Inbox work items to process</param>
  /// <param name="processor">Function to process a single inbox message, returns completed status</param>
  /// <param name="completionHandler">Handler for successful completion (e.g., queue to strategy)</param>
  /// <param name="failureHandler">Handler for failures (e.g., queue failure to strategy)</param>
  /// <param name="ct">Cancellation token</param>
  public async Task ProcessInboxWorkAsync(
    List<InboxWork> inboxWork,
    Func<InboxWork, Task<MessageProcessingStatus>> processor,
    Action<Guid, MessageProcessingStatus> completionHandler,
    Action<Guid, MessageProcessingStatus, string> failureHandler,
    CancellationToken ct = default
  ) {
    if (inboxWork == null || inboxWork.Count == 0) {
      return;
    }

    _logger?.LogDebug("Processing {Count} inbox messages", inboxWork.Count);

    // Group by stream, maintaining order within stream
    var streamGroups = inboxWork
      .GroupBy(w => w.StreamId ?? Guid.Empty)  // NULL stream = no ordering required, group together
      .Select(g => new StreamBatch<InboxWork> {
        StreamId = g.Key,
        Messages = g.OrderBy(m => m.SequenceOrder).ToList()  // Ensure ordering by sequence
      })
      .ToList();

    _logger?.LogDebug("Grouped into {StreamCount} streams", streamGroups.Count);

    if (_parallelizeStreams) {
      // Process different streams in parallel
      await Parallel.ForEachAsync(streamGroups, ct, async (streamBatch, token) => {
        await ProcessInboxStreamBatchAsync(streamBatch, processor, completionHandler, failureHandler, token);
      });
    } else {
      // Process streams sequentially (safer default)
      foreach (var streamBatch in streamGroups) {
        if (ct.IsCancellationRequested) {
          break;
        }

        await ProcessInboxStreamBatchAsync(streamBatch, processor, completionHandler, failureHandler, ct);
      }
    }
  }

  /// <summary>
  /// Processes outbox work maintaining stream order.
  /// Groups messages by stream_id and processes each stream sequentially.
  /// Optionally processes different streams in parallel.
  /// </summary>
  /// <param name="outboxWork">Outbox work items to process</param>
  /// <param name="processor">Function to process a single outbox message, returns completed status</param>
  /// <param name="completionHandler">Handler for successful completion</param>
  /// <param name="failureHandler">Handler for failures</param>
  /// <param name="ct">Cancellation token</param>
  public async Task ProcessOutboxWorkAsync(
    List<OutboxWork> outboxWork,
    Func<OutboxWork, Task<MessageProcessingStatus>> processor,
    Action<Guid, MessageProcessingStatus> completionHandler,
    Action<Guid, MessageProcessingStatus, string> failureHandler,
    CancellationToken ct = default
  ) {
    if (outboxWork == null || outboxWork.Count == 0) {
      return;
    }

    _logger?.LogDebug("Processing {Count} outbox messages", outboxWork.Count);

    // Group by stream, maintaining order within stream
    var streamGroups = outboxWork
      .GroupBy(w => w.StreamId ?? Guid.Empty)
      .Select(g => new StreamBatch<OutboxWork> {
        StreamId = g.Key,
        Messages = g.OrderBy(m => m.SequenceOrder).ToList()
      })
      .ToList();

    _logger?.LogDebug("Grouped into {StreamCount} streams", streamGroups.Count);

    if (_parallelizeStreams) {
      await Parallel.ForEachAsync(streamGroups, ct, async (streamBatch, token) => {
        await ProcessOutboxStreamBatchAsync(streamBatch, processor, completionHandler, failureHandler, token);
      });
    } else {
      foreach (var streamBatch in streamGroups) {
        if (ct.IsCancellationRequested) {
          break;
        }

        await ProcessOutboxStreamBatchAsync(streamBatch, processor, completionHandler, failureHandler, ct);
      }
    }
  }

  /// <summary>
  /// Processes messages for a single stream SEQUENTIALLY.
  /// Stops processing this stream on first failure (preserves ordering guarantee).
  /// </summary>
  private async Task ProcessInboxStreamBatchAsync(
    StreamBatch<InboxWork> streamBatch,
    Func<InboxWork, Task<MessageProcessingStatus>> processor,
    Action<Guid, MessageProcessingStatus> completionHandler,
    Action<Guid, MessageProcessingStatus, string> failureHandler,
    CancellationToken ct
  ) {
    _logger?.LogDebug(
      "Processing stream {StreamId} with {MessageCount} messages",
      streamBatch.StreamId == Guid.Empty ? "NULL" : streamBatch.StreamId.ToString(),
      streamBatch.Messages.Count
    );

    // Process messages in this stream SEQUENTIALLY (strict ordering)
    foreach (var message in streamBatch.Messages) {
      if (ct.IsCancellationRequested) {
        break;
      }

      try {
        var completedStatus = await processor(message);
        completionHandler(message.MessageId, completedStatus);

        _logger?.LogDebug(
          "Successfully processed message {MessageId} from stream {StreamId} with status {Status}",
          message.MessageId,
          streamBatch.StreamId,
          completedStatus
        );
      } catch (Exception ex) {
        _logger?.LogError(
          ex,
          "Failed to process message {MessageId} from stream {StreamId}",
          message.MessageId,
          streamBatch.StreamId
        );

        // Determine what succeeded before failure
        var partialStatus = message.Status;  // What was already completed
        failureHandler(message.MessageId, partialStatus, ex.Message);

        // STOP processing this stream on failure (maintain ordering)
        // Remaining messages will be retried in next batch
        _logger?.LogWarning(
          "Stopping stream {StreamId} processing due to failure. {RemainingCount} messages will retry later.",
          streamBatch.StreamId,
          streamBatch.Messages.Count - streamBatch.Messages.IndexOf(message) - 1
        );
        break;
      }
    }
  }

  /// <summary>
  /// Processes outbox messages for a single stream SEQUENTIALLY.
  /// </summary>
  private async Task ProcessOutboxStreamBatchAsync(
    StreamBatch<OutboxWork> streamBatch,
    Func<OutboxWork, Task<MessageProcessingStatus>> processor,
    Action<Guid, MessageProcessingStatus> completionHandler,
    Action<Guid, MessageProcessingStatus, string> failureHandler,
    CancellationToken ct
  ) {
    _logger?.LogDebug(
      "Processing outbox stream {StreamId} with {MessageCount} messages",
      streamBatch.StreamId == Guid.Empty ? "NULL" : streamBatch.StreamId.ToString(),
      streamBatch.Messages.Count
    );

    foreach (var message in streamBatch.Messages) {
      if (ct.IsCancellationRequested) {
        break;
      }

      try {
        var completedStatus = await processor(message);
        completionHandler(message.MessageId, completedStatus);

        _logger?.LogDebug(
          "Successfully processed outbox message {MessageId} from stream {StreamId}",
          message.MessageId,
          streamBatch.StreamId
        );
      } catch (Exception ex) {
        _logger?.LogError(
          ex,
          "Failed to process outbox message {MessageId} from stream {StreamId}",
          message.MessageId,
          streamBatch.StreamId
        );

        var partialStatus = message.Status;
        failureHandler(message.MessageId, partialStatus, ex.Message);

        _logger?.LogWarning(
          "Stopping outbox stream {StreamId} processing due to failure.",
          streamBatch.StreamId
        );
        break;
      }
    }
  }

  /// <summary>
  /// Represents a batch of messages from a single stream, ordered by sequence.
  /// </summary>
  private record StreamBatch<TWork> {
    public required Guid StreamId { get; init; }
    public required List<TWork> Messages { get; init; }
  }
}

/// <summary>
/// Configuration options for OrderedStreamProcessor.
/// </summary>
public class OrderedStreamProcessorOptions {
  /// <summary>
  /// Process different streams in parallel within an instance (default false).
  /// When true: Stream A and Stream B can be processed concurrently.
  /// When false: Streams processed sequentially (safer, simpler debugging).
  /// </summary>
  public bool ParallelizeStreams { get; set; } = false;
}
