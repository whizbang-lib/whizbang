namespace Whizbang.Core.Notifications;

/// <summary>
/// Categorizes the work signal carried by a notification. The producer (a SQL function
/// committing new work) emits one of these per category that received new rows; the
/// listener routes each to the appropriate worker's wake semaphore.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public enum WorkSignalCategory {
  /// <summary>New outbox messages were stored.</summary>
  Outbox,
  /// <summary>New inbox messages were stored.</summary>
  Inbox,
  /// <summary>New perspective_event work items were created.</summary>
  Perspective,
  /// <summary>
  /// Orphan redistribution — a previously-leased work item became unowned (typically
  /// because <c>cleanup_stale_instances</c> released the leases of a dead pod). The live
  /// claim worker should run a catch-up <c>claim_orphaned_*</c> across all work tables.
  /// </summary>
  OrphanRedistribute,
  /// <summary>
  /// A new <c>wh_dead_letters</c> row was inserted. <c>DeadLetterRecoveryWorker</c> wakes
  /// to scan for rows whose recovery policy has elapsed (slice 7c — added in v0.681).
  /// </summary>
  DeadLetterReady,
}

/// <summary>
/// Subscribes to work signals from the database and surfaces them to the C# layer.
/// Phase D of work-pump decomposition.
/// </summary>
/// <remarks>
/// Implementations open a long-lived direct connection (one per pod) that bypasses
/// pgbouncer (LISTEN doesn't survive transaction-pooling). When notifications arrive,
/// fire <see cref="OnSignal"/>; subscribers (typically <c>ClaimWorker.RequestImmediatePoll</c>)
/// wake immediately. <see cref="IsHealthy"/> reflects connection state — when unhealthy,
/// claim workers should fall back to fast polling cadence.
///
/// A NoOp implementation is bound when no <c>DirectConnectionString</c> is configured;
/// the system runs polling-only in that mode.
/// </remarks>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public interface IWorkNotificationListener {
  /// <summary>True when the listener has an active connection and recent keepalive.</summary>
  bool IsHealthy { get; }

  /// <summary>UTC timestamp of the most recently received notification (or null if none yet).</summary>
  DateTimeOffset? LastSignalAt { get; }

  /// <summary>Fired when a work signal arrives. Carries the category that received new rows.</summary>
  event Action<WorkSignalCategory>? OnSignal;

  /// <summary>Fired when <see cref="IsHealthy"/> changes; arg is the new state.</summary>
  event Action<bool>? OnHealthChanged;
}

/// <summary>
/// No-op implementation bound when no <c>DirectConnectionString</c> is configured.
/// Always reports unhealthy so claim workers stay on aggressive polling cadence.
/// </summary>
public sealed class NoOpWorkNotificationListener : IWorkNotificationListener {
  /// <inheritdoc />
  public bool IsHealthy => false;
  /// <inheritdoc />
  public DateTimeOffset? LastSignalAt => null;
  /// <inheritdoc />
  public event Action<WorkSignalCategory>? OnSignal { add { } remove { } }
  /// <inheritdoc />
  public event Action<bool>? OnHealthChanged { add { } remove { } }
}
