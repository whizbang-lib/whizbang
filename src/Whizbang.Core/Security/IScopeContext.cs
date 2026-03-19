using Whizbang.Core.Lenses;

namespace Whizbang.Core.Security;

/// <summary>
/// Ambient security context for the current operation.
/// Populated from HTTP claims, message headers, or explicit injection.
/// </summary>
/// <docs>fundamentals/security/security#scope-context</docs>
/// <tests>Whizbang.Core.Tests/Security/ScopeContextTests.cs</tests>
/// <example>
/// // Check permissions
/// if (scopeContext.HasPermission(Permission.Read("orders"))) {
///   // Load orders
/// }
///
/// // Check roles
/// if (scopeContext.HasRole("Admin")) {
///   // Admin-only functionality
/// }
///
/// // Check group membership
/// if (scopeContext.IsMemberOfAny(
///   SecurityPrincipalId.Group("sales-team"),
///   SecurityPrincipalId.Group("support-team"))) {
///   // Team-specific functionality
/// }
/// </example>
public interface IScopeContext {
  /// <summary>
  /// Current scope values (TenantId, UserId, etc.).
  /// </summary>
  PerspectiveScope Scope { get; }

  /// <summary>
  /// Role names assigned to current caller.
  /// </summary>
  IReadOnlySet<string> Roles { get; }

  /// <summary>
  /// Permissions from roles + direct grants.
  /// </summary>
  IReadOnlySet<Permission> Permissions { get; }

  /// <summary>
  /// All security principal IDs the caller belongs to.
  /// Pre-flattened: includes user ID + all group memberships (direct + inherited from nested groups).
  /// Used for "array overlap" filtering with PerspectiveScope.AllowedPrincipals.
  /// </summary>
  /// <example>
  /// // User "alice" is in "sales-team" which is in "all-employees"
  /// SecurityPrincipals = {
  ///   "user:alice",
  ///   "group:sales-team",
  ///   "group:all-employees"  // inherited
  /// }
  /// </example>
  IReadOnlySet<SecurityPrincipalId> SecurityPrincipals { get; }

  /// <summary>
  /// Raw claims from authentication.
  /// </summary>
  IReadOnlyDictionary<string, string> Claims { get; }

  /// <summary>
  /// The actual principal who initiated this operation (never hidden).
  /// Null for true system operations with no user involvement.
  /// For impersonation scenarios, this shows who is actually performing the action.
  /// </summary>
  /// <example>
  /// // Admin impersonating user for debugging
  /// ActualPrincipal = "admin@example.com"
  /// EffectivePrincipal = "user@example.com"
  /// </example>
  string? ActualPrincipal { get; }

  /// <summary>
  /// The effective principal the operation runs as.
  /// May differ from ActualPrincipal when impersonating.
  /// For system operations, this is "SYSTEM".
  /// </summary>
  string? EffectivePrincipal { get; }

  /// <summary>
  /// Type of security context establishment.
  /// Indicates whether this is a normal user operation, system operation,
  /// impersonation, or service account.
  /// </summary>
  SecurityContextType ContextType { get; }

  /// <summary>
  /// Check if caller has specific permission (with wildcard support).
  /// </summary>
  /// <param name="permission">The permission to check.</param>
  /// <returns>True if the caller has the permission; otherwise, false.</returns>
  bool HasPermission(Permission permission);

  /// <summary>
  /// Check if caller has ANY of the specified permissions.
  /// </summary>
  /// <param name="permissions">The permissions to check.</param>
  /// <returns>True if the caller has at least one of the permissions; otherwise, false.</returns>
  bool HasAnyPermission(params Permission[] permissions);

  /// <summary>
  /// Check if caller has ALL of the specified permissions.
  /// </summary>
  /// <param name="permissions">The permissions to check.</param>
  /// <returns>True if the caller has all of the permissions; otherwise, false.</returns>
  bool HasAllPermissions(params Permission[] permissions);

  /// <summary>
  /// Check if caller has specific role.
  /// </summary>
  /// <param name="roleName">The role name to check.</param>
  /// <returns>True if the caller has the role; otherwise, false.</returns>
  bool HasRole(string roleName);

  /// <summary>
  /// Check if caller has ANY of the specified roles.
  /// </summary>
  /// <param name="roleNames">The role names to check.</param>
  /// <returns>True if the caller has at least one of the roles; otherwise, false.</returns>
  bool HasAnyRole(params string[] roleNames);

  /// <summary>
  /// Check if caller is member of any of the specified security principals.
  /// </summary>
  /// <param name="principals">The principals to check membership against.</param>
  /// <returns>True if the caller is a member of at least one principal; otherwise, false.</returns>
  bool IsMemberOfAny(params SecurityPrincipalId[] principals);

  /// <summary>
  /// Check if caller is member of all of the specified security principals.
  /// </summary>
  /// <param name="principals">The principals to check membership against.</param>
  /// <returns>True if the caller is a member of all principals; otherwise, false.</returns>
  bool IsMemberOfAll(params SecurityPrincipalId[] principals);
}
