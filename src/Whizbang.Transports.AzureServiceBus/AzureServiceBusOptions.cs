using System.Text.Json.Serialization;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Configuration options for Azure Service Bus transport.
/// </summary>
public class AzureServiceBusOptions {
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
}
