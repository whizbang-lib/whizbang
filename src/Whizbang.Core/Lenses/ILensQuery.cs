namespace Whizbang.Core.Lenses;

/// <summary>
/// Read-only LINQ abstraction for querying perspective data.
/// Provides IQueryable access to full perspective rows (data, metadata, scope).
/// Implementation translates LINQ to database-specific queries (JSONB for PostgreSQL, JSON for SQL Server, etc.).
/// </summary>
/// <typeparam name="TModel">The read model type to query</typeparam>
/// <docs>core-concepts/lenses</docs>
public interface ILensQuery<TModel> where TModel : class {
  /// <summary>
  /// Queryable access to full perspective rows.
  /// Supports filtering, projection, joins across different perspectives.
  /// LINQ expressions translate to database-specific queries at runtime.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_ReturnsIQueryable_WithCorrectTypeAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByDataFields_ReturnsMatchingRowsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByMetadataFields_ReturnsMatchingRowsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByScopeFields_ReturnsMatchingRowsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanProjectAcrossColumns_ReturnsAnonymousTypeAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_SupportsCombinedFilters_FromAllColumnsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_SupportsComplexLinqOperations_WithOrderByAndSkipTakeAsync</tests>
  IQueryable<PerspectiveRow<TModel>> Query { get; }

  /// <summary>
  /// Fast single-item lookup by ID.
  /// Returns only the model data, not the full row.
  /// </summary>
  /// <param name="id">Unique identifier</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The read model, or null if not found</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:GetByIdAsync_WhenModelExists_ReturnsModelAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:GetByIdAsync_WhenModelDoesNotExist_ReturnsNullAsync</tests>
  Task<TModel?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
}
