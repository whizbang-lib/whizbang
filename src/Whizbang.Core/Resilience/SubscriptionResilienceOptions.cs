// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Resilience;

/// <summary>
/// Configuration options for subscription resilience in <see cref="Workers.TransportConsumerWorker"/>.
/// </summary>
/// <remarks>
/// <para>
/// These options control how the transport consumer worker retries subscription setup
/// when the transport (exchange, topic, queue) is not yet available. The default values
/// match <c>RabbitMQOptions</c> for consistency across connection and subscription retry.
/// </para>
/// <para>
/// <b>Key principle:</b> Subscriptions are critical infrastructure. By default,
/// the system retries forever (<see cref="RetryIndefinitely"/> = true) until success
/// or cancellation. There is no <c>MaxRetryAttempts</c> - only a <see cref="MaxRetryDelay"/>
/// that caps the exponential backoff.
/// </para>
/// </remarks>
/// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
/// <tests>tests/Whizbang.Core.Tests/Resilience/SubscriptionResilienceOptionsTests.cs</tests>
public class SubscriptionResilienceOptions {
  /// <summary>
  /// Number of initial retry attempts before switching to indefinite retry mode.
  /// During initial retries, each failure is logged as a warning.
  /// After initial retries, the system continues retrying but logs less frequently.
  /// Set to 0 to skip initial warning phase and go directly to indefinite retry.
  /// </summary>
  /// <value>Default: 5 (matches RabbitMQOptions)</value>
  /// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
  public int InitialRetryAttempts { get; set; } = 5;

  /// <summary>
  /// Initial delay before the first retry attempt.
  /// </summary>
  /// <value>Default: 1 second (matches RabbitMQOptions)</value>
  /// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
  public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

  /// <summary>
  /// Maximum delay between retry attempts (caps the exponential backoff).
  /// Once this delay is reached, retries continue at this interval indefinitely.
  /// </summary>
  /// <value>Default: 120 seconds (matches RabbitMQOptions)</value>
  /// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
  public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(120);

  /// <summary>
  /// Multiplier for exponential backoff between retries.
  /// Each retry delay = previous delay * multiplier (capped at <see cref="MaxRetryDelay"/>).
  /// Set to 1.0 to disable exponential backoff (constant delay).
  /// </summary>
  /// <value>Default: 2.0 (matches RabbitMQOptions)</value>
  /// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
  public double BackoffMultiplier { get; set; } = 2.0;

  /// <summary>
  /// How long a subscription must stay healthy before its next disconnect is treated as a fresh one.
  /// </summary>
  /// <remarks>
  /// <para>
  /// When a subscription that was established successfully disconnects, the reconnect waits before subscribing
  /// again. The first reconnect waits <see cref="InitialRetryDelay"/>. When the subscription it established
  /// disconnects again before staying healthy for this long, the next reconnect waits the previous reconnect's delay
  /// times <see cref="BackoffMultiplier"/>, capped at <see cref="MaxRetryDelay"/>, so a destination that keeps failing
  /// right after each successful subscribe (an entity that stays disabled, an identity that stays unauthorized, a
  /// channel that closes again as soon as it opens) backs off instead of reconnecting every
  /// <see cref="InitialRetryDelay"/> forever. A subscription that stays healthy for this long has recovered: its next
  /// disconnect waits <see cref="InitialRetryDelay"/> again.
  /// </para>
  /// <para>
  /// <see cref="TimeSpan.Zero"/> treats every disconnect as a fresh one, so every reconnect waits
  /// <see cref="InitialRetryDelay"/>.
  /// </para>
  /// </remarks>
  /// <value>Default: 30 seconds</value>
  /// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
  /// <tests>tests/Whizbang.Core.Tests/Resilience/SubscriptionResilienceOptionsTests.cs:ReconnectBackoffResetAfter_Default_IsThirtySecondsAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Resilience/SubscriptionRetryHelperReconnectBackoffTests.cs</tests>
  public TimeSpan ReconnectBackoffResetAfter { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// If true, retry indefinitely until subscription succeeds or cancellation is requested.
  /// If false, mark subscription as failed after <see cref="InitialRetryAttempts"/>.
  /// </summary>
  /// <value>Default: true (critical infrastructure - always retry)</value>
  /// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
  public bool RetryIndefinitely { get; set; } = true;

  /// <summary>
  /// Interval between health check sweeps that attempt to recover failed subscriptions.
  /// This background task periodically checks for subscriptions in Failed state and
  /// attempts to re-establish them.
  /// </summary>
  /// <value>Default: 1 minute</value>
  /// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
  /// <tests>tests/Whizbang.Core.Tests/Resilience/SubscriptionResilienceOptionsTests.cs:HealthCheckInterval_Default_IsOneMinuteAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Resilience/SubscriptionResilienceOptionsTests.cs:HealthCheckInterval_SetValue_ReturnsSetValueAsync</tests>
  public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromMinutes(1);

  /// <summary>
  /// Allow worker to start even if some subscriptions fail.
  /// If true, the worker continues with partial subscriptions and the health monitor
  /// will attempt to recover failed subscriptions in the background.
  /// If false, the worker will not start message processing until all subscriptions succeed.
  /// </summary>
  /// <value>Default: true (continue with partial subscriptions)</value>
  /// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
  /// <tests>tests/Whizbang.Core.Tests/Resilience/SubscriptionResilienceOptionsTests.cs:AllowPartialSubscriptions_Default_IsTrueAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Resilience/SubscriptionResilienceOptionsTests.cs:AllowPartialSubscriptions_SetFalse_ReturnsFalseAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/TransportConsumerWorkerResilienceTests.cs:Worker_WithPartialFailures_ContinuesWithSuccessfulSubscriptionsAsync</tests>
  public bool AllowPartialSubscriptions { get; set; } = true;
}
