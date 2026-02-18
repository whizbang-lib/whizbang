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
/// <docs>core-concepts/transport-consumer#subscription-resilience</docs>
/// <tests>tests/Whizbang.Core.Tests/Resilience/SubscriptionResilienceOptionsTests.cs</tests>
public class SubscriptionResilienceOptions {
  /// <summary>
  /// Number of initial retry attempts before switching to indefinite retry mode.
  /// During initial retries, each failure is logged as a warning.
  /// After initial retries, the system continues retrying but logs less frequently.
  /// Set to 0 to skip initial warning phase and go directly to indefinite retry.
  /// </summary>
  /// <value>Default: 5 (matches RabbitMQOptions)</value>
  /// <docs>core-concepts/transport-consumer#subscription-resilience</docs>
  public int InitialRetryAttempts { get; set; } = 5;

  /// <summary>
  /// Initial delay before the first retry attempt.
  /// </summary>
  /// <value>Default: 1 second (matches RabbitMQOptions)</value>
  /// <docs>core-concepts/transport-consumer#subscription-resilience</docs>
  public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

  /// <summary>
  /// Maximum delay between retry attempts (caps the exponential backoff).
  /// Once this delay is reached, retries continue at this interval indefinitely.
  /// </summary>
  /// <value>Default: 120 seconds (matches RabbitMQOptions)</value>
  /// <docs>core-concepts/transport-consumer#subscription-resilience</docs>
  public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(120);

  /// <summary>
  /// Multiplier for exponential backoff between retries.
  /// Each retry delay = previous delay * multiplier (capped at <see cref="MaxRetryDelay"/>).
  /// Set to 1.0 to disable exponential backoff (constant delay).
  /// </summary>
  /// <value>Default: 2.0 (matches RabbitMQOptions)</value>
  /// <docs>core-concepts/transport-consumer#subscription-resilience</docs>
  public double BackoffMultiplier { get; set; } = 2.0;

  /// <summary>
  /// If true, retry indefinitely until subscription succeeds or cancellation is requested.
  /// If false, mark subscription as failed after <see cref="InitialRetryAttempts"/>.
  /// </summary>
  /// <value>Default: true (critical infrastructure - always retry)</value>
  /// <docs>core-concepts/transport-consumer#subscription-resilience</docs>
  public bool RetryIndefinitely { get; set; } = true;

  /// <summary>
  /// Interval between health check sweeps that attempt to recover failed subscriptions.
  /// This background task periodically checks for subscriptions in Failed state and
  /// attempts to re-establish them.
  /// </summary>
  /// <value>Default: 1 minute</value>
  /// <docs>core-concepts/transport-consumer#subscription-resilience</docs>
  public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromMinutes(1);

  /// <summary>
  /// Allow worker to start even if some subscriptions fail.
  /// If true, the worker continues with partial subscriptions and the health monitor
  /// will attempt to recover failed subscriptions in the background.
  /// If false, the worker will not start message processing until all subscriptions succeed.
  /// </summary>
  /// <value>Default: true (continue with partial subscriptions)</value>
  /// <docs>core-concepts/transport-consumer#subscription-resilience</docs>
  public bool AllowPartialSubscriptions { get; set; } = true;
}
