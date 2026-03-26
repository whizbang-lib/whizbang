namespace Whizbang.Core.Lenses;

/// <summary>
/// Predefined query scope levels for the fluent scope-before-query API.
/// Each value maps to a specific combination of <see cref="ScopeFilters"/> flags
/// via <see cref="QueryScopeMapper.ToScopeFilter"/>.
/// </summary>
/// <docs>fundamentals/lenses/scoped-queries#query-scope</docs>
/// <tests>Whizbang.Core.Tests/Lenses/QueryScopeMapperTests.cs</tests>
public enum QueryScope {
  /// <summary>
  /// No filtering - full access to all data.
  /// Maps to <see cref="ScopeFilters.None"/>.
  /// </summary>
  Global,

  /// <summary>
  /// Filter by tenant only.
  /// Maps to <see cref="ScopeFilters.Tenant"/>.
  /// </summary>
  Tenant,

  /// <summary>
  /// Filter by tenant and organization.
  /// Maps to <see cref="ScopeFilters.Tenant"/> | <see cref="ScopeFilters.Organization"/>.
  /// </summary>
  Organization,

  /// <summary>
  /// Filter by tenant and customer.
  /// Maps to <see cref="ScopeFilters.Tenant"/> | <see cref="ScopeFilters.Customer"/>.
  /// </summary>
  Customer,

  /// <summary>
  /// Filter by tenant and user.
  /// Maps to <see cref="ScopeFilters.Tenant"/> | <see cref="ScopeFilters.User"/>.
  /// </summary>
  User,

  /// <summary>
  /// Filter by tenant and security principal membership.
  /// Maps to <see cref="ScopeFilters.Tenant"/> | <see cref="ScopeFilters.Principal"/>.
  /// </summary>
  Principal,

  /// <summary>
  /// Filter by tenant with user OR principal access (my records or shared with me).
  /// Maps to <see cref="ScopeFilters.Tenant"/> | <see cref="ScopeFilters.User"/> | <see cref="ScopeFilters.Principal"/>.
  /// </summary>
  UserOrPrincipal
}
