using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Whizbang.Core.Transports;

/// <summary>
/// <tests>tests/Whizbang.Transports.Tests/TransportDestinationTests.cs:TransportDestination_WithAddress_SetsAddressAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportDestinationTests.cs:TransportDestination_WithAddressAndRoutingKey_SetsPropertiesAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportDestinationTests.cs:TransportDestination_WithMetadata_SetsMetadataAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportDestinationTests.cs:TransportDestination_WithNullMetadata_AllowedAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportDestinationTests.cs:TransportDestination_RecordEquality_BehavesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportDestinationTests.cs:TransportDestination_EmptyOrWhitespaceAddress_ThrowsArgumentExceptionAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportDestinationTests.cs:TransportDestination_NullAddress_ThrowsArgumentExceptionAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportDestinationTests.cs:TransportDestination_WithEmptyMetadata_PreservesEmptyDictionaryAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportDestinationTests.cs:TransportDestination_MetadataIsReadOnly_CannotModifyAsync</tests>
/// Represents where a message should be sent.
/// Includes the address (queue/topic/service name), optional routing key,
/// and extensible metadata for transport-specific configuration.
/// </summary>
/// <param name="Address">The destination address (queue name, topic, service endpoint, etc.)</param>
/// <param name="RoutingKey">Optional routing key for topic-based routing (e.g., "orders.created")</param>
/// <param name="Metadata">Optional transport-specific metadata (priority, TTL, headers, etc.). Supports any JSON value type via JsonElement.</param>
public record TransportDestination(
  string Address,
  string? RoutingKey = null,
  IReadOnlyDictionary<string, JsonElement>? Metadata = null
) {
  /// <summary>
  /// Gets the destination address.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/TransportDestinationTests.cs:TransportDestination_WithAddress_SetsAddressAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportDestinationTests.cs:TransportDestination_WithAddressAndRoutingKey_SetsPropertiesAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportDestinationTests.cs:TransportDestination_WithMetadata_SetsMetadataAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportDestinationTests.cs:TransportDestination_WithNullMetadata_AllowedAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportDestinationTests.cs:TransportDestination_RecordEquality_BehavesCorrectlyAsync</tests>
  public string Address { get; init; } = ValidateAddress(Address);

  /// <summary>
  /// Validates the address is not null or empty.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/TransportDestinationTests.cs:TransportDestination_EmptyOrWhitespaceAddress_ThrowsArgumentExceptionAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportDestinationTests.cs:TransportDestination_NullAddress_ThrowsArgumentExceptionAsync</tests>
  private static string ValidateAddress(string address) {
    if (string.IsNullOrWhiteSpace(address)) {
      throw new ArgumentException("Address cannot be null or empty", nameof(address));
    }
    return address;
  }
}
