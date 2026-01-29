using Whizbang.Core.Security;

namespace Whizbang.Core.Lenses;

/// <summary>
/// Multi-tenancy and security scope for perspective rows.
/// Stored as JSONB/JSON in scope column, SEPARATE from the data model.
/// </summary>
/// <docs>core-concepts/scoping#perspective-scope</docs>
/// <tests>Whizbang.Core.Tests/Scoping/PerspectiveScopeTests.cs</tests>
/// <example>
/// var scope = new PerspectiveScope {
///   TenantId = "tenant-123",
///   UserId = "user-456",
///   AllowedPrincipals = [
///     SecurityPrincipalId.Group("sales-team"),
///     SecurityPrincipalId.User("manager-789")
///   ]
/// };
///
/// // Access via indexer
/// var tenant = scope["TenantId"];      // "tenant-123"
/// var custom = scope["CustomField"];   // from Extensions
/// </example>
public record PerspectiveScope {
  /// <summary>
  /// The tenant identifier for multi-tenancy isolation.
  /// </summary>
  public string? TenantId { get; init; }

  /// <summary>
  /// The customer identifier for customer-level isolation.
  /// </summary>
  public string? CustomerId { get; init; }

  /// <summary>
  /// The user identifier for user-level isolation.
  /// </summary>
  public string? UserId { get; init; }

  /// <summary>
  /// The organization identifier for organization-level isolation.
  /// </summary>
  public string? OrganizationId { get; init; }

  /// <summary>
  /// Security principals (users, groups, services) that have access to this record.
  /// Enables fine-grained access control: "who can see this record?"
  /// Query: WHERE AllowedPrincipals OVERLAPS caller.SecurityPrincipals
  /// </summary>
  /// <example>
  /// AllowedPrincipals = [
  ///   SecurityPrincipalId.Group("sales-team"),
  ///   SecurityPrincipalId.User("manager-456")
  /// ]
  /// </example>
  public IReadOnlyList<SecurityPrincipalId>? AllowedPrincipals { get; init; }

  /// <summary>
  /// Additional scope values as key-value pairs.
  /// Enables extensibility without schema changes.
  /// </summary>
  public IReadOnlyDictionary<string, string?>? Extensions { get; init; }

  /// <summary>
  /// Indexer for unified access to standard and extension properties.
  /// </summary>
  /// <param name="key">The property name to access.</param>
  /// <returns>The value of the property, or null if not found.</returns>
  public string? this[string key] => key switch {
    nameof(TenantId) => TenantId,
    nameof(CustomerId) => CustomerId,
    nameof(UserId) => UserId,
    nameof(OrganizationId) => OrganizationId,
    _ => Extensions?.GetValueOrDefault(key)
  };
}
