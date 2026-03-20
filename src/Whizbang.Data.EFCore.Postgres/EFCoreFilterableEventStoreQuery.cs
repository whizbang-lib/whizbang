using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EF Core implementation of <see cref="IEventStoreQuery"/> with scope filtering support.
/// Implements <see cref="IFilterableLens"/> to receive filter info from <see cref="IScopedLensFactory"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Supported Filters:</strong> EventStoreRecord.Scope uses <see cref="PerspectiveScope"/>
/// which supports Tenant, User, Organization, and Customer filters.
/// Principal filters are not applied for event queries.
/// </para>
/// <para>
/// Use <see cref="ScopeFilter.None"/> for global/admin access to all events.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/event-store-query</docs>
/// <tests>Whizbang.Data.EFCore.Postgres.Tests/EFCoreFilterableEventStoreQueryTests.cs</tests>
/// <remarks>
/// Initializes a new instance of <see cref="EFCoreFilterableEventStoreQuery"/>.
/// </remarks>
/// <param name="context">The EF Core DbContext.</param>
public class EFCoreFilterableEventStoreQuery(DbContext context) : IFilterableEventStoreQuery {
  private readonly DbContext _context = context ?? throw new ArgumentNullException(nameof(context));
  private ScopeFilterInfo _filterInfo;

  /// <inheritdoc/>
  public void ApplyFilter(ScopeFilterInfo filterInfo) {
    _filterInfo = filterInfo;
  }

  /// <inheritdoc/>
  /// <remarks>
  /// Returns a queryable that applies scope filters based on <see cref="ScopeFilterInfo"/>.
  /// Filter composition:
  /// <list type="bullet">
  ///   <item>Tenant: WHERE scope->>'t' = ?</item>
  ///   <item>User: AND scope->>'u' = ?</item>
  ///   <item>Organization: AND scope->>'o' = ?</item>
  ///   <item>Customer: AND scope->>'c' = ?</item>
  /// </list>
  /// </remarks>
  public IQueryable<EventStoreRecord> Query {
    get {
      var query = _context.Set<EventStoreRecord>().AsNoTracking();

      if (_filterInfo.IsEmpty) {
        return query;
      }

      // Apply tenant filter
      if (_filterInfo.Filters.HasFlag(ScopeFilter.Tenant) && _filterInfo.TenantId is not null) {
        query = query.Where(r => r.Scope != null && r.Scope.TenantId == _filterInfo.TenantId);
      }

      // Apply user filter
      if (_filterInfo.Filters.HasFlag(ScopeFilter.User) && _filterInfo.UserId is not null) {
        query = query.Where(r => r.Scope != null && r.Scope.UserId == _filterInfo.UserId);
      }

      // Apply organization filter
      if (_filterInfo.Filters.HasFlag(ScopeFilter.Organization) && _filterInfo.OrganizationId is not null) {
        query = query.Where(r => r.Scope != null && r.Scope.OrganizationId == _filterInfo.OrganizationId);
      }

      // Apply customer filter
      if (_filterInfo.Filters.HasFlag(ScopeFilter.Customer) && _filterInfo.CustomerId is not null) {
        query = query.Where(r => r.Scope != null && r.Scope.CustomerId == _filterInfo.CustomerId);
      }

      return query;
    }
  }

  /// <inheritdoc/>
  public IQueryable<EventStoreRecord> GetStreamEvents(Guid streamId) =>
    Query.Where(e => e.StreamId == streamId).OrderBy(e => e.Version);

  /// <inheritdoc/>
  public IQueryable<EventStoreRecord> GetEventsByType(string eventType) =>
    Query.Where(e => e.EventType == eventType);
}
