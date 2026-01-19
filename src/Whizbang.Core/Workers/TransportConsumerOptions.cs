using Whizbang.Core.Transports;

namespace Whizbang.Core.Workers;

/// <summary>
/// Configuration options for TransportConsumerWorker.
/// Specifies which destinations to subscribe to.
/// </summary>
/// <docs>components/workers/transport-consumer</docs>
public class TransportConsumerOptions {
  /// <summary>
  /// Gets the list of destinations to subscribe to.
  /// Each destination will create a separate subscription.
  /// </summary>
  public List<TransportDestination> Destinations { get; } = new();
}
