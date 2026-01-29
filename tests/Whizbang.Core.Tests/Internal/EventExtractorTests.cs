using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Internal;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Internal;

/// <summary>
/// Tests for EventExtractor which extracts IEvent instances from complex return types.
/// </summary>
public class EventExtractorTests {
  #region Null Handling

  [Test]
  public async Task ExtractEvents_WithNull_ReturnsEmptyAsync() {
    // Act
    var events = EventExtractor.ExtractEvents(null).ToList();

    // Assert
    await Assert.That(events).IsEmpty();
  }

  #endregion

  #region Single Event

  [Test]
  public async Task ExtractEvents_WithSingleEvent_ReturnsSingleEventAsync() {
    // Arrange
    var singleEvent = new TestEvent("Test");

    // Act
    var events = EventExtractor.ExtractEvents(singleEvent).ToList();

    // Assert
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0]).IsEqualTo(singleEvent);
  }

  [Test]
  public async Task ExtractEvents_WithNonEvent_ReturnsEmptyAsync() {
    // Arrange
    const string nonEvent = "not an event";

    // Act
    var events = EventExtractor.ExtractEvents(nonEvent).ToList();

    // Assert
    await Assert.That(events).IsEmpty();
  }

  [Test]
  public async Task ExtractEvents_WithPrimitiveValue_ReturnsEmptyAsync() {
    // Act
    var events = EventExtractor.ExtractEvents(42).ToList();

    // Assert
    await Assert.That(events).IsEmpty();
  }

  #endregion

  #region Arrays

  [Test]
  public async Task ExtractEvents_WithEventArray_ReturnsAllEventsAsync() {
    // Arrange
    var eventsArray = new IEvent[] {
      new TestEvent("First"),
      new TestEvent("Second"),
      new TestEvent("Third")
    };

    // Act
    var events = EventExtractor.ExtractEvents(eventsArray).ToList();

    // Assert
    await Assert.That(events).Count().IsEqualTo(3);
  }

  [Test]
  public async Task ExtractEvents_WithEmptyArray_ReturnsEmptyAsync() {
    // Arrange
    var emptyArray = Array.Empty<IEvent>();

    // Act
    var events = EventExtractor.ExtractEvents(emptyArray).ToList();

    // Assert
    await Assert.That(events).IsEmpty();
  }

  #endregion

  #region Enumerables

  [Test]
  public async Task ExtractEvents_WithEventEnumerable_ReturnsAllEventsAsync() {
    // Arrange
    IEvent[] eventEnumerable = [
      new TestEvent("First"),
      new TestEvent("Second")
    ];

    // Act
    var events = EventExtractor.ExtractEvents(eventEnumerable).ToList();

    // Assert
    await Assert.That(events).Count().IsEqualTo(2);
  }

  [Test]
  public async Task ExtractEvents_WithEmptyEnumerable_ReturnsEmptyAsync() {
    // Arrange
    IEvent[] emptyEnumerable = [];

    // Act
    var events = EventExtractor.ExtractEvents(emptyEnumerable).ToList();

    // Assert
    await Assert.That(events).IsEmpty();
  }

  [Test]
  public async Task ExtractEvents_WithNestedEnumerable_FlattensProperlyAsync() {
    // Arrange - List of event arrays
    var nestedStructure = new List<IEvent[]> {
      new IEvent[] { new TestEvent("A1"), new TestEvent("A2") },
      new IEvent[] { new TestEvent("B1") }
    };

    // Act
    var events = EventExtractor.ExtractEvents(nestedStructure).ToList();

    // Assert
    await Assert.That(events).Count().IsEqualTo(3);
  }

  #endregion

  #region Tuples

  [Test]
  public async Task ExtractEvents_WithTuple_ExtractsOnlyEventsAsync() {
    // Arrange
    var tuple = Tuple.Create(
      new TestEvent("Event1"),
      "non-event",
      new TestEvent("Event2")
    );

    // Act
    var events = EventExtractor.ExtractEvents(tuple).ToList();

    // Assert
    await Assert.That(events).Count().IsEqualTo(2);
  }

  [Test]
  public async Task ExtractEvents_WithValueTuple_ExtractsOnlyEventsAsync() {
    // Arrange
    var valueTuple = (
      Event1: new TestEvent("Event1"),
      Number: 42,
      Event2: new TestEvent("Event2")
    );

    // Act
    var events = EventExtractor.ExtractEvents(valueTuple).ToList();

    // Assert
    await Assert.That(events).Count().IsEqualTo(2);
  }

  [Test]
  public async Task ExtractEvents_WithTupleOfNonEvents_ReturnsEmptyAsync() {
    // Arrange
    var tuple = Tuple.Create("string", 42, 3.14);

    // Act
    var events = EventExtractor.ExtractEvents(tuple).ToList();

    // Assert
    await Assert.That(events).IsEmpty();
  }

  [Test]
  public async Task ExtractEvents_WithTupleContainingEventArray_FlattensProperlyAsync() {
    // Arrange
    var tupleWithArray = (
      Event: new TestEvent("Single"),
      Events: new IEvent[] { new TestEvent("Array1"), new TestEvent("Array2") }
    );

    // Act
    var events = EventExtractor.ExtractEvents(tupleWithArray).ToList();

    // Assert
    await Assert.That(events).Count().IsEqualTo(3);
  }

  [Test]
  public async Task ExtractEvents_WithTupleContainingNull_SkipsNullItemsAsync() {
    // Arrange
    var tupleWithNull = (
      Event: new TestEvent("Event"),
      NullItem: (IEvent?)null,
      Number: 42
    );

    // Act
    var events = EventExtractor.ExtractEvents(tupleWithNull).ToList();

    // Assert
    await Assert.That(events).Count().IsEqualTo(1);
  }

  #endregion

  #region Complex Structures

  [Test]
  public async Task ExtractEvents_WithMixedComplexStructure_ExtractsAllEventsAsync() {
    // Arrange - A tuple containing single events, arrays, and nested lists
    var complexStructure = (
      Single: new TestEvent("Single"),
      Array: new IEvent[] { new TestEvent("Array1"), new TestEvent("Array2") },
      Nested: new List<IEvent> { new TestEvent("List1") }
    );

    // Act
    var events = EventExtractor.ExtractEvents(complexStructure).ToList();

    // Assert
    await Assert.That(events).Count().IsEqualTo(4);
  }

  [Test]
  public async Task ExtractEvents_WithNestedListContainingNulls_SkipsNullsAsync() {
    // Arrange - Use a non-typed enumerable wrapper so nulls go through the recursive path
    var listWithNulls = new List<object?> {
      new TestEvent("Event1"),
      null,
      new TestEvent("Event2"),
      null
    };

    // Act
    var events = EventExtractor.ExtractEvents(listWithNulls).ToList();

    // Assert - Only the events are extracted, nulls are skipped in the recursive call
    await Assert.That(events).Count().IsEqualTo(2);
  }

  #endregion

  #region Test Types

  private sealed record TestEvent(string Name) : IEvent;

  #endregion
}
