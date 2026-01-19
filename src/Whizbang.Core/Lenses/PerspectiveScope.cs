namespace Whizbang.Core.Lenses;

/// <summary>
/// Multi-tenancy and security scope for perspective rows.
/// Contains identifiers for tenant isolation and access control.
/// Stored as JSONB/JSON in scope column.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByScopeFields_ReturnsMatchingRowsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanProjectAcrossColumns_ReturnsAnonymousTypeAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_SupportsCombinedFilters_FromAllColumnsAsync</tests>
public record PerspectiveScope {
  /// <summary>
  /// Tenant identifier for multi-tenant applications.
  /// Enables efficient tenant-isolated queries (e.g., WHERE scope->>'TenantId' = 'tenant-123').
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByScopeFields_ReturnsMatchingRowsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanProjectAcrossColumns_ReturnsAnonymousTypeAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_SupportsCombinedFilters_FromAllColumnsAsync</tests>
  public string? TenantId { get; init; }

  /// <summary>
  /// Customer identifier.
  /// Useful for customer-scoped queries (e.g., get all orders for a customer).
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:SeedPerspectiveAsync</tests>
  public string? CustomerId { get; init; }

  /// <summary>
  /// User identifier.
  /// For user-scoped queries (e.g., my orders, my profile).
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByScopeFields_ReturnsMatchingRowsAsync</tests>
  public string? UserId { get; init; }

  /// <summary>
  /// Organization identifier (for B2B scenarios).
  /// Enables org-level data isolation.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:SeedPerspectiveAsync</tests>
  public string? OrganizationId { get; init; }
}
