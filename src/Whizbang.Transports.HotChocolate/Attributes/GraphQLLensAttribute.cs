namespace Whizbang.Transports.HotChocolate;

/// <summary>
/// Marks a lens type for HotChocolate GraphQL integration.
/// The source generator discovers this attribute and generates GraphQL type extensions,
/// query resolvers, and schema registrations.
/// </summary>
/// <docs>v0.1.0/graphql/lens-integration</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/GraphQLLensAttributeTests.cs</tests>
/// <example>
/// // Simple - uses system default scope
/// [GraphQLLens(QueryName = "orders")]
/// public interface IOrderLens : ILensQuery&lt;OrderReadModel&gt; { }
///
/// // Data only - just TModel properties
/// [GraphQLLens(QueryName = "products", Scope = GraphQLLensScope.DataOnly)]
/// public interface IProductLens : ILensQuery&lt;ProductReadModel&gt; { }
///
/// // Full audit trail - Data + Metadata + SystemFields (no Scope/tenancy)
/// [GraphQLLens(
///     QueryName = "auditLog",
///     Scope = GraphQLLensScope.Data | GraphQLLensScope.Metadata | GraphQLLensScope.SystemFields)]
/// public interface IAuditLens : ILensQuery&lt;AuditReadModel&gt; { }
///
/// // Admin view - everything
/// [GraphQLLens(QueryName = "adminOrders", Scope = GraphQLLensScope.All)]
/// public interface IAdminOrderLens : ILensQuery&lt;OrderReadModel&gt; { }
///
/// // Customized paging and disabled sorting
/// [GraphQLLens(
///     QueryName = "recentOrders",
///     EnableSorting = false,
///     DefaultPageSize = 25,
///     MaxPageSize = 100)]
/// public interface IRecentOrderLens : ILensQuery&lt;OrderReadModel&gt; { }
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class GraphQLLensAttribute : Attribute {
  /// <summary>
  /// The GraphQL query field name. If null, defaults to the pluralized model name
  /// (e.g., "OrderReadModel" becomes "orders").
  /// </summary>
  public string? QueryName { get; set; }

  /// <summary>
  /// Determines which parts of <see cref="Whizbang.Core.Lenses.PerspectiveRow{TModel}"/>
  /// are exposed in GraphQL queries.
  /// Default: <see cref="GraphQLLensScope.Default"/> (uses system configuration).
  /// </summary>
  public GraphQLLensScope Scope { get; set; } = GraphQLLensScope.Default;

  /// <summary>
  /// Enable GraphQL filtering on this lens.
  /// When true, generates filter input types and applies <c>[UseFiltering]</c>.
  /// Default: true
  /// </summary>
  public bool EnableFiltering { get; set; } = true;

  /// <summary>
  /// Enable GraphQL sorting on this lens.
  /// When true, generates sort input types and applies <c>[UseSorting]</c>.
  /// Default: true
  /// </summary>
  public bool EnableSorting { get; set; } = true;

  /// <summary>
  /// Enable cursor-based paging on this lens.
  /// When true, applies <c>[UsePaging]</c> with Connection types.
  /// Default: true
  /// </summary>
  public bool EnablePaging { get; set; } = true;

  /// <summary>
  /// Enable field projections on this lens.
  /// When true, applies <c>[UseProjection]</c> for efficient queries.
  /// Default: true
  /// </summary>
  public bool EnableProjection { get; set; } = true;

  /// <summary>
  /// Default page size when paging is enabled.
  /// Default: 10
  /// </summary>
  public int DefaultPageSize { get; set; } = 10;

  /// <summary>
  /// Maximum allowed page size when paging is enabled.
  /// Prevents clients from requesting excessively large pages.
  /// Default: 100
  /// </summary>
  public int MaxPageSize { get; set; } = 100;
}
