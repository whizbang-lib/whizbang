namespace Whizbang.Transports.FastEndpoints;

/// <summary>
/// Standard request model for lens endpoints with filtering, sorting, and paging.
/// Used as a base for generated lens endpoint requests.
/// </summary>
/// <docs>v0.1.0/rest/filtering</docs>
/// <tests>tests/Whizbang.Transports.FastEndpoints.Tests/Unit/LensRequestTests.cs</tests>
/// <example>
/// // Query string: ?page=2&amp;pageSize=25&amp;sort=-createdAt&amp;filter[status]=active
/// var request = new LensRequest {
///     Page = 2,
///     PageSize = 25,
///     Sort = "-createdAt",
///     Filter = new Dictionary&lt;string, string&gt; { ["status"] = "active" }
/// };
/// </example>
public class LensRequest {
  /// <summary>
  /// Current page number (1-based).
  /// Default: 1
  /// </summary>
  public int Page { get; set; } = 1;

  /// <summary>
  /// Number of items per page.
  /// If null, uses the endpoint's DefaultPageSize.
  /// </summary>
  public int? PageSize { get; set; }

  /// <summary>
  /// Sort expression. Use prefix '-' for descending, '+' or no prefix for ascending.
  /// Multiple fields separated by comma.
  /// Examples: "-createdAt", "name", "-priority,createdAt"
  /// </summary>
  public string? Sort { get; set; }

  /// <summary>
  /// Filter expressions as key-value pairs.
  /// Mapped from query parameters like ?filter[name]=John&amp;filter[status]=active
  /// </summary>
  public Dictionary<string, string>? Filter { get; set; }
}
