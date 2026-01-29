namespace Whizbang.Core.Lenses;

/// <summary>
/// Defines a named scope for filtering lens queries.
/// Scopes enable automatic tenant/user filtering without manual WHERE clauses.
/// </summary>
/// <remarks>
/// <para>
/// Scope definitions configure how queries are filtered based on context values.
/// The source generator uses these definitions to create pre-compiled filter expressions.
/// </para>
/// <para>
/// Common scope patterns:
/// <list type="bullet">
///   <item><b>Tenant</b>: Filter by TenantId for multi-tenant isolation</item>
///   <item><b>User</b>: Filter by UserId for user-specific data</item>
///   <item><b>Global</b>: No filter (admin access to all data)</item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// options.Lenses.DefineScope("Tenant", scope => {
///   scope.FilterPropertyName = "TenantId";
///   scope.ContextKey = "TenantId";
///   scope.FilterInterfaceType = typeof(ITenantScoped);
/// });
/// </code>
/// </example>
/// <docs>core-concepts/scoped-lenses#scope-definition</docs>
/// <tests>Whizbang.Core.Tests/Lenses/ScopedLensFactoryTests.cs</tests>
public sealed class ScopeDefinition {
  /// <summary>
  /// Creates a new scope definition with the specified name.
  /// </summary>
  /// <param name="name">The unique name for this scope (e.g., "Tenant", "User", "Global").</param>
  public ScopeDefinition(string name) {
    Name = name ?? throw new ArgumentNullException(nameof(name));
  }

  /// <summary>
  /// The unique name for this scope.
  /// Used to select the scope when creating lenses via <see cref="IScopedLensFactory"/>.
  /// </summary>
  public string Name { get; }

  /// <summary>
  /// The property name to filter by (e.g., "TenantId", "UserId").
  /// Models implementing the filter interface must have this property.
  /// </summary>
  public string? FilterPropertyName { get; set; }

  /// <summary>
  /// The key to retrieve the filter value from the scope context.
  /// Usually matches <see cref="FilterPropertyName"/> but can differ.
  /// </summary>
  public string? ContextKey { get; set; }

  /// <summary>
  /// The filter comparison mode. Default is <see cref="FilterMode.Equals"/>.
  /// Use <see cref="FilterMode.In"/> for hierarchical filtering.
  /// </summary>
  public FilterMode FilterMode { get; set; } = FilterMode.Equals;

  /// <summary>
  /// When true, no filter is applied (admin/global access).
  /// </summary>
  public bool NoFilter { get; set; }

  /// <summary>
  /// Optional interface type that models must implement to be filtered.
  /// Only models implementing this interface will have the scope filter applied.
  /// </summary>
  public Type? FilterInterfaceType { get; set; }
}
