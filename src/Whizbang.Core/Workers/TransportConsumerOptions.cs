using Whizbang.Core.Transports;

namespace Whizbang.Core.Workers;

/// <summary>
/// Configuration options for TransportConsumerWorker.
/// Specifies which destinations to subscribe to.
/// </summary>
/// <docs>messaging/transports/transport-consumer</docs>
public class TransportConsumerOptions {
  /// <summary>
  /// Gets the list of destinations to subscribe to.
  /// Each destination will create a separate subscription.
  /// </summary>
  public List<TransportDestination> Destinations { get; } = [];

  /// <summary>
  /// Subscriber name used for generating queue names.
  /// For RabbitMQ, this becomes the queue name prefix: "{SubscriberName}-{exchange}".
  /// If not set, a default name will be generated.
  /// </summary>
  public string? SubscriberName { get; set; }
}
