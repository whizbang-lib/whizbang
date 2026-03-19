namespace Whizbang.Core.Lenses;

/// <summary>
/// Constants for tenant identification in multi-tenant scenarios.
/// </summary>
/// <docs>fundamentals/security/multi-tenancy</docs>
/// <tests>Whizbang.Core.Tests/Dispatch/SystemDispatcherBuilderTests.cs</tests>
public static class TenantConstants {
  /// <summary>
  /// Represents "all tenants" for cross-tenant system operations.
  /// Use with <c>AsSystem().ForAllTenants()</c> for explicit cross-tenant scope.
  /// Value is "*" (asterisk).
  /// </summary>
  /// <remarks>
  /// <para>Using "*" as the value because:</para>
  /// <list type="bullet">
  ///   <item><description>null is ambiguous (forgot to set vs intentional)</description></item>
  ///   <item><description>Universally understood as "wildcard/all"</description></item>
  ///   <item><description>Easy to identify in logs and database queries</description></item>
  /// </list>
  /// </remarks>
  /// <example>
  /// <code>
  /// // Cross-tenant system operation
  /// await _dispatcher.AsSystem().ForAllTenants().SendAsync(command);
  /// </code>
  /// </example>
  // Intentionally using PascalCase per CA1707 (avoid underscores in identifiers)
#pragma warning disable IDE1006 // Naming rule violation - using PascalCase for readability
  public const string AllTenants = "*";
#pragma warning restore IDE1006
}
