using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for ScopedEventStoreQuery and EventStoreQueryFactory.
/// Verifies auto-scoping per operation (fresh IEventStoreQuery/DbContext each time),
/// streaming via QueryAsync, materialization via ExecuteAsync, cancellation behavior,
/// shared-scope batching via the factory, and constructor/argument guard clauses.
/// </summary>
[Category("Integration")]
[Category("EventStoreQuery")]
[Category("Shard4")]
public class ScopedEventStoreQueryTests : EFCoreTestBase {
  private readonly Uuid7IdProvider _idProvider = new();

  // === Helper Methods ===

  private ServiceProvider BuildServiceProvider() {
    var services = new ServiceCollection();

    // Register DbContext as scoped (standard EF Core pattern)
    services.AddScoped(_ => CreateDbContext());

    // Register IEventStoreQuery as scoped (wraps the scoped DbContext)
    services.AddScoped<IEventStoreQuery>(sp =>
        new EFCoreFilterableEventStoreQuery(sp.GetRequiredService<WorkCoordinationDbContext>()));

    return services.BuildServiceProvider();
  }

  private static ScopedEventStoreQuery CreateSut(IServiceProvider provider) =>
      new(provider.GetRequiredService<IServiceScopeFactory>());

  private async Task<Guid> _seedEventAsync(Guid streamId, string eventType, int version) {
    var eventId = _idProvider.NewGuid();
    await using var context = CreateDbContext();

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
      Scope = new PerspectiveScope(),
      CreatedAt = DateTime.UtcNow
    };

    context.Set<EventStoreRecord>().Add(record);
    await context.SaveChangesAsync();
    return eventId;
  }

  // === ScopedEventStoreQuery Constructor ===

  [Test]
  public async Task Constructor_WithNullScopeFactory_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    await Assert.That(() => new ScopedEventStoreQuery(null!)).Throws<ArgumentNullException>();
  }

  // === QueryAsync ===

  [Test]
  public async Task QueryAsync_WithNullQueryBuilder_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    await using var provider = BuildServiceProvider();
    var sut = CreateSut(provider);

    // Act & Assert - guard fires eagerly, before enumeration begins
    await Assert.That(() => sut.QueryAsync(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task QueryAsync_StreamsAllRecordsFromScopedQueryAsync() {
    // Arrange
    await using var provider = BuildServiceProvider();
    var sut = CreateSut(provider);

    var stream1Id = _idProvider.NewGuid();
    var stream2Id = _idProvider.NewGuid();
    await _seedEventAsync(stream1Id, "Event1", 1);
    await _seedEventAsync(stream1Id, "Event2", 2);
    await _seedEventAsync(stream2Id, "Event3", 1);

    // Act
    var results = new List<EventStoreRecord>();
    await foreach (var record in sut.QueryAsync(q => q.Query)) {
      results.Add(record);
    }

    // Assert
    await Assert.That(results).Count().IsEqualTo(3);
  }

  [Test]
  public async Task QueryAsync_WithStreamFilterAndOrdering_ReturnsEventsInVersionOrderAsync() {
    // Arrange
    await using var provider = BuildServiceProvider();
    var sut = CreateSut(provider);

    var streamId = _idProvider.NewGuid();
    var otherStreamId = _idProvider.NewGuid();
    await _seedEventAsync(streamId, "Event3", 3);
    await _seedEventAsync(streamId, "Event1", 1);
    await _seedEventAsync(streamId, "Event2", 2);
    await _seedEventAsync(otherStreamId, "Unrelated", 1);

    // Act - queryBuilder receives the real scoped IEventStoreQuery
    var results = new List<EventStoreRecord>();
    await foreach (var record in sut.QueryAsync(q => q.GetStreamEvents(streamId))) {
      results.Add(record);
    }

    // Assert
    await Assert.That(results).Count().IsEqualTo(3);
    await Assert.That(results[0].Version).IsEqualTo(1);
    await Assert.That(results[1].Version).IsEqualTo(2);
    await Assert.That(results[2].Version).IsEqualTo(3);
  }

  [Test]
  public async Task QueryAsync_SecondEnumeration_UsesFreshScopedQueryInstanceAsync() {
    // Arrange
    await using var provider = BuildServiceProvider();
    var sut = CreateSut(provider);
    await _seedEventAsync(_idProvider.NewGuid(), "Event1", 1);

    var captured = new List<IEventStoreQuery>();
    IQueryable<EventStoreRecord> CaptureBuilder(IEventStoreQuery query) {
      captured.Add(query);
      return query.Query;
    }

    // Act - enumerate twice; each enumeration must create its own scope
    await foreach (var record in sut.QueryAsync(CaptureBuilder)) {
      _ = record;
    }
    await foreach (var record in sut.QueryAsync(CaptureBuilder)) {
      _ = record;
    }

    // Assert
    await Assert.That(captured).Count().IsEqualTo(2);
    await Assert.That(captured[0]).IsNotSameReferenceAs(captured[1]);
  }

  [Test]
  public async Task QueryAsync_WithCanceledToken_ThrowsOperationCanceledExceptionAsync() {
    // Arrange
    await using var provider = BuildServiceProvider();
    var sut = CreateSut(provider);
    await _seedEventAsync(_idProvider.NewGuid(), "Event1", 1);

    using var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act
    OperationCanceledException? caught = null;
    try {
      await foreach (var record in sut.QueryAsync(q => q.Query, cts.Token)) {
        _ = record;
      }
    } catch (OperationCanceledException ex) {
      caught = ex;
    }

    // Assert
    await Assert.That(caught).IsNotNull();
  }

  // === ExecuteAsync ===

  [Test]
  public async Task ExecuteAsync_WithNullQueryExecutor_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    await using var provider = BuildServiceProvider();
    var sut = CreateSut(provider);

    // Act & Assert
    await Assert.That(async () => await sut.ExecuteAsync<int>(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ExecuteAsync_MaterializesQueryResultAsync() {
    // Arrange
    await using var provider = BuildServiceProvider();
    var sut = CreateSut(provider);

    var streamId = _idProvider.NewGuid();
    await _seedEventAsync(streamId, "Event1", 1);
    await _seedEventAsync(streamId, "Event2", 2);
    await _seedEventAsync(_idProvider.NewGuid(), "Event3", 1);

    // Act
    var totalCount = await sut.ExecuteAsync(async (query, ct) => await query.Query.CountAsync(ct));
    var streamEvents = await sut.ExecuteAsync(
        async (query, ct) => await query.GetStreamEvents(streamId).ToListAsync(ct));

    // Assert
    await Assert.That(totalCount).IsEqualTo(3);
    await Assert.That(streamEvents).Count().IsEqualTo(2);
  }

  [Test]
  public async Task ExecuteAsync_EachCall_ResolvesFreshScopedQueryAsync() {
    // Arrange
    await using var provider = BuildServiceProvider();
    var sut = CreateSut(provider);

    // Act - capture the scoped IEventStoreQuery instance from each call
    var first = await sut.ExecuteAsync((query, _) => Task.FromResult(query));
    var second = await sut.ExecuteAsync((query, _) => Task.FromResult(query));

    // Assert - fresh scope (and therefore fresh query) per operation
    await Assert.That(first).IsNotSameReferenceAs(second);
  }

  [Test]
  public async Task ExecuteAsync_PassesCancellationTokenToExecutorAsync() {
    // Arrange
    await using var provider = BuildServiceProvider();
    var sut = CreateSut(provider);
    using var cts = new CancellationTokenSource();

    // Act
    var receivedMatchingToken = await sut.ExecuteAsync(
        (_, ct) => Task.FromResult(ct == cts.Token),
        cts.Token);

    // Assert
    await Assert.That(receivedMatchingToken).IsTrue();
  }

  // === EventStoreQueryFactory ===

  [Test]
  public async Task EventStoreQueryFactory_Constructor_WithNullScopeFactory_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    await Assert.That(() => new EventStoreQueryFactory(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task EventStoreQueryFactory_CreateScoped_SharesScopeAcrossQueriesAsync() {
    // Arrange
    await using var provider = BuildServiceProvider();
    var factory = new EventStoreQueryFactory(provider.GetRequiredService<IServiceScopeFactory>());

    var streamId = _idProvider.NewGuid();
    await _seedEventAsync(streamId, "OrderPlaced", 1);
    await _seedEventAsync(streamId, "OrderShipped", 2);

    // Act - multiple queries within one scope share the same IEventStoreQuery
    using var scoped = factory.CreateScoped();
    var totalCount = await scoped.Value.Query.CountAsync();
    var placedEvents = await scoped.Value.GetEventsByType("OrderPlaced").ToListAsync();

    // Assert
    await Assert.That(scoped.Value).IsNotNull();
    await Assert.That(totalCount).IsEqualTo(2);
    await Assert.That(placedEvents).Count().IsEqualTo(1);
    await Assert.That(placedEvents[0].EventType).IsEqualTo("OrderPlaced");
  }

  [Test]
  public async Task EventStoreQueryFactory_CreateScoped_MultipleScopes_ReturnDistinctInstancesAsync() {
    // Arrange
    await using var provider = BuildServiceProvider();
    var factory = new EventStoreQueryFactory(provider.GetRequiredService<IServiceScopeFactory>());
    await _seedEventAsync(_idProvider.NewGuid(), "Event1", 1);

    // Act
    using var scope1 = factory.CreateScoped();
    using var scope2 = factory.CreateScoped();
    var count1 = await scope1.Value.Query.CountAsync();
    var count2 = await scope2.Value.Query.CountAsync();

    // Assert - separate scopes resolve separate query instances over the same data
    await Assert.That(scope1.Value).IsNotSameReferenceAs(scope2.Value);
    await Assert.That(count1).IsEqualTo(1);
    await Assert.That(count2).IsEqualTo(1);
  }
}
