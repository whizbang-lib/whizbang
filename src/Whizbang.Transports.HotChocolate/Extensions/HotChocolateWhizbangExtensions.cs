using HotChocolate.Execution.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Transports.HotChocolate;

/// <summary>
/// Extension methods for registering Whizbang Lens support with HotChocolate GraphQL.
/// </summary>
/// <docs>v0.1.0/graphql/setup</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/ServiceRegistrationTests.cs</tests>
/// <example>
/// services.AddGraphQLServer()
///     .AddWhizbangLenses()
///     .AddQueryType&lt;Query&gt;();
///
/// // With configuration
/// services.AddGraphQLServer()
///     .AddWhizbangLenses(options => {
///         options.DefaultScope = GraphQLLensScope.All;
///         options.DefaultPageSize = 25;
///     });
/// </example>
public static class HotChocolateWhizbangExtensions {
  /// <summary>
  /// Adds Whizbang Lens support to HotChocolate with default options.
  /// Registers filter, sort, and projection conventions for <see cref="Whizbang.Core.Lenses.PerspectiveRow{TModel}"/> types.
  /// </summary>
  /// <param name="builder">The HotChocolate request executor builder.</param>
  /// <returns>The builder for chaining.</returns>
  public static IRequestExecutorBuilder AddWhizbangLenses(this IRequestExecutorBuilder builder)
      => builder.AddWhizbangLenses(_ => { });

  /// <summary>
  /// Adds Whizbang Lens support to HotChocolate with custom options.
  /// Registers filter, sort, and projection conventions for <see cref="Whizbang.Core.Lenses.PerspectiveRow{TModel}"/> types.
  /// </summary>
  /// <param name="builder">The HotChocolate request executor builder.</param>
  /// <param name="configure">Action to configure <see cref="WhizbangGraphQLOptions"/>.</param>
  /// <returns>The builder for chaining.</returns>
  public static IRequestExecutorBuilder AddWhizbangLenses(
      this IRequestExecutorBuilder builder,
      Action<WhizbangGraphQLOptions> configure) {
    var options = new WhizbangGraphQLOptions();
    configure(options);

    builder.Services.AddSingleton(options);

    builder
        .AddFiltering<WhizbangFilterConvention>()
        .AddSorting<WhizbangSortConvention>()
        .AddProjections();

    return builder;
  }
}
