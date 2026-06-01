namespace Whizbang.Core.Lenses;

/// <summary>
/// Event metadata stored in perspective rows.
/// Contains information about the event that created/updated this perspective.
/// Stored as JSONB/JSON in metadata column.
/// </summary>
/// <remarks>
/// <para>
/// <strong>EF Core 10 Compatibility:</strong>
/// This type is a <c>class</c> (not <c>record</c>) with default values to enable
/// <c>ComplexProperty().ToJson()</c> mapping. Records have generated copy-constructors
/// that can cause NullReferenceException in EF Core query materialization.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_StoresDefaultMetadataAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs</tests>
public class PerspectiveMetadata {
  /// <summary>
  /// Parameterless constructor for EF Core ComplexProperty materialization.
  /// </summary>
  public PerspectiveMetadata() { }

  /// <summary>
  /// Fully qualified event type name (e.g., "ECommerce.Contracts.Events.OrderCreatedEvent").
  /// Used to filter perspectives by event source.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_StoresDefaultMetadataAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs</tests>
  public string EventType { get; set; } = string.Empty;

  /// <summary>
  /// Unique identifier for the event.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_StoresDefaultMetadataAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs</tests>
  public string EventId { get; set; } = string.Empty;

  /// <summary>
  /// When the event occurred.
  /// Useful for time-range queries (e.g., orders created in last 30 days).
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_StoresDefaultMetadataAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs</tests>
  public DateTime Timestamp { get; set; }

  /// <summary>
  /// Correlation ID for distributed tracing.
  /// Links related events across service boundaries.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs</tests>
  public string? CorrelationId { get; set; }

  /// <summary>
  /// Causation ID (the event that caused this event).
  /// Builds event causality chains.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs</tests>
  public string? CausationId { get; set; }

  /// <summary>
  /// <c>wh_event_store.commit_sequence</c> of the last applied event. Monotonic per database,
  /// stamped at commit completion (slice 26). When both this and an incoming event's local
  /// commit_sequence are present, the runner's idempotency filter compares commit_sequence
  /// instead of event_id — needed because UUIDv7 event_ids can invert under concurrent
  /// emission while commit_sequence cannot.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WithMetadata_PersistsCommitSequenceForOrderedIdempotencyAsync</tests>
  public long? CommitSequence { get; set; }
}
