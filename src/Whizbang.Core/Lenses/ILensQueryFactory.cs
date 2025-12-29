using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Lenses;

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
