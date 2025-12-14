using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

namespace Whizbang.Hosting.Azure.ServiceBus;

/// <summary>
/// <tests>tests/Whizbang.Hosting.Azure.ServiceBus.Tests/ServiceBusSubscriptionExtensionsTests.cs:WithDestinationFilter_AddsCorrelationFilterRule_WithDestinationPropertyAsync</tests>
/// Extension methods for configuring Azure Service Bus subscriptions in Aspire.
/// </summary>
public static class ServiceBusSubscriptionExtensions {
  /// <summary>
  /// Adds a CorrelationFilter rule to the subscription that filters messages by the Destination application property.
  /// This enables inbox pattern routing where multiple services subscribe to the same topic with different destination filters.
  /// </summary>
  /// <param name="subscription">The subscription resource builder</param>
  /// <param name="destination">The destination value to filter on (e.g., "inventory-service")</param>
  /// <returns>The subscription resource builder for chaining</returns>
  /// <remarks>
  /// Aspire generates Bicep that provisions the subscription with a CorrelationFilter rule.
  /// In the emulator, Aspire sets up the filter at startup.
  /// The Destination property must be set in message ApplicationProperties when publishing.
  /// </remarks>
  public static IResourceBuilder<AzureServiceBusSubscriptionResource> WithDestinationFilter(
    this IResourceBuilder<AzureServiceBusSubscriptionResource> subscription,
    string destination) {

    ArgumentNullException.ThrowIfNull(subscription);
    ArgumentException.ThrowIfNullOrWhiteSpace(destination);

    return subscription.WithProperties(sub => {
      sub.Rules.Add(new AzureServiceBusRule("DestinationFilter") {
        CorrelationFilter = new() {
          Properties = { ["Destination"] = destination }
        }
      });
    });
  }
}
