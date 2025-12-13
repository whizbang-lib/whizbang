namespace Whizbang.Core.Observability;

/// <summary>
/// Provides service instance information for the current process.
/// This is the single source of truth for instance identity across:
/// - Work coordination (wh_service_instances table)
/// - Message tracing (MessageHop.ServiceInstanceId)
/// - Lease management (outbox/inbox processing)
/// Each service instance (process) has a unique ID generated at startup.
/// </summary>
/// <docs>core-concepts/observability</docs>
public interface IServiceInstanceProvider {
  /// <summary>
  /// Gets the unique instance ID for this service instance.
  /// The ID is generated once at startup and remains constant for the lifetime of the process.
  /// Used for instance tracking in wh_service_instances table and MessageHop tracing.
  /// </summary>
  Guid InstanceId { get; }

  /// <summary>
  /// Gets the service name (typically the entry assembly name).
  /// Example: "ECommerce.BFF.API", "ECommerce.InventoryWorker"
  /// </summary>
  string ServiceName { get; }

  /// <summary>
  /// Gets the host machine name where this instance is running.
  /// Used for debugging and distributed system visibility.
  /// </summary>
  string HostName { get; }

  /// <summary>
  /// Gets the process ID (PID) of this service instance.
  /// Used for identifying the specific OS process.
  /// </summary>
  int ProcessId { get; }

  /// <summary>
  /// Creates a ServiceInstanceInfo record from this provider's properties.
  /// Useful for capturing service instance information in observability hops.
  /// </summary>
  /// <returns>A ServiceInstanceInfo record containing all instance metadata</returns>
  ServiceInstanceInfo ToInfo();
}
