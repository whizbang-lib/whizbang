using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for IEventStoreQuery interface definition.
/// Validates interface contract for raw event store querying with IQueryable support.
/// </summary>
/// <docs>core-concepts/event-store-query</docs>
[Category("EventStoreQuery")]
public class IEventStoreQueryTests {
  [Test]
  public async Task IEventStoreQuery_HasQueryPropertyAsync() {
    // Assert - Query property returns IQueryable<EventStoreRecord>
    var queryProperty = typeof(IEventStoreQuery).GetProperty("Query");
    await Assert.That(queryProperty).IsNotNull();
    await Assert.That(queryProperty!.PropertyType).IsEqualTo(typeof(IQueryable<EventStoreRecord>));
  }

  [Test]
  public async Task IEventStoreQuery_HasGetStreamEventsMethodAsync() {
    // Assert - GetStreamEvents method exists with correct signature
    var method = typeof(IEventStoreQuery).GetMethod("GetStreamEvents");
    await Assert.That(method).IsNotNull();

    var parameters = method!.GetParameters();
    await Assert.That(parameters.Length).IsEqualTo(1);
    await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(Guid));
    await Assert.That(parameters[0].Name).IsEqualTo("streamId");
    await Assert.That(method.ReturnType).IsEqualTo(typeof(IQueryable<EventStoreRecord>));
  }

  [Test]
  public async Task IEventStoreQuery_HasGetEventsByTypeMethodAsync() {
    // Assert - GetEventsByType method exists with correct signature
    var method = typeof(IEventStoreQuery).GetMethod("GetEventsByType");
    await Assert.That(method).IsNotNull();

    var parameters = method!.GetParameters();
    await Assert.That(parameters.Length).IsEqualTo(1);
    await Assert.That(parameters[0].ParameterType).IsEqualTo(typeof(string));
    await Assert.That(parameters[0].Name).IsEqualTo("eventType");
    await Assert.That(method.ReturnType).IsEqualTo(typeof(IQueryable<EventStoreRecord>));
  }

  [Test]
  public async Task IEventStoreQuery_IsInterfaceAsync() {
    // Assert - IEventStoreQuery is an interface
    await Assert.That(typeof(IEventStoreQuery).IsInterface).IsTrue();
  }

  [Test]
  public async Task IEventStoreQuery_QueryPropertyIsReadOnlyAsync() {
    // Assert - Query property has getter but no setter
    var queryProperty = typeof(IEventStoreQuery).GetProperty("Query");
    await Assert.That(queryProperty).IsNotNull();
    await Assert.That(queryProperty!.GetMethod).IsNotNull();
    await Assert.That(queryProperty.SetMethod).IsNull();
  }
}
