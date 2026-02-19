using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for IFilterableEventStoreQuery interface definition.
/// Validates interface contract for filterable event store queries.
/// </summary>
/// <docs>core-concepts/event-store-query</docs>
[Category("EventStoreQuery")]
public class IFilterableEventStoreQueryTests {
  [Test]
  public async Task IFilterableEventStoreQuery_ExtendsIEventStoreQueryAsync() {
    // Assert - IFilterableEventStoreQuery should inherit from IEventStoreQuery
    await Assert.That(typeof(IFilterableEventStoreQuery).GetInterfaces())
        .Contains(typeof(IEventStoreQuery));
  }

  [Test]
  public async Task IFilterableEventStoreQuery_ExtendsIFilterableLensAsync() {
    // Assert - IFilterableEventStoreQuery should inherit from IFilterableLens
    await Assert.That(typeof(IFilterableEventStoreQuery).GetInterfaces())
        .Contains(typeof(IFilterableLens));
  }

  [Test]
  public async Task IFilterableEventStoreQuery_IsInterfaceAsync() {
    // Assert - IFilterableEventStoreQuery is an interface
    await Assert.That(typeof(IFilterableEventStoreQuery).IsInterface).IsTrue();
  }

  [Test]
  public async Task IFilterableEventStoreQuery_InheritsApplyFilterFromIFilterableLensAsync() {
    // Assert - IFilterableLens interface has ApplyFilter method
    var method = typeof(IFilterableLens).GetMethod("ApplyFilter");
    await Assert.That(method).IsNotNull();

    var parameters = method!.GetParameters();
    await Assert.That(parameters.Length).IsEqualTo(1);
    await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(ScopeFilterInfo));

    // IFilterableEventStoreQuery inherits this via IFilterableLens
    await Assert.That(typeof(IFilterableEventStoreQuery).GetInterfaces())
        .Contains(typeof(IFilterableLens));
  }

  [Test]
  public async Task IFilterableEventStoreQuery_InheritsQueryFromIEventStoreQueryAsync() {
    // Assert - IEventStoreQuery interface has Query property
    var queryProperty = typeof(IEventStoreQuery).GetProperty("Query");
    await Assert.That(queryProperty).IsNotNull();
    await Assert.That(queryProperty!.PropertyType).IsEqualTo(typeof(IQueryable<EventStoreRecord>));

    // IFilterableEventStoreQuery inherits this via IEventStoreQuery
    await Assert.That(typeof(IFilterableEventStoreQuery).GetInterfaces())
        .Contains(typeof(IEventStoreQuery));
  }
}
