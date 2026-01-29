namespace Whizbang.Core.Security;

/// <summary>
/// Configuration options for RBAC/ABAC security.
/// </summary>
/// <docs>core-concepts/security#configuration</docs>
/// <tests>Whizbang.Core.Tests/Security/SecurityOptionsTests.cs</tests>
/// <example>
/// services.AddWhizbang(options => {
///   options.Security
///     .DefineRole("Admin", b => b
///       .HasAllPermissions("orders")
///       .HasAllPermissions("customers"))
///     .DefineRole("Support", b => b
///       .HasReadPermission("orders")
///       .HasWritePermission("tickets"))
///     .ExtractPermissionsFromClaim("permissions")
///     .ExtractRolesFromClaim("roles")
///     .ExtractSecurityPrincipalsFromClaim("groups");
/// });
/// </example>
public sealed class SecurityOptions {
  private readonly Dictionary<string, Role> _roles = [];
  private readonly List<IPermissionExtractor> _extractors = [];

  /// <summary>
  /// Defined roles.
  /// </summary>
  public IReadOnlyDictionary<string, Role> Roles => _roles;

  /// <summary>
  /// Permission extractors.
  /// </summary>
  public IReadOnlyList<IPermissionExtractor> Extractors => _extractors;

  /// <summary>
  /// Define a named role with permissions.
  /// </summary>
  /// <param name="name">The role name.</param>
  /// <param name="configure">Configuration action for the role builder.</param>
  /// <returns>This options instance for chaining.</returns>
  public SecurityOptions DefineRole(string name, Action<RoleBuilder> configure) {
    var builder = new RoleBuilder(name);
    configure(builder);
    _roles[name] = builder.Build();
    return this;
  }

  /// <summary>
  /// Add permission extractor for claims.
  /// </summary>
  /// <param name="extractor">The extractor to add.</param>
  /// <returns>This options instance for chaining.</returns>
  public SecurityOptions ExtractPermissionsFrom(IPermissionExtractor extractor) {
    _extractors.Add(extractor);
    return this;
  }

  /// <summary>
  /// Extract permissions from a claim type.
  /// </summary>
  /// <param name="claimType">The claim type containing permission values.</param>
  /// <returns>This options instance for chaining.</returns>
  public SecurityOptions ExtractPermissionsFromClaim(string claimType) {
    _extractors.Add(new ClaimPermissionExtractor(claimType));
    return this;
  }

  /// <summary>
  /// Extract roles from a claim type.
  /// </summary>
  /// <param name="claimType">The claim type containing role names.</param>
  /// <returns>This options instance for chaining.</returns>
  public SecurityOptions ExtractRolesFromClaim(string claimType) {
    _extractors.Add(new ClaimRoleExtractor(claimType));
    return this;
  }

  /// <summary>
  /// Extract security principals from a claim type.
  /// </summary>
  /// <param name="claimType">The claim type containing security principal IDs.</param>
  /// <returns>This options instance for chaining.</returns>
  public SecurityOptions ExtractSecurityPrincipalsFromClaim(string claimType) {
    _extractors.Add(new ClaimSecurityPrincipalExtractor(claimType));
    return this;
  }
}
