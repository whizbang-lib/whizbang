using Whizbang.Core.Security;

namespace Whizbang.Core.Lenses;

/// <summary>
/// Factory for creating scoped lens instances with composable filtering.
/// Enables tenant/user-scoped queries without manual WHERE clauses.
/// </summary>
/// <remarks>
/// <para>
/// The factory pattern allows runtime scope selection while maintaining
/// compile-time type safety. Use <see cref="ScopeFilter"/> flags for
/// composable filtering, or string-based scope names for backward compatibility.
/// </para>
/// <para>
/// Use this factory when different request contexts need different
/// data filtering (e.g., tenant isolation, user-specific data).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Get lens with composable filters
/// var lens = _lensFactory.GetLens&lt;IOrderLens&gt;(ScopeFilter.Tenant | ScopeFilter.User);
///
/// // Get lens with permission check
/// var lens = _lensFactory.GetLens&lt;IOrderLens&gt;(ScopeFilter.Tenant, Permission.Read("orders"));
/// </code>
/// </example>
/// <docs>core-concepts/scoped-lenses</docs>
/// <tests>Whizbang.Core.Tests/Lenses/ScopedLensFactoryTests.cs</tests>
public interface IScopedLensFactory {
  // === Legacy API (string-based scope names) ===

  /// <summary>
  /// Gets a lens instance with the specified scope filter applied.
  /// </summary>
  /// <typeparam name="TLens">The lens interface type (must implement ILensQuery).</typeparam>
  /// <param name="scopeName">The scope name as defined in LensOptions.DefineScope().</param>
  /// <returns>A lens instance with the scope filter pre-applied.</returns>
  /// <exception cref="ArgumentException">Thrown when scopeName is not defined.</exception>
  TLens GetLens<TLens>(string scopeName) where TLens : ILensQuery;

  // === Primary API: Composable flags ===

  /// <summary>
  /// Get lens with composable scope filters.
  /// Filters are combined with AND (except User+Principal which is OR).
  /// </summary>
  /// <typeparam name="TLens">The lens interface type.</typeparam>
  /// <param name="filters">Composable scope filter flags.</param>
  /// <returns>A lens instance with the scope filters applied.</returns>
  /// <example>
  /// <code>
  /// // Tenant + Principal filtering
  /// var lens = factory.GetLens&lt;IOrderLens&gt;(ScopeFilter.Tenant | ScopeFilter.Principal);
  /// // WHERE TenantId = ? AND AllowedPrincipals ?| [...]
  /// </code>
  /// </example>
  TLens GetLens<TLens>(ScopeFilter filters) where TLens : ILensQuery;

  /// <summary>
  /// Get lens with scope filters AND permission requirement.
  /// Throws <see cref="Security.Exceptions.AccessDeniedException"/> if permission not satisfied.
  /// Emits AccessDenied system event on failure.
  /// </summary>
  /// <typeparam name="TLens">The lens interface type.</typeparam>
  /// <param name="filters">Composable scope filter flags.</param>
  /// <param name="requiredPermission">Permission the caller must have.</param>
  /// <returns>A lens instance with the scope filters applied.</returns>
  /// <exception cref="Security.Exceptions.AccessDeniedException">
  /// Thrown when caller lacks the required permission.
  /// </exception>
  TLens GetLens<TLens>(ScopeFilter filters, Permission requiredPermission) where TLens : ILensQuery;

  /// <summary>
  /// Get lens with scope filters AND any of the specified permissions.
  /// Caller must have at least one of the permissions.
  /// </summary>
  /// <typeparam name="TLens">The lens interface type.</typeparam>
  /// <param name="filters">Composable scope filter flags.</param>
  /// <param name="anyOfPermissions">Permissions where caller must have at least one.</param>
  /// <returns>A lens instance with the scope filters applied.</returns>
  /// <exception cref="Security.Exceptions.AccessDeniedException">
  /// Thrown when caller lacks all specified permissions.
  /// </exception>
  TLens GetLens<TLens>(ScopeFilter filters, params Permission[] anyOfPermissions) where TLens : ILensQuery;

  // === Convenience methods for common patterns ===

  /// <summary>
  /// Get lens with no filtering (admin access).
  /// </summary>
  /// <typeparam name="TLens">The lens interface type.</typeparam>
  /// <returns>A lens with no scope filtering applied.</returns>
  /// <remarks>Equivalent to GetLens(ScopeFilter.None)</remarks>
  TLens GetGlobalLens<TLens>() where TLens : ILensQuery;

  /// <summary>
  /// Get lens filtered by current tenant only.
  /// </summary>
  /// <typeparam name="TLens">The lens interface type.</typeparam>
  /// <returns>A lens filtered by TenantId.</returns>
  /// <remarks>Equivalent to GetLens(ScopeFilter.Tenant)</remarks>
  TLens GetTenantLens<TLens>() where TLens : ILensQuery;

  /// <summary>
  /// Get lens filtered by tenant + user.
  /// </summary>
  /// <typeparam name="TLens">The lens interface type.</typeparam>
  /// <returns>A lens filtered by TenantId and UserId.</returns>
  /// <remarks>Equivalent to GetLens(ScopeFilter.Tenant | ScopeFilter.User)</remarks>
  TLens GetUserLens<TLens>() where TLens : ILensQuery;

  /// <summary>
  /// Get lens filtered by tenant + organization.
  /// </summary>
  /// <typeparam name="TLens">The lens interface type.</typeparam>
  /// <returns>A lens filtered by TenantId and OrganizationId.</returns>
  TLens GetOrganizationLens<TLens>() where TLens : ILensQuery;

  /// <summary>
  /// Get lens filtered by tenant + customer.
  /// </summary>
  /// <typeparam name="TLens">The lens interface type.</typeparam>
  /// <returns>A lens filtered by TenantId and CustomerId.</returns>
  TLens GetCustomerLens<TLens>() where TLens : ILensQuery;

  /// <summary>
  /// Get lens filtered by tenant + security principal membership.
  /// Returns records where AllowedPrincipals overlaps caller's SecurityPrincipals.
  /// </summary>
  /// <typeparam name="TLens">The lens interface type.</typeparam>
  /// <returns>A lens filtered by TenantId and principal membership.</returns>
  /// <remarks>Equivalent to GetLens(ScopeFilter.Tenant | ScopeFilter.Principal)</remarks>
  TLens GetPrincipalLens<TLens>() where TLens : ILensQuery;

  /// <summary>
  /// Get lens for "my records OR shared with me" pattern.
  /// WHERE TenantId = ? AND (UserId = ? OR AllowedPrincipals ?| [...])
  /// </summary>
  /// <typeparam name="TLens">The lens interface type.</typeparam>
  /// <returns>A lens for user's own or shared records.</returns>
  /// <remarks>Equivalent to GetLens(ScopeFilter.Tenant | ScopeFilter.User | ScopeFilter.Principal)</remarks>
  TLens GetMyOrSharedLens<TLens>() where TLens : ILensQuery;
}
