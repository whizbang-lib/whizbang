namespace Whizbang.Core.Lenses;

/// <summary>
/// Complete perspective row including model data, metadata, scope, and system fields.
/// Represents the full structure stored in perspective tables.
/// Used in LINQ queries to access any column (data, metadata, scope, created_at, etc.).
/// </summary>
/// <typeparam name="TModel">The read model type</typeparam>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_WithOrderCreatedEvent_SavesOrderModelAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_StoresDefaultMetadataAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_StoresDefaultScopeAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_SetsTimestampsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_MultipleEvents_IncrementsVersionAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:GetByIdAsync_WhenModelExists_ReturnsModelAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_ReturnsIQueryable_WithCorrectTypeAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByDataFields_ReturnsMatchingRowsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByMetadataFields_ReturnsMatchingRowsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByScopeFields_ReturnsMatchingRowsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanProjectAcrossColumns_ReturnsAnonymousTypeAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_SupportsCombinedFilters_FromAllColumnsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_SupportsComplexLinqOperations_WithOrderByAndSkipTakeAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaDefinitionTests.cs:PerspectiveTable_ShouldHaveCorrectSchemaAsync</tests>
public class PerspectiveRow<TModel> where TModel : class {
  /// <summary>
  /// Unique identifier for this perspective row.
  /// Typically matches the aggregate root ID (e.g., OrderId, CustomerId).
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_WithOrderCreatedEvent_SavesOrderModelAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:GetByIdAsync_WhenModelExists_ReturnsModelAsync</tests>
  public required Guid Id { get; init; }

  /// <summary>
  /// The denormalized read model data.
  /// Stored as JSONB in PostgreSQL, JSON in SQL Server.
  /// Contains all queryable business data.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_WithOrderCreatedEvent_SavesOrderModelAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_MultipleEvents_IncrementsVersionAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:GetByIdAsync_WhenModelExists_ReturnsModelAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByDataFields_ReturnsMatchingRowsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanProjectAcrossColumns_ReturnsAnonymousTypeAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_SupportsCombinedFilters_FromAllColumnsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_SupportsComplexLinqOperations_WithOrderByAndSkipTakeAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaDefinitionTests.cs:PerspectiveTable_ShouldHaveCorrectSchemaAsync</tests>
  public required TModel Data { get; init; }

  /// <summary>
  /// Event metadata (event type, correlation, causation, timestamp).
  /// Stored as JSONB/JSON.
  /// Useful for filtering by event source or time range.
  /// </summary>
  /// <remarks>
  /// Uses <c>set</c> accessor (not <c>init</c>) for EF Core ComplexProperty materialization compatibility.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_StoresDefaultMetadataAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByMetadataFields_ReturnsMatchingRowsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanProjectAcrossColumns_ReturnsAnonymousTypeAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_SupportsCombinedFilters_FromAllColumnsAsync</tests>
  public required PerspectiveMetadata Metadata { get; set; }

  /// <summary>
  /// Multi-tenancy and security scope (tenant ID, user ID, org ID).
  /// Stored as JSONB/JSON.
  /// Enables efficient tenant isolation queries.
  /// </summary>
  /// <remarks>
  /// Uses <c>set</c> accessor (not <c>init</c>) for EF Core OwnsOne/ComplexProperty materialization compatibility.
  /// The <c>required</c> keyword ensures the property is set during initialization while <c>set</c> allows
  /// EF Core to populate the instance during query materialization.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_StoresDefaultScopeAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByScopeFields_ReturnsMatchingRowsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanProjectAcrossColumns_ReturnsAnonymousTypeAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_SupportsCombinedFilters_FromAllColumnsAsync</tests>
  public required PerspectiveScope Scope { get; set; }

  /// <summary>
  /// When this row was first created.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_SetsTimestampsAsync</tests>
  public required DateTime CreatedAt { get; init; }

  /// <summary>
  /// When this row was last updated.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_SetsTimestampsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaDefinitionTests.cs:PerspectiveTable_ShouldHaveCorrectSchemaAsync</tests>
  public required DateTime UpdatedAt { get; init; }

  /// <summary>
  /// Optimistic concurrency version number.
  /// Increments on each update.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_WithOrderCreatedEvent_SavesOrderModelAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OrderPerspectiveTests.cs:OrderPerspective_Update_MultipleEvents_IncrementsVersionAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaDefinitionTests.cs:PerspectiveTable_ShouldHaveCorrectSchemaAsync</tests>
  public required int Version { get; init; }
}
