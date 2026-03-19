using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Events.System;

// --- Perspective rebuild events ---

/// <summary>
/// Emitted when a perspective rebuild starts (any mode).
/// </summary>
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
/// <docs>fundamentals/perspectives/perspectives#rewind-events</docs>
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
/// <docs>fundamentals/perspectives/perspectives#rewind-events</docs>
public record PerspectiveRewindCompleted(
    [property: StreamId] Guid StreamId,
    string PerspectiveName,
    Guid TriggeringEventId,
    Guid FinalEventId,
    int EventsReplayed,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt
) : IEvent;

// --- Per-migration events (one per table/function) ---

/// <summary>
/// Emitted when an individual migration starts processing.
/// </summary>
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
public record MigrationBatchItemResult(
    string MigrationKey,
    MigrationStatus Status,
    string StatusDescription);

// --- Enums ---

/// <summary>
/// Status of a migration item in wh_schema_migrations.
/// </summary>
public enum MigrationStatus {
  Applied = 1,
  Updated = 2,
  Skipped = 3,
  MigratingInBackground = 4,
  Failed = -1
}

/// <summary>
/// Strategy used for executing a migration.
/// </summary>
public enum MigrationStrategy {
  DirectDdl,
  ColumnCopy,
  EventReplay
}

/// <summary>
/// Reason a migration failed.
/// </summary>
public enum MigrationFailureReason {
  Unknown = 0,
  SqlError = 1,
  Timeout = 2,
  ColumnTypeMismatch = 3,
  DataCopyFailed = 4,
  SwapFailed = 5
}
