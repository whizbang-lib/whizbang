using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
/// Integration tests for EFCoreFilterableEventStoreQuery with scope filtering.
/// Tests all supported filter combinations: tenant, user, and global access.
/// Note: EventStoreRecord.Scope (MessageScope) only supports TenantId and UserId filtering.
/// </summary>
[Category("Integration")]
[Category("EventStoreQuery")]
public class EFCoreFilterableEventStoreQueryTests : EFCoreTestBase {
  private readonly Uuid7IdProvider _idProvider = new();

  // === Helper Methods ===

  private async Task _seedEventAsync(
      DbContext context,
      Guid eventId,
      Guid streamId,
      string eventType,
      int version,
      string? tenantId = null,
      string? userId = null) {

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
        Hops = []
      },
      Scope = new MessageScope {
        TenantId = tenantId,
        UserId = userId
      },
      CreatedAt = DateTime.UtcNow
    };

    context.Set<EventStoreRecord>().Add(record);
    await context.SaveChangesAsync();
    context.ChangeTracker.Clear();
  }

  // === Tests for No Filtering (Global Access) ===

  [Test]
  public async Task Query_NoFilter_ReturnsAllEventsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var query = new EFCoreFilterableEventStoreQuery(context);

    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    await _seedEventAsync(context, event1Id, streamId, "Event1", 1, tenantId: "tenant-1");
    await _seedEventAsync(context, event2Id, streamId, "Event2", 2, tenantId: "tenant-2");

    // Apply empty filter (no filtering - global access)
    query.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.None,
      SecurityPrincipals = new HashSet<SecurityPrincipalId>()
    });

    // Act
    var result = await query.Query.ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(2);
  }

  // === Tests for Tenant Filtering ===

  [Test]
  public async Task Query_TenantFilter_ReturnsOnlyTenantEventsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var query = new EFCoreFilterableEventStoreQuery(context);

    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    var event3Id = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    await _seedEventAsync(context, event1Id, streamId, "Event1", 1, tenantId: "tenant-1");
    await _seedEventAsync(context, event2Id, streamId, "Event2", 2, tenantId: "tenant-1");
    await _seedEventAsync(context, event3Id, streamId, "Event3", 3, tenantId: "tenant-2");

    query.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant,
      TenantId = "tenant-1",
      SecurityPrincipals = new HashSet<SecurityPrincipalId>()
    });

    // Act
    var result = await query.Query.ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(2);
    await Assert.That(result.All(r => r.Scope?.TenantId == "tenant-1")).IsTrue();
  }

  // === Tests for User Filtering ===

  [Test]
  public async Task Query_TenantAndUserFilter_ReturnsOnlyUserEventsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var query = new EFCoreFilterableEventStoreQuery(context);

    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    var event3Id = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();
    await _seedEventAsync(context, event1Id, streamId, "Event1", 1, tenantId: "tenant-1", userId: "user-alice");
    await _seedEventAsync(context, event2Id, streamId, "Event2", 2, tenantId: "tenant-1", userId: "user-bob");
    await _seedEventAsync(context, event3Id, streamId, "Event3", 3, tenantId: "tenant-2", userId: "user-alice");

    query.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant | ScopeFilter.User,
      TenantId = "tenant-1",
      UserId = "user-alice",
      SecurityPrincipals = new HashSet<SecurityPrincipalId>()
    });

    // Act
    var result = await query.Query.ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(event1Id);
  }

  // === Tests for GetStreamEvents ===

  [Test]
  public async Task GetStreamEvents_ReturnsEventsOrderedByVersionAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var query = new EFCoreFilterableEventStoreQuery(context);

    var streamId = _idProvider.NewGuid();
    var event1Id = _idProvider.NewGuid();
    var event2Id = _idProvider.NewGuid();
    var event3Id = _idProvider.NewGuid();
    await _seedEventAsync(context, event3Id, streamId, "Event3", 3, tenantId: "tenant-1");
    await _seedEventAsync(context, event1Id, streamId, "Event1", 1, tenantId: "tenant-1");
    await _seedEventAsync(context, event2Id, streamId, "Event2", 2, tenantId: "tenant-1");

    query.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.None,
      SecurityPrincipals = new HashSet<SecurityPrincipalId>()
    });

    // Act
    var result = await query.GetStreamEvents(streamId).ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(3);
    await Assert.That(result[0].Version).IsEqualTo(1);
    await Assert.That(result[1].Version).IsEqualTo(2);
    await Assert.That(result[2].Version).IsEqualTo(3);
  }

  [Test]
  public async Task GetStreamEvents_WithTenantFilter_ReturnsFilteredEventsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var query = new EFCoreFilterableEventStoreQuery(context);

    var stream1Id = _idProvider.NewGuid();
    var stream2Id = _idProvider.NewGuid();
    await _seedEventAsync(context, _idProvider.NewGuid(), stream1Id, "Event1", 1, tenantId: "tenant-1");
    await _seedEventAsync(context, _idProvider.NewGuid(), stream1Id, "Event2", 2, tenantId: "tenant-2");

    query.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant,
      TenantId = "tenant-1",
      SecurityPrincipals = new HashSet<SecurityPrincipalId>()
    });

    // Act
    var result = await query.GetStreamEvents(stream1Id).ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Scope?.TenantId).IsEqualTo("tenant-1");
  }

  // === Tests for GetEventsByType ===

  [Test]
  public async Task GetEventsByType_ReturnsEventsOfTypeAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var query = new EFCoreFilterableEventStoreQuery(context);

    var streamId = _idProvider.NewGuid();
    await _seedEventAsync(context, _idProvider.NewGuid(), streamId, "OrderPlaced", 1, tenantId: "tenant-1");
    await _seedEventAsync(context, _idProvider.NewGuid(), streamId, "OrderShipped", 2, tenantId: "tenant-1");
    await _seedEventAsync(context, _idProvider.NewGuid(), streamId, "OrderPlaced", 3, tenantId: "tenant-1");

    query.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.None,
      SecurityPrincipals = new HashSet<SecurityPrincipalId>()
    });

    // Act
    var result = await query.GetEventsByType("OrderPlaced").ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(2);
    await Assert.That(result.All(r => r.EventType == "OrderPlaced")).IsTrue();
  }

  // === Tests for Null Scope Handling ===

  [Test]
  public async Task Query_EventWithNullScope_HandledGracefullyAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var query = new EFCoreFilterableEventStoreQuery(context);

    var eventId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();

    // Create event with null scope
    var record = new EventStoreRecord {
      Id = eventId,
      StreamId = streamId,
      AggregateId = streamId,
      AggregateType = "TestAggregate",
      Version = 1,
      EventType = "TestEvent",
      EventData = JsonDocument.Parse("{}").RootElement,
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(eventId),
        Hops = []
      },
      Scope = null, // Null scope
      CreatedAt = DateTime.UtcNow
    };

    context.Set<EventStoreRecord>().Add(record);
    await context.SaveChangesAsync();
    context.ChangeTracker.Clear();

    // Apply tenant filter
    query.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant,
      TenantId = "tenant-1",
      SecurityPrincipals = new HashSet<SecurityPrincipalId>()
    });

    // Act
    var result = await query.Query.ToListAsync();

    // Assert - event with null scope should not match tenant filter
    await Assert.That(result.Count).IsEqualTo(0);
  }

  // === Tests for IEventStoreQuery Interface ===

  [Test]
  public async Task ImplementsIEventStoreQueryAsync() {
    // Assert
    await Assert.That(typeof(EFCoreFilterableEventStoreQuery).GetInterfaces())
        .Contains(typeof(IEventStoreQuery));
  }

  [Test]
  public async Task ImplementsIFilterableLensAsync() {
    // Assert
    await Assert.That(typeof(EFCoreFilterableEventStoreQuery).GetInterfaces())
        .Contains(typeof(IFilterableLens));
  }

  [Test]
  public async Task ImplementsIFilterableEventStoreQueryAsync() {
    // Assert
    await Assert.That(typeof(EFCoreFilterableEventStoreQuery).GetInterfaces())
        .Contains(typeof(IFilterableEventStoreQuery));
  }
}
