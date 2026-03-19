namespace Whizbang.Core.Transports;

/// <summary>
/// Base interface for transport-specific metadata.
/// Transport metadata provides access to properties/headers that are specific to the transport layer
/// (e.g., Azure Service Bus application properties, RabbitMQ headers, Kafka headers).
/// </summary>
/// <remarks>
/// This interface enables security context extractors to access transport-level information
/// without knowing the specific transport implementation. Each transport provides its own
/// implementation with transport-specific properties.
///
/// Security extractors can use transport metadata to extract tokens, tenant IDs, user IDs,
/// and other security-relevant information that was set by the message producer.
/// </remarks>
/// <docs>fundamentals/security/message-security#transport-metadata</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/TransportMetadataTests.cs</tests>
public interface ITransportMetadata {
  /// <summary>
  /// Gets the name of the transport this metadata is from.
  /// Examples: "AzureServiceBus", "RabbitMQ", "Kafka", "InProcess".
  /// </summary>
  string TransportName { get; }

  /// <summary>
  /// Attempts to get a property value with the specified key.
  /// </summary>
  /// <typeparam name="T">The expected type of the property value</typeparam>
  /// <param name="key">The property key</param>
  /// <param name="value">The property value if found and of the correct type</param>
  /// <returns>True if the property exists and is of the correct type, false otherwise</returns>
  bool TryGetProperty<T>(string key, out T? value);

  /// <summary>
  /// Gets a property value with the specified key, returning default if not found.
  /// </summary>
  /// <typeparam name="T">The expected type of the property value</typeparam>
  /// <param name="key">The property key</param>
  /// <returns>The property value if found and of the correct type, default otherwise</returns>
  T? GetProperty<T>(string key);

  /// <summary>
  /// Checks if a property with the specified key exists.
  /// </summary>
  /// <param name="key">The property key</param>
  /// <returns>True if the property exists, false otherwise</returns>
  bool ContainsProperty(string key);
}
