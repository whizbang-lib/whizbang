namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Tests for ServiceBusSubscriptionNameHelper - ensures valid Azure Service Bus subscription names.
/// </summary>
/// <tests>src/Whizbang.Transports.AzureServiceBus/ServiceBusSubscriptionNameHelper.cs</tests>
public class ServiceBusSubscriptionNameHelperTests {

  /// <summary>
  /// Verifies that valid subscriber and topic names produce expected format.
  /// </summary>
  /// <tests>src/Whizbang.Transports.AzureServiceBus/ServiceBusSubscriptionNameHelper.cs:GenerateSubscriptionName</tests>
  [Test]
  public async Task GenerateSubscriptionName_WithValidNames_ReturnsExpectedFormatAsync() {
    // Arrange
    var subscriberName = "bff-service";
    var topicName = "jdx.contracts.chat";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert
    await Assert.That(result).IsEqualTo("bff-service-jdx.contracts.chat");
  }

  /// <summary>
  /// Verifies that wildcard characters (#) are sanitized to valid characters.
  /// </summary>
  /// <tests>src/Whizbang.Transports.AzureServiceBus/ServiceBusSubscriptionNameHelper.cs:GenerateSubscriptionName</tests>
  [Test]
  public async Task GenerateSubscriptionName_WithWildcard_SanitizesCorrectlyAsync() {
    // Arrange - topic name contains # wildcard (invalid for ASB)
    var subscriberName = "inventory";
    var topicName = "myapp.events#test";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert - # should be replaced with hyphen
    await Assert.That(result).IsEqualTo("inventory-myapp.events-test");
  }

  /// <summary>
  /// Verifies that names exceeding 50 characters are truncated.
  /// </summary>
  /// <tests>src/Whizbang.Transports.AzureServiceBus/ServiceBusSubscriptionNameHelper.cs:GenerateSubscriptionName</tests>
  [Test]
  public async Task GenerateSubscriptionName_ExceedsMaxLength_TruncatesTo50CharsAsync() {
    // Arrange - create names that exceed 50 chars when combined
    var subscriberName = "very-long-subscriber-name";
    var topicName = "equally.long.topic.namespace.events";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert
    await Assert.That(result.Length).IsLessThanOrEqualTo(50);
  }

  /// <summary>
  /// Verifies that empty subscriber name throws ArgumentException.
  /// </summary>
  /// <tests>src/Whizbang.Transports.AzureServiceBus/ServiceBusSubscriptionNameHelper.cs:GenerateSubscriptionName</tests>
  [Test]
  public async Task GenerateSubscriptionName_WithEmptySubscriberName_ThrowsArgumentExceptionAsync() {
    // Arrange
    var subscriberName = "";
    var topicName = "valid.topic";

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentException>(
      () => Task.FromResult(ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName)));
  }

  /// <summary>
  /// Verifies that empty topic name throws ArgumentException.
  /// </summary>
  /// <tests>src/Whizbang.Transports.AzureServiceBus/ServiceBusSubscriptionNameHelper.cs:GenerateSubscriptionName</tests>
  [Test]
  public async Task GenerateSubscriptionName_WithEmptyTopicName_ThrowsArgumentExceptionAsync() {
    // Arrange
    var subscriberName = "valid-subscriber";
    var topicName = "";

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentException>(
      () => Task.FromResult(ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName)));
  }

  /// <summary>
  /// Verifies that null subscriber name throws ArgumentException.
  /// </summary>
  /// <tests>src/Whizbang.Transports.AzureServiceBus/ServiceBusSubscriptionNameHelper.cs:GenerateSubscriptionName</tests>
  [Test]
  public async Task GenerateSubscriptionName_WithNullSubscriberName_ThrowsArgumentExceptionAsync() {
    // Arrange
    string? subscriberName = null;
    var topicName = "valid.topic";

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentException>(
      () => Task.FromResult(ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName!, topicName)));
  }

  /// <summary>
  /// Verifies that asterisk wildcards (*) are sanitized.
  /// </summary>
  /// <tests>src/Whizbang.Transports.AzureServiceBus/ServiceBusSubscriptionNameHelper.cs:GenerateSubscriptionName</tests>
  [Test]
  public async Task GenerateSubscriptionName_WithAsteriskWildcard_SanitizesCorrectlyAsync() {
    // Arrange
    var subscriberName = "svc";
    var topicName = "ns.*";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert - * should be replaced with hyphen
    await Assert.That(result).IsEqualTo("svc-ns.-");
  }

  /// <summary>
  /// Verifies that comma-separated patterns are sanitized.
  /// </summary>
  /// <tests>src/Whizbang.Transports.AzureServiceBus/ServiceBusSubscriptionNameHelper.cs:GenerateSubscriptionName</tests>
  [Test]
  public async Task GenerateSubscriptionName_WithCommaSeparatedPattern_SanitizesCorrectlyAsync() {
    // Arrange - comma-separated filter expression (invalid for ASB subscription name)
    var subscriberName = "worker";
    var topicName = "ns1,ns2";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert - commas should be replaced with hyphens
    await Assert.That(result).IsEqualTo("worker-ns1-ns2");
  }

  /// <summary>
  /// Verifies that forward slashes are sanitized.
  /// </summary>
  /// <tests>src/Whizbang.Transports.AzureServiceBus/ServiceBusSubscriptionNameHelper.cs:GenerateSubscriptionName</tests>
  [Test]
  public async Task GenerateSubscriptionName_WithForwardSlash_SanitizesCorrectlyAsync() {
    // Arrange
    var subscriberName = "api/v1";
    var topicName = "events/topic";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert - slashes should be replaced
    await Assert.That(result).IsEqualTo("api-v1-events-topic");
  }

  /// <summary>
  /// Verifies that backslashes are sanitized.
  /// </summary>
  /// <tests>src/Whizbang.Transports.AzureServiceBus/ServiceBusSubscriptionNameHelper.cs:GenerateSubscriptionName</tests>
  [Test]
  public async Task GenerateSubscriptionName_WithBackslash_SanitizesCorrectlyAsync() {
    // Arrange
    var subscriberName = @"domain\service";
    var topicName = "topic";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert - backslashes should be replaced
    await Assert.That(result).IsEqualTo("domain-service-topic");
  }

  /// <summary>
  /// Verifies that consecutive invalid characters result in single hyphen.
  /// </summary>
  /// <tests>src/Whizbang.Transports.AzureServiceBus/ServiceBusSubscriptionNameHelper.cs:GenerateSubscriptionName</tests>
  [Test]
  public async Task GenerateSubscriptionName_WithConsecutiveInvalidChars_RemovesDoubleHyphensAsync() {
    // Arrange
    var subscriberName = "svc";
    var topicName = "ns##test";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert - consecutive hyphens should be collapsed
    await Assert.That(result).DoesNotContain("--");
  }

  /// <summary>
  /// Verifies that result is lowercased for consistency.
  /// </summary>
  /// <tests>src/Whizbang.Transports.AzureServiceBus/ServiceBusSubscriptionNameHelper.cs:GenerateSubscriptionName</tests>
  [Test]
  public async Task GenerateSubscriptionName_WithMixedCase_ReturnsLowercaseAsync() {
    // Arrange
    var subscriberName = "MyService";
    var topicName = "MyApp.Events";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert
    await Assert.That(result).IsEqualTo("myservice-myapp.events");
  }

  /// <summary>
  /// Verifies that leading/trailing hyphens are trimmed.
  /// </summary>
  /// <tests>src/Whizbang.Transports.AzureServiceBus/ServiceBusSubscriptionNameHelper.cs:GenerateSubscriptionName</tests>
  [Test]
  public async Task GenerateSubscriptionName_WithLeadingTrailingInvalidChars_TrimsHyphensAsync() {
    // Arrange
    var subscriberName = "#svc#";
    var topicName = "*topic*";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert - no leading/trailing hyphens
    await Assert.That(result).DoesNotStartWith("-");
    await Assert.That(result).DoesNotEndWith("-");
  }
}
