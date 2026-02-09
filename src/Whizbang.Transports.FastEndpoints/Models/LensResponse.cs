namespace Whizbang.Transports.FastEndpoints;

/// <summary>
/// Standard paged response model for lens endpoints.
/// Includes data, paging metadata, and navigation helpers.
/// </summary>
/// <typeparam name="TModel">The read model type</typeparam>
/// <docs>v0.1.0/rest/lens-integration</docs>
/// <tests>tests/Whizbang.Transports.FastEndpoints.Tests/Unit/LensResponseTests.cs</tests>
/// <example>
/// var response = new LensResponse&lt;OrderReadModel&gt; {
///     Data = orders,
///     TotalCount = totalOrders,
///     Page = request.Page,
///     PageSize = request.PageSize ?? 10
/// };
/// </example>
public class LensResponse<TModel> where TModel : class {
  /// <summary>
  /// The data items for the current page.
  /// </summary>
  public IReadOnlyList<TModel> Data { get; set; } = [];

  /// <summary>
  /// Total number of items across all pages.
  /// </summary>
  public int TotalCount { get; set; }

  /// <summary>
  /// Current page number (1-based).
  /// </summary>
  public int Page { get; set; } = 1;

  /// <summary>
  /// Number of items per page.
  /// </summary>
  public int PageSize { get; set; } = 10;

  /// <summary>
  /// Total number of pages.
  /// Calculated from TotalCount and PageSize.
  /// </summary>
  public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;

  /// <summary>
  /// Whether there are more pages after the current page.
  /// </summary>
  public bool HasNextPage => Page < TotalPages;

  /// <summary>
  /// Whether there are pages before the current page.
  /// </summary>
  public bool HasPreviousPage => Page > 1;
}
