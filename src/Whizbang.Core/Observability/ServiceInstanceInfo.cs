namespace Whizbang.Core.Observability;

/// <summary>
/// Immutable record containing service instance identification and metadata.
/// Used for distributed tracing and observability to track which service instance
/// processed a message at each hop in its journey.
/// IMPORTANT: Can only be created by IServiceInstanceProvider - constructor is internal.
/// </summary>
public record ServiceInstanceInfo {
  /// <summary>
  /// The name of the service (e.g., "OrderService", "InventoryWorker")
  /// </summary>
  public required string ServiceName { get; init; }

  /// <summary>
  /// Unique UUIDv7 identifier for this specific service instance
  /// </summary>
  public required Guid InstanceId { get; init; }

  /// <summary>
  /// The machine/host name where the service is running
  /// </summary>
  public required string HostName { get; init; }

  /// <summary>
  /// The operating system process ID
  /// </summary>
  public required int ProcessId { get; init; }

  /// <summary>
  /// Public parameterless constructor required for JSON deserialization.
  /// The 'required' modifier on properties ensures all fields are set during initialization.
  /// </summary>
  public ServiceInstanceInfo() { }
};
