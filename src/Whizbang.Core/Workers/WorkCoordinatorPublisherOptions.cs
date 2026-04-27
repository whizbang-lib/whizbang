namespace Whizbang.Core.Workers;

/// <summary>
/// Compatibility shim for the deleted legacy publisher worker options.
/// New configuration lives on <see cref="ClaimWorkerOptions"/>, the per-flusher options,
/// <see cref="OutboxPublishWorkerOptions"/>, <see cref="InboxDispatchWorkerOptions"/>,
/// and <see cref="MaintenanceWorkerOptions"/>. This type is retained only so existing
/// test fixtures that bind to the <c>WorkCoordinatorPublisher</c> appsettings section
/// continue to compile while they are migrated.
/// </summary>
public sealed class WorkCoordinatorPublisherOptions {
  /// <summary>Threshold (seconds) before a non-heartbeating instance is considered abandoned.</summary>
  public int AbandonStaleInstanceThresholdSeconds { get; set; } = 30;

  /// <summary>Enable/disable flag retained for legacy bindings.</summary>
  public bool Enabled { get; set; } = true;

  /// <summary>Polling interval retained for legacy bindings.</summary>
  public int PollingIntervalMilliseconds { get; set; } = 100;

  /// <summary>Maximum streams per batch retained for legacy bindings.</summary>
  public int MaxStreamsPerBatch { get; set; } = 100;

  /// <summary>Lease seconds retained for legacy bindings.</summary>
  public int LeaseSeconds { get; set; } = 300;

  /// <summary>Heartbeat interval retained for legacy bindings.</summary>
  public int HeartbeatIntervalSeconds { get; set; } = 5;

  /// <summary>Debug mode flag retained for legacy bindings.</summary>
  public bool DebugMode { get; set; }

  /// <summary>Partition count retained for legacy bindings.</summary>
  public int PartitionCount { get; set; } = 10000;

  /// <summary>Idle threshold polls retained for legacy bindings.</summary>
  public int IdleThresholdPolls { get; set; } = 3;
}
