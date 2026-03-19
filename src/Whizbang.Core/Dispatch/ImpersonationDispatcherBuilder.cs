using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Core.Dispatch;

/// <summary>
/// Intermediate builder for impersonation operations that requires explicit tenant strategy selection.
/// Does NOT have dispatch methods - you must call <see cref="ForAllTenants"/>,
/// <see cref="ForTenant"/>, or <see cref="KeepTenant"/> first.
/// </summary>
/// <remarks>
/// <para>
/// This builder enforces compile-time tenant strategy selection for impersonation operations.
/// The absence of dispatch methods (SendAsync, PublishAsync, etc.) on this type ensures
/// developers must explicitly choose a tenant strategy before dispatching.
/// </para>
/// <para>
/// <b>Impersonation</b> allows an admin or service to perform operations on behalf of another user
/// while maintaining full audit trail of both the actual principal (who initiated) and the
/// effective principal (who the operation runs as).
/// </para>
/// <para>
/// <b>Tenant Strategies</b>:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <see cref="ForAllTenants"/> - Cross-tenant operations using <see cref="TenantConstants.AllTenants"/>
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="ForTenant"/> - Explicit tenant scope for tenant-specific operations
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="KeepTenant"/> - Preserve ambient tenant from current context
///     </description>
///   </item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // Support agent debugging a user's issue in their tenant
/// await _dispatcher.RunAs("target-user@example.com").ForTenant("user-tenant").SendAsync(debugCommand);
///
/// // Admin performing action on behalf of user, keeping current tenant
/// await _dispatcher.RunAs("target-user").KeepTenant().SendAsync(command);
///
/// // Cross-tenant admin operation (rare)
/// await _dispatcher.RunAs("admin-system").ForAllTenants().SendAsync(systemCommand);
///
/// // COMPILE ERROR: Must choose tenant strategy first!
/// // await _dispatcher.RunAs("user").SendAsync(command);
/// </code>
/// </example>
/// <docs>fundamentals/security/scope-propagation</docs>
/// <tests>Whizbang.Core.Tests/Dispatch/ImpersonationDispatcherBuilderTests.cs</tests>
public sealed class ImpersonationDispatcherBuilder {
  private readonly IDispatcher _dispatcher;
  private readonly string _effectiveIdentity;
  private readonly string? _actualPrincipal;
  private readonly string? _ambientTenantId;

  /// <summary>
  /// Creates a new impersonation builder that requires explicit tenant strategy selection.
  /// </summary>
  /// <param name="dispatcher">The dispatcher to use for sending messages.</param>
  /// <param name="effectiveIdentity">The identity to impersonate (who the operation runs as).</param>
  /// <param name="actualPrincipal">The actual principal (who initiated the operation).</param>
  /// <param name="ambientTenantId">The ambient tenant ID from current context (used by <see cref="KeepTenant"/>).</param>
  internal ImpersonationDispatcherBuilder(
      IDispatcher dispatcher,
      string effectiveIdentity,
      string? actualPrincipal,
      string? ambientTenantId) {
    _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    ArgumentException.ThrowIfNullOrWhiteSpace(effectiveIdentity, nameof(effectiveIdentity));
    _effectiveIdentity = effectiveIdentity;
    _actualPrincipal = actualPrincipal;
    _ambientTenantId = ambientTenantId;
  }

  /// <summary>
  /// Explicitly marks this as a cross-tenant impersonation operation affecting all tenants.
  /// Use sparingly - most impersonation operations should be tenant-scoped.
  /// </summary>
  /// <returns>A <see cref="DispatcherSecurityBuilder"/> with TenantId set to <see cref="TenantConstants.AllTenants"/>.</returns>
  /// <remarks>
  /// <para>
  /// This method sets the tenant ID to the special <see cref="TenantConstants.AllTenants"/> value ("*"),
  /// which indicates the impersonation operation intentionally affects all tenants or has no tenant scope.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// // Admin running system-wide operation as a specific user
  /// await _dispatcher.RunAs("service-account").ForAllTenants().SendAsync(crossTenantCommand);
  /// </code>
  /// </example>
  /// <docs>fundamentals/security/scope-propagation#cross-tenant-impersonation</docs>
  /// <tests>Whizbang.Core.Tests/Dispatch/ImpersonationDispatcherBuilderTests.cs:RunAs_ForAllTenants_SetsTenantIdToAllTenantsConstantAsync</tests>
  public DispatcherSecurityBuilder ForAllTenants() {
    return new DispatcherSecurityBuilder(
        _dispatcher,
        SecurityContextType.Impersonated,
        effectivePrincipal: _effectiveIdentity,
        actualPrincipal: _actualPrincipal,
        tenantId: TenantConstants.AllTenants);
  }

  /// <summary>
  /// Scopes this impersonation operation to a specific tenant.
  /// </summary>
  /// <param name="tenantId">The tenant ID to scope the operation to.</param>
  /// <returns>A <see cref="DispatcherSecurityBuilder"/> with the explicit tenant ID.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="tenantId"/> is null.</exception>
  /// <exception cref="ArgumentException">Thrown when <paramref name="tenantId"/> is empty or whitespace.</exception>
  /// <remarks>
  /// <para>
  /// This is the most common pattern for impersonation operations. Use this when you have
  /// an explicit tenant ID from the target user's context or business logic.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// // Support agent debugging issue in user's tenant
  /// await _dispatcher.RunAs("target-user@example.com").ForTenant("user-tenant").SendAsync(debugCommand);
  ///
  /// // Admin testing user's workflow
  /// await _dispatcher.RunAs(targetUserId).ForTenant(targetUserTenantId).SendAsync(testCommand);
  /// </code>
  /// </example>
  /// <docs>fundamentals/security/scope-propagation#tenant-scoped-impersonation</docs>
  /// <tests>Whizbang.Core.Tests/Dispatch/ImpersonationDispatcherBuilderTests.cs:RunAs_ForTenant_SetsExplicitTenantIdAsync</tests>
  public DispatcherSecurityBuilder ForTenant(string tenantId) {
    ArgumentNullException.ThrowIfNull(tenantId, nameof(tenantId));
    ArgumentException.ThrowIfNullOrWhiteSpace(tenantId, nameof(tenantId));

    return new DispatcherSecurityBuilder(
        _dispatcher,
        SecurityContextType.Impersonated,
        effectivePrincipal: _effectiveIdentity,
        actualPrincipal: _actualPrincipal,
        tenantId: tenantId);
  }

  /// <summary>
  /// Preserves the ambient tenant from the current context.
  /// Throws if no ambient tenant exists - use <see cref="ForAllTenants"/> for cross-tenant operations
  /// or <see cref="ForTenant"/> for explicit tenant scope.
  /// </summary>
  /// <returns>A <see cref="DispatcherSecurityBuilder"/> with the ambient tenant ID.</returns>
  /// <exception cref="InvalidOperationException">Thrown when no ambient tenant context exists.</exception>
  /// <remarks>
  /// <para>
  /// This method is useful when impersonating a user within the same tenant context
  /// as the current request. It fails fast if no tenant context exists, ensuring
  /// developers make an explicit choice rather than accidentally creating tenantless data.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// // Admin impersonating user within current tenant context
  /// await _dispatcher.RunAs(targetUserId).KeepTenant().SendAsync(command);
  /// </code>
  /// </example>
  /// <docs>fundamentals/security/scope-propagation#preserve-ambient-tenant</docs>
  /// <tests>Whizbang.Core.Tests/Dispatch/ImpersonationDispatcherBuilderTests.cs:RunAs_KeepTenant_PreservesAmbientTenantIdAsync</tests>
  public DispatcherSecurityBuilder KeepTenant() {
    if (string.IsNullOrEmpty(_ambientTenantId)) {
      throw new InvalidOperationException(
          "KeepTenant() called but no ambient tenant context exists. " +
          "Use ForAllTenants() for cross-tenant operations or ForTenant(id) for explicit tenant scope.");
    }

    return new DispatcherSecurityBuilder(
        _dispatcher,
        SecurityContextType.Impersonated,
        effectivePrincipal: _effectiveIdentity,
        actualPrincipal: _actualPrincipal,
        tenantId: _ambientTenantId);
  }
}
