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
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Timer/scheduler job - true system operation
/// await dispatcher.AsSystem().SendAsync(new ReseedSystemEvent());
/// // Audit: ContextType=System, ActualPrincipal=null, EffectivePrincipal="SYSTEM"
///
/// // Admin triggering system operation (user context exists)
/// await dispatcher.AsSystem().SendAsync(new MaintenanceCommand());
/// // Audit: ContextType=System, ActualPrincipal="admin@example.com", EffectivePrincipal="SYSTEM"
///
/// // Support impersonating a user
/// await dispatcher.RunAs("target-user").SendAsync(command);
/// // Audit: ContextType=Impersonated, ActualPrincipal="support@example.com", EffectivePrincipal="target-user"
/// </code>
/// </example>
/// <docs>core-concepts/message-security#explicit-security-context-api</docs>
/// <tests>Whizbang.Core.Tests/Dispatch/DispatcherSecurityBuilderTests.cs</tests>
public static class DispatcherSecurityExtensions {
  /// <summary>
  /// Dispatch as system identity.
  /// If a user context exists, it's preserved as ActualPrincipal for audit.
  /// Use for timers, schedulers, background jobs, or user-initiated system operations.
  /// </summary>
  /// <param name="dispatcher">The dispatcher instance.</param>
  /// <returns>A builder for dispatching with system security context.</returns>
  /// <example>
  /// <code>
  /// // Timer job (no user context)
  /// await dispatcher.AsSystem().SendAsync(new ReseedSystemEvent());
  ///
  /// // Admin clicking "Run as System" button
  /// await dispatcher.AsSystem().SendAsync(new MaintenanceCommand());
  /// </code>
  /// </example>
  public static DispatcherSecurityBuilder AsSystem(this IDispatcher dispatcher) {
    // Capture current user as "actual principal" if one exists (uses static accessor)
    var actualPrincipal = ScopeContextAccessor.CurrentContext?.Scope?.UserId;

    return new DispatcherSecurityBuilder(
      dispatcher,
      SecurityContextType.System,
      effectivePrincipal: "SYSTEM",
      actualPrincipal: actualPrincipal);
  }

  /// <summary>
  /// Dispatch as a specific identity (impersonation).
  /// Audit trail shows both actual user AND effective identity.
  /// </summary>
  /// <param name="dispatcher">The dispatcher instance.</param>
  /// <param name="effectiveIdentity">The identity to run as (e.g., "target-user@example.com").</param>
  /// <returns>A builder for dispatching with impersonated security context.</returns>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="effectiveIdentity"/> is null.
  /// </exception>
  /// <exception cref="ArgumentException">
  /// Thrown when <paramref name="effectiveIdentity"/> is empty or whitespace.
  /// </exception>
  /// <example>
  /// <code>
  /// // Support staff impersonating a customer for debugging
  /// await dispatcher.RunAs("customer@example.com").SendAsync(command);
  /// // Audit shows: ActualPrincipal="support@example.com", EffectivePrincipal="customer@example.com"
  /// </code>
  /// </example>
  public static DispatcherSecurityBuilder RunAs(this IDispatcher dispatcher, string effectiveIdentity) {
    ArgumentNullException.ThrowIfNull(effectiveIdentity, nameof(effectiveIdentity));
    ArgumentException.ThrowIfNullOrWhiteSpace(effectiveIdentity, nameof(effectiveIdentity));

    // Capture current user as "actual principal" (uses static accessor)
    var actualPrincipal = ScopeContextAccessor.CurrentContext?.Scope?.UserId;

    return new DispatcherSecurityBuilder(
      dispatcher,
      SecurityContextType.Impersonated,
      effectivePrincipal: effectiveIdentity,
      actualPrincipal: actualPrincipal);
  }
}
