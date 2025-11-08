using System;
using System.Collections.Generic;

namespace Whizbang.Core.Transports;

/// <summary>
/// Represents where a message should be sent.
/// Includes the address (queue/topic/service name), optional routing key,
/// and extensible metadata for transport-specific configuration.
/// </summary>
/// <param name="Address">The destination address (queue name, topic, service endpoint, etc.)</param>
/// <param name="RoutingKey">Optional routing key for topic-based routing (e.g., "orders.created")</param>
/// <param name="Metadata">Optional transport-specific metadata (priority, TTL, headers, etc.)</param>
public record TransportDestination(
  string Address,
  string? RoutingKey = null,
  IReadOnlyDictionary<string, object>? Metadata = null
) {
  /// <summary>
  /// Gets the destination address.
  /// </summary>
  public string Address { get; init; } = ValidateAddress(Address);

  /// <summary>
  /// Validates the address is not null or empty.
  /// </summary>
  private static string ValidateAddress(string address) {
    if (string.IsNullOrWhiteSpace(address)) {
      throw new ArgumentException("Address cannot be null or empty", nameof(address));
    }
    return address;
  }
}
