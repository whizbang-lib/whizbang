using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Core.Dispatch;

/// <summary>
/// Extension methods for explicit security context on <see cref="IDispatcher"/>.
/// Enables system operations and impersonation with full audit trail.
/// </summary>
/// <remarks>
/// <para>
/// These extensions provide explicit security context for dispatch operations,
/// replacing the need for marker interfaces or implicit fallbacks.
/// </para>
/// <para>
/// <b>Key Design Principles</b>:
/// <list type="bullet">
///   <item>No implicit fallback to elevated permissions (security hole)</item>
///   <item>Code must explicitly request system or elevated context</item>
///   <item>Full audit trail captures both actual and effective identity</item>
///   <item>Previous context is restored after dispatch completes</item>
///   <item>Explicit tenant strategy required before dispatch (compile-time enforcement)</item>
/// </list>
/// </para>
/// <para>
/// <b>BREAKING CHANGE</b>: Both <see cref="AsSystem"/> and <see cref="RunAs"/> now return
/// intermediate builders that require explicit tenant strategy selection before dispatching.
/// You must call <c>.ForAllTenants()</c>, <c>.ForTenant(id)</c>, or <c>.KeepTenant()</c>
/// before calling dispatch methods.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Timer/scheduler job - cross-tenant system operation
/// await dispatcher.AsSystem().ForAllTenants().SendAsync(new ReseedSystemEvent());
///
/// // Admin triggering tenant-specific system operation
/// await dispatcher.AsSystem().KeepTenant().SendAsync(new MaintenanceCommand());
///
/// // System operation for a specific tenant
/// await dispatcher.AsSystem().ForTenant("tenant-123").SendAsync(new TenantMaintenanceCommand());
///
/// // Support impersonating a user in their tenant
/// await dispatcher.RunAs("target-user").ForTenant("user-tenant").SendAsync(command);
/// </code>
/// </example>
/// <docs>fundamentals/security/scope-propagation</docs>
/// <tests>Whizbang.Core.Tests/Dispatch/DispatcherSecurityBuilderTests.cs</tests>
/// <tests>Whizbang.Core.Tests/Dispatch/SystemDispatcherBuilderTests.cs</tests>
/// <tests>Whizbang.Core.Tests/Dispatch/ImpersonationDispatcherBuilderTests.cs</tests>
public static class DispatcherSecurityExtensions {
  /// <summary>
  /// Starts a system operation builder that requires explicit tenant strategy.
  /// If a user context exists, it's preserved as ActualPrincipal for audit.
  /// Use for timers, schedulers, background jobs, or user-initiated system operations.
  /// </summary>
  /// <param name="dispatcher">The dispatcher instance.</param>
  /// <returns>
  /// A <see cref="SystemDispatcherBuilder"/> that requires calling
  /// <see cref="SystemDispatcherBuilder.ForAllTenants"/>,
  /// <see cref="SystemDispatcherBuilder.ForTenant"/>, or
  /// <see cref="SystemDispatcherBuilder.KeepTenant"/> before dispatching.
  /// </returns>
  /// <remarks>
  /// <para>
  /// <b>BREAKING CHANGE</b>: This method now returns <see cref="SystemDispatcherBuilder"/>
  /// instead of <see cref="DispatcherSecurityBuilder"/>. You must choose a tenant strategy
  /// before dispatching.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// // Timer job - cross-tenant operation
  /// await dispatcher.AsSystem().ForAllTenants().SendAsync(new ReseedSystemEvent());
  ///
  /// // Admin clicking "Run as System" button - keep current tenant
  /// await dispatcher.AsSystem().KeepTenant().SendAsync(new MaintenanceCommand());
  ///
  /// // COMPILE ERROR: Must choose tenant strategy first!
  /// // await dispatcher.AsSystem().SendAsync(command);
  /// </code>
  /// </example>
  /// <docs>fundamentals/security/scope-propagation#system-operations</docs>
  /// <tests>Whizbang.Core.Tests/Dispatch/SystemDispatcherBuilderTests.cs</tests>
  public static SystemDispatcherBuilder AsSystem(this IDispatcher dispatcher) {
    // Capture current context (uses static accessor)
    var currentContext = ScopeContextAccessor.CurrentContext;
    var actualPrincipal = currentContext?.Scope?.UserId;
    var ambientTenantId = currentContext?.Scope?.TenantId;

    return new SystemDispatcherBuilder(
        dispatcher,
        actualPrincipal: actualPrincipal,
        ambientTenantId: ambientTenantId);
  }

  /// <summary>
  /// Starts an impersonation operation builder that requires explicit tenant strategy.
  /// Audit trail shows both actual user AND effective identity.
  /// </summary>
  /// <param name="dispatcher">The dispatcher instance.</param>
  /// <param name="effectiveIdentity">The identity to run as (e.g., "target-user@example.com").</param>
  /// <returns>
  /// An <see cref="ImpersonationDispatcherBuilder"/> that requires calling
  /// <see cref="ImpersonationDispatcherBuilder.ForAllTenants"/>,
  /// <see cref="ImpersonationDispatcherBuilder.ForTenant"/>, or
  /// <see cref="ImpersonationDispatcherBuilder.KeepTenant"/> before dispatching.
  /// </returns>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="effectiveIdentity"/> is null.
  /// </exception>
  /// <exception cref="ArgumentException">
  /// Thrown when <paramref name="effectiveIdentity"/> is empty or whitespace.
  /// </exception>
  /// <remarks>
  /// <para>
  /// <b>BREAKING CHANGE</b>: This method now returns <see cref="ImpersonationDispatcherBuilder"/>
  /// instead of <see cref="DispatcherSecurityBuilder"/>. You must choose a tenant strategy
  /// before dispatching.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// // Support staff impersonating a customer in their tenant
  /// await dispatcher.RunAs("customer@example.com").ForTenant("customer-tenant").SendAsync(command);
  /// // Audit shows: ActualPrincipal="support@example.com", EffectivePrincipal="customer@example.com"
  ///
  /// // Admin impersonating user in current tenant context
  /// await dispatcher.RunAs("target-user").KeepTenant().SendAsync(command);
  ///
  /// // COMPILE ERROR: Must choose tenant strategy first!
  /// // await dispatcher.RunAs("user").SendAsync(command);
  /// </code>
  /// </example>
  /// <docs>fundamentals/security/scope-propagation#impersonation-operations</docs>
  /// <tests>Whizbang.Core.Tests/Dispatch/ImpersonationDispatcherBuilderTests.cs</tests>
  public static ImpersonationDispatcherBuilder RunAs(this IDispatcher dispatcher, string effectiveIdentity) {
    ArgumentNullException.ThrowIfNull(effectiveIdentity);
    ArgumentException.ThrowIfNullOrWhiteSpace(effectiveIdentity);

    // Capture current context (uses static accessor)
    var currentContext = ScopeContextAccessor.CurrentContext;
    var actualPrincipal = currentContext?.Scope?.UserId;
    var ambientTenantId = currentContext?.Scope?.TenantId;

    return new ImpersonationDispatcherBuilder(
        dispatcher,
        effectiveIdentity: effectiveIdentity,
        actualPrincipal: actualPrincipal,
        ambientTenantId: ambientTenantId);
  }
}
