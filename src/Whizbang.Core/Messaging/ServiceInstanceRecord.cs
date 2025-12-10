using System.Text.Json;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Database entity for service instance tracking.
/// Tracks active service instances with heartbeat timestamps for distributed work coordination
/// and failure detection. Used by WorkCoordinator to identify orphaned work.
/// </summary>
public sealed class ServiceInstanceRecord {
  /// <summary>
  /// Unique identifier for this service instance (generated at startup).
  /// Primary key used to track which instance owns which messages.
  /// </summary>
  public required Guid InstanceId { get; set; }

  /// <summary>
  /// Name of the service (e.g., "InventoryWorker", "OrderService").
  /// Used for monitoring and debugging.
  /// </summary>
  public required string ServiceName { get; set; }

  /// <summary>
  /// Hostname where the service is running.
  /// Used for debugging and infrastructure monitoring.
  /// </summary>
  public required string HostName { get; set; }

  /// <summary>
  /// Operating system process ID.
  /// Used for debugging and process tracking.
  /// </summary>
  public required int ProcessId { get; set; }

  /// <summary>
  /// UTC timestamp when this instance started.
  /// Automatically set by database on insert.
  /// </summary>
  public DateTimeOffset StartedAt { get; set; }

  /// <summary>
  /// UTC timestamp of the last heartbeat from this instance.
  /// Updated periodically by WorkCoordinator.
  /// Used to detect crashed instances (stale heartbeats indicate failure).
  /// </summary>
  public DateTimeOffset LastHeartbeatAt { get; set; }

  /// <summary>
  /// Additional instance metadata stored as JSON.
  /// Contains version, environment, configuration, etc.
  /// Schema: { "Version": "1.0.0", "Environment": "Production", "Region": "us-east-1", ... }
  /// </summary>
  public JsonDocument? Metadata { get; set; }
}
