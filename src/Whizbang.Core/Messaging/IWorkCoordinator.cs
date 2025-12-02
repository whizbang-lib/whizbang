namespace Whizbang.Core.Messaging;

/// <summary>
/// Coordinates work processing across multiple service instances using lease-based coordination.
/// Provides atomic operations for heartbeat updates, message completion tracking,
/// and orphaned work recovery.
/// </summary>
public interface IWorkCoordinator {
  /// <summary>
  /// Processes a batch of work in a single atomic operation:
  /// - Updates instance heartbeat
  /// - Marks completed outbox messages
  /// - Marks failed outbox messages
  /// - Marks completed inbox messages
  /// - Marks failed inbox messages
  /// - Claims and returns orphaned work (expired leases)
  ///
  /// This minimizes database round-trips and ensures consistency.
  /// </summary>
  /// <param name="instanceId">Service instance ID</param>
  /// <param name="outboxCompletedIds">IDs of successfully published outbox messages</param>
  /// <param name="outboxFailedMessages">Failed outbox messages with error details</param>
  /// <param name="inboxCompletedIds">IDs of successfully processed inbox messages</param>
  /// <param name="inboxFailedMessages">Failed inbox messages with error details</param>
  /// <param name="leaseSeconds">Lease duration in seconds (default 300 = 5 minutes)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Work batch containing orphaned messages that need processing</returns>
  Task<WorkBatch> ProcessWorkBatchAsync(
    Guid instanceId,
    Guid[] outboxCompletedIds,
    FailedMessage[] outboxFailedMessages,
    Guid[] inboxCompletedIds,
    FailedMessage[] inboxFailedMessages,
    int leaseSeconds = 300,
    CancellationToken cancellationToken = default
  );
}

/// <summary>
/// Result of ProcessWorkBatchAsync containing orphaned work that needs processing.
/// </summary>
public record WorkBatch {
  /// <summary>
  /// Orphaned outbox messages (expired leases) that need to be published.
  /// </summary>
  public required List<OrphanedOutboxMessage> OrphanedOutbox { get; init; }

  /// <summary>
  /// Orphaned inbox messages (expired leases) that need to be processed.
  /// </summary>
  public required List<OrphanedInboxMessage> OrphanedInbox { get; init; }
}

/// <summary>
/// Represents a failed message with error information.
/// Used for marking messages as failed in the database.
/// </summary>
public record FailedMessage {
  /// <summary>
  /// Message ID that failed.
  /// </summary>
  public required Guid MessageId { get; init; }

  /// <summary>
  /// Error message or exception details.
  /// </summary>
  public required string Error { get; init; }
}

/// <summary>
/// Represents an orphaned outbox message that needs to be published.
/// Returned by ProcessWorkBatchAsync when a message's lease has expired.
/// </summary>
public record OrphanedOutboxMessage {
  /// <summary>
  /// Unique message ID.
  /// </summary>
  public required Guid MessageId { get; init; }

  /// <summary>
  /// Destination to publish to (topic name).
  /// </summary>
  public required string Destination { get; init; }

  /// <summary>
  /// Fully-qualified message type name.
  /// </summary>
  public required string MessageType { get; init; }

  /// <summary>
  /// Message payload as JSON string.
  /// </summary>
  public required string MessageData { get; init; }

  /// <summary>
  /// Message metadata as JSON string.
  /// Contains correlation ID, causation ID, hops, etc.
  /// </summary>
  public required string Metadata { get; init; }

  /// <summary>
  /// Scope information as JSON string (nullable).
  /// Contains tenant/user/partition info.
  /// </summary>
  public string? Scope { get; init; }

  /// <summary>
  /// Number of previous publishing attempts.
  /// </summary>
  public required int Attempts { get; init; }
}

/// <summary>
/// Represents an orphaned inbox message that needs to be processed.
/// Returned by ProcessWorkBatchAsync when a message's lease has expired.
/// </summary>
public record OrphanedInboxMessage {
  /// <summary>
  /// Unique message ID.
  /// </summary>
  public required Guid MessageId { get; init; }

  /// <summary>
  /// Fully-qualified message type name.
  /// </summary>
  public required string MessageType { get; init; }

  /// <summary>
  /// Message payload as JSON string.
  /// </summary>
  public required string MessageData { get; init; }

  /// <summary>
  /// Message metadata as JSON string.
  /// Contains correlation ID, causation ID, hops, etc.
  /// </summary>
  public required string Metadata { get; init; }

  /// <summary>
  /// Scope information as JSON string (nullable).
  /// Contains tenant/user/partition info.
  /// </summary>
  public string? Scope { get; init; }
}
