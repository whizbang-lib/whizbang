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
  IQueryable<PerspectiveRow<TModel>> Query { get; }

  /// <summary>
  /// Fast single-item lookup by ID.
  /// Returns only the model data, not the full row.
  /// </summary>
  /// <param name="id">Unique identifier</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The read model, or null if not found</returns>
  Task<TModel?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
}
