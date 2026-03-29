using System;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Transports;

/// <summary>
/// Abstract base class for transport configuration options.
/// Each concrete transport (RabbitMQ, Azure Service Bus, etc.) derives from this class
/// to inherit shared settings while adding transport-specific configuration.
///
/// Settings are validated at startup against the transport's declared capabilities.
/// When a setting is configured but the transport lacks the required capability,
/// a warning is logged and the setting is effectively ignored by the transport.
/// </summary>
/// <docs>messaging/transports/transport-options</docs>
public abstract class TransportOptions {
  // ──────────────────────────────────────────────
  // Message Processing
  // ──────────────────────────────────────────────

  /// <summary>
  /// The maximum number of messages that can be processed concurrently by a single consumer.
  ///
  /// <para><b>Example:</b> With <c>ConcurrentMessageLimit = 10</c>, up to 10 messages are dispatched
  /// to handlers in parallel. The 11th message waits until one of the 10 in-flight messages completes.</para>
  ///
  /// <para><b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="MessagePrefetchCount"/> — controls how many messages are buffered locally
  ///   from the broker, not how many are actively being processed.</item>
  ///   <item><see cref="ConcurrentOrderedStreams"/> — controls parallelism across ordered streams,
  ///   while this setting controls parallelism for unordered or within-stream processing.</item>
  /// </list>
  /// </para>
  ///
  /// <para><b>Capability gate:</b> <see cref="TransportCapabilities.PublishSubscribe"/>.</para>
  /// <para><b>Default:</b> 10</para>
  /// </summary>
  public int ConcurrentMessageLimit { get; set; } = 10;

  /// <summary>
  /// The number of messages to pre-fetch from the broker into a local buffer ahead of processing.
  /// Pre-fetching reduces latency by having messages ready before the consumer asks for them.
  ///
  /// <para>This setting uses a sliding window: as messages are consumed from the buffer, new messages
  /// are fetched to keep the buffer at the configured size. It does <b>not</b> wait for the full
  /// prefetch amount before delivering messages to the consumer.</para>
  ///
  /// <para><b>Example:</b> With <c>MessagePrefetchCount = 20</c>, the transport maintains up to
  /// 20 messages in a local buffer. When 5 are consumed, the transport immediately requests 5 more
  /// from the broker — it never blocks waiting for all 20 to arrive.</para>
  ///
  /// <para><b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="ConcurrentMessageLimit"/> — controls how many messages are actively processed
  ///   in parallel, not how many are buffered locally.</item>
  /// </list>
  /// </para>
  ///
  /// <para><b>Capability gate:</b> <see cref="TransportCapabilities.PublishSubscribe"/>.
  /// Set to 0 to disable prefetching.</para>
  /// <para><b>Default:</b> 0 (disabled)</para>
  /// </summary>
  public int MessagePrefetchCount { get; set; }

  // ──────────────────────────────────────────────
  // Reliability & Dead-Lettering
  // ──────────────────────────────────────────────

  /// <summary>
  /// The maximum number of times a failing message is redelivered before being moved to a dead-letter
  /// queue. This count includes the initial delivery attempt plus subsequent retries.
  ///
  /// <para><b>Example:</b> With <c>FailedMessageRetryLimit = 10</c>, a message is delivered once initially.
  /// If it fails, it is retried up to 9 more times (for a total of 10 attempts). After the 10th failure,
  /// it is dead-lettered.</para>
  ///
  /// <para><b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="InitialConnectionRetryAttempts"/> — controls how many times the transport
  ///   retries the <em>initial connection</em> to the broker, not individual message delivery.</item>
  ///   <item><see cref="RetryConnectionIndefinitely"/> — controls connection-level retry behavior,
  ///   not message-level retry behavior.</item>
  /// </list>
  /// </para>
  ///
  /// <para><b>Capability gate:</b> <see cref="TransportCapabilities.Reliable"/>. If the transport
  /// does not support reliable delivery, this setting is ignored.</para>
  /// <para><b>Default:</b> 10</para>
  /// </summary>
  public int FailedMessageRetryLimit { get; set; } = 10;

  /// <summary>
  /// Whether the transport should automatically create dead-letter queues, exchanges, or
  /// subscriptions required for dead-lettering failed messages.
  ///
  /// <para><b>Example:</b> With <c>AutoProvisionDeadLetterInfrastructure = true</c> and a queue
  /// named <c>orders</c>, the transport automatically creates a dead-letter queue named
  /// <c>orders-dead-letter</c> (or equivalent) when the subscription is first established.</para>
  ///
  /// <para><b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="AutoProvisionInfrastructure"/> — controls auto-creation of primary topics,
  ///   subscriptions, and queues, not dead-letter infrastructure specifically.</item>
  /// </list>
  /// </para>
  ///
  /// <para><b>Capability gate:</b> <see cref="TransportCapabilities.Reliable"/>. If the transport
  /// does not support reliable delivery, dead-lettering is not available and this setting is ignored.</para>
  /// <para><b>Default:</b> true</para>
  /// </summary>
  public bool AutoProvisionDeadLetterInfrastructure { get; set; } = true;

  // ──────────────────────────────────────────────
  // Ordering
  // ──────────────────────────────────────────────

  /// <summary>
  /// Whether the transport should enforce FIFO (first-in, first-out) ordering within a
  /// stream or partition. When enabled, messages sharing the same stream/partition key
  /// are delivered strictly in order.
  ///
  /// <para><b>Example:</b> With <c>EnableOrderedDelivery = true</c> and messages published with
  /// partition key <c>"order-123"</c>, the consumer receives those messages in the exact order
  /// they were published. Messages with different partition keys may still be processed in parallel.</para>
  ///
  /// <para><b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="ConcurrentOrderedStreams"/> — controls how many independent ordered streams
  ///   are processed in parallel, not whether ordering is enabled.</item>
  ///   <item><see cref="ConcurrentMessageLimit"/> — controls general parallelism. When ordered delivery
  ///   is enabled, parallelism is constrained to one message at a time per stream.</item>
  /// </list>
  /// </para>
  ///
  /// <para><b>Capability gate:</b> <see cref="TransportCapabilities.Ordered"/>. If the transport
  /// does not support ordered delivery, this setting is ignored.</para>
  /// <para><b>Default:</b> true</para>
  /// </summary>
  public bool EnableOrderedDelivery { get; set; } = true;

  /// <summary>
  /// The maximum number of ordered streams (partitions) that can be processed concurrently
  /// when <see cref="EnableOrderedDelivery"/> is enabled. Each stream maintains strict FIFO
  /// ordering internally, but multiple streams are processed in parallel.
  ///
  /// <para><b>Example:</b> With <c>ConcurrentOrderedStreams = 64</c> and 200 active partition keys,
  /// up to 64 partitions have their next message processed concurrently. The remaining 136 partitions
  /// wait until a processing slot becomes available.</para>
  ///
  /// <para><b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="ConcurrentMessageLimit"/> — controls general message parallelism regardless
  ///   of ordering semantics.</item>
  ///   <item><see cref="EnableOrderedDelivery"/> — must be <c>true</c> for this setting to take effect.</item>
  /// </list>
  /// </para>
  ///
  /// <para><b>Capability gate:</b> <see cref="TransportCapabilities.Ordered"/>. If the transport
  /// does not support ordered delivery, this setting is ignored.</para>
  /// <para><b>Default:</b> 64</para>
  /// </summary>
  public int ConcurrentOrderedStreams { get; set; } = 64;

  // ──────────────────────────────────────────────
  // Infrastructure
  // ──────────────────────────────────────────────

  /// <summary>
  /// Whether the transport should automatically create topics, subscriptions, queues, and
  /// other infrastructure required for message delivery. When disabled, infrastructure must
  /// be pre-provisioned before the transport starts.
  ///
  /// <para><b>Example:</b> With <c>AutoProvisionInfrastructure = true</c>, publishing to a topic
  /// named <c>order-events</c> automatically creates that topic (and any required subscriptions)
  /// if they do not already exist.</para>
  ///
  /// <para><b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="AutoProvisionDeadLetterInfrastructure"/> — controls auto-creation of
  ///   dead-letter queues specifically, not primary messaging infrastructure.</item>
  /// </list>
  /// </para>
  ///
  /// <para><b>Capability gate:</b> None. Applies to all transports that support provisioning.</para>
  /// <para><b>Default:</b> true</para>
  /// </summary>
  public bool AutoProvisionInfrastructure { get; set; } = true;

  // ──────────────────────────────────────────────
  // Connection Retry
  // ──────────────────────────────────────────────

  /// <summary>
  /// The number of times to retry the initial connection to the message broker before giving up.
  /// This only applies during startup; once connected, reconnection behavior is controlled by
  /// <see cref="RetryConnectionIndefinitely"/>.
  ///
  /// <para><b>Example:</b> With <c>InitialConnectionRetryAttempts = 5</c>, the transport attempts
  /// to connect up to 5 times at startup. If all 5 attempts fail and
  /// <see cref="RetryConnectionIndefinitely"/> is <c>false</c>, startup fails with an exception.</para>
  ///
  /// <para><b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="FailedMessageRetryLimit"/> — controls per-message retry attempts,
  ///   not connection-level retries.</item>
  ///   <item><see cref="RetryConnectionIndefinitely"/> — when <c>true</c>, overrides this limit
  ///   and retries forever after the initial attempts are exhausted.</item>
  /// </list>
  /// </para>
  ///
  /// <para><b>Capability gate:</b> None. Applies to all transports.</para>
  /// <para><b>Default:</b> 5</para>
  /// </summary>
  public int InitialConnectionRetryAttempts { get; set; } = 5;

  /// <summary>
  /// The delay before the first connection retry attempt. Subsequent retries use exponential
  /// backoff controlled by <see cref="ConnectionRetryBackoffMultiplier"/>, up to
  /// <see cref="MaxConnectionRetryDelay"/>.
  ///
  /// <para><b>Example:</b> With <c>InitialConnectionRetryDelay = TimeSpan.FromSeconds(1)</c> and
  /// <c>ConnectionRetryBackoffMultiplier = 2.0</c>, retry delays are: 1s, 2s, 4s, 8s, ...</para>
  ///
  /// <para><b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="MaxConnectionRetryDelay"/> — the ceiling that caps exponential backoff growth.</item>
  /// </list>
  /// </para>
  ///
  /// <para><b>Capability gate:</b> None. Applies to all transports.</para>
  /// <para><b>Default:</b> 1 second</para>
  /// </summary>
  public TimeSpan InitialConnectionRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

  /// <summary>
  /// The maximum delay between connection retry attempts. Exponential backoff grows from
  /// <see cref="InitialConnectionRetryDelay"/> but is capped at this value.
  ///
  /// <para><b>Example:</b> With <c>MaxConnectionRetryDelay = TimeSpan.FromSeconds(120)</c>,
  /// <c>InitialConnectionRetryDelay = TimeSpan.FromSeconds(1)</c>, and
  /// <c>ConnectionRetryBackoffMultiplier = 2.0</c>, the delays are: 1s, 2s, 4s, 8s, 16s, 32s,
  /// 64s, 120s, 120s, 120s, ... — never exceeding 120 seconds.</para>
  ///
  /// <para><b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="InitialConnectionRetryDelay"/> — the starting delay before backoff applies.</item>
  /// </list>
  /// </para>
  ///
  /// <para><b>Capability gate:</b> None. Applies to all transports.</para>
  /// <para><b>Default:</b> 120 seconds</para>
  /// </summary>
  public TimeSpan MaxConnectionRetryDelay { get; set; } = TimeSpan.FromSeconds(120);

  /// <summary>
  /// The multiplier applied to the previous retry delay to calculate the next delay (exponential backoff).
  /// Each successive retry delay is computed as <c>previousDelay * ConnectionRetryBackoffMultiplier</c>,
  /// capped at <see cref="MaxConnectionRetryDelay"/>.
  ///
  /// <para><b>Example:</b> With <c>ConnectionRetryBackoffMultiplier = 2.0</c> and
  /// <c>InitialConnectionRetryDelay = TimeSpan.FromSeconds(1)</c>, the delays are:
  /// 1s, 2s, 4s, 8s, 16s, ...</para>
  ///
  /// <para><b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="InitialConnectionRetryDelay"/> — the base delay that this multiplier scales.</item>
  ///   <item><see cref="MaxConnectionRetryDelay"/> — the ceiling that prevents unbounded growth.</item>
  /// </list>
  /// </para>
  ///
  /// <para><b>Capability gate:</b> None. Applies to all transports.</para>
  /// <para><b>Default:</b> 2.0</para>
  /// </summary>
  public double ConnectionRetryBackoffMultiplier { get; set; } = 2.0;

  /// <summary>
  /// Whether the transport should retry the connection indefinitely after the initial
  /// <see cref="InitialConnectionRetryAttempts"/> are exhausted. When <c>true</c>, the transport
  /// never gives up on reconnecting — useful for long-running services that must survive
  /// transient broker outages.
  ///
  /// <para><b>Example:</b> With <c>RetryConnectionIndefinitely = true</c> and
  /// <c>InitialConnectionRetryAttempts = 5</c>, the transport tries 5 times quickly at startup.
  /// If those fail, it continues retrying with exponential backoff forever, logging warnings
  /// on each failure.</para>
  ///
  /// <para><b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="InitialConnectionRetryAttempts"/> — controls the burst of retries at startup
  ///   before this indefinite retry behavior takes over.</item>
  ///   <item><see cref="FailedMessageRetryLimit"/> — controls per-message retries, not connection retries.</item>
  /// </list>
  /// </para>
  ///
  /// <para><b>Capability gate:</b> None. Applies to all transports.</para>
  /// <para><b>Default:</b> true</para>
  /// </summary>
  public bool RetryConnectionIndefinitely { get; set; } = true;

  // ──────────────────────────────────────────────
  // Validation
  // ──────────────────────────────────────────────

  /// <summary>
  /// Validates the configured options against the transport's declared capabilities and logs
  /// warnings for settings that will have no effect because the transport lacks the required capability.
  /// Only warns when settings differ from their defaults — unchanged defaults do not trigger warnings.
  /// </summary>
  /// <param name="capabilities">The capabilities reported by the transport.</param>
  /// <param name="logger">Optional logger for emitting warnings. When <c>null</c>, validation is silent.</param>
  public virtual void ValidateForCapabilities(TransportCapabilities capabilities, ILogger? logger) {
    if (logger is null || !logger.IsEnabled(LogLevel.Warning)) {
      return;
    }

    // Message Processing — gated by PublishSubscribe capability
    if (!capabilities.HasFlag(TransportCapabilities.PublishSubscribe)) {
      if (ConcurrentMessageLimit != 10) {
        _logCapabilityWarning(logger, nameof(ConcurrentMessageLimit), ConcurrentMessageLimit, nameof(TransportCapabilities.PublishSubscribe));
      }

      if (MessagePrefetchCount != 0) {
        _logCapabilityWarning(logger, nameof(MessagePrefetchCount), MessagePrefetchCount, nameof(TransportCapabilities.PublishSubscribe));
      }
    }

    // Reliability & Dead-Lettering — gated by Reliable capability
    if (!capabilities.HasFlag(TransportCapabilities.Reliable)) {
      if (FailedMessageRetryLimit != 10) {
        _logCapabilityWarning(logger, nameof(FailedMessageRetryLimit), FailedMessageRetryLimit, nameof(TransportCapabilities.Reliable));
      }

      if (!AutoProvisionDeadLetterInfrastructure) {
        _logCapabilityWarning(logger, nameof(AutoProvisionDeadLetterInfrastructure), AutoProvisionDeadLetterInfrastructure, nameof(TransportCapabilities.Reliable));
      }
    }

    // Ordering — gated by Ordered capability
    if (!capabilities.HasFlag(TransportCapabilities.Ordered)) {
      if (EnableOrderedDelivery) {
        _logCapabilityWarning(logger, nameof(EnableOrderedDelivery), EnableOrderedDelivery, nameof(TransportCapabilities.Ordered));
      }

      if (ConcurrentOrderedStreams != 64) {
        _logCapabilityWarning(logger, nameof(ConcurrentOrderedStreams), ConcurrentOrderedStreams, nameof(TransportCapabilities.Ordered));
      }
    }
  }

  private static void _logCapabilityWarning(ILogger logger, string settingName, object settingValue, string requiredCapability) {
    if (logger.IsEnabled(LogLevel.Warning)) {
#pragma warning disable CA1848 // Use LoggerMessage delegates — this is a low-frequency startup validation path
      logger.LogWarning(
        "TransportOptions.{SettingName} is set to {SettingValue} but the transport does not support the {RequiredCapability} capability. This setting will be ignored.",
        settingName,
        settingValue,
        requiredCapability);
#pragma warning restore CA1848
    }
  }
}
