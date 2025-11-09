namespace Whizbang.Core.Transports;

/// <summary>
/// Defines where a message should be published when created locally.
/// Used by PolicyConfiguration to specify publishing targets.
/// </summary>
public record PublishTarget {
  /// <summary>
  /// Type of transport to publish to
  /// </summary>
  public required TransportType TransportType { get; init; }

  /// <summary>
  /// Destination for the message (topic, queue, exchange)
  /// </summary>
  public required string Destination { get; init; }

  /// <summary>
  /// Routing key (RabbitMQ only)
  /// </summary>
  public string? RoutingKey { get; init; }
}
