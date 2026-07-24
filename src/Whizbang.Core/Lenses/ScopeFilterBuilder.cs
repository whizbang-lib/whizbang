using Whizbang.Core.Security;

namespace Whizbang.Core.Lenses;

/// <summary>
/// Information about scope filters to be applied.
/// </summary>
/// <docs>fundamentals/security/scoping#filter-composition</docs>
/// <tests>Whizbang.Core.Tests/Lenses/ScopeFilterBuilderTests.cs</tests>
public readonly record struct ScopeFilterInfo {
  /// <summary>
  /// The scope filter flags to apply.
  /// </summary>
  public ScopeFilters Filters { get; init; }

  /// <summary>
  /// Tenant ID to filter by (if Tenant flag is set).
  /// </summary>
  public string? TenantId { get; init; }

  /// <summary>
  /// User ID to filter by (if User flag is set).
  /// </summary>
  public string? UserId { get; init; }

  /// <summary>
  /// Organization ID to filter by (if Organization flag is set).
  /// </summary>
  public string? OrganizationId { get; init; }

  /// <summary>
  /// Customer ID to filter by (if Customer flag is set).
  /// </summary>
  public string? CustomerId { get; init; }

  /// <summary>
  /// Security principals to filter by (if Principal flag is set).
  /// Filters records where AllowedPrincipals overlaps with these principals.
  /// </summary>
  public IReadOnlySet<SecurityPrincipalId> SecurityPrincipals { get; init; }

  /// <summary>
  /// When true, User and Principal filters should be OR'd together.
  /// When false, all filters are AND'd together.
  /// </summary>
  /// <remarks>
  /// This special case handles the "my records OR shared with me" pattern:
  /// WHERE TenantId = ? AND (UserId = ? OR AllowedPrincipals ?| [...])
  /// </remarks>
  public bool UseOrLogicForUserAndPrincipal { get; init; }

  /// <summary>
  /// True if no filters are applied.
  /// </summary>
  public bool IsEmpty => Filters == ScopeFilters.None;
}

/// <summary>
/// Builds scope filter information from ScopeFilters flags and context.
/// Handles special OR logic when User + Principal are both specified.
/// </summary>
/// <docs>fundamentals/security/scoping#filter-composition</docs>
/// <tests>Whizbang.Core.Tests/Lenses/ScopeFilterBuilderTests.cs</tests>
public static class ScopeFilterBuilder {
  /// <summary>
  /// Build filter information from flags and context.
  /// </summary>
  /// <remarks>
  /// Filter composition rules:
  /// - Most filters are AND'd together
  /// - User + Principal is special: becomes (UserId = ? OR AllowedPrincipals ?| ?)
  /// - Example: Tenant | User | Principal =>
  ///   WHERE TenantId = ? AND (UserId = ? OR AllowedPrincipals ?| ?)
  /// </remarks>
  /// <param name="filters">The scope filter flags to apply.</param>
  /// <param name="context">The current scope context with values.</param>
  /// <returns>Filter information for query building.</returns>
  /// <exception cref="InvalidOperationException">
  /// Thrown when required scope values are missing.
  /// </exception>
  public static ScopeFilterInfo Build(ScopeFilters filters, IScopeContext context) {
    // Fast path for no filtering
    if (filters == ScopeFilters.None) {
      return new ScopeFilterInfo {
        Filters = ScopeFilters.None,
        SecurityPrincipals = new HashSet<SecurityPrincipalId>()
      };
    }

    // Validate and extract required values
    string? tenantId = null;
    if (filters.HasFlag(ScopeFilters.Tenant)) {
      tenantId = context.Scope.TenantId
        ?? throw new InvalidOperationException("Tenant filter requested but TenantId is not set in scope context.");
    }

    string? userId = null;
    if (filters.HasFlag(ScopeFilters.User)) {
      userId = context.Scope.UserId
        ?? throw new InvalidOperationException("User filter requested but UserId is not set in scope context.");
    }

    string? organizationId = null;
    if (filters.HasFlag(ScopeFilters.Organization)) {
      organizationId = context.Scope.OrganizationId
        ?? throw new InvalidOperationException("Organization filter requested but OrganizationId is not set in scope context.");
    }

    string? customerId = null;
    if (filters.HasFlag(ScopeFilters.Customer)) {
      customerId = context.Scope.CustomerId
        ?? throw new InvalidOperationException("Customer filter requested but CustomerId is not set in scope context.");
    }

    IReadOnlySet<SecurityPrincipalId> principals = new HashSet<SecurityPrincipalId>();
    if (filters.HasFlag(ScopeFilters.Principal)) {
      if (context.SecurityPrincipals.Count == 0) {
        throw new InvalidOperationException("Principal filter requested but SecurityPrincipals is empty in scope context.");
      }
      principals = context.SecurityPrincipals;
    }

    // Determine if we should use OR logic for User + Principal
    var useOrLogic = filters.HasFlag(ScopeFilters.User) && filters.HasFlag(ScopeFilters.Principal);

    return new ScopeFilterInfo {
      Filters = filters,
      TenantId = tenantId,
      UserId = userId,
      OrganizationId = organizationId,
      CustomerId = customerId,
      SecurityPrincipals = principals,
      UseOrLogicForUserAndPrincipal = useOrLogic
    };
  }
}
