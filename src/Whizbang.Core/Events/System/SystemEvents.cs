using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Events.System;

// --- Perspective rebuild events ---

/// <summary>
/// Emitted when a perspective rebuild starts (any mode).
/// </summary>
/// <param name="StreamId">The unique identifier for this rebuild stream.</param>
/// <param name="PerspectiveName">Name of the perspective being rebuilt.</param>
/// <param name="Mode">The rebuild strategy being used.</param>
/// <param name="TotalStreams">Total number of event streams to process.</param>
/// <param name="StartedAt">When the rebuild operation started.</param>
/// <docs>fundamentals/perspectives/perspectives#rebuild-events</docs>
public record PerspectiveRebuildStarted(
    [property: StreamId] Guid StreamId,
    string PerspectiveName,
    RebuildMode Mode,
    int TotalStreams,
    DateTimeOffset StartedAt
) : IEvent;

/// <summary>
/// Emitted periodically during a rebuild to report progress.
/// </summary>
/// <param name="StreamId">The unique identifier for this rebuild stream.</param>
/// <param name="PerspectiveName">Name of the perspective being rebuilt.</param>
/// <param name="Mode">The rebuild strategy being used.</param>
/// <param name="ProcessedStreams">Number of streams processed so far.</param>
/// <param name="TotalStreams">Total number of event streams to process.</param>
/// <param name="EventsReplayed">Total events replayed so far.</param>
/// <param name="StartedAt">When the rebuild operation started.</param>
/// <docs>fundamentals/perspectives/perspectives#rebuild-events</docs>
public record PerspectiveRebuildProgress(
    [property: StreamId] Guid StreamId,
    string PerspectiveName,
    RebuildMode Mode,
    int ProcessedStreams,
    int TotalStreams,
    int EventsReplayed,
    DateTimeOffset StartedAt
) : IEvent;

/// <summary>
/// Emitted when a perspective rebuild completes successfully.
/// </summary>
/// <param name="StreamId">The unique identifier for this rebuild stream.</param>
/// <param name="PerspectiveName">Name of the perspective that was rebuilt.</param>
/// <param name="Mode">The rebuild strategy that was used.</param>
/// <param name="StreamsProcessed">Total number of streams processed.</param>
/// <param name="EventsReplayed">Total number of events replayed.</param>
/// <param name="Duration">Wall-clock time for the rebuild.</param>
/// <docs>fundamentals/perspectives/perspectives#rebuild-events</docs>
public record PerspectiveRebuildCompleted(
    [property: StreamId] Guid StreamId,
    string PerspectiveName,
    RebuildMode Mode,
    int StreamsProcessed,
    int EventsReplayed,
    TimeSpan Duration
) : IEvent;

/// <summary>
/// Emitted when a perspective rebuild fails.
/// </summary>
/// <param name="StreamId">The unique identifier for this rebuild stream.</param>
/// <param name="PerspectiveName">Name of the perspective that failed to rebuild.</param>
/// <param name="Mode">The rebuild strategy that was used.</param>
/// <param name="Error">Error message describing the failure.</param>
/// <param name="StreamsProcessedBeforeFailure">Number of streams successfully processed before the failure.</param>
/// <param name="Duration">Wall-clock time before the failure occurred.</param>
/// <docs>fundamentals/perspectives/perspectives#rebuild-events</docs>
public record PerspectiveRebuildFailed(
    [property: StreamId] Guid StreamId,
    string PerspectiveName,
    RebuildMode Mode,
    string Error,
    int StreamsProcessedBeforeFailure,
    TimeSpan Duration
) : IEvent;

// --- Perspective rewind events ---

/// <summary>
/// Emitted when a perspective rewind begins due to a late-arriving event.
/// </summary>
/// <param name="StreamId">The unique identifier for this rewind stream.</param>
/// <param name="PerspectiveName">Name of the perspective being rewound.</param>
/// <param name="TriggeringEventId">The late-arriving event that triggered the rewind.</param>
/// <param name="ReplayFromSnapshotEventId">Event ID of the snapshot to replay from, if available.</param>
/// <param name="HasSnapshot">Whether a snapshot was available for the rewind.</param>
/// <param name="StartedAt">When the rewind operation started.</param>
/// <docs>fundamentals/perspectives/perspectives#rewind-events</docs>
[AuditEvent(Exclude = true, Reason = "Infrastructure event — no ambient security context during background rewind")]
public record PerspectiveRewindStarted(
    [property: StreamId] Guid StreamId,
    string PerspectiveName,
    Guid TriggeringEventId,
    Guid? ReplayFromSnapshotEventId,
    bool HasSnapshot,
    DateTimeOffset StartedAt
) : IEvent;

/// <summary>
/// Emitted when a perspective rewind completes successfully.
/// </summary>
/// <param name="StreamId">The unique identifier for this rewind stream.</param>
/// <param name="PerspectiveName">Name of the perspective that was rewound.</param>
/// <param name="TriggeringEventId">The late-arriving event that triggered the rewind.</param>
/// <param name="FinalEventId">The last event ID after replay completed.</param>
/// <param name="EventsReplayed">Total number of events replayed during the rewind.</param>
/// <param name="StartedAt">When the rewind operation started.</param>
/// <param name="CompletedAt">When the rewind operation completed.</param>
/// <docs>fundamentals/perspectives/perspectives#rewind-events</docs>
[AuditEvent(Exclude = true, Reason = "Infrastructure event — no ambient security context during background rewind")]
public record PerspectiveRewindCompleted(
    [property: StreamId] Guid StreamId,
    string PerspectiveName,
    Guid TriggeringEventId,
    Guid FinalEventId,
    int EventsReplayed,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt
) : IEvent;

/// <summary>
/// Emitted once per stream before any per-perspective rewinds begin.
/// Brackets all <see cref="PerspectiveRewindStarted"/> events for the same stream.
/// </summary>
/// <param name="StreamId">The stream requiring rewind.</param>
/// <param name="PerspectiveNames">All perspectives that need rewind on this stream.</param>
/// <param name="TriggerEventId">The late-arriving event that triggered the rewind.</param>
/// <param name="StartedAt">When the stream-level rewind operation started.</param>
/// <docs>fundamentals/perspectives/rewind#stream-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Events/System/StreamRewindEventTests.cs</tests>
[AuditEvent(Exclude = true, Reason = "Infrastructure event — no ambient security context during background rewind")]
public record StreamRewindStarted(
    [property: StreamId] Guid StreamId,
    string[] PerspectiveNames,
    Guid TriggerEventId,
    DateTimeOffset StartedAt
) : IEvent;

/// <summary>
/// Emitted once per stream after all per-perspective rewinds complete.
/// Brackets all <see cref="PerspectiveRewindCompleted"/> events for the same stream.
/// </summary>
/// <param name="StreamId">The stream that was rewound.</param>
/// <param name="PerspectiveNames">All perspectives that were rewound on this stream.</param>
/// <param name="TotalEventsReplayed">Aggregate count of events replayed across all perspectives.</param>
/// <param name="StartedAt">When the stream-level rewind operation started.</param>
/// <param name="CompletedAt">When all perspective rewinds completed.</param>
/// <docs>fundamentals/perspectives/rewind#stream-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Events/System/StreamRewindEventTests.cs</tests>
[AuditEvent(Exclude = true, Reason = "Infrastructure event — no ambient security context during background rewind")]
public record StreamRewindCompleted(
    [property: StreamId] Guid StreamId,
    string[] PerspectiveNames,
    int TotalEventsReplayed,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt
) : IEvent;

// --- Per-migration events (one per table/function) ---

/// <summary>
/// Emitted when an individual migration starts processing.
/// </summary>
/// <param name="StreamId">The unique identifier for this migration stream.</param>
/// <param name="MigrationKey">Key identifying the migration item (e.g., table or function name).</param>
/// <param name="Strategy">The strategy used to execute this migration.</param>
/// <param name="OldHash">Hash of the previous migration definition, or null if new.</param>
/// <param name="NewHash">Hash of the new migration definition.</param>
/// <docs>operations/infrastructure/migrations#migration-events</docs>
public record MigrationItemStarted(
    [property: StreamId] Guid StreamId,
    string MigrationKey,
    MigrationStrategy Strategy,
    string? OldHash,
    string NewHash
) : IEvent;

/// <summary>
/// Emitted when an individual migration completes.
/// </summary>
/// <param name="StreamId">The unique identifier for this migration stream.</param>
/// <param name="MigrationKey">Key identifying the migration item.</param>
/// <param name="Status">The outcome status of the migration.</param>
/// <param name="StatusDescription">Human-readable description of the status.</param>
/// <param name="Duration">Wall-clock time for this migration item.</param>
/// <docs>operations/infrastructure/migrations#migration-events</docs>
public record MigrationItemCompleted(
    [property: StreamId] Guid StreamId,
    string MigrationKey,
    MigrationStatus Status,
    string StatusDescription,
    TimeSpan Duration
) : IEvent;

/// <summary>
/// Emitted when an individual migration fails.
/// </summary>
/// <param name="StreamId">The unique identifier for this migration stream.</param>
/// <param name="MigrationKey">Key identifying the migration item that failed.</param>
/// <param name="Status">The failure status of the migration.</param>
/// <param name="FailureReason">Categorized reason for the failure.</param>
/// <param name="Error">Error message describing the failure.</param>
/// <param name="Duration">Wall-clock time before the failure occurred.</param>
/// <docs>operations/infrastructure/migrations#migration-events</docs>
public record MigrationItemFailed(
    [property: StreamId] Guid StreamId,
    string MigrationKey,
    MigrationStatus Status,
    MigrationFailureReason FailureReason,
    string Error,
    TimeSpan Duration
) : IEvent;

// --- Overall migration batch events ---

/// <summary>
/// Emitted when the full migration batch starts (all infrastructure + perspectives).
/// </summary>
/// <param name="StreamId">The unique identifier for this migration batch stream.</param>
/// <param name="LibraryVersion">The Whizbang library version running the migrations.</param>
/// <param name="TotalMigrations">Total number of migration items in the batch.</param>
/// <param name="TotalPerspectives">Total number of perspectives included in the batch.</param>
/// <docs>operations/infrastructure/migrations#migration-events</docs>
public record MigrationBatchStarted(
    [property: StreamId] Guid StreamId,
    string LibraryVersion,
    int TotalMigrations,
    int TotalPerspectives
) : IEvent;

/// <summary>
/// Emitted when the full migration batch completes. Includes per-item results.
/// </summary>
/// <param name="StreamId">The unique identifier for this migration batch stream.</param>
/// <param name="LibraryVersion">The Whizbang library version that ran the migrations.</param>
/// <param name="Results">Per-item results for every migration in the batch.</param>
/// <param name="Applied">Number of migrations that were newly applied.</param>
/// <param name="Updated">Number of migrations that were updated (hash changed).</param>
/// <param name="Skipped">Number of migrations that were skipped (already current).</param>
/// <param name="Failed">Number of migrations that failed.</param>
/// <param name="TotalDuration">Wall-clock time for the entire batch.</param>
/// <docs>operations/infrastructure/migrations#migration-events</docs>
public record MigrationBatchCompleted(
    [property: StreamId] Guid StreamId,
    string LibraryVersion,
    MigrationBatchItemResult[] Results,
    int Applied,
    int Updated,
    int Skipped,
    int Failed,
    TimeSpan TotalDuration
) : IEvent;

/// <summary>
/// Result for a single item within a migration batch.
/// </summary>
/// <param name="MigrationKey">Key identifying the migration item.</param>
/// <param name="Status">The outcome status of the migration item.</param>
/// <param name="StatusDescription">Human-readable description of the status.</param>
public record MigrationBatchItemResult(
    string MigrationKey,
    MigrationStatus Status,
    string StatusDescription);

// --- Enums ---

/// <summary>
/// Status of a migration item in wh_schema_migrations.
/// </summary>
public enum MigrationStatus {
  /// <summary>Migration was newly applied.</summary>
  Applied = 1,
  /// <summary>Migration was updated (definition hash changed).</summary>
  Updated = 2,
  /// <summary>Migration was skipped (already current).</summary>
  Skipped = 3,
  /// <summary>Migration is being applied in the background.</summary>
  MigratingInBackground = 4,
  /// <summary>Migration failed to apply.</summary>
  Failed = -1
}

/// <summary>
/// Strategy used for executing a migration.
/// </summary>
public enum MigrationStrategy {
  /// <summary>Executes DDL statements directly.</summary>
  DirectDdl,
  /// <summary>Copies data to a new column, then swaps.</summary>
  ColumnCopy,
  /// <summary>Replays events to rebuild the target.</summary>
  EventReplay
}

/// <summary>
/// Reason a migration failed.
/// </summary>
public enum MigrationFailureReason {
  /// <summary>Failure reason could not be determined.</summary>
  Unknown = 0,
  /// <summary>A SQL error occurred during migration execution.</summary>
  SqlError = 1,
  /// <summary>The migration exceeded its allowed execution time.</summary>
  Timeout = 2,
  /// <summary>Source and target column types are incompatible.</summary>
  ColumnTypeMismatch = 3,
  /// <summary>Data copy between columns failed.</summary>
  DataCopyFailed = 4,
  /// <summary>Column swap after data copy failed.</summary>
  SwapFailed = 5
}
