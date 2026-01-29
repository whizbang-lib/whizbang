namespace Whizbang.Core.Security;

/// <summary>
/// Named collection of permissions for Role-Based Access Control (RBAC).
/// Roles aggregate permissions into logical groups that can be assigned to users.
/// </summary>
/// <docs>core-concepts/security#roles</docs>
/// <tests>Whizbang.Core.Tests/Security/RoleTests.cs</tests>
/// <example>
/// // Define a role with specific permissions
/// var orderManager = new Role {
///   Name = "OrderManager",
///   Permissions = new HashSet&lt;Permission&gt; {
///     Permission.Read("orders"),
///     Permission.Write("orders"),
///     Permission.Delete("orders")
///   }
/// };
///
/// // Check if role grants a permission
/// orderManager.HasPermission(Permission.Read("orders"));  // true
/// orderManager.HasPermission(Permission.Admin("orders")); // false
/// </example>
public sealed record Role {
  /// <summary>
  /// The name of the role.
  /// </summary>
  public required string Name { get; init; }

  /// <summary>
  /// The permissions granted by this role.
  /// </summary>
  public required IReadOnlySet<Permission> Permissions { get; init; }

  /// <summary>
  /// Checks if this role grants the specified permission.
  /// Supports wildcard matching where:
  /// - "resource:*" matches any action on that resource
  /// - "*:action" matches that action on any resource
  /// - "*:*" matches any permission
  /// </summary>
  /// <param name="permission">The permission to check.</param>
  /// <returns>True if this role grants the permission; otherwise, false.</returns>
  public bool HasPermission(Permission permission) =>
    Permissions.Any(p => p.Matches(permission));
}
