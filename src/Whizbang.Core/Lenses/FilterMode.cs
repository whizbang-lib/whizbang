namespace Whizbang.Core.Lenses;

/// <summary>
/// Specifies how scope filters are applied to lens queries.
/// </summary>
/// <docs>core-concepts/scoped-lenses#filter-modes</docs>
public enum FilterMode {
  /// <summary>
  /// Filter using equality comparison (WHERE property = @value).
  /// Use for single-value filtering like TenantId = "tenant-123".
  /// </summary>
  Equals = 0,

  /// <summary>
  /// Filter using IN clause (WHERE property IN @values).
  /// Use for hierarchical filtering like TenantId IN ("parent", "child1", "child2").
  /// </summary>
  In = 1
}
