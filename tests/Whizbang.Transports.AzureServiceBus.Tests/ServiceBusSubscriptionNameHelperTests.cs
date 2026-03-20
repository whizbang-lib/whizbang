namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Tests for ServiceBusSubscriptionNameHelper - ensures valid Azure Service Bus subscription names.
/// </summary>
/// <tests>src/Whizbang.Transports.AzureServiceBus/ServiceBusSubscriptionNameHelper.cs</tests>
public class ServiceBusSubscriptionNameHelperTests {

  /// <summary>
  /// Verifies that valid subscriber and topic names produce expected format.
  /// </summary>
  [Test]
  public async Task GenerateSubscriptionNameWithValidNamesReturnsExpectedFormatAsync() {
    // Arrange
    const string subscriberName = "bff-service";
    const string topicName = "jdx.contracts.chat";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert
    await Assert.That(result).IsEqualTo("bff-service-jdx.contracts.chat");
  }

  /// <summary>
  /// Verifies that wildcard characters (#) are sanitized to valid characters.
  /// </summary>
  [Test]
  public async Task GenerateSubscriptionNameWithWildcardSanitizesCorrectlyAsync() {
    // Arrange - topic name contains # wildcard (invalid for ASB)
    const string subscriberName = "inventory";
    const string topicName = "myapp.events#test";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert - # should be replaced with hyphen
    await Assert.That(result).IsEqualTo("inventory-myapp.events-test");
  }

  /// <summary>
  /// Verifies that names exceeding 50 characters are truncated.
  /// </summary>
  [Test]
  public async Task GenerateSubscriptionNameExceedsMaxLengthTruncatesTo50CharsAsync() {
    // Arrange - create names that exceed 50 chars when combined
    const string subscriberName = "very-long-subscriber-name";
    const string topicName = "equally.long.topic.namespace.events";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert
    await Assert.That(result.Length).IsLessThanOrEqualTo(50);
  }

  /// <summary>
  /// Verifies that empty subscriber name throws ArgumentException.
  /// </summary>
  [Test]
  public async Task GenerateSubscriptionNameWithEmptySubscriberNameThrowsArgumentExceptionAsync() {
    // Arrange
    const string subscriberName = "";
    const string topicName = "valid.topic";

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentException>(
      () => Task.FromResult(ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName)));
  }

  /// <summary>
  /// Verifies that empty topic name throws ArgumentException.
  /// </summary>
  [Test]
  public async Task GenerateSubscriptionNameWithEmptyTopicNameThrowsArgumentExceptionAsync() {
    // Arrange
    const string subscriberName = "valid-subscriber";
    const string topicName = "";

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentException>(
      () => Task.FromResult(ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName)));
  }

  /// <summary>
  /// Verifies that null subscriber name throws ArgumentException.
  /// </summary>
  [Test]
  public async Task GenerateSubscriptionNameWithNullSubscriberNameThrowsArgumentExceptionAsync() {
    // Arrange
    const string? subscriberName = null;
    const string topicName = "valid.topic";

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentException>(
      () => Task.FromResult(ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName!, topicName)));
  }

  /// <summary>
  /// Verifies that asterisk wildcards (*) are sanitized.
  /// </summary>
  [Test]
  public async Task GenerateSubscriptionNameWithAsteriskWildcardSanitizesCorrectlyAsync() {
    // Arrange
    const string subscriberName = "svc";
    const string topicName = "ns.*";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert - * should be replaced with hyphen, trailing hyphen trimmed
    await Assert.That(result).IsEqualTo("svc-ns.");
  }

  /// <summary>
  /// Verifies that comma-separated patterns are sanitized.
  /// </summary>
  [Test]
  public async Task GenerateSubscriptionNameWithCommaSeparatedPatternSanitizesCorrectlyAsync() {
    // Arrange - comma-separated filter expression (invalid for ASB subscription name)
    const string subscriberName = "worker";
    const string topicName = "ns1,ns2";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert - commas should be replaced with hyphens
    await Assert.That(result).IsEqualTo("worker-ns1-ns2");
  }

  /// <summary>
  /// Verifies that forward slashes are sanitized.
  /// </summary>
  [Test]
  public async Task GenerateSubscriptionNameWithForwardSlashSanitizesCorrectlyAsync() {
    // Arrange
    const string subscriberName = "api/v1";
    const string topicName = "events/topic";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert - slashes should be replaced
    await Assert.That(result).IsEqualTo("api-v1-events-topic");
  }

  /// <summary>
  /// Verifies that backslashes are sanitized.
  /// </summary>
  [Test]
  public async Task GenerateSubscriptionNameWithBackslashSanitizesCorrectlyAsync() {
    // Arrange
    const string subscriberName = @"domain\service";
    const string topicName = "topic";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert - backslashes should be replaced
    await Assert.That(result).IsEqualTo("domain-service-topic");
  }

  /// <summary>
  /// Verifies that consecutive invalid characters result in single hyphen.
  /// </summary>
  [Test]
  public async Task GenerateSubscriptionNameWithConsecutiveInvalidCharsRemovesDoubleHyphensAsync() {
    // Arrange
    const string subscriberName = "svc";
    const string topicName = "ns##test";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert - consecutive hyphens should be collapsed
    await Assert.That(result).DoesNotContain("--");
  }

  /// <summary>
  /// Verifies that result is lowercased for consistency.
  /// </summary>
  [Test]
  public async Task GenerateSubscriptionNameWithMixedCaseReturnsLowercaseAsync() {
    // Arrange
    const string subscriberName = "MyService";
    const string topicName = "MyApp.Events";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert
    await Assert.That(result).IsEqualTo("myservice-myapp.events");
  }

  /// <summary>
  /// Verifies that leading/trailing hyphens are trimmed.
  /// </summary>
  [Test]
  public async Task GenerateSubscriptionNameWithLeadingTrailingInvalidCharsTrimsHyphensAsync() {
    // Arrange
    const string subscriberName = "#svc#";
    const string topicName = "*topic*";

    // Act
    var result = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);

    // Assert - no leading/trailing hyphens
    await Assert.That(result).DoesNotStartWith("-");
    await Assert.That(result).DoesNotEndWith("-");
  }
}
