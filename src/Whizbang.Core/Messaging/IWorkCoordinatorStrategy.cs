using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Strategy for coordinating work batch operations.
/// Defines when and how messages are stored and completions/failures are reported.
/// Implementations can provide immediate, scoped (unit-of-work), or interval-based processing.
/// </summary>
public interface IWorkCoordinatorStrategy {
  /// <summary>
  /// Queues an outbox message to be stored.
  /// When it's actually stored depends on the strategy (immediate, on flush, on interval, etc.).
  /// </summary>
  /// <param name="message">Pre-serialized outbox message to store</param>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:QueueOutboxMessage_FlushesOnCallAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyTests.cs:DisposeAsync_FlushesQueuedMessagesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:BackgroundTimer_FlushesEveryIntervalAsync</tests>
  void QueueOutboxMessage(OutboxMessage message);

  /// <summary>
  /// Async overload of <see cref="QueueOutboxMessage"/>. The default implementation simply
  /// delegates to the sync method and returns <see cref="Task.CompletedTask"/> — strategies
  /// that don't need async semantics get this for free. Strategies that route through async
  /// abstractions (e.g. <see cref="IOutboxBatchStrategy"/> for producer-side stream-affinity
  /// batching) override this to perform real async work and may make the sync overload throw
  /// to surface migration gaps.
  /// </summary>
  /// <param name="message">Pre-serialized outbox message to store.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>internals/outbox-batch-strategy</docs>
  Task QueueOutboxMessageAsync(OutboxMessage message, CancellationToken cancellationToken = default) {
    QueueOutboxMessage(message);
    return Task.CompletedTask;
  }

  /// <summary>
  /// Queues an inbox message to be stored.
  /// Includes atomic deduplication and optional event store integration.
  /// </summary>
  /// <param name="message">Pre-serialized inbox message to store</param>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:QueueInboxMessage_FlushesOnCallAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyTests.cs:DisposeAsync_FlushesQueuedMessagesAsync</tests>
  void QueueInboxMessage(InboxMessage message);

  /// <summary>
  /// Queues an outbox message completion with granular status tracking.
  /// </summary>
  /// <param name="messageId">Message ID that completed</param>
  /// <param name="completedStatus">Which stages completed successfully</param>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyTests.cs:MultipleQueues_FlushedTogetherOnDisposalAsync</tests>
  void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus);

  /// <summary>
  /// Queues an inbox message completion with granular status tracking.
  /// </summary>
  /// <param name="messageId">Message ID that completed</param>
  /// <param name="completedStatus">Which stages completed successfully</param>
  void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus);

  /// <summary>
  /// Queues an outbox message failure with partial completion tracking.
  /// </summary>
  /// <param name="messageId">Message ID that failed</param>
  /// <param name="completedStatus">Which stages succeeded before failure</param>
  /// <param name="errorMessage">Error message or exception details</param>
  void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage);

  /// <summary>
  /// Queues an inbox message failure with partial completion tracking.
  /// </summary>
  /// <param name="messageId">Message ID that failed</param>
  /// <param name="completedStatus">Which stages succeeded before failure</param>
  /// <param name="errorMessage">Error message or exception details</param>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyTests.cs:MultipleQueues_FlushedTogetherOnDisposalAsync</tests>
  void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage);

  /// <summary>
  /// Signals the end of a queueing batch. The strategy decides when to actually flush:
  /// Immediate and Scoped flush now, Interval defers to its timer, Batch defers to debounce/size trigger.
  /// Use for fire-and-forget callers (cascade-to-outbox, routed publish/send) that do not consume the WorkBatch.
  /// </summary>
  /// <param name="flags">Work batch flags (e.g., SkipInboxClaiming)</param>
  /// <param name="ct">Cancellation token</param>
  /// <docs>data/work-coordinator-strategies</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/FlushApiTests.cs</tests>
  Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default);

  /// <summary>
  /// Forces an immediate flush and returns the resulting WorkBatch. Bypasses any batching window
  /// (Interval timer, Batch debounce). Use for dedup callers that must consume the WorkBatch
  /// (inbox consumers filtering work by MessageId) or end-of-scope flushes that must block until persisted.
  /// </summary>
  /// <param name="flags">Work batch flags (e.g., SkipInboxClaiming)</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>Work batch containing messages to process</returns>
  /// <docs>data/work-coordinator-strategies</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/FlushApiTests.cs</tests>
  Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default);
}

/// <summary>
/// Strategy types for work coordinator configuration.
/// </summary>
/// <docs>data/work-coordinator-strategies</docs>
public enum WorkCoordinatorStrategy {
  /// <summary>
  /// Immediate strategy - calls process_work_batch immediately for each operation.
  /// Lowest latency, highest database load.
  /// </summary>
  Immediate,

  /// <summary>
  /// Scoped strategy - batches operations within a scope (e.g., HTTP request).
  /// Flushes on scope disposal (IAsyncDisposable pattern).
  /// Good balance of latency and efficiency.
  /// </summary>
  Scoped,

  /// <summary>
  /// Interval strategy - batches operations and flushes on a timer.
  /// Lowest database load, higher latency.
  /// Useful for background workers with high throughput.
  /// </summary>
  Interval,

  /// <summary>
  /// Batch strategy - flushes when batch size is reached OR after a debounce quiet period.
  /// Combines count-based and time-based triggers for optimal throughput.
  /// Best for: Bulk imports, seeding, high-volume background processing.
  /// </summary>
  Batch
}

/// <summary>
/// Configuration options for work coordinator strategies.
/// </summary>
/// <docs>data/work-coordinator-strategies</docs>
public class WorkCoordinatorOptions {
  /// <summary>
  /// Total number of partitions (default 10,000).
  /// Higher values = finer-grained distribution.
  /// </summary>
  public int PartitionCount { get; set; } = 10_000;

  /// <summary>
  /// Process different streams in parallel within an instance (default false).
  /// When true: Stream A and Stream B can be processed concurrently.
  /// When false: Streams processed sequentially (safer, simpler debugging).
  /// </summary>
  public bool ParallelizeStreams { get; set; }

  /// <summary>
  /// Strategy for flushing work (Immediate, Scoped, Interval).
  /// </summary>
  public WorkCoordinatorStrategy Strategy { get; set; } = WorkCoordinatorStrategy.Scoped;

  /// <summary>
  /// Interval for batch flushing (ms) when Strategy = Interval.
  /// </summary>
  public int IntervalMilliseconds { get; set; } = 100;

  /// <summary>
  /// Keep completed messages for debugging (default: Development mode only).
  /// </summary>
  public bool DebugMode { get; set; }

  /// <summary>
  /// Lease duration in seconds (default 300 = 5 minutes).
  /// </summary>
  public int LeaseSeconds { get; set; } = 300;

  /// <summary>
  /// Grace period before a non-heartbeating instance is abandoned, in seconds (default: 30).
  /// With ~1 s ticks, 30 missed heartbeats prove an instance is dead. After this, the instance's
  /// message leases are released and its stream ownership no longer blocks cross-instance claims.
  /// See <see cref="Whizbang.Core.Workers.WorkCoordinatorPublisherOptions.AbandonStaleInstanceThresholdSeconds"/>
  /// for the full rationale and tuning guidance.
  /// </summary>
  public int AbandonStaleInstanceThresholdSeconds { get; set; } = 30;

  /// <summary>
  /// Coalescing window in milliseconds for Required flushes on Interval strategy.
  /// When > 0, a Required flush waits this long to pick up other queued items before executing.
  /// Default: 0 (no coalescing). Recommended: 50ms for Interval strategy.
  /// </summary>
  public int CoalesceWindowMilliseconds { get; set; }

  /// <summary>
  /// Number of queued messages that triggers an immediate flush when Strategy = Batch.
  /// When the total queued message count reaches this threshold, flush fires immediately
  /// without waiting for the debounce timer.
  /// Default: 100.
  /// </summary>
  public int BatchSize { get; set; } = 100;
}
