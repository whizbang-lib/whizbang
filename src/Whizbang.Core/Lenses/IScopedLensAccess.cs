namespace Whizbang.Core.Lenses;

/// <summary>
/// Intermediate interface providing scoped access to a single perspective model.
/// Returned by <see cref="ILensQuery{TModel}.Scope"/> and related methods
/// to enforce scope selection before querying.
/// </summary>
/// <typeparam name="TModel">The read model type to query.</typeparam>
/// <docs>fundamentals/lenses/scoped-queries#scoped-lens-access</docs>
/// <tests>Whizbang.Core.Tests/Lenses/ScopedLensAccessTests.cs</tests>
public interface IScopedLensAccess<TModel> where TModel : class {
  /// <summary>
  /// Queryable access to full perspective rows with scope filters pre-applied.
  /// </summary>
  IQueryable<PerspectiveRow<TModel>> Query { get; }

  /// <summary>
  /// Fast single-item lookup by ID within the applied scope.
  /// Returns null if the item does not exist or is not visible in the current scope.
  /// </summary>
  /// <param name="id">Unique identifier.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The read model, or null if not found within scope.</returns>
  Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
