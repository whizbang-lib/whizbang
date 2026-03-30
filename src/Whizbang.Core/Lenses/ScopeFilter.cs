namespace Whizbang.Core.Lenses;

/// <summary>
/// Composable scope filters for lens queries.
/// Combine with bitwise OR to apply multiple filters.
/// </summary>
/// <docs>fundamentals/security/scoping#composable-filters</docs>
/// <tests>Whizbang.Core.Tests/Lenses/ScopeFilterTests.cs</tests>
/// <example>
/// // Single filter
/// var tenantFilter = ScopeFilters.Tenant;
///
/// // Combined filters (all conditions must match)
/// var userFilter = ScopeFilters.Tenant | ScopeFilters.User;
///
/// // User OR Principal access (special OR logic)
/// var sharedFilter = ScopeFilters.Tenant | ScopeFilters.User | ScopeFilters.Principal;
/// </example>
[Flags]
public enum ScopeFilters {
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
/// Extension methods and common filter patterns for ScopeFilters.
/// </summary>
/// <docs>fundamentals/security/scoping#filter-patterns</docs>
/// <tests>Whizbang.Core.Tests/Lenses/ScopeFilterTests.cs</tests>
public static class ScopeFilterExtensions {
  /// <summary>
  /// Common pattern: Tenant + User isolation.
  /// WHERE TenantId = ? AND UserId = ?
  /// </summary>
  public static ScopeFilters TenantUser => ScopeFilters.Tenant | ScopeFilters.User;

  /// <summary>
  /// Common pattern: Tenant + Principal-based access.
  /// WHERE TenantId = ? AND AllowedPrincipals ?| [...]
  /// </summary>
  public static ScopeFilters TenantPrincipal => ScopeFilters.Tenant | ScopeFilters.Principal;

  /// <summary>
  /// Common pattern: Tenant + User's own OR shared with them.
  /// Note: User + Principal are OR'd, not AND'd.
  /// WHERE TenantId = ? AND (UserId = ? OR AllowedPrincipals ?| [...])
  /// </summary>
  public static ScopeFilters TenantUserOrPrincipal =>
    ScopeFilters.Tenant | ScopeFilters.User | ScopeFilters.Principal;
}
