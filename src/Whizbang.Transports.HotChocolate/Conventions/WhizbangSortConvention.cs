using HotChocolate.Data.Sorting;

namespace Whizbang.Transports.HotChocolate;

/// <summary>
/// Custom sort convention for Whizbang Lenses.
/// Configures sorting support for <see cref="Whizbang.Core.Lenses.PerspectiveRow{TModel}"/> types
/// with scope-aware field inclusion.
/// </summary>
/// <docs>graphql/sorting</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/SortConventionTests.cs</tests>
/// <example>
/// services.AddGraphQLServer()
///     .AddSorting&lt;WhizbangSortConvention&gt;()
///     .AddWhizbangLenses();
/// </example>
public class WhizbangSortConvention : SortConvention {
  /// <inheritdoc/>
  protected override void Configure(ISortConventionDescriptor descriptor) {
    descriptor.AddDefaults();
  }
}
