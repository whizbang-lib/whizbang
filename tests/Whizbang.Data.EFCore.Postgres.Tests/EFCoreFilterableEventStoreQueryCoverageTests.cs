using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage-round tests for <see cref="EFCoreFilterableEventStoreQuery"/>'s Organization and
/// Customer scope filters -- the two <see cref="ScopeFilters"/> branches
/// <see cref="EFCoreFilterableEventStoreQueryTests"/> never exercises (only Tenant, User, and
/// their combination are driven there). Requires a live PostgreSQL database: the filter is a
/// LINQ predicate over a JSONB-backed <c>ComplexProperty().ToJson()</c> column, and proving it
/// actually narrows results (not just that the expression compiles) means seeding real rows and
/// querying them back through the Npgsql provider.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreFilterableEventStoreQuery.cs</code-under-test>
[Category("Shard1")]
public class EFCoreFilterableEventStoreQueryCoverageTests : EFCoreTestBase {
  private readonly Uuid7IdProvider _idProvider = new();

  private async Task _seedEventAsync(
      DbContext context,
      Guid eventId,
      Guid streamId,
      string eventType,
      int version,
      string? organizationId = null,
      string? customerId = null) {

    var record = new EventStoreRecord {
      Id = eventId,
      StreamId = streamId,
      AggregateId = streamId,
      AggregateType = "TestAggregate",
      Version = version,
      EventType = eventType,
      EventData = JsonDocument.Parse("{}").RootElement,
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(eventId),
        Hops = [],
      },
      Scope = new PerspectiveScope {
        OrganizationId = organizationId,
        CustomerId = customerId,
      },
      CreatedAt = DateTime.UtcNow,
    };

    context.Set<EventStoreRecord>().Add(record);
    await context.SaveChangesAsync();
    context.ChangeTracker.Clear();
  }

  // A scope filter that silently stopped applying is a data leak, not a bug anyone notices.
  // This locks that the Organization branch actually narrows the result set -- the seeded
  // org-2 row must never come back for an org-1 caller.
  [Test]
  public async Task Query_OrganizationFilter_ReturnsOnlyOrganizationEventsAsync() {
    await using var context = CreateDbContext();
    var query = new EFCoreFilterableEventStoreQuery(context);

    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    var event3Id = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    await _seedEventAsync(context, event1Id, streamId, "Event1", 1, organizationId: "org-1");
    await _seedEventAsync(context, event2Id, streamId, "Event2", 2, organizationId: "org-1");
    await _seedEventAsync(context, event3Id, streamId, "Event3", 3, organizationId: "org-2");

    query.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilters.Organization,
      OrganizationId = "org-1",
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
    });

    var result = await query.Query.ToListAsync();

    // Both seeded org-1 rows must be visible, not just a count that happens to match --
    // otherwise this test could pass while the seeded data was invisible to the query.
    await Assert.That(result.Count).IsEqualTo(2);
    var resultIds = result.Select(r => r.Id).ToHashSet();
    await Assert.That(resultIds.Contains(event1Id)).IsTrue();
    await Assert.That(resultIds.Contains(event2Id)).IsTrue();
    await Assert.That(resultIds.Contains(event3Id)).IsFalse()
      .Because("org-2's event must never be visible to an org-1 caller");
  }

  // Same invariant for the Customer branch: a caller scoped to one customer must never see
  // another customer's events.
  [Test]
  public async Task Query_CustomerFilter_ReturnsOnlyCustomerEventsAsync() {
    await using var context = CreateDbContext();
    var query = new EFCoreFilterableEventStoreQuery(context);

    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    var event3Id = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    await _seedEventAsync(context, event1Id, streamId, "Event1", 1, customerId: "cust-1");
    await _seedEventAsync(context, event2Id, streamId, "Event2", 2, customerId: "cust-2");
    await _seedEventAsync(context, event3Id, streamId, "Event3", 3, customerId: "cust-1");

    query.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilters.Customer,
      CustomerId = "cust-1",
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
    });

    var result = await query.Query.ToListAsync();

    await Assert.That(result.Count).IsEqualTo(2);
    var resultIds = result.Select(r => r.Id).ToHashSet();
    await Assert.That(resultIds.Contains(event1Id)).IsTrue();
    await Assert.That(resultIds.Contains(event3Id)).IsTrue();
    await Assert.That(resultIds.Contains(event2Id)).IsFalse()
      .Because("cust-2's event must never be visible to a cust-1 caller");
  }
}
