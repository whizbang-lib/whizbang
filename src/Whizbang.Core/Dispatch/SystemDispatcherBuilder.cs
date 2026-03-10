using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Core.Dispatch;

/// <summary>
/// Intermediate builder for system operations that requires explicit tenant strategy selection.
/// Does NOT have dispatch methods - you must call <see cref="ForAllTenants"/>,
/// <see cref="ForTenant"/>, or <see cref="KeepTenant"/> first.
/// </summary>
/// <remarks>
/// <para>
/// This builder enforces compile-time tenant strategy selection for system operations.
/// The absence of dispatch methods (SendAsync, PublishAsync, etc.) on this type ensures
/// developers must explicitly choose a tenant strategy before dispatching.
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
/// // Cross-tenant system operation (use sparingly)
/// await _dispatcher.AsSystem().ForAllTenants().SendAsync(systemEvent);
///
/// // Tenant-scoped system operation (most common)
/// await _dispatcher.AsSystem().ForTenant("tenant-123").SendAsync(maintenanceCommand);
///
/// // Preserve ambient tenant context
/// await _dispatcher.AsSystem().KeepTenant().SendAsync(command);
///
/// // COMPILE ERROR: Must choose tenant strategy first!
/// // await _dispatcher.AsSystem().SendAsync(command);
/// </code>
/// </example>
/// <docs>core-concepts/scope-propagation</docs>
/// <tests>Whizbang.Core.Tests/Dispatch/SystemDispatcherBuilderTests.cs</tests>
public sealed class SystemDispatcherBuilder {
  private readonly IDispatcher _dispatcher;
  private readonly string? _actualPrincipal;
  private readonly string? _ambientTenantId;

  /// <summary>
  /// Creates a new system operation builder that requires explicit tenant strategy selection.
  /// </summary>
  /// <param name="dispatcher">The dispatcher to use for sending messages.</param>
  /// <param name="actualPrincipal">The actual principal (who initiated the operation, may be null for true system ops).</param>
  /// <param name="ambientTenantId">The ambient tenant ID from current context (used by <see cref="KeepTenant"/>).</param>
  internal SystemDispatcherBuilder(
      IDispatcher dispatcher,
      string? actualPrincipal,
      string? ambientTenantId) {
    _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    _actualPrincipal = actualPrincipal;
    _ambientTenantId = ambientTenantId;
  }

  /// <summary>
  /// Explicitly marks this as a cross-tenant operation affecting all tenants.
  /// Use sparingly - most operations should be tenant-scoped.
  /// </summary>
  /// <returns>A <see cref="DispatcherSecurityBuilder"/> with TenantId set to <see cref="TenantConstants.AllTenants"/>.</returns>
  /// <remarks>
  /// <para>
  /// This method sets the tenant ID to the special <see cref="TenantConstants.AllTenants"/> value ("*"),
  /// which indicates the operation intentionally affects all tenants or has no tenant scope.
  /// </para>
  /// <para>
  /// <b>Use cases</b>:
  /// </para>
  /// <list type="bullet">
  ///   <item><description>System-wide maintenance operations</description></item>
  ///   <item><description>Cross-tenant reporting or analytics</description></item>
  ///   <item><description>Global configuration updates</description></item>
  /// </list>
  /// </remarks>
  /// <example>
  /// <code>
  /// // System-wide reindexing operation
  /// await _dispatcher.AsSystem().ForAllTenants().SendAsync(new ReindexAllTenantsCommand());
  /// </code>
  /// </example>
  /// <docs>core-concepts/scope-propagation#cross-tenant-operations</docs>
  /// <tests>Whizbang.Core.Tests/Dispatch/SystemDispatcherBuilderTests.cs:AsSystem_ForAllTenants_SetsTenantIdToAllTenantsConstantAsync</tests>
  public DispatcherSecurityBuilder ForAllTenants() {
    return new DispatcherSecurityBuilder(
        _dispatcher,
        SecurityContextType.System,
        effectivePrincipal: "SYSTEM",
        actualPrincipal: _actualPrincipal,
        tenantId: TenantConstants.AllTenants);
  }

  /// <summary>
  /// Scopes this system operation to a specific tenant.
  /// </summary>
  /// <param name="tenantId">The tenant ID to scope the operation to.</param>
  /// <returns>A <see cref="DispatcherSecurityBuilder"/> with the explicit tenant ID.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="tenantId"/> is null.</exception>
  /// <exception cref="ArgumentException">Thrown when <paramref name="tenantId"/> is empty or whitespace.</exception>
  /// <remarks>
  /// <para>
  /// This is the most common pattern for system operations that should be scoped to a specific tenant.
  /// Use this when you have an explicit tenant ID from configuration, message context, or business logic.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// // Maintenance operation for a specific tenant
  /// await _dispatcher.AsSystem().ForTenant("tenant-123").SendAsync(new TenantMaintenanceCommand());
  ///
  /// // Scheduled job processing tenant-specific data
  /// foreach (var tenantId in tenantIds) {
  ///     await _dispatcher.AsSystem().ForTenant(tenantId).SendAsync(new ProcessTenantDataCommand());
  /// }
  /// </code>
  /// </example>
  /// <docs>core-concepts/scope-propagation#tenant-scoped-system-operations</docs>
  /// <tests>Whizbang.Core.Tests/Dispatch/SystemDispatcherBuilderTests.cs:AsSystem_ForTenant_SetsExplicitTenantIdAsync</tests>
  public DispatcherSecurityBuilder ForTenant(string tenantId) {
    ArgumentNullException.ThrowIfNull(tenantId, nameof(tenantId));
    ArgumentException.ThrowIfNullOrWhiteSpace(tenantId, nameof(tenantId));

    return new DispatcherSecurityBuilder(
        _dispatcher,
        SecurityContextType.System,
        effectivePrincipal: "SYSTEM",
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
  /// This method is useful when a system operation should operate within the same tenant context
  /// as the current request or message being processed. It fails fast if no tenant context exists,
  /// ensuring developers make an explicit choice rather than accidentally creating tenantless data.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// // System operation that preserves current tenant context
  /// // (e.g., timer triggered within a tenant-scoped workflow)
  /// await _dispatcher.AsSystem().KeepTenant().SendAsync(new ProcessPendingItemsCommand());
  /// </code>
  /// </example>
  /// <docs>core-concepts/scope-propagation#preserve-ambient-tenant</docs>
  /// <tests>Whizbang.Core.Tests/Dispatch/SystemDispatcherBuilderTests.cs:AsSystem_KeepTenant_PreservesAmbientTenantIdAsync</tests>
  public DispatcherSecurityBuilder KeepTenant() {
    if (string.IsNullOrEmpty(_ambientTenantId)) {
      throw new InvalidOperationException(
          "KeepTenant() called but no ambient tenant context exists. " +
          "Use ForAllTenants() for cross-tenant operations or ForTenant(id) for explicit tenant scope.");
    }

    return new DispatcherSecurityBuilder(
        _dispatcher,
        SecurityContextType.System,
        effectivePrincipal: "SYSTEM",
        actualPrincipal: _actualPrincipal,
        tenantId: _ambientTenantId);
  }
}
