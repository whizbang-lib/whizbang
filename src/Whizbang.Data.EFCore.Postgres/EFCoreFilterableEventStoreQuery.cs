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
/// <strong>Supported Filters:</strong> MessageScope only contains TenantId and UserId,
/// so only <see cref="ScopeFilter.Tenant"/> and <see cref="ScopeFilter.User"/> are applied.
/// Organization, Customer, and Principal filters are ignored for event queries.
/// </para>
/// <para>
/// Use <see cref="ScopeFilter.None"/> for global/admin access to all events.
/// </para>
/// </remarks>
/// <docs>core-concepts/event-store-query</docs>
/// <tests>Whizbang.Data.EFCore.Postgres.Tests/EFCoreFilterableEventStoreQueryTests.cs</tests>
public class EFCoreFilterableEventStoreQuery : IFilterableEventStoreQuery {
  private readonly DbContext _context;
  private ScopeFilterInfo _filterInfo;

  /// <summary>
  /// Initializes a new instance of <see cref="EFCoreFilterableEventStoreQuery"/>.
  /// </summary>
  /// <param name="context">The EF Core DbContext.</param>
  public EFCoreFilterableEventStoreQuery(DbContext context) {
    _context = context ?? throw new ArgumentNullException(nameof(context));
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
  /// </list>
  /// Note: Organization, Customer, and Principal filters are not supported for events
  /// because <see cref="MessageScope"/> does not contain these fields.
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

      // Note: Organization, Customer, and Principal filters are not applied
      // because MessageScope doesn't have these fields

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
