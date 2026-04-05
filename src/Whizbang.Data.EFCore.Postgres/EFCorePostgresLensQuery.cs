#pragma warning disable CS0618

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Whizbang.Core.Configuration;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

// WHIZ400: Suppress for internal implementation - the runtime checks verify T is valid
#pragma warning disable WHIZ400

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:GetByIdAsync_WhenModelExists_ReturnsModelAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:GetByIdAsync_WhenModelDoesNotExist_ReturnsNullAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_ReturnsIQueryable_WithCorrectTypeAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByDataFields_ReturnsMatchingRowsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByMetadataFields_ReturnsMatchingRowsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanFilterByScopeFields_ReturnsMatchingRowsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_CanProjectAcrossColumns_ReturnsAnonymousTypeAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_SupportsCombinedFilters_FromAllColumnsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Query_SupportsComplexLinqOperations_WithOrderByAndSkipTakeAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Constructor_WithNullContext_ThrowsArgumentNullExceptionAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryTests.cs:Constructor_WithNullTableName_ThrowsArgumentNullExceptionAsync</tests>
/// EF Core implementation of <see cref="ILensQuery{TModel}"/> for PostgreSQL.
/// Provides LINQ-based querying over perspective data with support for filtering and projection
/// across data, metadata, and scope columns.
/// </summary>
/// <typeparam name="TModel">The model type stored in the perspective</typeparam>
public class EFCorePostgresLensQuery<TModel> : ILensQuery<TModel>
    where TModel : class {

  private readonly DbContext _context;
  private readonly IScopeContextAccessor _scopeContextAccessor;
  private readonly QueryScope _defaultQueryScope;

  /// <summary>
  /// Initializes a new instance of <see cref="EFCorePostgresLensQuery{TModel}"/>.
  /// </summary>
  /// <param name="context">The EF Core DbContext</param>
  /// <param name="tableName">The table name for this perspective (for diagnostics/logging)</param>
  /// <param name="scopeContextAccessor">Accessor for ambient scope context</param>
  /// <param name="options">Whizbang core options containing default query scope</param>
  public EFCorePostgresLensQuery(
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
  internal EFCorePostgresLensQuery(DbContext context, string tableName)
      : this(context, tableName, NullScopeContextAccessor.Instance, GlobalScopeOptions.Instance) { }

  /// <inheritdoc/>
  public IScopedLensAccess<TModel> Scope(QueryScope scope) =>
      ScopedAccessHelper.CreateScopedAccess<TModel>(_context, scope, _scopeContextAccessor, null);

  /// <inheritdoc/>
  public IScopedLensAccess<TModel> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues) =>
      ScopedAccessHelper.CreateScopedAccess<TModel>(_context, scope, _scopeContextAccessor, overrideValues);

  /// <inheritdoc/>
  public IScopedLensAccess<TModel> DefaultScope =>
      ScopedAccessHelper.CreateScopedAccess<TModel>(_context, _defaultQueryScope, _scopeContextAccessor, null);

  /// <inheritdoc/>
  public IQueryable<PerspectiveRow<TModel>> Query => DefaultScope.Query;

  /// <inheritdoc/>
  public async Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
    return await DefaultScope.GetByIdAsync(id, cancellationToken);
  }
}

/// <summary>
/// Helper for creating scoped access instances. Shared by single-model and multi-model query types.
/// </summary>
internal static class ScopedAccessHelper {
  internal static IScopedLensAccess<TModel> CreateScopedAccess<TModel>(
      DbContext context,
      QueryScope scope,
      IScopeContextAccessor scopeContextAccessor,
      ScopeFilterOverride? overrideValues)
      where TModel : class {
    var filters = QueryScopeMapper.ToScopeFilter(scope);

    if (filters == ScopeFilters.None) {
      return new UnfilteredScopedAccess<TModel>(context);
    }

    var scopeContext = scopeContextAccessor.Current
      ?? throw new InvalidOperationException(
          $"Scope '{scope}' requires ambient scope context but IScopeContextAccessor.Current is null. " +
          "Ensure scope context middleware is configured.");

    IScopeContext effectiveContext = scopeContext;
    if (overrideValues.HasValue) {
      effectiveContext = new OverrideScopeContext(scopeContext, overrideValues.Value);
    }

    var filterInfo = ScopeFilterBuilder.Build(filters, effectiveContext);
    return new FilteredScopedAccess<TModel>(context, filterInfo);
  }

  internal static IQueryable<PerspectiveRow<TModel>> ApplyFilterInfo<TModel>(
      IQueryable<PerspectiveRow<TModel>> query,
      ScopeFilterInfo filterInfo)
      where TModel : class {
    if (filterInfo.Filters.HasFlag(ScopeFilters.Tenant) && filterInfo.TenantId is not null) {
      query = query.Where(r => r.Scope.TenantId == filterInfo.TenantId);
    }

    if (filterInfo.Filters.HasFlag(ScopeFilters.Organization) && filterInfo.OrganizationId is not null) {
      query = query.Where(r => r.Scope.OrganizationId == filterInfo.OrganizationId);
    }

    if (filterInfo.Filters.HasFlag(ScopeFilters.Customer) && filterInfo.CustomerId is not null) {
      query = query.Where(r => r.Scope.CustomerId == filterInfo.CustomerId);
    }

    var hasUserFilter = filterInfo.Filters.HasFlag(ScopeFilters.User) && filterInfo.UserId is not null;
    var hasPrincipalFilter = filterInfo.Filters.HasFlag(ScopeFilters.Principal) && filterInfo.SecurityPrincipals.Count > 0;

    if (filterInfo.UseOrLogicForUserAndPrincipal && hasUserFilter && hasPrincipalFilter) {
      query = query.FilterByUserOrPrincipals(filterInfo.UserId, filterInfo.SecurityPrincipals);
    } else {
      if (hasUserFilter) {
        query = query.Where(r => r.Scope.UserId == filterInfo.UserId);
      }
      if (hasPrincipalFilter) {
        query = query.FilterByPrincipals(filterInfo.SecurityPrincipals);
      }
    }

    return query;
  }
}

internal sealed class UnfilteredScopedAccess<TModel>(DbContext context) : IScopedLensAccess<TModel>
    where TModel : class {

  // Static per TModel — computed once, zero per-query overhead.
  // Uses closed generic type for zero-reflection AOT-safe lookup.
  private static readonly bool _isSplitMode =
      SplitModeChangeTrackerHydrator.HasHydrator(typeof(PerspectiveRow<TModel>));

  public IQueryable<PerspectiveRow<TModel>> Query {
    get {
      if (_isSplitMode) {
        SplitModeChangeTrackerHydrator.EnsureHooked(context);
        return context.Set<PerspectiveRow<TModel>>().AsQueryable();
      }
      return context.Set<PerspectiveRow<TModel>>().AsNoTracking();
    }
  }

  public async Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
    var row = await Query.OrderBy(r => r.Id).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    return row?.Data;
  }
}

internal sealed class FilteredScopedAccess<TModel>(DbContext context, ScopeFilterInfo filterInfo) : IScopedLensAccess<TModel>
    where TModel : class {

  // Static per TModel — computed once, zero per-query overhead.
  private static readonly bool _isSplitMode =
      SplitModeChangeTrackerHydrator.HasHydrator(typeof(PerspectiveRow<TModel>));

  public IQueryable<PerspectiveRow<TModel>> Query {
    get {
      IQueryable<PerspectiveRow<TModel>> baseQuery;
      if (_isSplitMode) {
        SplitModeChangeTrackerHydrator.EnsureHooked(context);
        baseQuery = context.Set<PerspectiveRow<TModel>>().AsQueryable();
      } else {
        baseQuery = context.Set<PerspectiveRow<TModel>>().AsNoTracking();
      }
      return ScopedAccessHelper.ApplyFilterInfo(baseQuery, filterInfo);
    }
  }

  public async Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
    var row = await Query.OrderBy(r => r.Id).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    return row?.Data;
  }
}

internal sealed class OverrideScopeContext(IScopeContext inner, ScopeFilterOverride overrides) : IScopeContext {
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

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2}"/> for PostgreSQL.
/// Provides LINQ-based querying over two perspective types with shared DbContext for joins.
/// AOT-compatible: uses typeof() comparisons which are compile-time constants.
/// </summary>
/// <typeparam name="T1">First model type</typeparam>
/// <typeparam name="T2">Second model type</typeparam>
/// <docs>fundamentals/lenses/multi-model-queries</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryMultiGenericTests.cs</tests>
public sealed class EFCorePostgresLensQuery<T1, T2> : ILensQuery<T1, T2>
    where T1 : class
    where T2 : class {

  private readonly DbContext _context;
  private readonly IScopeContextAccessor _scopeContextAccessor;
  private readonly QueryScope _defaultQueryScope;
  private bool _disposed;

  public EFCorePostgresLensQuery(
      DbContext dbContext,
      IReadOnlyDictionary<Type, string> tableNames,
      IScopeContextAccessor scopeContextAccessor,
      IOptions<WhizbangCoreOptions> options) {
    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(tableNames);
    _context = dbContext;
    _scopeContextAccessor = scopeContextAccessor ?? throw new ArgumentNullException(nameof(scopeContextAccessor));
    _defaultQueryScope = options?.Value.DefaultQueryScope ?? QueryScope.Tenant;
  }

  internal EFCorePostgresLensQuery(DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames)
      : this(dbContext, tableNames, NullScopeContextAccessor.Instance, GlobalScopeOptions.Instance) { }

  public IScopedMultiLensAccess<T1, T2> Scope(QueryScope scope) =>
      new MultiModelScopedAccess<T1, T2>(_context, scope, _scopeContextAccessor, null);

  public IScopedMultiLensAccess<T1, T2> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues) =>
      new MultiModelScopedAccess<T1, T2>(_context, scope, _scopeContextAccessor, overrideValues);

  public IScopedMultiLensAccess<T1, T2> DefaultScope =>
      new MultiModelScopedAccess<T1, T2>(_context, _defaultQueryScope, _scopeContextAccessor, null);

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class => DefaultScope.Query<T>();

  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class =>
      await DefaultScope.GetByIdAsync<T>(id, cancellationToken);

  public void Dispose() {
    if (!_disposed) {
      _context.Dispose();
      _disposed = true;
    }
  }

  public async ValueTask DisposeAsync() {
    if (!_disposed) {
      await _context.DisposeAsync();
      _disposed = true;
    }
  }
}

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{T1, T2, T3}"/> for PostgreSQL.
/// </summary>
/// <docs>fundamentals/lenses/multi-model-queries</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresLensQueryMultiGenericTests.cs</tests>
public sealed class EFCorePostgresLensQuery<T1, T2, T3> : ILensQuery<T1, T2, T3>
    where T1 : class
    where T2 : class
    where T3 : class {

  private readonly DbContext _context;
  private readonly IScopeContextAccessor _scopeContextAccessor;
  private readonly QueryScope _defaultQueryScope;
  private bool _disposed;

  public EFCorePostgresLensQuery(
      DbContext dbContext,
      IReadOnlyDictionary<Type, string> tableNames,
      IScopeContextAccessor scopeContextAccessor,
      IOptions<WhizbangCoreOptions> options) {
    ArgumentNullException.ThrowIfNull(dbContext);
    ArgumentNullException.ThrowIfNull(tableNames);
    _context = dbContext;
    _scopeContextAccessor = scopeContextAccessor ?? throw new ArgumentNullException(nameof(scopeContextAccessor));
    _defaultQueryScope = options?.Value.DefaultQueryScope ?? QueryScope.Tenant;
  }

  internal EFCorePostgresLensQuery(DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames)
      : this(dbContext, tableNames, NullScopeContextAccessor.Instance, GlobalScopeOptions.Instance) { }

  public IScopedMultiLensAccess<T1, T2, T3> Scope(QueryScope scope) =>
      new MultiModelScopedAccess<T1, T2, T3>(_context, scope, _scopeContextAccessor, null);

  public IScopedMultiLensAccess<T1, T2, T3> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues) =>
      new MultiModelScopedAccess<T1, T2, T3>(_context, scope, _scopeContextAccessor, overrideValues);

  public IScopedMultiLensAccess<T1, T2, T3> DefaultScope =>
      new MultiModelScopedAccess<T1, T2, T3>(_context, _defaultQueryScope, _scopeContextAccessor, null);

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class => DefaultScope.Query<T>();

  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class =>
      await DefaultScope.GetByIdAsync<T>(id, cancellationToken);

  public void Dispose() {
    if (!_disposed) { _context.Dispose(); _disposed = true; }
  }

  public async ValueTask DisposeAsync() {
    if (!_disposed) { await _context.DisposeAsync(); _disposed = true; }
  }
}

public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4> : ILensQuery<T1, T2, T3, T4>
    where T1 : class where T2 : class where T3 : class where T4 : class {

  private readonly DbContext _context;
  private readonly IScopeContextAccessor _scopeContextAccessor;
  private readonly QueryScope _defaultQueryScope;
  private bool _disposed;

  public EFCorePostgresLensQuery(
      DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames,
      IScopeContextAccessor scopeContextAccessor, IOptions<WhizbangCoreOptions> options) {
    ArgumentNullException.ThrowIfNull(dbContext); ArgumentNullException.ThrowIfNull(tableNames);
    _context = dbContext;
    _scopeContextAccessor = scopeContextAccessor ?? throw new ArgumentNullException(nameof(scopeContextAccessor));
    _defaultQueryScope = options?.Value.DefaultQueryScope ?? QueryScope.Tenant;
  }

  internal EFCorePostgresLensQuery(DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames)
      : this(dbContext, tableNames, NullScopeContextAccessor.Instance, GlobalScopeOptions.Instance) { }

  public IScopedMultiLensAccess<T1, T2, T3, T4> Scope(QueryScope scope) =>
      new MultiModelScopedAccess<T1, T2, T3, T4>(_context, scope, _scopeContextAccessor, null);
  public IScopedMultiLensAccess<T1, T2, T3, T4> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues) =>
      new MultiModelScopedAccess<T1, T2, T3, T4>(_context, scope, _scopeContextAccessor, overrideValues);
  public IScopedMultiLensAccess<T1, T2, T3, T4> DefaultScope =>
      new MultiModelScopedAccess<T1, T2, T3, T4>(_context, _defaultQueryScope, _scopeContextAccessor, null);

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class => DefaultScope.Query<T>();
  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class =>
      await DefaultScope.GetByIdAsync<T>(id, cancellationToken);

  public void Dispose() { if (!_disposed) { _context.Dispose(); _disposed = true; } }
  public async ValueTask DisposeAsync() { if (!_disposed) { await _context.DisposeAsync(); _disposed = true; } }
}

public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4, T5> : ILensQuery<T1, T2, T3, T4, T5>
    where T1 : class where T2 : class where T3 : class where T4 : class where T5 : class {

  private readonly DbContext _context;
  private readonly IScopeContextAccessor _scopeContextAccessor;
  private readonly QueryScope _defaultQueryScope;
  private bool _disposed;

  public EFCorePostgresLensQuery(
      DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames,
      IScopeContextAccessor scopeContextAccessor, IOptions<WhizbangCoreOptions> options) {
    ArgumentNullException.ThrowIfNull(dbContext); ArgumentNullException.ThrowIfNull(tableNames);
    _context = dbContext;
    _scopeContextAccessor = scopeContextAccessor ?? throw new ArgumentNullException(nameof(scopeContextAccessor));
    _defaultQueryScope = options?.Value.DefaultQueryScope ?? QueryScope.Tenant;
  }

  internal EFCorePostgresLensQuery(DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames)
      : this(dbContext, tableNames, NullScopeContextAccessor.Instance, GlobalScopeOptions.Instance) { }

  public IScopedMultiLensAccess<T1, T2, T3, T4, T5> Scope(QueryScope scope) =>
      new MultiModelScopedAccess<T1, T2, T3, T4, T5>(_context, scope, _scopeContextAccessor, null);
  public IScopedMultiLensAccess<T1, T2, T3, T4, T5> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues) =>
      new MultiModelScopedAccess<T1, T2, T3, T4, T5>(_context, scope, _scopeContextAccessor, overrideValues);
  public IScopedMultiLensAccess<T1, T2, T3, T4, T5> DefaultScope =>
      new MultiModelScopedAccess<T1, T2, T3, T4, T5>(_context, _defaultQueryScope, _scopeContextAccessor, null);

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class => DefaultScope.Query<T>();
  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class =>
      await DefaultScope.GetByIdAsync<T>(id, cancellationToken);

  public void Dispose() { if (!_disposed) { _context.Dispose(); _disposed = true; } }
  public async ValueTask DisposeAsync() { if (!_disposed) { await _context.DisposeAsync(); _disposed = true; } }
}

public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4, T5, T6> : ILensQuery<T1, T2, T3, T4, T5, T6>
    where T1 : class where T2 : class where T3 : class where T4 : class where T5 : class where T6 : class {

  private readonly DbContext _context;
  private readonly IScopeContextAccessor _scopeContextAccessor;
  private readonly QueryScope _defaultQueryScope;
  private bool _disposed;

  public EFCorePostgresLensQuery(
      DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames,
      IScopeContextAccessor scopeContextAccessor, IOptions<WhizbangCoreOptions> options) {
    ArgumentNullException.ThrowIfNull(dbContext); ArgumentNullException.ThrowIfNull(tableNames);
    _context = dbContext;
    _scopeContextAccessor = scopeContextAccessor ?? throw new ArgumentNullException(nameof(scopeContextAccessor));
    _defaultQueryScope = options?.Value.DefaultQueryScope ?? QueryScope.Tenant;
  }

  internal EFCorePostgresLensQuery(DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames)
      : this(dbContext, tableNames, NullScopeContextAccessor.Instance, GlobalScopeOptions.Instance) { }

  public IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6> Scope(QueryScope scope) =>
      new MultiModelScopedAccess<T1, T2, T3, T4, T5, T6>(_context, scope, _scopeContextAccessor, null);
  public IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues) =>
      new MultiModelScopedAccess<T1, T2, T3, T4, T5, T6>(_context, scope, _scopeContextAccessor, overrideValues);
  public IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6> DefaultScope =>
      new MultiModelScopedAccess<T1, T2, T3, T4, T5, T6>(_context, _defaultQueryScope, _scopeContextAccessor, null);

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class => DefaultScope.Query<T>();
  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class =>
      await DefaultScope.GetByIdAsync<T>(id, cancellationToken);

  public void Dispose() { if (!_disposed) { _context.Dispose(); _disposed = true; } }
  public async ValueTask DisposeAsync() { if (!_disposed) { await _context.DisposeAsync(); _disposed = true; } }
}

public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4, T5, T6, T7> : ILensQuery<T1, T2, T3, T4, T5, T6, T7>
    where T1 : class where T2 : class where T3 : class where T4 : class where T5 : class where T6 : class where T7 : class {

  private readonly DbContext _context;
  private readonly IScopeContextAccessor _scopeContextAccessor;
  private readonly QueryScope _defaultQueryScope;
  private bool _disposed;

  public EFCorePostgresLensQuery(
      DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames,
      IScopeContextAccessor scopeContextAccessor, IOptions<WhizbangCoreOptions> options) {
    ArgumentNullException.ThrowIfNull(dbContext); ArgumentNullException.ThrowIfNull(tableNames);
    _context = dbContext;
    _scopeContextAccessor = scopeContextAccessor ?? throw new ArgumentNullException(nameof(scopeContextAccessor));
    _defaultQueryScope = options?.Value.DefaultQueryScope ?? QueryScope.Tenant;
  }

  internal EFCorePostgresLensQuery(DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames)
      : this(dbContext, tableNames, NullScopeContextAccessor.Instance, GlobalScopeOptions.Instance) { }

  public IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7> Scope(QueryScope scope) =>
      new MultiModelScopedAccess<T1, T2, T3, T4, T5, T6, T7>(_context, scope, _scopeContextAccessor, null);
  public IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues) =>
      new MultiModelScopedAccess<T1, T2, T3, T4, T5, T6, T7>(_context, scope, _scopeContextAccessor, overrideValues);
  public IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7> DefaultScope =>
      new MultiModelScopedAccess<T1, T2, T3, T4, T5, T6, T7>(_context, _defaultQueryScope, _scopeContextAccessor, null);

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class => DefaultScope.Query<T>();
  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class =>
      await DefaultScope.GetByIdAsync<T>(id, cancellationToken);

  public void Dispose() { if (!_disposed) { _context.Dispose(); _disposed = true; } }
  public async ValueTask DisposeAsync() { if (!_disposed) { await _context.DisposeAsync(); _disposed = true; } }
}

public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4, T5, T6, T7, T8> : ILensQuery<T1, T2, T3, T4, T5, T6, T7, T8>
    where T1 : class where T2 : class where T3 : class where T4 : class where T5 : class where T6 : class where T7 : class where T8 : class {

  private readonly DbContext _context;
  private readonly IScopeContextAccessor _scopeContextAccessor;
  private readonly QueryScope _defaultQueryScope;
  private bool _disposed;

  public EFCorePostgresLensQuery(
      DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames,
      IScopeContextAccessor scopeContextAccessor, IOptions<WhizbangCoreOptions> options) {
    ArgumentNullException.ThrowIfNull(dbContext); ArgumentNullException.ThrowIfNull(tableNames);
    _context = dbContext;
    _scopeContextAccessor = scopeContextAccessor ?? throw new ArgumentNullException(nameof(scopeContextAccessor));
    _defaultQueryScope = options?.Value.DefaultQueryScope ?? QueryScope.Tenant;
  }

  internal EFCorePostgresLensQuery(DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames)
      : this(dbContext, tableNames, NullScopeContextAccessor.Instance, GlobalScopeOptions.Instance) { }

  public IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8> Scope(QueryScope scope) =>
      new MultiModelScopedAccess<T1, T2, T3, T4, T5, T6, T7, T8>(_context, scope, _scopeContextAccessor, null);
  public IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues) =>
      new MultiModelScopedAccess<T1, T2, T3, T4, T5, T6, T7, T8>(_context, scope, _scopeContextAccessor, overrideValues);
  public IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8> DefaultScope =>
      new MultiModelScopedAccess<T1, T2, T3, T4, T5, T6, T7, T8>(_context, _defaultQueryScope, _scopeContextAccessor, null);

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class => DefaultScope.Query<T>();
  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class =>
      await DefaultScope.GetByIdAsync<T>(id, cancellationToken);

  public void Dispose() { if (!_disposed) { _context.Dispose(); _disposed = true; } }
  public async ValueTask DisposeAsync() { if (!_disposed) { await _context.DisposeAsync(); _disposed = true; } }
}

public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4, T5, T6, T7, T8, T9> : ILensQuery<T1, T2, T3, T4, T5, T6, T7, T8, T9>
    where T1 : class where T2 : class where T3 : class where T4 : class where T5 : class where T6 : class where T7 : class where T8 : class where T9 : class {

  private readonly DbContext _context;
  private readonly IScopeContextAccessor _scopeContextAccessor;
  private readonly QueryScope _defaultQueryScope;
  private bool _disposed;

  public EFCorePostgresLensQuery(
      DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames,
      IScopeContextAccessor scopeContextAccessor, IOptions<WhizbangCoreOptions> options) {
    ArgumentNullException.ThrowIfNull(dbContext); ArgumentNullException.ThrowIfNull(tableNames);
    _context = dbContext;
    _scopeContextAccessor = scopeContextAccessor ?? throw new ArgumentNullException(nameof(scopeContextAccessor));
    _defaultQueryScope = options?.Value.DefaultQueryScope ?? QueryScope.Tenant;
  }

  internal EFCorePostgresLensQuery(DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames)
      : this(dbContext, tableNames, NullScopeContextAccessor.Instance, GlobalScopeOptions.Instance) { }

  public IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9> Scope(QueryScope scope) =>
      new MultiModelScopedAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9>(_context, scope, _scopeContextAccessor, null);
  public IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues) =>
      new MultiModelScopedAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9>(_context, scope, _scopeContextAccessor, overrideValues);
  public IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9> DefaultScope =>
      new MultiModelScopedAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9>(_context, _defaultQueryScope, _scopeContextAccessor, null);

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class => DefaultScope.Query<T>();
  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class =>
      await DefaultScope.GetByIdAsync<T>(id, cancellationToken);

  public void Dispose() { if (!_disposed) { _context.Dispose(); _disposed = true; } }
  public async ValueTask DisposeAsync() { if (!_disposed) { await _context.DisposeAsync(); _disposed = true; } }
}

public sealed class EFCorePostgresLensQuery<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> : ILensQuery<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>
    where T1 : class where T2 : class where T3 : class where T4 : class where T5 : class where T6 : class where T7 : class where T8 : class where T9 : class where T10 : class {

  private readonly DbContext _context;
  private readonly IScopeContextAccessor _scopeContextAccessor;
  private readonly QueryScope _defaultQueryScope;
  private bool _disposed;

  public EFCorePostgresLensQuery(
      DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames,
      IScopeContextAccessor scopeContextAccessor, IOptions<WhizbangCoreOptions> options) {
    ArgumentNullException.ThrowIfNull(dbContext); ArgumentNullException.ThrowIfNull(tableNames);
    _context = dbContext;
    _scopeContextAccessor = scopeContextAccessor ?? throw new ArgumentNullException(nameof(scopeContextAccessor));
    _defaultQueryScope = options?.Value.DefaultQueryScope ?? QueryScope.Tenant;
  }

  internal EFCorePostgresLensQuery(DbContext dbContext, IReadOnlyDictionary<Type, string> tableNames)
      : this(dbContext, tableNames, NullScopeContextAccessor.Instance, GlobalScopeOptions.Instance) { }

  public IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> Scope(QueryScope scope) =>
      new MultiModelScopedAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(_context, scope, _scopeContextAccessor, null);
  public IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues) =>
      new MultiModelScopedAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(_context, scope, _scopeContextAccessor, overrideValues);
  public IScopedMultiLensAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> DefaultScope =>
      new MultiModelScopedAccess<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(_context, _defaultQueryScope, _scopeContextAccessor, null);

  public IQueryable<PerspectiveRow<T>> Query<T>() where T : class => DefaultScope.Query<T>();
  public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : class =>
      await DefaultScope.GetByIdAsync<T>(id, cancellationToken);

  public void Dispose() { if (!_disposed) { _context.Dispose(); _disposed = true; } }
  public async ValueTask DisposeAsync() { if (!_disposed) { await _context.DisposeAsync(); _disposed = true; } }
}
