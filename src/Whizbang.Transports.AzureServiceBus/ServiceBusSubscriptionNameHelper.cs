namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Helper for generating valid Azure Service Bus subscription names.
/// Sanitizes invalid characters and ensures names conform to ASB requirements.
/// </summary>
/// <remarks>
/// Azure Service Bus subscription names:
/// - Maximum 50 characters
/// - Cannot contain: #, *, /, \, ,
/// - Should be lowercase for consistency
/// </remarks>
/// <docs>components/transports/azure-service-bus#subscription-naming</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusSubscriptionNameHelperTests.cs</tests>
public static class ServiceBusSubscriptionNameHelper {
  private const int MAX_SUBSCRIPTION_NAME_LENGTH = 50;

  /// <summary>
  /// Generates a valid Azure Service Bus subscription name from subscriber and topic names.
  /// </summary>
  /// <param name="subscriberName">The service/subscriber name (e.g., "bff-service").</param>
  /// <param name="topicName">The topic name being subscribed to (e.g., "jdx.contracts.chat").</param>
  /// <returns>A valid Azure Service Bus subscription name in format: {subscriberName}-{topicName}</returns>
  /// <exception cref="ArgumentException">Thrown when subscriberName or topicName is null or whitespace.</exception>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusSubscriptionNameHelperTests.cs:GenerateSubscriptionName_WithValidNames_ReturnsExpectedFormatAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusSubscriptionNameHelperTests.cs:GenerateSubscriptionName_WithWildcard_SanitizesCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusSubscriptionNameHelperTests.cs:GenerateSubscriptionName_ExceedsMaxLength_TruncatesTo50CharsAsync</tests>
  public static string GenerateSubscriptionName(string subscriberName, string topicName) {
    // TDD RED: This will fail all tests
    throw new NotImplementedException("TDD RED phase - implementation pending");
  }

  /// <summary>
  /// Sanitizes a string to be a valid Azure Service Bus subscription name.
  /// </summary>
  /// <param name="name">The raw name to sanitize.</param>
  /// <returns>A sanitized name suitable for use as an ASB subscription name.</returns>
  private static string _sanitizeSubscriptionName(string name) {
    throw new NotImplementedException("TDD RED phase - implementation pending");
  }
}
