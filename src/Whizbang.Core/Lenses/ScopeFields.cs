namespace Whizbang.Core.Lenses;

/// <summary>
/// Bit flags identifying individual fields of <see cref="PerspectiveScope"/>.
/// Used by <see cref="InheritScopeAttribute"/> to declare which fields a perspective
/// inherits from the creating message scope.
/// </summary>
/// <docs>fundamentals/security/scoping#scope-inheritance</docs>
/// <tests>tests/Whizbang.Core.Tests/Scoping/InheritScopeAttributeTests.cs</tests>
[Flags]
public enum ScopeFields {
  /// <summary>No fields. Equivalent to "do not inherit anything".</summary>
  None = 0,
  /// <summary>The <see cref="PerspectiveScope.TenantId"/> field.</summary>
  Tenant = 1,
  /// <summary>The <see cref="PerspectiveScope.UserId"/> field.</summary>
  User = 2,
  /// <summary>The <see cref="PerspectiveScope.CustomerId"/> field.</summary>
  Customer = 4,
  /// <summary>The <see cref="PerspectiveScope.OrganizationId"/> field.</summary>
  Organization = 8,
  /// <summary>The <see cref="PerspectiveScope.AllowedPrincipals"/> list.</summary>
  AllowedPrincipals = 16,
  /// <summary>The <see cref="PerspectiveScope.Extensions"/> list.</summary>
  Extensions = 32,
  /// <summary>Every named field. Equivalent to legacy "copy everything" behavior.</summary>
  All = Tenant | User | Customer | Organization | AllowedPrincipals | Extensions,
}
