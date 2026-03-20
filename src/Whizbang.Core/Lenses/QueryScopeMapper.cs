namespace Whizbang.Core.Lenses;

/// <summary>
/// Maps <see cref="QueryScope"/> enum values to their corresponding <see cref="ScopeFilter"/> flags.
/// </summary>
/// <docs>fundamentals/lenses/scoped-queries#query-scope</docs>
/// <tests>Whizbang.Core.Tests/Lenses/QueryScopeMapperTests.cs</tests>
public static class QueryScopeMapper {
  /// <summary>
  /// Converts a <see cref="QueryScope"/> value to the corresponding <see cref="ScopeFilter"/> flags.
  /// </summary>
  /// <param name="scope">The query scope to convert.</param>
  /// <returns>The corresponding scope filter flags.</returns>
  /// <exception cref="ArgumentOutOfRangeException">Thrown when an undefined QueryScope value is provided.</exception>
  public static ScopeFilter ToScopeFilter(QueryScope scope) => scope switch {
    QueryScope.Global => ScopeFilter.None,
    QueryScope.Tenant => ScopeFilter.Tenant,
    QueryScope.Organization => ScopeFilter.Tenant | ScopeFilter.Organization,
    QueryScope.Customer => ScopeFilter.Tenant | ScopeFilter.Customer,
    QueryScope.User => ScopeFilter.Tenant | ScopeFilter.User,
    QueryScope.Principal => ScopeFilter.Tenant | ScopeFilter.Principal,
    QueryScope.UserOrPrincipal => ScopeFilter.Tenant | ScopeFilter.User | ScopeFilter.Principal,
    _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, $"Unknown QueryScope value: {scope}")
  };
}
