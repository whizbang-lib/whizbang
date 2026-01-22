namespace Whizbang.Core.Security.Attributes;

/// <summary>
/// Requires specific permission to access rows of this type.
/// </summary>
/// <docs>core-concepts/security#permission-based-rls</docs>
/// <tests>Whizbang.Core.Tests/Security/SecurityAttributeTests.cs</tests>
/// <example>
/// [RequirePermission("orders:read")]
/// public class Order {
///   public string OrderId { get; init; }
/// }
///
/// // Multiple permissions (all must be satisfied)
/// [RequirePermission("orders:read")]
/// [RequirePermission("orders:sensitive")]
/// public class SensitiveOrder {
///   public string OrderId { get; init; }
/// }
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequirePermissionAttribute : Attribute {
  /// <summary>
  /// The required permission.
  /// </summary>
  public Permission Permission { get; }

  /// <summary>
  /// Creates a require permission attribute.
  /// </summary>
  /// <param name="permission">The permission string in resource:action format.</param>
  public RequirePermissionAttribute(string permission) {
    Permission = new Permission(permission);
  }
}
