namespace Whizbang.Core.Lenses;

/// <summary>
/// Maps <see cref="QueryScope"/> enum values to their corresponding <see cref="ScopeFilters"/> flags.
/// </summary>
/// <docs>fundamentals/lenses/scoped-queries#query-scope</docs>
/// <tests>Whizbang.Core.Tests/Lenses/QueryScopeMapperTests.cs</tests>
public static class QueryScopeMapper {
  /// <summary>
  /// Converts a <see cref="QueryScope"/> value to the corresponding <see cref="ScopeFilters"/> flags.
  /// </summary>
  /// <param name="scope">The query scope to convert.</param>
  /// <returns>The corresponding scope filter flags.</returns>
  /// <exception cref="ArgumentOutOfRangeException">Thrown when an undefined QueryScope value is provided.</exception>
  public static ScopeFilters ToScopeFilter(QueryScope scope) => scope switch {
    QueryScope.Global => ScopeFilters.None,
    QueryScope.Tenant => ScopeFilters.Tenant,
    QueryScope.Organization => ScopeFilters.Tenant | ScopeFilters.Organization,
    QueryScope.Customer => ScopeFilters.Tenant | ScopeFilters.Customer,
    QueryScope.User => ScopeFilters.Tenant | ScopeFilters.User,
    QueryScope.Principal => ScopeFilters.Tenant | ScopeFilters.Principal,
    QueryScope.UserOrPrincipal => ScopeFilters.Tenant | ScopeFilters.User | ScopeFilters.Principal,
    _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, $"Unknown QueryScope value: {scope}")
  };
}
