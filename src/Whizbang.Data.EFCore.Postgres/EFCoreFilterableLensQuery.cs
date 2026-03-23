#pragma warning disable CS0618

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Whizbang.Core.Configuration;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{TModel}"/> with scope filtering support.
/// Implements <see cref="IFilterableLens"/> to receive filter info from <see cref="IScopedLensFactory"/>.
/// </summary>
/// <docs>fundamentals/lenses/scoped-lenses#ef-core-implementation</docs>
/// <tests>Whizbang.Data.EFCore.Postgres.Tests/EFCoreFilterableLensQueryTests.cs</tests>
/// <typeparam name="TModel">The model type stored in the perspective</typeparam>
public class EFCoreFilterableLensQuery<TModel> : ILensQuery<TModel>, IFilterableLens
    where TModel : class {

  private readonly DbContext _context;
  private readonly IScopeContextAccessor _scopeContextAccessor;
  private readonly QueryScope _defaultQueryScope;
  private ScopeFilterInfo _filterInfo;

  /// <summary>
  /// Initializes a new instance of <see cref="EFCoreFilterableLensQuery{TModel}"/>.
  /// </summary>
  /// <param name="context">The EF Core DbContext</param>
  /// <param name="tableName">The table name for this perspective (for diagnostics/logging)</param>
  /// <param name="scopeContextAccessor">Accessor for ambient scope context</param>
  /// <param name="options">Whizbang core options containing default query scope</param>
  public EFCoreFilterableLensQuery(
      DbContext context,
      string tableName,
      IScopeContextAccessor scopeContextAccessor,
      IOptions<WhizbangCoreOptions> options) {
    _context = context ?? throw new ArgumentNullException(nameof(context));
    ArgumentNullException.ThrowIfNull(tableName);
    _scopeContextAccessor = scopeContextAccessor ?? throw new ArgumentNullException(nameof(scopeContextAccessor));
    _defaultQueryScope = options?.Value.DefaultQueryScope ?? QueryScope.Tenant;
  }

  /// <summary>
  /// Backward-compatible constructor for tests. Uses Global scope (no filtering).
  /// </summary>
  internal EFCoreFilterableLensQuery(DbContext context, string tableName)
      : this(context, tableName, NullScopeContextAccessor.Instance, GlobalScopeOptions.Instance) { }

  /// <inheritdoc/>
  public IScopedLensAccess<TModel> Scope(QueryScope scope) => _createScopedAccess(scope, null);

  /// <inheritdoc/>
  public IScopedLensAccess<TModel> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues) =>
      _createScopedAccess(scope, overrideValues);

  /// <inheritdoc/>
  public IScopedLensAccess<TModel> DefaultScope => _createScopedAccess(_defaultQueryScope, null);

  /// <inheritdoc/>
  public void ApplyFilter(ScopeFilterInfo filterInfo) {
    _filterInfo = filterInfo;
  }

  /// <inheritdoc/>
  /// <remarks>
  /// Returns a queryable that applies scope filters based on <see cref="ScopeFilterInfo"/>.
  /// If no filter info has been applied via <see cref="ApplyFilter"/>, delegates to DefaultScope.
  /// </remarks>
  public IQueryable<PerspectiveRow<TModel>> Query {
    get {
      if (_filterInfo.IsEmpty) {
        return DefaultScope.Query;
      }

      return _applyFilterInfo(_context.Set<PerspectiveRow<TModel>>().AsNoTracking(), _filterInfo);
    }
  }

  /// <inheritdoc/>
  public async Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
    if (_filterInfo.IsEmpty) {
      return await DefaultScope.GetByIdAsync(id, cancellationToken);
    }

    var row = await Query.OrderBy(r => r.Id).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    return row?.Data;
  }

  private IScopedLensAccess<TModel> _createScopedAccess(QueryScope scope, ScopeFilterOverride? overrideValues) {
    var filters = QueryScopeMapper.ToScopeFilter(scope);

    if (filters == ScopeFilter.None) {
      return new UnfilteredScopedAccess(_context);
    }

    var context = _scopeContextAccessor.Current
      ?? throw new InvalidOperationException(
          $"Scope '{scope}' requires ambient scope context but IScopeContextAccessor.Current is null. " +
          "Ensure scope context middleware is configured.");

    // Apply overrides if provided
    IScopeContext effectiveContext = context;
    if (overrideValues.HasValue) {
      var ov = overrideValues.Value;
      effectiveContext = new OverrideScopeContext(context, ov);
    }

    var filterInfo = ScopeFilterBuilder.Build(filters, effectiveContext);
    return new FilteredScopedAccess(_context, filterInfo);
  }

  private static IQueryable<PerspectiveRow<TModel>> _applyFilterInfo(
      IQueryable<PerspectiveRow<TModel>> query,
      ScopeFilterInfo filterInfo) {
    // Apply tenant filter (always AND'd first)
    if (filterInfo.Filters.HasFlag(ScopeFilter.Tenant) && filterInfo.TenantId is not null) {
      query = query.Where(r => r.Scope.TenantId == filterInfo.TenantId);
    }

    // Apply organization filter
    if (filterInfo.Filters.HasFlag(ScopeFilter.Organization) && filterInfo.OrganizationId is not null) {
      query = query.Where(r => r.Scope.OrganizationId == filterInfo.OrganizationId);
    }

    // Apply customer filter
    if (filterInfo.Filters.HasFlag(ScopeFilter.Customer) && filterInfo.CustomerId is not null) {
      query = query.Where(r => r.Scope.CustomerId == filterInfo.CustomerId);
    }

    // Handle User and Principal filters with special OR logic when both are present
    var hasUserFilter = filterInfo.Filters.HasFlag(ScopeFilter.User) && filterInfo.UserId is not null;
    var hasPrincipalFilter = filterInfo.Filters.HasFlag(ScopeFilter.Principal) && filterInfo.SecurityPrincipals.Count > 0;

    if (filterInfo.UseOrLogicForUserAndPrincipal && hasUserFilter && hasPrincipalFilter) {
      // "My records OR shared with me" pattern
      query = query.FilterByUserOrPrincipals(filterInfo.UserId, filterInfo.SecurityPrincipals);
    } else {
      // Apply User filter (AND)
      if (hasUserFilter) {
        query = query.Where(r => r.Scope.UserId == filterInfo.UserId);
      }

      // Apply Principal filter (AND)
      if (hasPrincipalFilter) {
        query = query.FilterByPrincipals(filterInfo.SecurityPrincipals);
      }
    }

    return query;
  }

  /// <summary>
  /// Scoped access that returns unfiltered queries (Global scope).
  /// </summary>
  private sealed class UnfilteredScopedAccess(DbContext context) : IScopedLensAccess<TModel> {
    public IQueryable<PerspectiveRow<TModel>> Query =>
        context.Set<PerspectiveRow<TModel>>().AsNoTracking();

    public async Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
      var row = await Query.OrderBy(r => r.Id).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
      return row?.Data;
    }
  }

  /// <summary>
  /// Scoped access that applies filter info to queries.
  /// </summary>
  private sealed class FilteredScopedAccess(DbContext context, ScopeFilterInfo filterInfo) : IScopedLensAccess<TModel> {
    public IQueryable<PerspectiveRow<TModel>> Query =>
        _applyFilterInfo(context.Set<PerspectiveRow<TModel>>().AsNoTracking(), filterInfo);

    public async Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
      var row = await Query.OrderBy(r => r.Id).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
      return row?.Data;
    }
  }

  /// <summary>
  /// Wraps an IScopeContext with override values.
  /// </summary>
  private sealed class OverrideScopeContext(IScopeContext inner, ScopeFilterOverride overrides) : IScopeContext {
    public PerspectiveScope Scope => new() {
      TenantId = overrides.TenantId ?? inner.Scope.TenantId,
      UserId = overrides.UserId ?? inner.Scope.UserId,
      OrganizationId = overrides.OrganizationId ?? inner.Scope.OrganizationId,
      CustomerId = overrides.CustomerId ?? inner.Scope.CustomerId
    };
    public IReadOnlySet<string> Roles => inner.Roles;
    public IReadOnlySet<Permission> Permissions => inner.Permissions;
    public IReadOnlySet<SecurityPrincipalId> SecurityPrincipals => inner.SecurityPrincipals;
    public IReadOnlyDictionary<string, string> Claims => inner.Claims;
    public string? ActualPrincipal => inner.ActualPrincipal;
    public string? EffectivePrincipal => inner.EffectivePrincipal;
    public SecurityContextType ContextType => inner.ContextType;
    public bool HasPermission(Permission permission) => inner.HasPermission(permission);
    public bool HasAnyPermission(params Permission[] permissions) => inner.HasAnyPermission(permissions);
    public bool HasAllPermissions(params Permission[] permissions) => inner.HasAllPermissions(permissions);
    public bool HasRole(string roleName) => inner.HasRole(roleName);
    public bool HasAnyRole(params string[] roleNames) => inner.HasAnyRole(roleNames);
    public bool IsMemberOfAny(params SecurityPrincipalId[] principals) => inner.IsMemberOfAny(principals);
    public bool IsMemberOfAll(params SecurityPrincipalId[] principals) => inner.IsMemberOfAll(principals);
  }
}
