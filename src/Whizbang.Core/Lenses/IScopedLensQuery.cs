namespace Whizbang.Core.Lenses;

/// <summary>
/// Auto-scoping lens query for use in singleton services, background workers, or test fixtures.
/// Each operation creates its own service scope, ensuring fresh DbContext and avoiding stale data.
/// For batch operations requiring multiple queries in one scope, use <see cref="ILensQueryFactory{TModel}"/>.
/// </summary>
/// <typeparam name="TModel">The perspective model type</typeparam>
/// <docs>lenses/scoped-queries</docs>
/// <tests>Whizbang.Core.Tests/Lenses/ScopedLensQueryTests.cs</tests>
public interface IScopedLensQuery<TModel> where TModel : class {
  /// <summary>
  /// Executes a query with auto-created scope.
  /// Returns IAsyncEnumerable for streaming results (scope disposed after enumeration).
  /// </summary>
  /// <param name="queryBuilder">Function that builds the query using the scoped ILensQuery</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Async enumerable of perspective rows</returns>
  IAsyncEnumerable<PerspectiveRow<TModel>> QueryAsync(
      Func<ILensQuery<TModel>, IQueryable<PerspectiveRow<TModel>>> queryBuilder,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Executes a projection query with auto-created scope.
  /// </summary>
  /// <typeparam name="TResult">The projected result type</typeparam>
  /// <param name="queryBuilder">Function that builds the query using the scoped ILensQuery</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Async enumerable of projected results</returns>
  IAsyncEnumerable<TResult> QueryAsync<TResult>(
      Func<ILensQuery<TModel>, IQueryable<TResult>> queryBuilder,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Fast single-item lookup by ID with auto-created scope.
  /// </summary>
  /// <param name="id">The entity ID</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The model instance, or null if not found</returns>
  Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

  /// <summary>
  /// Executes a materialized query with auto-created scope.
  /// Use for ToListAsync, FirstOrDefaultAsync, etc.
  /// </summary>
  /// <typeparam name="TResult">The query result type</typeparam>
  /// <param name="queryExecutor">Function that executes the query and materializes results</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The query result</returns>
  Task<TResult> ExecuteAsync<TResult>(
      Func<ILensQuery<TModel>, CancellationToken, Task<TResult>> queryExecutor,
      CancellationToken cancellationToken = default);
}
