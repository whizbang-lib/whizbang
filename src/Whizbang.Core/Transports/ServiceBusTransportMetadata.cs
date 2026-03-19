namespace Whizbang.Core.Transports;

/// <summary>
/// Transport metadata implementation for Azure Service Bus.
/// Wraps Service Bus application properties for security context extraction.
/// </summary>
/// <remarks>
/// Azure Service Bus messages can contain application properties (string key-value pairs)
/// that are set by the message producer. These properties can carry security tokens,
/// tenant IDs, user IDs, roles, and other contextual information.
///
/// This class provides an immutable view of these properties that can be accessed
/// by security context extractors during message processing.
///
/// Common application properties for security:
/// - X-Security-Token: JWT or other token
/// - X-Tenant-Id: Multi-tenant identifier
/// - X-User-Id: User identifier
/// - X-Roles: Comma-separated role list
/// </remarks>
/// <docs>fundamentals/security/message-security#service-bus-metadata</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/TransportMetadataTests.cs</tests>
public sealed class ServiceBusTransportMetadata : ITransportMetadata {
  private readonly Dictionary<string, object> _applicationProperties;

  /// <summary>
  /// Creates a new ServiceBusTransportMetadata from application properties.
  /// </summary>
  /// <param name="applicationProperties">The Service Bus application properties</param>
  /// <exception cref="ArgumentNullException">Thrown when applicationProperties is null</exception>
  public ServiceBusTransportMetadata(IDictionary<string, object> applicationProperties) {
    ArgumentNullException.ThrowIfNull(applicationProperties);

    // Create immutable copy to prevent modification
    _applicationProperties = new Dictionary<string, object>(applicationProperties);
  }

  /// <inheritdoc />
  public string TransportName => "AzureServiceBus";

  /// <summary>
  /// Gets all application properties from the Service Bus message.
  /// </summary>
  public IReadOnlyDictionary<string, object> ApplicationProperties => _applicationProperties;

  /// <inheritdoc />
  public bool TryGetProperty<T>(string key, out T? value) {
    if (_applicationProperties.TryGetValue(key, out var rawValue) && rawValue is T typedValue) {
      value = typedValue;
      return true;
    }

    value = default;
    return false;
  }

  /// <inheritdoc />
  public T? GetProperty<T>(string key) {
    if (_applicationProperties.TryGetValue(key, out var rawValue) && rawValue is T typedValue) {
      return typedValue;
    }

    return default;
  }

  /// <inheritdoc />
  public bool ContainsProperty(string key) {
    return _applicationProperties.ContainsKey(key);
  }
}
