namespace Whizbang.Core.Messaging;

/// <summary>
/// Ephemeral stream assignment record tracking which instance owns each active stream.
/// Stored in wh_active_streams table.
/// Provides sticky assignment and cross-subsystem coordination (outbox/inbox/perspectives).
/// Streams are added when first work arrives and removed when all work completes.
/// </summary>
public sealed class ActiveStreamRecord {
  /// <summary>
  /// UUIDv7 stream identifier - naturally time-ordered.
  /// </summary>
  public required Guid StreamId { get; set; }

  /// <summary>
  /// Partition number (0-9999) computed via compute_partition(stream_id, partition_count).
  /// Same stream_id always maps to same partition (deterministic hashing).
  /// </summary>
  public required int PartitionNumber { get; set; }

  /// <summary>
  /// Instance that currently owns this stream.
  /// NULL indicates orphaned stream (available for claiming).
  /// </summary>
  public Guid? AssignedInstanceId { get; set; }

  /// <summary>
  /// When the lease expires.
  /// NULL indicates no lease.
  /// Expired leases can be claimed by other instances.
  /// </summary>
  public DateTime? LeaseExpiry { get; set; }

  /// <summary>
  /// When this stream was first added to active streams table.
  /// </summary>
  public DateTime CreatedAt { get; set; }

  /// <summary>
  /// Last time this stream assignment or lease was updated.
  /// </summary>
  public DateTime UpdatedAt { get; set; }
}
