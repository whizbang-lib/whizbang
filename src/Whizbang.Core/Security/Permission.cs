using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Core.Security;

/// <summary>
/// Type-safe permission identifier using resource:action pattern.
/// Supports wildcard matching for flexible permission hierarchies.
/// </summary>
/// <docs>core-concepts/security#permissions</docs>
/// <tests>Whizbang.Core.Tests/Security/PermissionTests.cs</tests>
/// <example>
/// // Create specific permissions
/// var readOrders = Permission.Read("orders");      // "orders:read"
/// var adminUsers = Permission.Admin("users");      // "users:admin"
///
/// // Create from string
/// Permission permission = "orders:write";
///
/// // Wildcard permissions
/// var allOrders = Permission.All("orders");        // "orders:*"
/// var globalAdmin = new Permission("*:*");         // matches everything
///
/// // Check if permission satisfies requirement
/// allOrders.Matches(readOrders);  // true - "orders:*" matches "orders:read"
/// </example>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
  Justification = "Permission is the correct domain term for this type")]
public readonly record struct Permission : IEquatable<Permission> {
  /// <summary>
  /// The permission value in resource:action format.
  /// </summary>
  public string Value { get; }

  /// <summary>
  /// Creates a new permission with the specified value.
  /// </summary>
  /// <param name="value">The permission string in resource:action format.</param>
  /// <exception cref="ArgumentException">Thrown when value is null, empty, or whitespace.</exception>
  public Permission(string value) {
    ArgumentException.ThrowIfNullOrWhiteSpace(value);
    Value = value;
  }

  /// <summary>
  /// Implicitly converts a Permission to its string value.
  /// </summary>
  public static implicit operator string(Permission p) => p.Value;

  /// <summary>
  /// Implicitly converts a string to a Permission.
  /// </summary>
  public static implicit operator Permission(string s) => new(s);

  /// <summary>
  /// Creates a read permission for the specified resource.
  /// </summary>
  /// <param name="resource">The resource name (e.g., "orders", "users").</param>
  /// <returns>A permission like "orders:read".</returns>
  public static Permission Read(string resource) => new($"{resource}:read");

  /// <summary>
  /// Creates a write permission for the specified resource.
  /// </summary>
  /// <param name="resource">The resource name (e.g., "orders", "users").</param>
  /// <returns>A permission like "orders:write".</returns>
  public static Permission Write(string resource) => new($"{resource}:write");

  /// <summary>
  /// Creates a delete permission for the specified resource.
  /// </summary>
  /// <param name="resource">The resource name (e.g., "orders", "users").</param>
  /// <returns>A permission like "orders:delete".</returns>
  public static Permission Delete(string resource) => new($"{resource}:delete");

  /// <summary>
  /// Creates an admin permission for the specified resource.
  /// </summary>
  /// <param name="resource">The resource name (e.g., "orders", "users").</param>
  /// <returns>A permission like "orders:admin".</returns>
  public static Permission Admin(string resource) => new($"{resource}:admin");

  /// <summary>
  /// Creates a wildcard permission that matches all actions on the specified resource.
  /// </summary>
  /// <param name="resource">The resource name (e.g., "orders", "users").</param>
  /// <returns>A permission like "orders:*" that matches any action on the resource.</returns>
  public static Permission All(string resource) => new($"{resource}:*");

  /// <summary>
  /// Checks if this permission satisfies the required permission.
  /// Supports wildcard matching where:
  /// - "*:*" matches any permission
  /// - "resource:*" matches any action on that resource
  /// - "*:action" matches that action on any resource
  /// </summary>
  /// <param name="required">The permission that is required.</param>
  /// <returns>True if this permission satisfies the requirement; otherwise, false.</returns>
  /// <example>
  /// var held = new Permission("orders:*");
  /// var required = new Permission("orders:read");
  /// held.Matches(required); // true
  /// </example>
  public bool Matches(Permission required) {
    // Exact match
    if (Value == required.Value) {
      return true;
    }

    // Global wildcard matches everything
    if (Value == "*:*") {
      return true;
    }

    // Parse both permissions
    var parts = Value.Split(':');
    var reqParts = required.Value.Split(':');

    // Must be in resource:action format
    if (parts.Length != 2 || reqParts.Length != 2) {
      return false;
    }

    var resourceMatch = parts[0] == "*" || parts[0] == reqParts[0];
    var actionMatch = parts[1] == "*" || parts[1] == reqParts[1];

    return resourceMatch && actionMatch;
  }

  /// <summary>
  /// Returns the permission value as a string.
  /// </summary>
  public override string ToString() => Value;
}
