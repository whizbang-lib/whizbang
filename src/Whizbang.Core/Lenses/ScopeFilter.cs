namespace Whizbang.Core.Lenses;

/// <summary>
/// Composable scope filters for lens queries.
/// Combine with bitwise OR to apply multiple filters.
/// </summary>
/// <docs>core-concepts/scoping#composable-filters</docs>
/// <tests>Whizbang.Core.Tests/Lenses/ScopeFilterTests.cs</tests>
/// <example>
/// // Single filter
/// var tenantFilter = ScopeFilter.Tenant;
///
/// // Combined filters (all conditions must match)
/// var userFilter = ScopeFilter.Tenant | ScopeFilter.User;
///
/// // User OR Principal access (special OR logic)
/// var sharedFilter = ScopeFilter.Tenant | ScopeFilter.User | ScopeFilter.Principal;
/// </example>
[Flags]
public enum ScopeFilter {
  /// <summary>
  /// No filtering - full access to all data (admin/global).
  /// </summary>
  None = 0,

  /// <summary>
  /// Filter by TenantId (WHERE TenantId = ?).
  /// </summary>
  Tenant = 1 << 0,

  /// <summary>
  /// Filter by OrganizationId (AND OrganizationId = ?).
  /// </summary>
  Organization = 1 << 1,

  /// <summary>
  /// Filter by CustomerId (AND CustomerId = ?).
  /// </summary>
  Customer = 1 << 2,

  /// <summary>
  /// Filter by UserId (AND UserId = ?).
  /// </summary>
  User = 1 << 3,

  /// <summary>
  /// Filter by security principal membership.
  /// WHERE AllowedPrincipals OVERLAPS caller.SecurityPrincipals
  /// </summary>
  Principal = 1 << 4
}

/// <summary>
/// Extension methods and common filter patterns for ScopeFilter.
/// </summary>
/// <docs>core-concepts/scoping#filter-patterns</docs>
/// <tests>Whizbang.Core.Tests/Lenses/ScopeFilterTests.cs</tests>
public static class ScopeFilterExtensions {
  /// <summary>
  /// Common pattern: Tenant + User isolation.
  /// WHERE TenantId = ? AND UserId = ?
  /// </summary>
  public static ScopeFilter TenantUser => ScopeFilter.Tenant | ScopeFilter.User;

  /// <summary>
  /// Common pattern: Tenant + Principal-based access.
  /// WHERE TenantId = ? AND AllowedPrincipals ?| [...]
  /// </summary>
  public static ScopeFilter TenantPrincipal => ScopeFilter.Tenant | ScopeFilter.Principal;

  /// <summary>
  /// Common pattern: Tenant + User's own OR shared with them.
  /// Note: User + Principal are OR'd, not AND'd.
  /// WHERE TenantId = ? AND (UserId = ? OR AllowedPrincipals ?| [...])
  /// </summary>
  public static ScopeFilter TenantUserOrPrincipal =>
    ScopeFilter.Tenant | ScopeFilter.User | ScopeFilter.Principal;
}
