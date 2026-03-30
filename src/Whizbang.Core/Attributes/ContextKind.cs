namespace Whizbang.Core.Attributes;

/// <summary>
/// Specifies the kind of security context value to auto-populate on a message property.
/// Values are sourced from the current SecurityContext during message dispatch.
/// </summary>
/// <remarks>
/// <para>
/// Use with <see cref="PopulateFromContextAttribute"/> to automatically capture
/// security context information on messages for audit trails and multi-tenancy.
/// </para>
/// <para>
/// <list type="bullet">
/// <item><description><see cref="UserId"/> - The current user's identifier</description></item>
/// <item><description><see cref="TenantId"/> - The current tenant's identifier</description></item>
/// </list>
/// </para>
/// </remarks>
/// <docs>extending/attributes/auto-populate</docs>
/// <tests>tests/Whizbang.Core.Tests/AutoPopulate/PopulateFromContextAttributeTests.cs</tests>
public enum ContextKind {
  /// <summary>
  /// Populated with the current user's identifier from SecurityContext.UserId.
  /// Useful for audit trails and tracking who initiated an action.
  /// </summary>
  UserId = 0,

  /// <summary>
  /// Populated with the current tenant's identifier from SecurityContext.TenantId.
  /// Essential for multi-tenant applications to track data ownership.
  /// </summary>
  TenantId = 1
}
