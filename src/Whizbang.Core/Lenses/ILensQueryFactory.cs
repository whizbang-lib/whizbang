using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Lenses;

/// <summary>
/// Non-generic factory for creating ILensQuery instances sharing a single DbContext.
/// The factory owns the DbContext and disposes it when disposed.
/// Registered as Transient - each injection gets a fresh factory with its own DbContext.
///
/// <para>
/// <strong>Common case (parallel resolvers):</strong> Inject <see cref="ILensQuery{TModel}"/>
/// directly - each injection gets its own factory internally, safe for parallel access.
/// </para>
/// <para>
/// <strong>Joins/shared DbContext:</strong> Inject <see cref="ILensQueryFactory"/> and call
/// <see cref="GetQuery{TModel}"/> multiple times - all queries share the same DbContext.
/// </para>
/// </summary>
/// <docs>lenses/lens-query-factory</docs>
/// <tests>Whizbang.Core.Tests/Lenses/FactoryOwnedLensQueryTests.cs</tests>
public interface ILensQueryFactory : IAsyncDisposable {
  /// <summary>
  /// Gets an ILensQuery for the specified model type, sharing this factory's DbContext.
  /// Multiple calls return queries that share the same DbContext (for joins).
  /// </summary>
  /// <typeparam name="TModel">The perspective model type</typeparam>
  /// <returns>An ILensQuery that uses this factory's DbContext</returns>
  ILensQuery<TModel> GetQuery<TModel>() where TModel : class;
}

/// <summary>
/// Factory for creating scoped ILensQuery instances.
/// Use for batch operations where multiple queries should share one scope (and DbContext).
/// </summary>
/// <typeparam name="TModel">The perspective model type</typeparam>
/// <docs>lenses/scoped-queries</docs>
/// <tests>Whizbang.Core.Tests/Lenses/LensQueryFactoryTests.cs</tests>
public interface ILensQueryFactory<TModel> where TModel : class {
  /// <summary>
  /// Creates a scoped ILensQuery instance.
  /// IMPORTANT: Caller MUST dispose the returned object to release scope.
  /// </summary>
  /// <returns>Disposable wrapper containing the scoped lens query</returns>
  LensQueryScope<TModel> CreateScoped();
}

/// <summary>
/// Disposable wrapper for scoped ILensQuery instances.
/// Ensures proper scope and DbContext disposal.
/// </summary>
/// <typeparam name="TModel">The perspective model type</typeparam>
public sealed class LensQueryScope<TModel> : IDisposable where TModel : class {
  private readonly IServiceScope _scope;

  /// <summary>
  /// Creates a new lens query scope wrapper.
  /// </summary>
  /// <param name="scope">The service scope to manage</param>
  /// <param name="lensQuery">The scoped lens query instance</param>
  internal LensQueryScope(IServiceScope scope, ILensQuery<TModel> lensQuery) {
    _scope = scope ?? throw new ArgumentNullException(nameof(scope));
    Value = lensQuery ?? throw new ArgumentNullException(nameof(lensQuery));
  }

  /// <summary>
  /// The scoped ILensQuery instance.
  /// Valid until Dispose() is called.
  /// </summary>
  public ILensQuery<TModel> Value { get; }

  /// <summary>
  /// Disposes the service scope and releases the DbContext.
  /// </summary>
  public void Dispose() {
    _scope.Dispose();
  }
}
