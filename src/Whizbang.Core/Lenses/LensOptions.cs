namespace Whizbang.Core.Lenses;

/// <summary>
/// Configuration options for lens scoping and filtering.
/// Defines named scopes that apply automatic filters to lens queries.
/// </summary>
/// <remarks>
/// <para>
/// Lens options are configured at service registration time. The source generator
/// uses these definitions to create pre-compiled filter expressions for each scope.
/// </para>
/// <para>
/// Common patterns include tenant isolation, user-specific data, and global (admin) access.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// services.AddWhizbang(options => {
///   options.Lenses.DefineScope("Tenant", scope => {
///     scope.FilterPropertyName = "TenantId";
///     scope.ContextKey = "TenantId";
///     scope.FilterInterfaceType = typeof(ITenantScoped);
///   });
///
///   options.Lenses.DefineScope("Global", scope => {
///     scope.NoFilter = true;
///   });
/// });
/// </code>
/// </example>
/// <docs>core-concepts/scoped-lenses#configuration</docs>
/// <tests>Whizbang.Core.Tests/Lenses/ScopedLensFactoryTests.cs</tests>
public sealed class LensOptions {
  private readonly List<ScopeDefinition> _scopes = [];

  /// <summary>
  /// Gets the list of defined scopes.
  /// </summary>
  public IReadOnlyList<ScopeDefinition> Scopes => _scopes;

  /// <summary>
  /// Defines a named scope for lens filtering.
  /// </summary>
  /// <param name="name">The unique name for this scope (e.g., "Tenant", "User", "Global").</param>
  /// <param name="configure">Action to configure the scope definition.</param>
  /// <returns>This instance for method chaining.</returns>
  /// <example>
  /// <code>
  /// options.Lenses
  ///   .DefineScope("Tenant", scope => {
  ///     scope.FilterPropertyName = "TenantId";
  ///     scope.ContextKey = "TenantId";
  ///     scope.FilterInterfaceType = typeof(ITenantScoped);
  ///   })
  ///   .DefineScope("User", scope => {
  ///     scope.FilterPropertyName = "UserId";
  ///     scope.ContextKey = "UserId";
  ///   })
  ///   .DefineScope("Global", scope => {
  ///     scope.NoFilter = true;
  ///   });
  /// </code>
  /// </example>
  public LensOptions DefineScope(string name, Action<ScopeDefinition> configure) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(configure);

    var definition = new ScopeDefinition(name);
    configure(definition);
    _scopes.Add(definition);
    return this;
  }

  /// <summary>
  /// Gets a scope definition by name (case-insensitive).
  /// </summary>
  /// <param name="name">The scope name to look up.</param>
  /// <returns>The scope definition if found; otherwise, null.</returns>
  public ScopeDefinition? GetScope(string name) {
    ArgumentNullException.ThrowIfNull(name);
    return _scopes.Find(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
  }
}
