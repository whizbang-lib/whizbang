namespace Whizbang.Core.Workers;

/// <summary>
/// Configuration options for message processing concurrency and inbox batching.
/// Controls how the transport consumer workers manage DB connection pressure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency control:</b> Each concurrent message handler holds a DB connection
/// from the pool for the duration of <c>process_work_batch</c>. With multiple subscriptions
/// and high <c>MaxConcurrentSessions</c>, the total concurrent handlers can exceed the
/// connection pool size. <see cref="MaxConcurrentMessages"/> caps total concurrent handlers
/// across all subscriptions via a shared semaphore.
/// </para>
/// <para>
/// <b>Inbox batching:</b> Instead of each handler making its own <c>process_work_batch</c>
/// DB call, the <see cref="IInboxBatchStrategy"/> collects messages and flushes them in a
/// single call. The batch parameters control when the flush triggers.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Override defaults before AddTransportConsumer():
/// services.AddSingleton(new MessageProcessingOptions {
///     MaxConcurrentMessages = 80,
///     InboxBatchSize = 50,
///     InboxBatchSlideMs = 100,
///     InboxBatchMaxWaitMs = 2000
/// });
/// </code>
/// </example>
/// <docs>messaging/transports/transport-consumer#message-processing-options</docs>
public sealed class MessageProcessingOptions {
  /// <summary>
  /// Maximum number of messages processed concurrently across ALL subscriptions.
  /// Each concurrent message holds a DB connection from the pool for the duration
  /// of inbox dedup, lifecycle stages, and completion flush.
  /// <para>
  /// Set to 0 to disable the global concurrency limit (not recommended).
  /// </para>
  /// Default: 40 (leaves headroom in a 100-connection pool for health checks,
  /// publisher worker, perspective worker, and other DB operations).
  /// </summary>
  /// <docs>messaging/transports/transport-consumer#concurrency</docs>
  public int MaxConcurrentMessages { get; set; } = 40;

  /// <summary>
  /// Maximum number of inbox messages to collect before flushing the dedup batch.
  /// When this many messages have been enqueued, the batch flushes immediately
  /// regardless of the sliding window or hard max timers.
  /// Default: 100
  /// </summary>
  /// <docs>messaging/transports/transport-consumer#inbox-batching</docs>
  public int InboxBatchSize { get; set; } = 100;

  /// <summary>
  /// Sliding window duration in milliseconds. After each new message is enqueued,
  /// the timer resets. When the timer expires (no new messages for this duration),
  /// the batch flushes with whatever messages have accumulated.
  /// <para>
  /// This optimizes for throughput during bursts: as long as messages keep arriving
  /// within the window, the batch grows. When the burst subsides, the partial batch
  /// flushes after a short quiet period.
  /// </para>
  /// Default: 50ms
  /// </summary>
  /// <docs>messaging/transports/transport-consumer#inbox-batching</docs>
  public int InboxBatchSlideMs { get; set; } = 50;

  /// <summary>
  /// Hard maximum wait time in milliseconds. The batch flushes after this duration
  /// regardless of whether messages are still arriving. This prevents unbounded
  /// latency during sustained high-throughput periods where the sliding window
  /// never expires.
  /// <para>
  /// The timer starts when the first message in a batch is enqueued and does NOT
  /// reset on subsequent messages.
  /// </para>
  /// Default: 1000ms (1 second)
  /// </summary>
  /// <docs>messaging/transports/transport-consumer#inbox-batching</docs>
  public int InboxBatchMaxWaitMs { get; set; } = 1000;
}
