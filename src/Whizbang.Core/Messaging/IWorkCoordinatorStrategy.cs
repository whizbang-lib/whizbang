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
  /// Flushes all queued operations by calling process_work_batch.
  /// Returns work items that need processing.
  /// When flush occurs depends on the strategy implementation.
  /// </summary>
  /// <param name="flags">Work batch flags (e.g., DebugMode)</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>Work batch containing messages to process</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:FlushAsync_ImmediatelyCallsWorkCoordinatorAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyTests.cs:FlushAsync_BeforeDisposal_FlushesImmediatelyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:ManualFlushAsync_DoesNotWaitForTimerAsync</tests>
  Task<WorkBatch> FlushAsync(WorkBatchFlags flags, CancellationToken ct = default);
}

/// <summary>
/// Strategy types for work coordinator configuration.
/// </summary>
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
  Interval
}

/// <summary>
/// Configuration options for work coordinator strategies.
/// </summary>
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
  /// Stale instance threshold in seconds (default 600 = 10 minutes).
  /// </summary>
  public int StaleThresholdSeconds { get; set; } = 600;
}
