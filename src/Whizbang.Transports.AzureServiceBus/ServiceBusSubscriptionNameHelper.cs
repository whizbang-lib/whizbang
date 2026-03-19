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
/// <docs>messaging/transports/azure-service-bus#subscription-naming</docs>
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
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusSubscriptionNameHelperTests.cs:GenerateSubscriptionNameWithValidNamesReturnsExpectedFormatAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusSubscriptionNameHelperTests.cs:GenerateSubscriptionNameWithWildcardSanitizesCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusSubscriptionNameHelperTests.cs:GenerateSubscriptionNameExceedsMaxLengthTruncatesTo50CharsAsync</tests>
  public static string GenerateSubscriptionName(string subscriberName, string topicName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(subscriberName, nameof(subscriberName));
    ArgumentException.ThrowIfNullOrWhiteSpace(topicName, nameof(topicName));

    // Combine names with hyphen separator
    var rawName = $"{subscriberName}-{topicName}";

    // Sanitize the combined name
    var sanitized = _sanitizeSubscriptionName(rawName);

    // Truncate to max length if needed
    if (sanitized.Length > MAX_SUBSCRIPTION_NAME_LENGTH) {
      sanitized = sanitized[..MAX_SUBSCRIPTION_NAME_LENGTH];

      // Ensure we don't end with a hyphen after truncation
      sanitized = sanitized.TrimEnd('-');
    }

    return sanitized;
  }

  /// <summary>
  /// Sanitizes a string to be a valid Azure Service Bus subscription name.
  /// </summary>
  /// <param name="name">The raw name to sanitize.</param>
  /// <returns>A sanitized name suitable for use as an ASB subscription name.</returns>
  private static string _sanitizeSubscriptionName(string name) {
    // Lowercase for consistency
    var sanitized = name.ToLowerInvariant();

    // Replace invalid characters with hyphens
    // Invalid chars: #, *, /, \, ,
    sanitized = sanitized
      .Replace("#", "-")
      .Replace("*", "-")
      .Replace("/", "-")
      .Replace("\\", "-")
      .Replace(",", "-");

    // Remove consecutive hyphens (collapse to single hyphen)
    while (sanitized.Contains("--", StringComparison.Ordinal)) {
      sanitized = sanitized.Replace("--", "-");
    }

    // Trim leading/trailing hyphens
    sanitized = sanitized.Trim('-');

    return sanitized;
  }
}
