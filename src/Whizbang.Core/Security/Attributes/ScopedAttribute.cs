using Whizbang.Core.Lenses;

namespace Whizbang.Core.Security.Attributes;

/// <summary>
/// Marks a property for automatic row-level security filtering.
/// Applied to scope properties (TenantId, UserId, etc.) on models.
/// </summary>
/// <docs>core-concepts/security#row-level-security</docs>
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
public sealed class ScopedAttribute : Attribute {
  /// <summary>
  /// The scope filter to apply for this property.
  /// </summary>
  public ScopeFilter Filter { get; }

  /// <summary>
  /// Creates a scoped attribute with tenant-level filtering (default).
  /// </summary>
  public ScopedAttribute() : this(ScopeFilter.Tenant) { }

  /// <summary>
  /// Creates a scoped attribute with the specified filter.
  /// </summary>
  /// <param name="filter">The scope filter to apply.</param>
  public ScopedAttribute(ScopeFilter filter) {
    Filter = filter;
  }
}
