namespace Whizbang.Transports.FastEndpoints;

/// <summary>
/// Base class for REST lens endpoints providing standard filtering, sorting, and paging.
/// Generated endpoints inherit from this class and can override hooks for customization.
/// </summary>
/// <typeparam name="TModel">The read model type</typeparam>
/// <docs>v0.1.0/rest/lens-integration</docs>
/// <tests>tests/Whizbang.Transports.FastEndpoints.Tests/Unit/LensEndpointBaseTests.cs</tests>
/// <example>
/// // Generated endpoint (partial, extensible):
/// public partial class OrderLensEndpoint : LensEndpointBase&lt;OrderReadModel&gt; {
///     protected override async ValueTask OnBeforeQueryAsync(LensRequest request, CancellationToken ct) {
///         // Add custom filtering, logging, etc.
///     }
/// }
/// </example>
public abstract class LensEndpointBase<TModel> where TModel : class {
  /// <summary>
  /// Hook called before executing the query.
  /// Override to add validation, logging, or modify the request.
  /// </summary>
  /// <param name="request">The incoming request</param>
  /// <param name="ct">Cancellation token</param>
  protected virtual ValueTask OnBeforeQueryAsync(LensRequest request, CancellationToken ct)
      => ValueTask.CompletedTask;

  /// <summary>
  /// Hook called after executing the query.
  /// Override to add post-processing, logging, or modify the response.
  /// </summary>
  /// <param name="request">The original request</param>
  /// <param name="response">The response to be returned</param>
  /// <param name="ct">Cancellation token</param>
  protected virtual ValueTask OnAfterQueryAsync(LensRequest request, LensResponse<TModel> response, CancellationToken ct)
      => ValueTask.CompletedTask;

  /// <summary>
  /// Calculate skip and take values from the request with bounds checking.
  /// </summary>
  /// <param name="request">The incoming request</param>
  /// <param name="defaultPageSize">Default page size if not specified in request</param>
  /// <param name="maxPageSize">Maximum allowed page size</param>
  /// <returns>Tuple of (skip, take) values</returns>
  protected (int skip, int take) CalculatePaging(LensRequest request, int defaultPageSize, int maxPageSize) {
    // Ensure page is at least 1
    var page = Math.Max(1, request.Page);

    // Use request page size, default, or clamp to max
    var pageSize = request.PageSize ?? defaultPageSize;
    pageSize = Math.Min(pageSize, maxPageSize);
    pageSize = Math.Max(1, pageSize);

    var skip = (page - 1) * pageSize;
    return (skip, pageSize);
  }

  /// <summary>
  /// Parse a sort expression string into structured sort expressions.
  /// </summary>
  /// <param name="sort">Sort string (e.g., "-createdAt,name,+status")</param>
  /// <returns>List of parsed sort expressions</returns>
  protected IReadOnlyList<SortExpression> ParseSortExpression(string? sort) {
    if (string.IsNullOrWhiteSpace(sort)) {
      return [];
    }

    var results = new List<SortExpression>();
    var fields = sort.Split(',', StringSplitOptions.RemoveEmptyEntries);

    foreach (var field in fields) {
      var trimmed = field.Trim();
      if (string.IsNullOrEmpty(trimmed)) {
        continue;
      }

      bool descending;
      string fieldName;

      if (trimmed.StartsWith('-')) {
        descending = true;
        fieldName = trimmed[1..].Trim();
      } else if (trimmed.StartsWith('+')) {
        descending = false;
        fieldName = trimmed[1..].Trim();
      } else {
        descending = false;
        fieldName = trimmed;
      }

      if (!string.IsNullOrEmpty(fieldName)) {
        results.Add(new SortExpression(fieldName, descending));
      }
    }

    return results;
  }
}

/// <summary>
/// Represents a parsed sort expression with field name and direction.
/// </summary>
/// <param name="Field">The field name to sort by</param>
/// <param name="Descending">True for descending order, false for ascending</param>
public readonly record struct SortExpression(string Field, bool Descending);
