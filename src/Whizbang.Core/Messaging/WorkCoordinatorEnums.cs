namespace Whizbang.Core.Messaging;

/// <summary>
/// Flags indicating metadata about work items returned from ProcessWorkBatchAsync.
/// Multiple flags can be combined using bitwise OR.
/// </summary>
[Flags]
public enum WorkBatchFlags {
  /// <summary>
  /// No special flags.
  /// </summary>
  None = 0,

  /// <summary>
  /// This work item was just stored in this call (new message).
  /// Indicates immediate processing path vs. orphaned work recovery.
  /// </summary>
  NewlyStored = 1 << 0,

  /// <summary>
  /// This work item was claimed from another failed instance (orphaned work).
  /// Indicates lease expired and work was reassigned.
  /// </summary>
  Orphaned = 1 << 1,

  /// <summary>
  /// Debug mode enabled - completed messages will be kept instead of deleted.
  /// Useful for development and troubleshooting.
  /// </summary>
  DebugMode = 1 << 2,

  /// <summary>
  /// This message was also written to the event store.
  /// Only applicable for events (implements IEvent).
  /// </summary>
  FromEventStore = 1 << 3,

  /// <summary>
  /// High priority message - should be processed before normal messages.
  /// Future enhancement for priority queuing.
  /// </summary>
  HighPriority = 1 << 4,

  /// <summary>
  /// This message is being retried after a previous failure.
  /// Indicates retry path vs. first-time processing.
  /// </summary>
  RetryAfterFailure = 1 << 5

  // Bits 6-31 reserved for future use
}

/// <summary>
/// Flags tracking which stages of message processing have been completed.
/// Multiple flags can be combined using bitwise OR to track partial completion.
/// This enables retrying only failed stages instead of the entire pipeline.
/// </summary>
[Flags]
public enum MessageProcessingStatus {
  /// <summary>
  /// No processing stages completed.
  /// </summary>
  None = 0,

  /// <summary>
  /// Message has been persisted to inbox/outbox table.
  /// First stage of any message processing.
  /// </summary>
  Stored = 1 << 0,

  /// <summary>
  /// Event has been written to the event store (events only).
  /// Occurs during outbox storage (before publishing) or inbox storage (on receipt).
  /// </summary>
  EventStored = 1 << 1,

  /// <summary>
  /// Message has been successfully published to transport (outbox only).
  /// Indicates message left this service via Service Bus/transport.
  /// </summary>
  Published = 1 << 2,

  /// <summary>
  /// Receptor/handler has processed the message successfully.
  /// Indicates business logic execution completed.
  /// </summary>
  ReceptorProcessed = 1 << 3,

  /// <summary>
  /// All perspectives have been updated successfully.
  /// Indicates read model projections completed.
  /// </summary>
  PerspectiveProcessed = 1 << 4,

  /// <summary>
  /// Pre-perspective receptors executed successfully (future enhancement).
  /// Allows custom logic before perspective updates.
  /// </summary>
  PrePerspectiveProcessed = 1 << 5,

  /// <summary>
  /// Post-perspective receptors executed successfully (future enhancement).
  /// Allows custom logic after perspective updates (e.g., notifications).
  /// </summary>
  PostPerspectiveProcessed = 1 << 6,

  // Bits 7-14 reserved for future pipeline stages

  /// <summary>
  /// Message processing failed at some stage.
  /// Combined with other flags to indicate which stages succeeded before failure.
  /// For example: (Stored | EventStored | Failed) means storage succeeded but publishing failed.
  /// </summary>
  Failed = 1 << 15,

  /// <summary>
  /// Composite status: Both receptor and perspective processing completed.
  /// Message is fully processed and can be removed from inbox/outbox.
  /// </summary>
  FullyCompleted = ReceptorProcessed | PerspectiveProcessed  // 24 (0x18)
}
