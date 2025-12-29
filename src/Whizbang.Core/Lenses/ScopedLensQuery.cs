using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Lenses;

/// <summary>
/// Auto-scoping lens query implementation that creates a fresh service scope for each operation.
/// Ensures DbContext isolation and prevents stale data when used from singleton services.
/// </summary>
/// <typeparam name="TModel">The perspective model type</typeparam>
/// <docs>lenses/scoped-queries</docs>
/// <tests>Whizbang.Core.Tests/Lenses/ScopedLensQueryTests.cs</tests>
public class ScopedLensQuery<TModel> : IScopedLensQuery<TModel> where TModel : class {
  private readonly IServiceScopeFactory _scopeFactory;

  /// <summary>
  /// Creates a new auto-scoping lens query.
  /// </summary>
  /// <param name="scopeFactory">Service scope factory for creating scopes per operation</param>
  public ScopedLensQuery(IServiceScopeFactory scopeFactory) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    _scopeFactory = scopeFactory;
  }

  /// <inheritdoc/>
  public async IAsyncEnumerable<PerspectiveRow<TModel>> QueryAsync(
      Func<ILensQuery<TModel>, IQueryable<PerspectiveRow<TModel>>> queryBuilder,
      [EnumeratorCancellation] CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(queryBuilder);

    await using var scope = _scopeFactory.CreateAsyncScope();
    var lensQuery = scope.ServiceProvider.GetRequiredService<ILensQuery<TModel>>();

    var query = queryBuilder(lensQuery);

    // Materialize the query within the scope before yielding
    // This ensures we fetch all data while DbContext is still alive
    var results = query.ToList();

    foreach (var row in results) {
      cancellationToken.ThrowIfCancellationRequested();
      yield return row;
    }
  }

  /// <inheritdoc/>
  public async IAsyncEnumerable<TResult> QueryAsync<TResult>(
      Func<ILensQuery<TModel>, IQueryable<TResult>> queryBuilder,
      [EnumeratorCancellation] CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(queryBuilder);

    await using var scope = _scopeFactory.CreateAsyncScope();
    var lensQuery = scope.ServiceProvider.GetRequiredService<ILensQuery<TModel>>();

    var query = queryBuilder(lensQuery);

    // Materialize the query within the scope before yielding
    var results = query.ToList();

    foreach (var result in results) {
      cancellationToken.ThrowIfCancellationRequested();
      yield return result;
    }
  }

  /// <inheritdoc/>
  public async Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
    await using var scope = _scopeFactory.CreateAsyncScope();
    var lensQuery = scope.ServiceProvider.GetRequiredService<ILensQuery<TModel>>();

    return await lensQuery.GetByIdAsync(id, cancellationToken);
  }

  /// <inheritdoc/>
  public async Task<TResult> ExecuteAsync<TResult>(
      Func<ILensQuery<TModel>, CancellationToken, Task<TResult>> queryExecutor,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(queryExecutor);

    await using var scope = _scopeFactory.CreateAsyncScope();
    var lensQuery = scope.ServiceProvider.GetRequiredService<ILensQuery<TModel>>();

    return await queryExecutor(lensQuery, cancellationToken);
  }
}
