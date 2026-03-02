namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Configuration options for Azure Service Bus transport.
/// </summary>
/// <docs>components/transports/azure-service-bus</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceCollectionExtensionsTests.cs</tests>
public class AzureServiceBusOptions {
  /// <summary>
  /// If true, automatically create topics and subscriptions when subscribing.
  /// Requires IServiceBusAdminClient to be registered (auto-registered when true).
  /// Default: true (auto-provision infrastructure)
  /// </summary>
  /// <docs>components/transports/azure-service-bus#auto-provisioning</docs>
  public bool AutoProvisionInfrastructure { get; set; } = true;

  /// <summary>
  /// Maximum number of concurrent message processing calls.
  /// Default: 10
  /// </summary>
  public int MaxConcurrentCalls { get; set; } = 10;

  /// <summary>
  /// Maximum duration to automatically renew message locks.
  /// Default: 5 minutes
  /// </summary>
  public TimeSpan MaxAutoLockRenewalDuration { get; set; } = TimeSpan.FromMinutes(5);

  /// <summary>
  /// Maximum number of delivery attempts before dead-lettering a message.
  /// Default: 10
  /// </summary>
  public int MaxDeliveryAttempts { get; set; } = 10;

  /// <summary>
  /// Default subscription name to use when none is specified in the destination routing key.
  /// Default: "default"
  /// </summary>
  public string DefaultSubscriptionName { get; set; } = "default";

  #region Connection Retry Options

  /// <summary>
  /// Number of initial retry attempts before switching to indefinite retry mode.
  /// During initial retries, each failure is logged as a warning.
  /// After initial retries, the system continues retrying indefinitely but logs less frequently.
  /// Set to 0 to skip initial warning phase and go directly to indefinite retry.
  /// Default: 5
  /// </summary>
  /// <docs>components/transports/azure-service-bus#connection-retry</docs>
  public int InitialRetryAttempts { get; set; } = 5;

  /// <summary>
  /// Initial delay before the first retry attempt.
  /// Default: 1 second
  /// </summary>
  /// <docs>components/transports/azure-service-bus#connection-retry</docs>
  public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

  /// <summary>
  /// Maximum delay between retry attempts (caps the exponential backoff).
  /// Once this delay is reached, retries continue at this interval indefinitely.
  /// Default: 120 seconds
  /// </summary>
  /// <docs>components/transports/azure-service-bus#connection-retry</docs>
  public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(120);

  /// <summary>
  /// Multiplier for exponential backoff between retries.
  /// Each retry delay = previous delay * multiplier (capped at MaxRetryDelay).
  /// Default: 2.0
  /// </summary>
  /// <docs>components/transports/azure-service-bus#connection-retry</docs>
  public double BackoffMultiplier { get; set; } = 2.0;

  /// <summary>
  /// If true, retry indefinitely until connection succeeds or cancellation is requested.
  /// If false, throw after InitialRetryAttempts.
  /// Default: true (critical transport - always retry)
  /// </summary>
  /// <docs>components/transports/azure-service-bus#connection-retry</docs>
  public bool RetryIndefinitely { get; set; } = true;

  #endregion
}
