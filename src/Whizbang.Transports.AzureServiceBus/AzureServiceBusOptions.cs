namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Configuration options for Azure Service Bus transport.
/// </summary>
/// <docs>messaging/transports/azure-service-bus</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceCollectionExtensionsTests.cs</tests>
public class AzureServiceBusOptions {
  /// <summary>
  /// If true, automatically create topics and subscriptions when subscribing.
  /// Requires IServiceBusAdminClient to be registered (auto-registered when true).
  /// Default: true (auto-provision infrastructure)
  /// </summary>
  /// <docs>messaging/transports/azure-service-bus#auto-provisioning</docs>
  public bool AutoProvisionInfrastructure { get; set; } = true;

  /// <summary>
  /// Maximum time a single SendMessageAsync may take before the transport reports a
  /// TimeoutException. Guards against the Azure Service Bus emulator's occasional
  /// first-send hang (a plain CancellationToken also hangs there). Default 30 seconds.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusErrorHandlingTests.cs</tests>
  public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How many messages can be processed at the same time by a single consumer instance.
  /// This controls throughput — higher values process more messages in parallel but use more resources.
  /// Only applies when <see cref="EnableSessions"/> is false (non-session mode).
  /// When sessions are enabled, use <see cref="MaxConcurrentSessions"/> instead.
  /// <para>
  /// <b>Example:</b> With <c>MaxConcurrentCalls = 10</c>, if 50 messages arrive at once,
  /// 10 are processed in parallel and the remaining 40 wait in the subscription until a slot opens.
  /// </para>
  /// <para>
  /// <b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="MaxDeliveryAttempts"/> — how many times a <em>single failing message</em> is retried before dead-lettering</item>
  ///   <item><see cref="MaxConcurrentSessions"/> — how many <em>sessions/streams</em> are processed in parallel (session mode only)</item>
  /// </list>
  /// </para>
  /// Default: 200. The transport consumer uses batch receive (SubscribeBatchAsync) so higher
  /// values are safe and improve throughput by filling the batch collector faster.
  /// </summary>
  /// <docs>messaging/transports/azure-service-bus#concurrency</docs>
  public int MaxConcurrentCalls { get; set; } = 200;

  /// <summary>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusTransportUnitTests.cs:PublishMaxConcurrency_DefaultsTo200Async</tests>
  /// How many stream groups are sent to Azure Service Bus in parallel during a batch publish.
  /// Azure Service Bus requires all messages in a single <c>ServiceBusMessageBatch</c> to share
  /// the same <c>SessionId</c>, so <see cref="ITransport.BulkPublishAsync"/> groups outgoing
  /// messages by <c>StreamId</c> and sends one batch per group. This option controls how many
  /// of those per-stream batches are flight-sent concurrently.
  /// <para>
  /// <b>Example:</b> With <c>PublishMaxConcurrency = 200</c> and a fan-out burst of 7,894 messages
  /// across 7,894 unique StreamIds, 200 batches are sent in parallel. At ~250 ms per batch
  /// round-trip, the burst drains in ~10 seconds (40 sequential cycles × 250 ms). A value of 8
  /// would drain the same burst in ~4 minutes (987 cycles × 250 ms).
  /// </para>
  /// <para>
  /// <b>Tuning guidance:</b>
  /// <list type="bullet">
  ///   <item><b>Fan-out workloads</b> (many unique StreamIds, one or few messages each): 200–500. Each stream is a separate batch round-trip, so parallelism directly multiplies throughput <em>up to the broker's ingest rate</em>.</item>
  ///   <item><b>Bulk imports</b> (few streams, many messages each): 50–100 is sufficient. Batches fill up within a stream, so the batch size + <see cref="MaxDeliveryAttempts"/> do most of the work.</item>
  ///   <item><b>ASB Standard tier:</b> the broker itself is typically the bottleneck before client parallelism saturates. Measured ceiling on fan-out under Standard is ~50–60 events/s sustained regardless of client MaxDOP; extra concurrency above ~16–32 produces <c>ServiceCommunicationProblem</c> / <c>SessionLockLost</c> retries rather than more throughput. A value of 200 is still safe (namespace caps connections at 1,000 across producers+consumers) but won't buy additional throughput beyond the TU limit. Consider 32–64 if retry churn is visible in your logs.</item>
  ///   <item><b>ASB Premium tier:</b> much higher throughput units and connection caps; 200–500 is where the value of this option materializes.</item>
  /// </list>
  /// </para>
  /// <para>
  /// <b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="MaxConcurrentCalls"/> — consumer parallelism in non-session mode (receive-side)</item>
  ///   <item><see cref="MaxConcurrentSessions"/> — consumer parallelism in session mode (receive-side)</item>
  /// </list>
  /// </para>
  /// <para>
  /// <b>Future work:</b> auto-tuning (adaptive sizing based on observed latency / broker
  /// backpressure, Little's Law sizing) is not yet supported. .NET has no built-in
  /// "auto-tune parallelism for I/O" API — <see cref="System.Threading.Tasks.ParallelOptions.MaxDegreeOfParallelism"/>
  /// = -1 defaults to <see cref="System.Environment.ProcessorCount"/> which is a CPU-bound
  /// heuristic unsuitable for AMQP sends. See the Whizbang plans/ directory for the design
  /// sketch of an adaptive implementation.
  /// </para>
  /// Default: 200 (matches <see cref="MaxConcurrentSessions"/> for producer/consumer parity)
  /// </summary>
  /// <docs>messaging/transports/azure-service-bus#publish-concurrency</docs>
  public int PublishMaxConcurrency { get; set; } = 200;

  /// <summary>
  /// How long the client automatically renews the lock on a message while it is being processed.
  /// If processing takes longer than this duration, the lock expires and the broker may redeliver
  /// the message to another consumer (causing a duplicate).
  /// <para>
  /// <b>Example:</b> With <c>MaxAutoLockRenewalDuration = 5 minutes</c> and a message that takes
  /// 3 minutes to process, the lock is renewed automatically and processing completes normally.
  /// But if processing takes 7 minutes, the lock expires at 5 minutes, the broker considers
  /// the message abandoned, and redelivers it — resulting in duplicate processing.
  /// </para>
  /// <para>
  /// <b>Rule of thumb:</b> Set this to at least 2x your longest expected message processing time.
  /// </para>
  /// Default: 5 minutes
  /// </summary>
  /// <docs>messaging/transports/azure-service-bus#lock-renewal</docs>
  public TimeSpan MaxAutoLockRenewalDuration { get; set; } = TimeSpan.FromMinutes(5);

  /// <summary>
  /// How many times a single failing message is redelivered before being moved to the dead-letter queue.
  /// Each time a message handler throws an exception, the broker increments the delivery count
  /// and redelivers the message. Once this limit is reached, the message is dead-lettered instead.
  /// This value is set on the Azure Service Bus subscription at creation time.
  /// <para>
  /// <b>Example:</b> With <c>MaxDeliveryAttempts = 10</c>, a message that always fails is retried
  /// 10 times total (1 initial delivery + 9 retries). On the 10th failure, it moves to the
  /// dead-letter sub-queue where it can be inspected or reprocessed manually.
  /// </para>
  /// <para>
  /// <b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="MaxConcurrentCalls"/> — how many <em>different messages</em> are processed in parallel (throughput)</item>
  ///   <item><see cref="InitialRetryAttempts"/> — how many times the <em>transport connection</em> is retried on startup (not per-message)</item>
  /// </list>
  /// </para>
  /// Default: 10
  /// </summary>
  /// <docs>messaging/transports/azure-service-bus#dead-lettering</docs>
  public int MaxDeliveryAttempts { get; set; } = 10;

  /// <summary>
  /// Default subscription name to use when none is specified in the destination routing key.
  /// Default: "default"
  /// </summary>
  public string DefaultSubscriptionName { get; set; } = "default";

  #region Session / FIFO Ordering

  /// <summary>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusTransportUnitTests.cs:EnableSessions_DefaultsToTrueAsync</tests>
  /// When true, subscriptions are created with RequiresSession = true and messages with
  /// a StreamId will have their SessionId set for FIFO ordering within a stream.
  /// Existing subscriptions without sessions are auto-migrated (delete + recreate).
  /// Default: true (FIFO ordering works out of the box)
  /// </summary>
  /// <docs>messaging/transports/azure-service-bus#sessions</docs>
  public bool EnableSessions { get; set; } = true;

  /// <summary>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusTransportUnitTests.cs:MaxConcurrentSessions_DefaultsTo200Async</tests>
  /// How many sessions (streams) can be processed at the same time by a single consumer instance.
  /// Each session maintains strict FIFO ordering internally — messages within one session are
  /// always processed one at a time, in order. This setting controls how many <em>different</em>
  /// sessions are handled in parallel. Only applies when <see cref="EnableSessions"/> is true.
  /// <para>
  /// <b>Example:</b> With <c>MaxConcurrentSessions = 200</c> and 2,000 active streams, 200 sessions
  /// are processed in parallel. Each session processes its messages one at a time in order.
  /// The remaining 1,800 sessions wait until a slot opens.
  /// </para>
  /// <para>
  /// <b>Fan-out workloads (one event per stream × many streams):</b> session processors open a
  /// connection and lock per session. When each session has only one or two messages, session-open
  /// overhead dominates. A high session cap lets many streams be served in parallel so the overhead
  /// amortizes across more throughput. 200 matches <see cref="MaxConcurrentCalls"/> for parity.
  /// </para>
  /// <para>
  /// <b>Memory:</b> each session holds up to <see cref="PrefetchCount"/> buffered messages. Default
  /// pairing is tuned for fan-out — high concurrency (200), modest prefetch (50) — bounds in-flight
  /// buffer to ~10,000 messages per consumer while keeping per-session-open overhead low.
  /// </para>
  /// <para>
  /// <b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="MaxConcurrentCalls"/> — parallel message processing in non-session mode (ignored when sessions are enabled)</item>
  ///   <item><see cref="MaxDeliveryAttempts"/> — how many times a <em>single failing message</em> is retried (per-message, not per-session)</item>
  /// </list>
  /// </para>
  /// Default: 200
  /// </summary>
  /// <docs>messaging/transports/azure-service-bus#sessions</docs>
  public int MaxConcurrentSessions { get; set; } = 200;

  /// <summary>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusTransportUnitTests.cs:SessionIdleTimeout_DefaultsToOneSecondAsync</tests>
  /// Maximum time a session processor will wait for a new message in the currently-held session
  /// before releasing the session and accepting a different one. Only applies when
  /// <see cref="EnableSessions"/> is true.
  /// <para>
  /// The Azure SDK default is 60 seconds. For fan-out workloads (one event per stream × many
  /// streams) this produces a 60-second plateau after every batch of concurrent sessions: each
  /// session receives its single message, processes it, then <em>holds the concurrency slot idle
  /// for the full 60 s</em> waiting for a second message that will never arrive. No new sessions
  /// can be accepted until the wait expires.
  /// </para>
  /// <para>
  /// Defaulted to 1 second: fan-out sessions release almost immediately; bulk-import sessions
  /// (where a single stream may receive many messages in a short burst) still hold the session
  /// long enough that the next message keeps the session alive. Tune higher (5–30 s) only if
  /// your workload has sustained multi-message bursts within a single stream separated by gaps
  /// of a few seconds.
  /// </para>
  /// Default: 1 second
  /// </summary>
  /// <docs>messaging/transports/azure-service-bus#session-idle-timeout</docs>
  public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromSeconds(1);

  /// <summary>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusTransportUnitTests.cs:PrefetchCount_DefaultsTo50Async</tests>
  /// Number of messages the client buffers locally ahead of processing, <b>per session receiver</b>
  /// (session mode) or per processor (non-session mode). Without prefetch (count = 0), every message
  /// requires a synchronous AMQP round-trip — typically the dominant bottleneck at scale.
  /// <para>
  /// <b>Tuning for fan-out (one event per stream × many streams):</b> most sessions carry only one
  /// or two messages. Prefetch beyond 10 adds no throughput but multiplies in-flight memory:
  /// <c>MaxConcurrentSessions × PrefetchCount</c> is the max buffered message count. Prefer high
  /// session concurrency + modest prefetch. Default pairing: 200 × 50 = 10,000 buffered msgs max.
  /// </para>
  /// <para>
  /// <b>Tuning for bulk import (many events per stream):</b> raise to match typical per-stream burst
  /// size (100–200). With few concurrent streams the total buffer stays bounded.
  /// </para>
  /// <para>
  /// <b>Crash-recovery trade-off:</b> prefetched-but-unprocessed messages wait for session-lock
  /// expiry before redelivery if the consumer dies. Keep bounded.
  /// </para>
  /// Default: 50
  /// </summary>
  /// <docs>messaging/transports/azure-service-bus#prefetch</docs>
  public int PrefetchCount { get; set; } = 50;

  #endregion

  #region Connection Retry Options

  /// <summary>
  /// Number of initial retry attempts before switching to indefinite retry mode.
  /// During initial retries, each failure is logged as a warning.
  /// After initial retries, the system continues retrying indefinitely but logs less frequently.
  /// Set to 0 to skip initial warning phase and go directly to indefinite retry.
  /// Default: 5
  /// </summary>
  /// <docs>messaging/transports/azure-service-bus#connection-retry</docs>
  public int InitialRetryAttempts { get; set; } = 5;

  /// <summary>
  /// Initial delay before the first retry attempt.
  /// Default: 1 second
  /// </summary>
  /// <docs>messaging/transports/azure-service-bus#connection-retry</docs>
  public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

  /// <summary>
  /// Maximum delay between retry attempts (caps the exponential backoff).
  /// Once this delay is reached, retries continue at this interval indefinitely.
  /// Default: 120 seconds
  /// </summary>
  /// <docs>messaging/transports/azure-service-bus#connection-retry</docs>
  public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(120);

  /// <summary>
  /// Multiplier for exponential backoff between retries.
  /// Each retry delay = previous delay * multiplier (capped at MaxRetryDelay).
  /// Default: 2.0
  /// </summary>
  /// <docs>messaging/transports/azure-service-bus#connection-retry</docs>
  public double BackoffMultiplier { get; set; } = 2.0;

  /// <summary>
  /// If true, retry indefinitely until connection succeeds or cancellation is requested.
  /// If false, throw after InitialRetryAttempts.
  /// Default: true (critical transport - always retry)
  /// </summary>
  /// <docs>messaging/transports/azure-service-bus#connection-retry</docs>
  public bool RetryIndefinitely { get; set; } = true;

  #endregion
}
