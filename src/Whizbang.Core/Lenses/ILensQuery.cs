namespace Whizbang.Core.Lenses;

#pragma warning disable S2326 // Unused type parameters should be removed
#pragma warning disable S2436 // Reduce the number of type parameters in the generic type
// T1, T2, T3, etc. are intentionally declared at the interface level to document which model types
// are valid for the Query<T>() and GetByIdAsync<T>() methods. The runtime implementation enforces
// that T must be one of the declared types. This pattern enables multi-model queries with shared DbContext.
// Supporting up to 10 model types allows complex multi-table joins while maintaining type safety.

/// <summary>
/// Non-generic marker interface for lens query types.
/// Used by <see cref="IScopedLensFactory"/> to constrain generic type parameters.
/// </summary>
/// <docs>core-concepts/lenses</docs>
public interface ILensQuery { }

/// <summary>
/// Read-only LINQ abstraction for querying perspective data (scoped lifetime).
/// Provides IQueryable access to full perspective rows (data, metadata, scope).
/// Implementation translates LINQ to database-specific queries (JSONB for PostgreSQL, JSON for SQL Server, etc.).
///
/// <para>
/// <strong>For web APIs and receptors:</strong> Use this interface (scoped lifetime).
/// </para>
/// <para>
/// <strong>For singleton services:</strong> Use <see cref="IScopedLensQuery{TModel}"/> (auto-scoping)
/// or <see cref="ILensQueryFactory{TModel}"/> (manual scope control for batch operations).
/// </para>
/// </summary>
/// <typeparam name="TModel">The read model type to query</typeparam>
/// <docs>core-concepts/lenses</docs>
/// <docs>lenses/scoped-queries</docs>
public interface ILensQuery<TModel> : ILensQuery where TModel : class {
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
  Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Multi-model lens query supporting joins across two perspective types.
/// All Query&lt;T&gt;() calls share the same underlying DbContext for join support.
/// Implements IAsyncDisposable to manage DbContext lifecycle.
/// </summary>
/// <typeparam name="T1">First read model type</typeparam>
/// <typeparam name="T2">Second read model type</typeparam>
/// <docs>core-concepts/lenses</docs>
/// <docs>lenses/multi-model-queries</docs>
public interface ILensQuery<T1, T2> : ILensQuery, IAsyncDisposable
    where T1 : class
    where T2 : class {
  /// <summary>
  /// Gets queryable for the specified model type.
  /// All queries share the same DbContext, enabling LINQ joins.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2</typeparam>
  /// <exception cref="ArgumentException">Thrown if T is not a valid type parameter</exception>
  IQueryable<PerspectiveRow<T>> Query<T>() where T : class;

  /// <summary>
  /// Fast single-item lookup by ID for the specified model type.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2</typeparam>
  /// <exception cref="ArgumentException">Thrown if T is not a valid type parameter</exception>
  Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <summary>
/// Multi-model lens query supporting joins across three perspective types.
/// </summary>
/// <docs>core-concepts/lenses</docs>
/// <docs>lenses/multi-model-queries</docs>
public interface ILensQuery<T1, T2, T3> : ILensQuery, IAsyncDisposable
    where T1 : class
    where T2 : class
    where T3 : class {
  /// <summary>
  /// Gets queryable for the specified model type.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2, T3</typeparam>
  IQueryable<PerspectiveRow<T>> Query<T>() where T : class;

  /// <summary>
  /// Fast single-item lookup by ID for the specified model type.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2, T3</typeparam>
  Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <summary>
/// Multi-model lens query supporting joins across four perspective types.
/// </summary>
/// <docs>core-concepts/lenses</docs>
/// <docs>lenses/multi-model-queries</docs>
public interface ILensQuery<T1, T2, T3, T4> : ILensQuery, IAsyncDisposable
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class {
  /// <summary>
  /// Gets queryable for the specified model type.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2, T3, T4</typeparam>
  IQueryable<PerspectiveRow<T>> Query<T>() where T : class;

  /// <summary>
  /// Fast single-item lookup by ID for the specified model type.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2, T3, T4</typeparam>
  Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <summary>
/// Multi-model lens query supporting joins across five perspective types.
/// </summary>
/// <docs>core-concepts/lenses</docs>
/// <docs>lenses/multi-model-queries</docs>
public interface ILensQuery<T1, T2, T3, T4, T5> : ILensQuery, IAsyncDisposable
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class {
  /// <summary>
  /// Gets queryable for the specified model type.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2, T3, T4, T5</typeparam>
  IQueryable<PerspectiveRow<T>> Query<T>() where T : class;

  /// <summary>
  /// Fast single-item lookup by ID for the specified model type.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2, T3, T4, T5</typeparam>
  Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <summary>
/// Multi-model lens query supporting joins across six perspective types.
/// </summary>
/// <docs>core-concepts/lenses</docs>
/// <docs>lenses/multi-model-queries</docs>
public interface ILensQuery<T1, T2, T3, T4, T5, T6> : ILensQuery, IAsyncDisposable
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class
    where T6 : class {
  /// <summary>
  /// Gets queryable for the specified model type.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2, T3, T4, T5, T6</typeparam>
  IQueryable<PerspectiveRow<T>> Query<T>() where T : class;

  /// <summary>
  /// Fast single-item lookup by ID for the specified model type.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2, T3, T4, T5, T6</typeparam>
  Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <summary>
/// Multi-model lens query supporting joins across seven perspective types.
/// </summary>
/// <docs>core-concepts/lenses</docs>
/// <docs>lenses/multi-model-queries</docs>
public interface ILensQuery<T1, T2, T3, T4, T5, T6, T7> : ILensQuery, IAsyncDisposable
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class
    where T6 : class
    where T7 : class {
  /// <summary>
  /// Gets queryable for the specified model type.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2, T3, T4, T5, T6, T7</typeparam>
  IQueryable<PerspectiveRow<T>> Query<T>() where T : class;

  /// <summary>
  /// Fast single-item lookup by ID for the specified model type.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2, T3, T4, T5, T6, T7</typeparam>
  Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <summary>
/// Multi-model lens query supporting joins across eight perspective types.
/// </summary>
/// <docs>core-concepts/lenses</docs>
/// <docs>lenses/multi-model-queries</docs>
public interface ILensQuery<T1, T2, T3, T4, T5, T6, T7, T8> : ILensQuery, IAsyncDisposable
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class
    where T6 : class
    where T7 : class
    where T8 : class {
  /// <summary>
  /// Gets queryable for the specified model type.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2, T3, T4, T5, T6, T7, T8</typeparam>
  IQueryable<PerspectiveRow<T>> Query<T>() where T : class;

  /// <summary>
  /// Fast single-item lookup by ID for the specified model type.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2, T3, T4, T5, T6, T7, T8</typeparam>
  Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <summary>
/// Multi-model lens query supporting joins across nine perspective types.
/// </summary>
/// <docs>core-concepts/lenses</docs>
/// <docs>lenses/multi-model-queries</docs>
public interface ILensQuery<T1, T2, T3, T4, T5, T6, T7, T8, T9> : ILensQuery, IAsyncDisposable
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class
    where T6 : class
    where T7 : class
    where T8 : class
    where T9 : class {
  /// <summary>
  /// Gets queryable for the specified model type.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2, T3, T4, T5, T6, T7, T8, T9</typeparam>
  IQueryable<PerspectiveRow<T>> Query<T>() where T : class;

  /// <summary>
  /// Fast single-item lookup by ID for the specified model type.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2, T3, T4, T5, T6, T7, T8, T9</typeparam>
  Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}

/// <summary>
/// Multi-model lens query supporting joins across ten perspective types.
/// </summary>
/// <docs>core-concepts/lenses</docs>
/// <docs>lenses/multi-model-queries</docs>
public interface ILensQuery<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> : ILensQuery, IAsyncDisposable
    where T1 : class
    where T2 : class
    where T3 : class
    where T4 : class
    where T5 : class
    where T6 : class
    where T7 : class
    where T8 : class
    where T9 : class
    where T10 : class {
  /// <summary>
  /// Gets queryable for the specified model type.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2, T3, T4, T5, T6, T7, T8, T9, T10</typeparam>
  IQueryable<PerspectiveRow<T>> Query<T>() where T : class;

  /// <summary>
  /// Fast single-item lookup by ID for the specified model type.
  /// </summary>
  /// <typeparam name="T">Must be one of: T1, T2, T3, T4, T5, T6, T7, T8, T9, T10</typeparam>
  Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class;
}
