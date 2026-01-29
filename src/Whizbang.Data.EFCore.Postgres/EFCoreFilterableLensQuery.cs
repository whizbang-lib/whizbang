using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EF Core implementation of <see cref="ILensQuery{TModel}"/> with scope filtering support.
/// Implements <see cref="IFilterableLens"/> to receive filter info from <see cref="IScopedLensFactory"/>.
/// </summary>
/// <docs>core-concepts/scoped-lenses#ef-core-implementation</docs>
/// <tests>Whizbang.Data.EFCore.Postgres.Tests/EFCoreFilterableLensQueryTests.cs</tests>
/// <typeparam name="TModel">The model type stored in the perspective</typeparam>
public class EFCoreFilterableLensQuery<TModel> : ILensQuery<TModel>, IFilterableLens
    where TModel : class {

  private readonly DbContext _context;
  private readonly string _tableName;
  private ScopeFilterInfo _filterInfo;

  /// <summary>
  /// Initializes a new instance of <see cref="EFCoreFilterableLensQuery{TModel}"/>.
  /// </summary>
  /// <param name="context">The EF Core DbContext</param>
  /// <param name="tableName">The table name for this perspective (for diagnostics/logging)</param>
  public EFCoreFilterableLensQuery(DbContext context, string tableName) {
    _context = context ?? throw new ArgumentNullException(nameof(context));
    _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
  }

  /// <inheritdoc/>
  public void ApplyFilter(ScopeFilterInfo filterInfo) {
    _filterInfo = filterInfo;
  }

  /// <inheritdoc/>
  /// <remarks>
  /// Returns a queryable that applies scope filters based on <see cref="ScopeFilterInfo"/>.
  /// Filter composition:
  /// <list type="bullet">
  ///   <item>Tenant: WHERE scope->>'TenantId' = ?</item>
  ///   <item>User: AND scope->>'UserId' = ?</item>
  ///   <item>Organization: AND scope->>'OrganizationId' = ?</item>
  ///   <item>Customer: AND scope->>'CustomerId' = ?</item>
  ///   <item>Principal: AND scope @> '{"AllowedPrincipals":["user:xxx"]}'</item>
  ///   <item>User+Principal (OR logic): AND (UserId = ? OR AllowedPrincipals contains any)</item>
  /// </list>
  /// </remarks>
  public IQueryable<PerspectiveRow<TModel>> Query {
    get {
      var query = _context.Set<PerspectiveRow<TModel>>().AsNoTracking();

      if (_filterInfo.IsEmpty) {
        return query;
      }

      // Apply tenant filter (always AND'd first)
      if (_filterInfo.Filters.HasFlag(ScopeFilter.Tenant) && _filterInfo.TenantId is not null) {
        query = query.Where(r => r.Scope.TenantId == _filterInfo.TenantId);
      }

      // Apply organization filter
      if (_filterInfo.Filters.HasFlag(ScopeFilter.Organization) && _filterInfo.OrganizationId is not null) {
        query = query.Where(r => r.Scope.OrganizationId == _filterInfo.OrganizationId);
      }

      // Apply customer filter
      if (_filterInfo.Filters.HasFlag(ScopeFilter.Customer) && _filterInfo.CustomerId is not null) {
        query = query.Where(r => r.Scope.CustomerId == _filterInfo.CustomerId);
      }

      // Handle User and Principal filters with special OR logic when both are present
      var hasUserFilter = _filterInfo.Filters.HasFlag(ScopeFilter.User) && _filterInfo.UserId is not null;
      var hasPrincipalFilter = _filterInfo.Filters.HasFlag(ScopeFilter.Principal) && _filterInfo.SecurityPrincipals.Count > 0;

      if (_filterInfo.UseOrLogicForUserAndPrincipal && hasUserFilter && hasPrincipalFilter) {
        // "My records OR shared with me" pattern
        query = query.FilterByUserOrPrincipals(_filterInfo.UserId, _filterInfo.SecurityPrincipals);
      } else {
        // Apply User filter (AND)
        if (hasUserFilter) {
          query = query.Where(r => r.Scope.UserId == _filterInfo.UserId);
        }

        // Apply Principal filter (AND)
        if (hasPrincipalFilter) {
          query = query.FilterByPrincipals(_filterInfo.SecurityPrincipals);
        }
      }

      return query;
    }
  }

  /// <inheritdoc/>
  public async Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
    // Apply filters and then look for specific ID
    var row = await Query.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    return row?.Data;
  }
}
