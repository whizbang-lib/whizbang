#pragma warning disable S3604 // Primary constructor field/property initializers are intentional

using Whizbang.Core.Lenses;

namespace Whizbang.Core.Security.Attributes;

/// <summary>
/// Marks a property for automatic row-level security filtering.
/// Applied to scope properties (TenantId, UserId, etc.) on models.
/// </summary>
/// <docs>fundamentals/security/security#row-level-security</docs>
/// <tests>Whizbang.Core.Tests/Security/SecurityAttributeTests.cs</tests>
/// <example>
/// public class Order {
///   [Scoped]
///   public string TenantId { get; init; }
///
///   [Scoped(ScopeFilter.Tenant | ScopeFilter.User)]
///   public string UserId { get; init; }
/// }
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ScopedAttribute(ScopeFilter filter = ScopeFilter.Tenant) : Attribute {
  /// <summary>
  /// The scope filter to apply for this property.
  /// </summary>
  public ScopeFilter Filter { get; } = filter;
}
