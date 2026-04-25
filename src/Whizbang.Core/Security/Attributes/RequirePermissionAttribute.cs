namespace Whizbang.Core.Security.Attributes;

/// <summary>
/// Requires specific permission to access rows of this type.
/// </summary>
/// <docs>fundamentals/security/security#permission-based-rls</docs>
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
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute(string permission) : Attribute {
  /// <summary>
  /// The required permission.
  /// </summary>
  public Permission Permission { get; } = new Permission(permission);

  /// <summary>
  /// The operation kind this attribute applies to. Default <see cref="ScopeOperation.Any"/>
  /// preserves the legacy "applies to any access" behavior on class targets. Use
  /// <see cref="ScopeOperation.Read"/> for Query-only enforcement and
  /// <see cref="ScopeOperation.Write"/> for Mutation-only enforcement when paired with
  /// HotChocolate's resolver middleware.
  /// </summary>
  public ScopeOperation Operation { get; init; } = ScopeOperation.Any;
}
