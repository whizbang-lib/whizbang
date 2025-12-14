namespace Whizbang.Core.Transports;

/// <summary>
/// Interface for checking whether a transport is ready to accept messages.
/// Implementations can check connectivity, health, or other readiness criteria.
/// </summary>
/// <remarks>
/// This interface is used by the WorkCoordinatorPublisherWorker to determine if messages
/// should be published or buffered. When a transport is not ready, messages are kept in
/// the outbox with renewed leases until the transport becomes available.
///
/// Examples of readiness checks:
/// - Azure Service Bus: Check if namespace is accessible
/// - RabbitMQ: Check if connection is established
/// - Kafka: Check if brokers are reachable
/// - HTTP: Check if endpoint is responding
/// </remarks>
/// <docs>components/transports</docs>
public interface ITransportReadinessCheck {
  /// <summary>
  /// Checks if the transport is ready to accept messages.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token to cancel the readiness check.</param>
  /// <returns>True if the transport is ready, false otherwise.</returns>
  /// <remarks>
  /// This method should be fast and lightweight. If the check requires network I/O,
  /// consider implementing caching or circuit breaker patterns to avoid excessive overhead.
  /// </remarks>
  Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);
}
