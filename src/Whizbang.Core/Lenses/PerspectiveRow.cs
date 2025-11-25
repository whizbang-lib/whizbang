namespace Whizbang.Core.Lenses;

/// <summary>
/// Complete perspective row including model data, metadata, scope, and system fields.
/// Represents the full structure stored in perspective tables.
/// Used in LINQ queries to access any column (data, metadata, scope, created_at, etc.).
/// </summary>
/// <typeparam name="TModel">The read model type</typeparam>
public class PerspectiveRow<TModel> where TModel : class {
  /// <summary>
  /// Unique identifier for this perspective row.
  /// Typically matches the aggregate root ID (e.g., OrderId, CustomerId).
  /// </summary>
  public required string Id { get; init; }

  /// <summary>
  /// The denormalized read model data.
  /// Stored as JSONB in PostgreSQL, JSON in SQL Server.
  /// Contains all queryable business data.
  /// </summary>
  public required TModel Data { get; init; }

  /// <summary>
  /// Event metadata (event type, correlation, causation, timestamp).
  /// Stored as JSONB/JSON.
  /// Useful for filtering by event source or time range.
  /// </summary>
  public required PerspectiveMetadata Metadata { get; init; }

  /// <summary>
  /// Multi-tenancy and security scope (tenant ID, user ID, org ID).
  /// Stored as JSONB/JSON.
  /// Enables efficient tenant isolation queries.
  /// </summary>
  public required PerspectiveScope Scope { get; init; }

  /// <summary>
  /// When this row was first created.
  /// </summary>
  public required DateTime CreatedAt { get; init; }

  /// <summary>
  /// When this row was last updated.
  /// </summary>
  public required DateTime UpdatedAt { get; init; }

  /// <summary>
  /// Optimistic concurrency version number.
  /// Increments on each update.
  /// </summary>
  public required int Version { get; init; }
}
