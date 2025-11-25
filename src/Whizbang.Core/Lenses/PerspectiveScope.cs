namespace Whizbang.Core.Lenses;

/// <summary>
/// Multi-tenancy and security scope for perspective rows.
/// Contains identifiers for tenant isolation and access control.
/// Stored as JSONB/JSON in scope column.
/// </summary>
public record PerspectiveScope {
  /// <summary>
  /// Tenant identifier for multi-tenant applications.
  /// Enables efficient tenant-isolated queries (e.g., WHERE scope->>'TenantId' = 'tenant-123').
  /// </summary>
  public string? TenantId { get; init; }

  /// <summary>
  /// Customer identifier.
  /// Useful for customer-scoped queries (e.g., get all orders for a customer).
  /// </summary>
  public string? CustomerId { get; init; }

  /// <summary>
  /// User identifier.
  /// For user-scoped queries (e.g., my orders, my profile).
  /// </summary>
  public string? UserId { get; init; }

  /// <summary>
  /// Organization identifier (for B2B scenarios).
  /// Enables org-level data isolation.
  /// </summary>
  public string? OrganizationId { get; init; }
}
