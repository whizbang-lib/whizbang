using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Internal;

namespace Whizbang.Core.Tests.Internal;

/// <summary>
/// Tests for EventExtractor utility.
/// Ensures all complex return types are properly handled for event extraction.
/// </summary>
[Category("Core")]
[Category("Internal")]
public class EventExtractorTests {
  // Test event types
  private record _testEvent1(string Name) : IEvent;
  private record _testEvent2(int Value) : IEvent;
  private record _testEvent3(bool Flag) : IEvent;
  private record _nonEvent(string Data); // Does not implement IEvent

  [Test]
  public async Task ExtractEvents_WithNull_ReturnsEmptyAsync() {
    // Arrange
    object? result = null;

    // Act
    var events = EventExtractor.ExtractEvents(result);

    // Assert
    await Assert.That(events).HasCount().EqualTo(0);
  }

  [Test]
  public async Task ExtractEvents_WithSingleEvent_ReturnsSingleEventAsync() {
    // Arrange
    var evt = new _testEvent1("test");

    // Act
    var events = EventExtractor.ExtractEvents(evt);

    // Assert
    await Assert.That(events).HasCount().EqualTo(1);
    await Assert.That(events.First()).IsEqualTo(evt);
  }

  [Test]
  public async Task ExtractEvents_WithNonEvent_ReturnsEmptyAsync() {
    // Arrange
    var _nonEvent = new _nonEvent("data");

    // Act
    var events = EventExtractor.ExtractEvents(_nonEvent);

    // Assert
    await Assert.That(events).HasCount().EqualTo(0);
  }

  [Test]
  public async Task ExtractEvents_WithEventArray_ReturnsAllEventsAsync() {
    // Arrange
    var evt1 = new _testEvent1("first");
    var evt2 = new _testEvent2(42);
    var evt3 = new _testEvent3(true);
    IEvent[] eventArray = [evt1, evt2, evt3];

    // Act
    var events = EventExtractor.ExtractEvents(eventArray);

    // Assert
    await Assert.That(events).HasCount().EqualTo(3);
    await Assert.That(events.ElementAt(0)).IsEqualTo(evt1);
    await Assert.That(events.ElementAt(1)).IsEqualTo(evt2);
    await Assert.That(events.ElementAt(2)).IsEqualTo(evt3);
  }

  [Test]
  public async Task ExtractEvents_WithEventEnumerable_ReturnsAllEventsAsync() {
    // Arrange
    var evt1 = new _testEvent1("first");
    var evt2 = new _testEvent2(42);
    IEnumerable<IEvent> eventEnumerable = new List<IEvent> { evt1, evt2 };

    // Act
    var events = EventExtractor.ExtractEvents(eventEnumerable);

    // Assert
    await Assert.That(events).HasCount().EqualTo(2);
    await Assert.That(events.ElementAt(0)).IsEqualTo(evt1);
    await Assert.That(events.ElementAt(1)).IsEqualTo(evt2);
  }

  [Test]
  public async Task ExtractEvents_WithTuple_ExtractsOnlyEventsAsync() {
    // Arrange
    var evt1 = new _testEvent1("event");
    var _nonEvent = new _nonEvent("data");
    var evt2 = new _testEvent2(99);
    var tuple = (evt1, _nonEvent, evt2, 42);

    // Act
    var events = EventExtractor.ExtractEvents(tuple);

    // Assert
    await Assert.That(events).HasCount().EqualTo(2);
    await Assert.That(events.ElementAt(0)).IsEqualTo(evt1);
    await Assert.That(events.ElementAt(1)).IsEqualTo(evt2);
  }

  [Test]
  public async Task ExtractEvents_WithValueTuple_ExtractsOnlyEventsAsync() {
    // Arrange
    var evt1 = new _testEvent1("value-tuple-event");
    var evt2 = new _testEvent2(123);
    ValueTuple<_testEvent1, string, _testEvent2> valueTuple = (evt1, "string", evt2);

    // Act
    var events = EventExtractor.ExtractEvents(valueTuple);

    // Assert
    await Assert.That(events).HasCount().EqualTo(2);
    await Assert.That(events.ElementAt(0)).IsEqualTo(evt1);
    await Assert.That(events.ElementAt(1)).IsEqualTo(evt2);
  }

  [Test]
  public async Task ExtractEvents_WithTupleContainingEventArray_FlattensProperlyAsync() {
    // Arrange
    var evt1 = new _testEvent1("tuple-event");
    var evt2 = new _testEvent2(1);
    var evt3 = new _testEvent3(false);
    IEvent[] eventArray = [evt2, evt3];
    var tuple = (evt1, eventArray, "string");

    // Act
    var events = EventExtractor.ExtractEvents(tuple);

    // Assert
    await Assert.That(events).HasCount().EqualTo(3);
    await Assert.That(events.ElementAt(0)).IsEqualTo(evt1);
    await Assert.That(events.ElementAt(1)).IsEqualTo(evt2);
    await Assert.That(events.ElementAt(2)).IsEqualTo(evt3);
  }

  [Test]
  public async Task ExtractEvents_WithNestedEnumerable_FlattensProperlyAsync() {
    // Arrange
    var evt1 = new _testEvent1("outer");
    var evt2 = new _testEvent2(10);
    var evt3 = new _testEvent3(true);
    var innerList = new List<IEvent> { evt2, evt3 };
    IEnumerable<object> outerEnumerable = new List<object> { evt1, innerList };

    // Act
    var events = EventExtractor.ExtractEvents(outerEnumerable);

    // Assert
    await Assert.That(events).HasCount().EqualTo(3);
    await Assert.That(events.ElementAt(0)).IsEqualTo(evt1);
    await Assert.That(events.ElementAt(1)).IsEqualTo(evt2);
    await Assert.That(events.ElementAt(2)).IsEqualTo(evt3);
  }

  [Test]
  public async Task ExtractEvents_WithEmptyArray_ReturnsEmptyAsync() {
    // Arrange
    IEvent[] emptyArray = [];

    // Act
    var events = EventExtractor.ExtractEvents(emptyArray);

    // Assert
    await Assert.That(events).HasCount().EqualTo(0);
  }

  [Test]
  public async Task ExtractEvents_WithEmptyEnumerable_ReturnsEmptyAsync() {
    // Arrange
    IEnumerable<IEvent> emptyEnumerable = [];

    // Act
    var events = EventExtractor.ExtractEvents(emptyEnumerable);

    // Assert
    await Assert.That(events).HasCount().EqualTo(0);
  }

  [Test]
  public async Task ExtractEvents_WithTupleOfNonEvents_ReturnsEmptyAsync() {
    // Arrange
    var tuple = ("string", 42, new _nonEvent("data"));

    // Act
    var events = EventExtractor.ExtractEvents(tuple);

    // Assert
    await Assert.That(events).HasCount().EqualTo(0);
  }

  [Test]
  public async Task ExtractEvents_WithMixedComplexStructure_ExtractsAllEventsAsync() {
    // Arrange
    var evt1 = new _testEvent1("complex-1");
    var evt2 = new _testEvent2(100);
    var evt3 = new _testEvent3(true);
    var evt4 = new _testEvent1("complex-2");
    var innerArray = new IEvent[] { evt2, evt3 };
    var tuple = (evt1, innerArray, new _nonEvent("ignore"), evt4);

    // Act
    var events = EventExtractor.ExtractEvents(tuple);

    // Assert
    await Assert.That(events).HasCount().EqualTo(4);
    await Assert.That(events.ElementAt(0)).IsEqualTo(evt1);
    await Assert.That(events.ElementAt(1)).IsEqualTo(evt2);
    await Assert.That(events.ElementAt(2)).IsEqualTo(evt3);
    await Assert.That(events.ElementAt(3)).IsEqualTo(evt4);
  }
}
