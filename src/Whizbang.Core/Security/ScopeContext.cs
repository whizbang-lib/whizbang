using Whizbang.Core.Lenses;

namespace Whizbang.Core.Security;

/// <summary>
/// Default implementation of IScopeContext.
/// </summary>
/// <docs>core-concepts/security#scope-context</docs>
/// <tests>Whizbang.Core.Tests/Security/ScopeContextTests.cs</tests>
public sealed class ScopeContext : IScopeContext {
  /// <inheritdoc />
  public required PerspectiveScope Scope { get; init; }

  /// <inheritdoc />
  public required IReadOnlySet<string> Roles { get; init; }

  /// <inheritdoc />
  public required IReadOnlySet<Permission> Permissions { get; init; }

  /// <inheritdoc />
  public required IReadOnlySet<SecurityPrincipalId> SecurityPrincipals { get; init; }

  /// <inheritdoc />
  public required IReadOnlyDictionary<string, string> Claims { get; init; }

  /// <inheritdoc />
  public bool HasPermission(Permission permission) =>
    Permissions.Any(p => p.Matches(permission));

  /// <inheritdoc />
  public bool HasAnyPermission(params Permission[] permissions) =>
    permissions.Any(HasPermission);

  /// <inheritdoc />
  public bool HasAllPermissions(params Permission[] permissions) =>
    permissions.All(HasPermission);

  /// <inheritdoc />
  public bool HasRole(string roleName) =>
    Roles.Contains(roleName);

  /// <inheritdoc />
  public bool HasAnyRole(params string[] roleNames) =>
    roleNames.Any(Roles.Contains);

  /// <inheritdoc />
  public bool IsMemberOfAny(params SecurityPrincipalId[] principals) =>
    principals.Any(SecurityPrincipals.Contains);

  /// <inheritdoc />
  public bool IsMemberOfAll(params SecurityPrincipalId[] principals) =>
    principals.All(SecurityPrincipals.Contains);

  /// <summary>
  /// Empty context for anonymous/system operations.
  /// </summary>
  public static ScopeContext Empty { get; } = new() {
    Scope = new PerspectiveScope(),
    Roles = new HashSet<string>(),
    Permissions = new HashSet<Permission>(),
    SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
    Claims = new Dictionary<string, string>()
  };
}
