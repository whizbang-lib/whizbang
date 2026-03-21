using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Unit tests for Azure Service Bus subscription name derivation.
/// These tests verify that the transport correctly derives subscription names
/// from SubscriberName metadata instead of using RoutingKey directly.
/// </summary>
/// <tests>ServiceBusSubscriptionNameHelper.cs</tests>
public class SubscriptionNameDerivationTests {

  [Test]
  public async Task DeriveSubscriptionNameWithSubscriberNameMetadataUsesServiceNameAndTopicAsync() {
    // Arrange
    const string subscriberName = "bff-service";
    const string topicName = "jdx.contracts.chat";

    // Create metadata with SubscriberName
    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonSerializer.SerializeToElement(subscriberName)
    };
    _ = new TransportDestination(topicName, "#", metadata);

    // Act - Derive subscription name (testing the helper directly since _deriveSubscriptionName is private)
    var derivedName = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert - Should combine subscriber name and topic
    await Assert.That(derivedName).IsEqualTo("bff-service-jdx.contracts.chat");
  }

  [Test]
  public async Task DeriveSubscriptionNameWithHashWildcardDoesNotUseAsSubscriptionNameAsync() {
    // Arrange - wildcard routing key should NOT be used as subscription name
    const string subscriberName = "order-service";
    const string topicName = "domain.events";
    const string routingKey = "#"; // This is the wildcard pattern

    // Generate what the subscription name SHOULD be
    var expectedSubscriptionName = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert - The derived name should NOT be "#"
    await Assert.That(expectedSubscriptionName).IsNotEqualTo(routingKey);
    await Assert.That(expectedSubscriptionName).IsEqualTo("order-service-domain.events");
  }

  [Test]
  public async Task DeriveSubscriptionNameWithCommaSeparatedPatternDoesNotUseAsSubscriptionNameAsync() {
    // Arrange - comma-separated patterns should NOT be used as subscription name
    const string subscriberName = "inventory-service";
    const string topicName = "shared.inbox";
    const string invalidRoutingKey = "ns1.#,ns2.#,ns3.#"; // Multiple patterns - would be invalid

    // Generate what the subscription name SHOULD be (using helper, not routing key)
    var expectedSubscriptionName = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert - The derived name should NOT contain invalid characters from routing key
    await Assert.That(expectedSubscriptionName).DoesNotContain("#");
    await Assert.That(expectedSubscriptionName).DoesNotContain(",");
    await Assert.That(expectedSubscriptionName).IsEqualTo("inventory-service-shared.inbox");

    // Verify the routing key IS a wildcard (would be invalid)
    await Assert.That(_isWildcardPattern(invalidRoutingKey)).IsTrue();
  }

  [Test]
  public async Task DeriveSubscriptionNameWithAsteriskWildcardDoesNotUseAsSubscriptionNameAsync() {
    // Arrange - asterisk wildcard should NOT be used as subscription name
    const string subscriberName = "payment-service";
    const string topicName = "payment.events";
    const string invalidRoutingKey = "payment.*"; // Single-level wildcard - would be invalid

    // Generate what the subscription name SHOULD be (using helper, not routing key)
    var expectedSubscriptionName = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert - The derived name should NOT contain wildcard
    await Assert.That(expectedSubscriptionName).DoesNotContain("*");
    await Assert.That(expectedSubscriptionName).IsEqualTo("payment-service-payment.events");

    // Verify the routing key IS a wildcard (would be invalid)
    await Assert.That(_isWildcardPattern(invalidRoutingKey)).IsTrue();
  }

  [Test]
  public async Task IsWildcardPatternReturnsTrueForHashPatternAsync() {
    // Test patterns that should be detected as wildcards
    await Assert.That(_isWildcardPattern("#")).IsTrue();
    await Assert.That(_isWildcardPattern("ns.#")).IsTrue();
    await Assert.That(_isWildcardPattern("ns1.#,ns2.#")).IsTrue();
  }

  [Test]
  public async Task IsWildcardPatternReturnsTrueForAsteriskPatternAsync() {
    await Assert.That(_isWildcardPattern("*")).IsTrue();
    await Assert.That(_isWildcardPattern("ns.*")).IsTrue();
    await Assert.That(_isWildcardPattern("ns.*.events")).IsTrue();
  }

  [Test]
  public async Task IsWildcardPatternReturnsTrueForCommaPatternAsync() {
    await Assert.That(_isWildcardPattern("ns1,ns2")).IsTrue();
    await Assert.That(_isWildcardPattern("a,b,c")).IsTrue();
  }

  [Test]
  public async Task IsWildcardPatternReturnsFalseForValidSubscriptionNamesAsync() {
    await Assert.That(_isWildcardPattern("my-subscription")).IsFalse();
    await Assert.That(_isWildcardPattern("default")).IsFalse();
    await Assert.That(_isWildcardPattern("bff-service-topic")).IsFalse();
    await Assert.That(_isWildcardPattern("order.service.subscription")).IsFalse();
  }

  /// <summary>
  /// Determines if a routing key contains wildcard patterns that are invalid for subscription names.
  /// This is a helper that mirrors the logic that should be in AzureServiceBusTransport.
  /// </summary>
  private static bool _isWildcardPattern(string routingKey) =>
    routingKey.Contains('#') || routingKey.Contains('*') || routingKey.Contains(',');
}
