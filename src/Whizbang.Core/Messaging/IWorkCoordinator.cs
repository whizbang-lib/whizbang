namespace Whizbang.Core.Messaging;

/// <summary>
/// Coordinates work processing across multiple service instances using lease-based coordination.
/// Provides atomic operations for heartbeat updates, message completion tracking,
/// and orphaned work recovery.
/// </summary>
public interface IWorkCoordinator {
  /// <summary>
  /// Processes a batch of work in a single atomic operation:
  /// - Registers/updates instance with heartbeat
  /// - Cleans up stale instances (expired heartbeats)
  /// - Stores new outbox messages (immediate processing)
  /// - Stores new inbox messages (deduplication + event store)
  /// - Reports completions with granular status tracking
  /// - Reports failures with partial completion tracking
  /// - Claims partitions and returns work for this instance
  ///
  /// This minimizes database round-trips and ensures consistency.
  /// </summary>
  /// <param name="instanceId">Service instance ID</param>
  /// <param name="serviceName">Service name (e.g., 'InventoryWorker')</param>
  /// <param name="hostName">Host machine name</param>
  /// <param name="processId">Operating system process ID</param>
  /// <param name="metadata">Optional instance metadata (e.g., version, environment)</param>
  /// <param name="outboxCompletions">Outbox message completions with granular status</param>
  /// <param name="outboxFailures">Outbox message failures with partial completion tracking</param>
  /// <param name="inboxCompletions">Inbox message completions with granular status</param>
  /// <param name="inboxFailures">Inbox message failures with partial completion tracking</param>
  /// <param name="newOutboxMessages">New outbox messages to store (for immediate processing)</param>
  /// <param name="newInboxMessages">New inbox messages to store (with deduplication)</param>
  /// <param name="renewOutboxLeaseIds">Message IDs to renew lease for (outbox) - for buffered messages awaiting transport</param>
  /// <param name="renewInboxLeaseIds">Message IDs to renew lease for (inbox) - for buffered messages awaiting processing</param>
  /// <param name="flags">Work batch flags (e.g., DebugMode to preserve completed messages)</param>
  /// <param name="partitionCount">Total number of partitions (default 10,000)</param>
  /// <param name="maxPartitionsPerInstance">Max partitions per instance (default 100)</param>
  /// <param name="leaseSeconds">Lease duration in seconds (default 300 = 5 minutes)</param>
  /// <param name="staleThresholdSeconds">Stale instance threshold in seconds (default 600 = 10 minutes)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Work batch containing messages that need processing (including newly stored messages for immediate processing)</returns>
  Task<WorkBatch> ProcessWorkBatchAsync(
    Guid instanceId,
    string serviceName,
    string hostName,
    int processId,
    Dictionary<string, object>? metadata,
    MessageCompletion[] outboxCompletions,
    MessageFailure[] outboxFailures,
    MessageCompletion[] inboxCompletions,
    MessageFailure[] inboxFailures,
    NewOutboxMessage[] newOutboxMessages,
    NewInboxMessage[] newInboxMessages,
    Guid[] renewOutboxLeaseIds,
    Guid[] renewInboxLeaseIds,
    WorkBatchFlags flags = WorkBatchFlags.None,
    int partitionCount = 10_000,
    int maxPartitionsPerInstance = 100,
    int leaseSeconds = 300,
    int staleThresholdSeconds = 600,
    CancellationToken cancellationToken = default
  );
}

/// <summary>
/// Result of ProcessWorkBatchAsync containing work that needs processing.
/// </summary>
public record WorkBatch {
  /// <summary>
  /// Outbox work to publish (includes both new pending messages and orphaned messages with expired leases).
  /// </summary>
  public required List<OutboxWork> OutboxWork { get; init; }

  /// <summary>
  /// Inbox work to process (includes both new pending messages and orphaned messages with expired leases).
  /// From the application's perspective, these are the next messages to handle.
  /// </summary>
  public required List<InboxWork> InboxWork { get; init; }
}

/// <summary>
/// Represents a new outbox message to be stored in process_work_batch.
/// Used for immediate processing pattern (store + immediately return for publishing).
/// </summary>
public record NewOutboxMessage {
  /// <summary>
  /// Unique message ID (should be UUIDv7 for time-ordered, database-friendly IDs).
  /// </summary>
  public required Guid MessageId { get; init; }

  /// <summary>
  /// Destination to publish to (topic name).
  /// </summary>
  public required string Destination { get; init; }

  /// <summary>
  /// Fully-qualified message type name.
  /// </summary>
  public required string EventType { get; init; }

  /// <summary>
  /// Message payload as JSON string.
  /// </summary>
  public required string EventData { get; init; }

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
  /// Stream ID for ordering (aggregate ID or message ID).
  /// Events from the same stream must be processed in order.
  /// </summary>
  public Guid? StreamId { get; init; }

  /// <summary>
  /// Whether this message is an event (implements IEvent).
  /// If true and stream_id is not null, it will be persisted to the event store.
  /// </summary>
  public bool IsEvent { get; init; }
}

/// <summary>
/// Represents a new inbox message to be stored in process_work_batch.
/// Includes atomic deduplication (ON CONFLICT DO NOTHING) and optional event store integration.
/// </summary>
public record NewInboxMessage {
  /// <summary>
  /// Unique message ID (should be UUIDv7 for time-ordered, database-friendly IDs).
  /// </summary>
  public required Guid MessageId { get; init; }

  /// <summary>
  /// Handler name (e.g., "ServiceBusConsumer").
  /// </summary>
  public required string HandlerName { get; init; }

  /// <summary>
  /// Fully-qualified message type name.
  /// </summary>
  public required string EventType { get; init; }

  /// <summary>
  /// Message payload as JSON string.
  /// </summary>
  public required string EventData { get; init; }

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
  /// Stream ID for ordering (aggregate ID or message ID).
  /// Events from the same stream must be processed in order.
  /// </summary>
  public Guid? StreamId { get; init; }

  /// <summary>
  /// Whether this message is an event (implements IEvent).
  /// If true and stream_id is not null, it will be persisted to the event store.
  /// </summary>
  public bool IsEvent { get; init; }
}

/// <summary>
/// Represents a message completion with granular status tracking.
/// Indicates which processing stages completed successfully.
/// </summary>
public record MessageCompletion {
  /// <summary>
  /// Message ID that completed.
  /// </summary>
  public required Guid MessageId { get; init; }

  /// <summary>
  /// Which stages of processing completed successfully.
  /// Use bitwise OR to combine multiple stages (e.g., ReceptorProcessed | PerspectiveProcessed).
  /// </summary>
  public required MessageProcessingStatus Status { get; init; }
}

/// <summary>
/// Represents a message failure with partial completion tracking.
/// Indicates which stages succeeded before the failure occurred.
/// </summary>
public record MessageFailure {
  /// <summary>
  /// Message ID that failed.
  /// </summary>
  public required Guid MessageId { get; init; }

  /// <summary>
  /// Which stages of processing completed successfully before failure.
  /// For example: (Stored | EventStored) indicates storage succeeded but next stage failed.
  /// </summary>
  public required MessageProcessingStatus CompletedStatus { get; init; }

  /// <summary>
  /// Error message or exception details.
  /// </summary>
  public required string Error { get; init; }

  /// <summary>
  /// Classified reason for the failure.
  /// Enables typed filtering and handling of different failure scenarios.
  /// Defaults to Unknown if not specified.
  /// </summary>
  public MessageFailureReason Reason { get; init; } = MessageFailureReason.Unknown;
}

/// <summary>
/// Legacy: Represents a failed message with error information.
/// Deprecated - use MessageFailure instead for better status tracking.
/// </summary>
[Obsolete("Use MessageFailure instead for granular status tracking")]
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
/// Represents outbox work that needs to be published.
/// Includes both new pending messages and messages with expired leases (orphaned).
/// </summary>
public record OutboxWork {
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
  /// Stream ID for ordering (aggregate ID or message ID).
  /// Events from the same stream must be processed in order.
  /// </summary>
  public Guid? StreamId { get; init; }

  /// <summary>
  /// Partition number (computed from stream_id via consistent hashing).
  /// Used for load distribution and ensuring same stream goes to same instance.
  /// </summary>
  public int? PartitionNumber { get; init; }

  /// <summary>
  /// Number of previous publishing attempts.
  /// </summary>
  public required int Attempts { get; init; }

  /// <summary>
  /// Current processing status flags.
  /// Indicates which stages have been completed (e.g., Stored, EventStored, Published).
  /// </summary>
  public MessageProcessingStatus Status { get; init; }

  /// <summary>
  /// Work batch flags indicating metadata about this work item.
  /// Examples: NewlyStored, Orphaned, FromEventStore, RetryAfterFailure.
  /// </summary>
  public WorkBatchFlags Flags { get; init; }

  /// <summary>
  /// Sequence order for maintaining ordering within a stream.
  /// Epoch milliseconds from created_at timestamp.
  /// </summary>
  public long SequenceOrder { get; init; }
}

/// <summary>
/// Represents inbox work that needs to be processed.
/// Includes both new pending messages and messages with expired leases (orphaned).
/// From the application's perspective, these are the next messages to handle.
/// </summary>
public record InboxWork {
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

  /// <summary>
  /// Stream ID for ordering (aggregate ID or message ID).
  /// Events from the same stream must be processed in order.
  /// </summary>
  public Guid? StreamId { get; init; }

  /// <summary>
  /// Partition number (computed from stream_id via consistent hashing).
  /// Used for load distribution and ensuring same stream goes to same instance.
  /// </summary>
  public int? PartitionNumber { get; init; }

  /// <summary>
  /// Current processing status flags.
  /// Indicates which stages have been completed (e.g., Stored, EventStored, ReceptorProcessed).
  /// </summary>
  public MessageProcessingStatus Status { get; init; }

  /// <summary>
  /// Work batch flags indicating metadata about this work item.
  /// Examples: NewlyStored, Orphaned, FromEventStore, RetryAfterFailure.
  /// </summary>
  public WorkBatchFlags Flags { get; init; }

  /// <summary>
  /// Sequence order for maintaining ordering within a stream.
  /// Epoch milliseconds from received_at timestamp.
  /// </summary>
  public long SequenceOrder { get; init; }
}
