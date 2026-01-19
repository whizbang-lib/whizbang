namespace Whizbang.Core.Lenses;

/// <summary>
/// Event metadata stored in perspective rows.
/// Contains information about the event that created/updated this perspective.
/// Stored as JSONB/JSON in metadata column.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_StoresDefaultMetadataAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs</tests>
public record PerspectiveMetadata {
  /// <summary>
  /// Fully qualified event type name (e.g., "ECommerce.Contracts.Events.OrderCreatedEvent").
  /// Used to filter perspectives by event source.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_StoresDefaultMetadataAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs</tests>
  public required string EventType { get; init; }

  /// <summary>
  /// Unique identifier for the event.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_StoresDefaultMetadataAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs</tests>
  public required string EventId { get; init; }

  /// <summary>
  /// When the event occurred.
  /// Useful for time-range queries (e.g., orders created in last 30 days).
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_StoresDefaultMetadataAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs</tests>
  public required DateTime Timestamp { get; init; }

  /// <summary>
  /// Correlation ID for distributed tracing.
  /// Links related events across service boundaries.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs</tests>
  public string? CorrelationId { get; init; }

  /// <summary>
  /// Causation ID (the event that caused this event).
  /// Builds event causality chains.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs</tests>
  public string? CausationId { get; init; }
}
