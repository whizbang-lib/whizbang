namespace Whizbang.Core.Workers;

/// <summary>
/// Configuration options for transport-level batch message collection.
/// Controls how the transport collects messages before flushing them
/// as a batch to the inbox via <c>process_work_batch</c>.
/// </summary>
/// <remarks>
/// <para>
/// Three flush triggers (whichever fires first):
/// <list type="bullet">
///   <item><b>Batch size</b>: <see cref="BatchSize"/> messages accumulated → immediate flush</item>
///   <item><b>Sliding window</b>: <see cref="SlideMs"/> ms since last enqueue → flush partial batch</item>
///   <item><b>Hard max</b>: <see cref="MaxWaitMs"/> ms since first message in batch → flush regardless</item>
/// </list>
/// </para>
/// </remarks>
/// <docs>messaging/transports/transport-consumer#batch-options</docs>
public sealed class TransportBatchOptions {
  /// <summary>
  /// Maximum number of messages to collect before flushing the batch.
  /// When this many messages have been enqueued, the batch flushes immediately
  /// regardless of the sliding window or hard max timers.
  /// Default: 200
  /// </summary>
  /// <docs>messaging/transports/transport-consumer#batch-options</docs>
  public int BatchSize { get; set; } = 200;

  /// <summary>
  /// Sliding window duration in milliseconds. After each new message is enqueued,
  /// the timer resets. When the timer expires (no new messages for this duration),
  /// the batch flushes with whatever messages have accumulated.
  /// Default: 20ms
  /// </summary>
  /// <docs>messaging/transports/transport-consumer#batch-options</docs>
  public int SlideMs { get; set; } = 20;

  /// <summary>
  /// Hard maximum wait time in milliseconds. The batch flushes after this duration
  /// regardless of whether messages are still arriving. This prevents unbounded
  /// latency during sustained high-throughput periods.
  /// Default: 1000ms (1 second)
  /// </summary>
  /// <docs>messaging/transports/transport-consumer#batch-options</docs>
  public int MaxWaitMs { get; set; } = 1000;
}
