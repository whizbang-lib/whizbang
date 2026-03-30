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
  /// Default: 10
  /// </summary>
  /// <docs>messaging/transports/azure-service-bus#concurrency</docs>
  public int MaxConcurrentCalls { get; set; } = 10;

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
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusTransportUnitTests.cs:MaxConcurrentSessions_DefaultsTo64Async</tests>
  /// How many sessions (streams) can be processed at the same time by a single consumer instance.
  /// Each session maintains strict FIFO ordering internally — messages within one session are
  /// always processed one at a time, in order. This setting controls how many <em>different</em>
  /// sessions are handled in parallel. Only applies when <see cref="EnableSessions"/> is true.
  /// <para>
  /// <b>Example:</b> With <c>MaxConcurrentSessions = 64</c> and 200 active streams, 64 sessions
  /// are processed in parallel. Each session processes its messages one at a time in order.
  /// The remaining 136 sessions wait until a slot opens.
  /// </para>
  /// <para>
  /// <b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="MaxConcurrentCalls"/> — parallel message processing in non-session mode (ignored when sessions are enabled)</item>
  ///   <item><see cref="MaxDeliveryAttempts"/> — how many times a <em>single failing message</em> is retried (per-message, not per-session)</item>
  /// </list>
  /// </para>
  /// Default: 64
  /// </summary>
  /// <docs>messaging/transports/azure-service-bus#sessions</docs>
  public int MaxConcurrentSessions { get; set; } = 64;

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
