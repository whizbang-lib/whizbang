using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Lenses;

/// <summary>
/// Factory implementation for creating scoped ILensQuery instances.
/// Each CreateScoped() call creates a new service scope and resolves a fresh ILensQuery.
/// </summary>
/// <typeparam name="TModel">The perspective model type</typeparam>
/// <docs>lenses/scoped-queries</docs>
/// <tests>Whizbang.Core.Tests/Lenses/LensQueryFactoryTests.cs</tests>
public class LensQueryFactory<TModel> : ILensQueryFactory<TModel> where TModel : class {
  private readonly IServiceScopeFactory _scopeFactory;

  /// <summary>
  /// Creates a new lens query factory.
  /// </summary>
  /// <param name="scopeFactory">Service scope factory for creating scopes</param>
  public LensQueryFactory(IServiceScopeFactory scopeFactory) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    _scopeFactory = scopeFactory;
  }

  /// <inheritdoc/>
  public LensQueryScope<TModel> CreateScoped() {
    var scope = _scopeFactory.CreateScope();
    var lensQuery = scope.ServiceProvider.GetRequiredService<ILensQuery<TModel>>();

    return new LensQueryScope<TModel>(scope, lensQuery);
  }
}
