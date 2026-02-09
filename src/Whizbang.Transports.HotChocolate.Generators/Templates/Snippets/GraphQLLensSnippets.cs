namespace Whizbang.Transports.HotChocolate.Generators.Templates.Snippets;

/// <summary>
/// Snippets for GraphQL lens code generation.
/// These are extracted by region name and used to generate query methods.
/// </summary>
internal static class GraphQLLensSnippets {
  #region LENS_QUERY_METHOD_WITH_ALL
  /// <summary>
  /// Query field for __QUERY_NAME__.
  /// Returns paginated, filtered, and sorted results from the __INTERFACE_NAME__ lens.
  /// </summary>
  [UsePaging(DefaultPageSize = __DEFAULT_PAGE_SIZE__, MaxPageSize = __MAX_PAGE_SIZE__)]
  [UseProjection]
  [UseFiltering]
  [UseSorting]
  public IQueryable<PerspectiveRow<__MODEL_TYPE__>> __METHOD_NAME__(
      [Service] __INTERFACE_TYPE__ lens) {
    return lens.Query;
  }
  #endregion

  #region LENS_QUERY_METHOD_NO_PAGING
  /// <summary>
  /// Query field for __QUERY_NAME__.
  /// Returns filtered and sorted results from the __INTERFACE_NAME__ lens.
  /// </summary>
  [UseProjection]
  [UseFiltering]
  [UseSorting]
  public IQueryable<PerspectiveRow<__MODEL_TYPE__>> __METHOD_NAME__(
      [Service] __INTERFACE_TYPE__ lens) {
    return lens.Query;
  }
  #endregion

  #region LENS_QUERY_METHOD_NO_FILTERING
  /// <summary>
  /// Query field for __QUERY_NAME__.
  /// Returns paginated and sorted results from the __INTERFACE_NAME__ lens.
  /// </summary>
  [UsePaging(DefaultPageSize = __DEFAULT_PAGE_SIZE__, MaxPageSize = __MAX_PAGE_SIZE__)]
  [UseProjection]
  [UseSorting]
  public IQueryable<PerspectiveRow<__MODEL_TYPE__>> __METHOD_NAME__(
      [Service] __INTERFACE_TYPE__ lens) {
    return lens.Query;
  }
  #endregion

  #region LENS_QUERY_METHOD_NO_SORTING
  /// <summary>
  /// Query field for __QUERY_NAME__.
  /// Returns paginated and filtered results from the __INTERFACE_NAME__ lens.
  /// </summary>
  [UsePaging(DefaultPageSize = __DEFAULT_PAGE_SIZE__, MaxPageSize = __MAX_PAGE_SIZE__)]
  [UseProjection]
  [UseFiltering]
  public IQueryable<PerspectiveRow<__MODEL_TYPE__>> __METHOD_NAME__(
      [Service] __INTERFACE_TYPE__ lens) {
    return lens.Query;
  }
  #endregion

  #region LENS_QUERY_METHOD_MINIMAL
  /// <summary>
  /// Query field for __QUERY_NAME__.
  /// Returns results from the __INTERFACE_NAME__ lens.
  /// </summary>
  [UseProjection]
  public IQueryable<PerspectiveRow<__MODEL_TYPE__>> __METHOD_NAME__(
      [Service] __INTERFACE_TYPE__ lens) {
    return lens.Query;
  }
  #endregion

  #region LENS_INFO_PROPERTY
  /// <summary>
  /// Information about the __QUERY_NAME__ lens.
  /// </summary>
  public static (string QueryName, string InterfaceName, string ModelType) __PROPERTY_NAME__Info =>
      ("__QUERY_NAME__", "__INTERFACE_NAME__", "__MODEL_TYPE__");
  #endregion
}
