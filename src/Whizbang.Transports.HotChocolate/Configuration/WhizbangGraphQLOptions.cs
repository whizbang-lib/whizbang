namespace Whizbang.Transports.HotChocolate;

/// <summary>
/// Configuration options for Whizbang GraphQL integration.
/// These options provide system-wide defaults that can be overridden per-lens
/// using <see cref="GraphQLLensAttribute"/>.
/// </summary>
/// <docs>graphql/setup#configuration</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/WhizbangGraphQLOptionsTests.cs</tests>
/// <example>
/// services.AddGraphQLServer()
///     .AddWhizbangLenses(options => {
///         options.DefaultScope = GraphQLLensScope.Data | GraphQLLensScope.SystemFields;
///         options.DefaultPageSize = 25;
///         options.MaxPageSize = 200;
///     });
/// </example>
public class WhizbangGraphQLOptions {
  /// <summary>
  /// Default scope when <see cref="GraphQLLensAttribute.Scope"/> is set to
  /// <see cref="GraphQLLensScope.Default"/>.
  /// Default: <see cref="GraphQLLensScope.DataOnly"/>
  /// </summary>
  public GraphQLLensScope DefaultScope { get; set; } = GraphQLLensScope.DataOnly;

  /// <summary>
  /// Default page size for cursor-based paging when not specified on the lens.
  /// Default: 10
  /// </summary>
  public int DefaultPageSize { get; set; } = 10;

  /// <summary>
  /// Maximum allowed page size for cursor-based paging when not specified on the lens.
  /// Default: 100
  /// </summary>
  public int MaxPageSize { get; set; } = 100;

  /// <summary>
  /// Whether to include metadata fields in filter and sort types by default.
  /// Only applies when the lens scope includes <see cref="GraphQLLensScope.Metadata"/>.
  /// Default: true
  /// </summary>
  public bool IncludeMetadataInFilters { get; set; } = true;

  /// <summary>
  /// Whether to include scope fields in filter and sort types by default.
  /// Only applies when the lens scope includes <see cref="GraphQLLensScope.Scope"/>.
  /// Default: true
  /// </summary>
  public bool IncludeScopeInFilters { get; set; } = true;
}
