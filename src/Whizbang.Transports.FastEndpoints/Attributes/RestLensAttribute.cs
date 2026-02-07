namespace Whizbang.Transports.FastEndpoints;

/// <summary>
/// Marks a lens type for FastEndpoints REST integration.
/// The source generator discovers this attribute and generates REST endpoint classes
/// with filtering, sorting, and paging support via query parameters.
/// </summary>
/// <docs>v0.1.0/rest/lens-integration</docs>
/// <tests>tests/Whizbang.Transports.FastEndpoints.Tests/Unit/RestLensAttributeTests.cs</tests>
/// <example>
/// // Simple REST endpoint
/// [RestLens(Route = "/api/orders")]
/// public interface IOrderLens : ILensQuery&lt;OrderReadModel&gt; { }
///
/// // With path parameters
/// [RestLens(Route = "/api/v1/orders/{id}")]
/// public interface IOrderDetailLens : ILensQuery&lt;OrderReadModel&gt; { }
///
/// // Customized paging and disabled sorting
/// [RestLens(
///     Route = "/api/recent-orders",
///     EnableSorting = false,
///     DefaultPageSize = 25,
///     MaxPageSize = 100)]
/// public interface IRecentOrderLens : ILensQuery&lt;OrderReadModel&gt; { }
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class RestLensAttribute : Attribute {
  /// <summary>
  /// The REST route for this lens endpoint.
  /// Examples: "/api/orders", "/api/v1/products/{id}"
  /// If null, defaults to a route based on the model name.
  /// </summary>
  public string? Route { get; set; }

  /// <summary>
  /// Enable query parameter filtering on this lens.
  /// When true, accepts filter parameters like ?filter[name]=value.
  /// Default: true
  /// </summary>
  public bool EnableFiltering { get; set; } = true;

  /// <summary>
  /// Enable query parameter sorting on this lens.
  /// When true, accepts sort parameters like ?sort=-createdAt.
  /// Default: true
  /// </summary>
  public bool EnableSorting { get; set; } = true;

  /// <summary>
  /// Enable paging on this lens.
  /// When true, accepts page and pageSize parameters.
  /// Default: true
  /// </summary>
  public bool EnablePaging { get; set; } = true;

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
