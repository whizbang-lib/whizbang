namespace Whizbang.Core.Observability;

/// <summary>
/// Immutable record containing service instance identification and metadata.
/// Used for distributed tracing and observability to track which service instance
/// processed a message at each hop in its journey.
/// </summary>
/// <param name="ServiceName">The name of the service (e.g., "OrderService", "InventoryWorker")</param>
/// <param name="InstanceId">Unique UUIDv7 identifier for this specific service instance</param>
/// <param name="HostName">The machine/host name where the service is running</param>
/// <param name="ProcessId">The operating system process ID</param>
public record ServiceInstanceInfo(
  string ServiceName,
  Guid InstanceId,
  string HostName,
  int ProcessId
);
