namespace Whizbang.Core.Lenses;

/// <summary>
/// ILensQuery wrapper that owns its factory and disposes it when disposed.
/// Used for transient ILensQuery registration - each injection creates factory + query.
/// </summary>
/// <typeparam name="TModel">The perspective model type</typeparam>
/// <docs>lenses/lens-query-factory</docs>
/// <tests>Whizbang.Core.Tests/Lenses/FactoryOwnedLensQueryTests.cs</tests>
public sealed class FactoryOwnedLensQuery<TModel> : ILensQuery<TModel>, IAsyncDisposable
    where TModel : class {
  private readonly ILensQueryFactory _factory;
  private readonly ILensQuery<TModel> _inner;
  private bool _disposed;

  /// <summary>
  /// Creates a new FactoryOwnedLensQuery that wraps the specified factory.
  /// </summary>
  /// <param name="factory">The factory to own and dispose. Must not be null.</param>
  /// <exception cref="ArgumentNullException">Thrown when factory is null.</exception>
  public FactoryOwnedLensQuery(ILensQueryFactory factory) {
    _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    _inner = factory.GetQuery<TModel>();
  }

  /// <inheritdoc />
  public IQueryable<PerspectiveRow<TModel>> Query => _inner.Query;

  /// <inheritdoc />
  public Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
      _inner.GetByIdAsync(id, cancellationToken);

  /// <summary>
  /// Disposes the factory, which releases the underlying DbContext.
  /// Safe to call multiple times.
  /// </summary>
  public async ValueTask DisposeAsync() {
    if (!_disposed) {
      await _factory.DisposeAsync();
      _disposed = true;
    }
  }
}
