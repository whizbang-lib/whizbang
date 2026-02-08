using HotChocolate.Data.Filters;

namespace Whizbang.Transports.HotChocolate;

/// <summary>
/// Custom filter convention for Whizbang Lenses.
/// Configures filtering support for <see cref="Whizbang.Core.Lenses.PerspectiveRow{TModel}"/> types
/// with scope-aware field inclusion.
/// </summary>
/// <docs>graphql/filtering</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/FilterConventionTests.cs</tests>
/// <example>
/// services.AddGraphQLServer()
///     .AddFiltering&lt;WhizbangFilterConvention&gt;()
///     .AddWhizbangLenses();
/// </example>
public class WhizbangFilterConvention : FilterConvention {
  /// <inheritdoc/>
  protected override void Configure(IFilterConventionDescriptor descriptor) {
    descriptor.AddDefaults();
  }
}
