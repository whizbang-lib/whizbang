namespace Whizbang.Core.Observability;

/// <summary>
/// Security context for a message at a specific hop.
/// Contains authentication and authorization metadata that can change from hop to hop.
/// Extensible for future security requirements (roles, claims, permissions, etc.).
/// </summary>
public record SecurityContext {
  /// <summary>
  /// User identifier for authentication and authorization.
  /// </summary>
  public string? UserId { get; init; }

  /// <summary>
  /// Tenant identifier for multi-tenancy scenarios.
  /// </summary>
  public string? TenantId { get; init; }
}
