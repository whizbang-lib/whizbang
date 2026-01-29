namespace Whizbang.Core.Security;

/// <summary>
/// Fluent builder for defining roles with permissions.
/// </summary>
/// <docs>core-concepts/security#role-definition</docs>
/// <tests>Whizbang.Core.Tests/Security/RoleBuilderTests.cs</tests>
/// <example>
/// var orderManager = new RoleBuilder("OrderManager")
///   .HasReadPermission("orders")
///   .HasWritePermission("orders")
///   .HasDeletePermission("orders")
///   .HasReadPermission("customers")
///   .Build();
/// </example>
public sealed class RoleBuilder {
  private readonly string _name;
  private readonly HashSet<Permission> _permissions = [];

  /// <summary>
  /// Creates a new role builder with the specified role name.
  /// </summary>
  /// <param name="name">The name of the role.</param>
  public RoleBuilder(string name) => _name = name;

  /// <summary>
  /// Adds a permission to the role.
  /// </summary>
  /// <param name="permission">The permission to add.</param>
  /// <returns>This builder for chaining.</returns>
  public RoleBuilder HasPermission(Permission permission) {
    _permissions.Add(permission);
    return this;
  }

  /// <summary>
  /// Adds a permission from a string value.
  /// </summary>
  /// <param name="permission">The permission string in resource:action format.</param>
  /// <returns>This builder for chaining.</returns>
  public RoleBuilder HasPermission(string permission) =>
    HasPermission(new Permission(permission));

  /// <summary>
  /// Adds a read permission for the specified resource.
  /// </summary>
  /// <param name="resource">The resource name.</param>
  /// <returns>This builder for chaining.</returns>
  public RoleBuilder HasReadPermission(string resource) =>
    HasPermission(Permission.Read(resource));

  /// <summary>
  /// Adds a write permission for the specified resource.
  /// </summary>
  /// <param name="resource">The resource name.</param>
  /// <returns>This builder for chaining.</returns>
  public RoleBuilder HasWritePermission(string resource) =>
    HasPermission(Permission.Write(resource));

  /// <summary>
  /// Adds a delete permission for the specified resource.
  /// </summary>
  /// <param name="resource">The resource name.</param>
  /// <returns>This builder for chaining.</returns>
  public RoleBuilder HasDeletePermission(string resource) =>
    HasPermission(Permission.Delete(resource));

  /// <summary>
  /// Adds an admin permission for the specified resource.
  /// </summary>
  /// <param name="resource">The resource name.</param>
  /// <returns>This builder for chaining.</returns>
  public RoleBuilder HasAdminPermission(string resource) =>
    HasPermission(Permission.Admin(resource));

  /// <summary>
  /// Adds a wildcard permission that matches all actions on the specified resource.
  /// </summary>
  /// <param name="resource">The resource name.</param>
  /// <returns>This builder for chaining.</returns>
  public RoleBuilder HasAllPermissions(string resource) =>
    HasPermission(Permission.All(resource));

  /// <summary>
  /// Builds the role with all configured permissions.
  /// </summary>
  /// <returns>A new Role instance.</returns>
  public Role Build() => new() {
    Name = _name,
    Permissions = _permissions
  };
}
