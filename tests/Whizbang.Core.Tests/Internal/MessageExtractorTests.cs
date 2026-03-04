using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Internal;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Internal;

/// <summary>
/// Tests for MessageExtractor which extracts IMessage instances (events and commands) from complex return types.
/// </summary>
public class MessageExtractorTests {
  #region Null Handling

  [Test]
  public async Task ExtractEvents_WithNull_ReturnsEmptyAsync() {
    // Act
    var events = MessageExtractor.ExtractMessages(null).ToList();

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
    var events = MessageExtractor.ExtractMessages(singleEvent).ToList();

    // Assert
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0]).IsEqualTo(singleEvent);
  }

  [Test]
  public async Task ExtractEvents_WithNonEvent_ReturnsEmptyAsync() {
    // Arrange
    const string nonEvent = "not an event";

    // Act
    var events = MessageExtractor.ExtractMessages(nonEvent).ToList();

    // Assert
    await Assert.That(events).IsEmpty();
  }

  [Test]
  public async Task ExtractEvents_WithPrimitiveValue_ReturnsEmptyAsync() {
    // Act
    var events = MessageExtractor.ExtractMessages(42).ToList();

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
    var events = MessageExtractor.ExtractMessages(eventsArray).ToList();

    // Assert
    await Assert.That(events).Count().IsEqualTo(3);
  }

  [Test]
  public async Task ExtractEvents_WithEmptyArray_ReturnsEmptyAsync() {
    // Arrange
    var emptyArray = Array.Empty<IEvent>();

    // Act
    var events = MessageExtractor.ExtractMessages(emptyArray).ToList();

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
    var events = MessageExtractor.ExtractMessages(eventEnumerable).ToList();

    // Assert
    await Assert.That(events).Count().IsEqualTo(2);
  }

  [Test]
  public async Task ExtractEvents_WithEmptyEnumerable_ReturnsEmptyAsync() {
    // Arrange
    IEvent[] emptyEnumerable = [];

    // Act
    var events = MessageExtractor.ExtractMessages(emptyEnumerable).ToList();

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
    var events = MessageExtractor.ExtractMessages(nestedStructure).ToList();

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
    var events = MessageExtractor.ExtractMessages(tuple).ToList();

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
    var events = MessageExtractor.ExtractMessages(valueTuple).ToList();

    // Assert
    await Assert.That(events).Count().IsEqualTo(2);
  }

  [Test]
  public async Task ExtractEvents_WithTupleOfNonEvents_ReturnsEmptyAsync() {
    // Arrange
    var tuple = Tuple.Create("string", 42, 3.14);

    // Act
    var events = MessageExtractor.ExtractMessages(tuple).ToList();

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
    var events = MessageExtractor.ExtractMessages(tupleWithArray).ToList();

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
    var events = MessageExtractor.ExtractMessages(tupleWithNull).ToList();

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
    var events = MessageExtractor.ExtractMessages(complexStructure).ToList();

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
    var events = MessageExtractor.ExtractMessages(listWithNulls).ToList();

    // Assert - Only the events are extracted, nulls are skipped in the recursive call
    await Assert.That(events).Count().IsEqualTo(2);
  }

  #endregion

  #region Command Extraction

  [Test]
  public async Task ExtractEvents_WithSingleCommand_ReturnsCommandAsync() {
    // Arrange
    var command = new TestCommand("CreateOrder");

    // Act
    var messages = MessageExtractor.ExtractMessages(command).ToList();

    // Assert - Commands should also be extracted since they are IMessage
    await Assert.That(messages).Count().IsEqualTo(1);
  }

  [Test]
  public async Task ExtractEvents_WithCommandArray_ReturnsAllCommandsAsync() {
    // Arrange
    ICommand[] commands = [
      new TestCommand("First"),
      new TestCommand("Second")
    ];

    // Act
    var messages = MessageExtractor.ExtractMessages(commands).ToList();

    // Assert
    await Assert.That(messages).Count().IsEqualTo(2);
  }

  [Test]
  public async Task ExtractEvents_WithMixedEventsAndCommands_ReturnsAllAsync() {
    // Arrange - Tuple with both events and commands
    var mixed = (
      Event: new TestEvent("OrderCreated"),
      Command: new TestCommand("SendNotification"),
      AnotherEvent: new TestEvent("NotificationSent")
    );

    // Act
    var messages = MessageExtractor.ExtractMessages(mixed).ToList();

    // Assert - Should extract both events AND commands
    await Assert.That(messages).Count().IsEqualTo(3);
  }

  [Test]
  public async Task ExtractEvents_WithTupleContainingCommandArray_FlattensProperlyAsync() {
    // Arrange
    var tupleWithCommands = (
      Event: new TestEvent("Single"),
      Commands: new ICommand[] { new TestCommand("Cmd1"), new TestCommand("Cmd2") }
    );

    // Act
    var messages = MessageExtractor.ExtractMessages(tupleWithCommands).ToList();

    // Assert - Should extract the event plus both commands
    await Assert.That(messages).Count().IsEqualTo(3);
  }

  #endregion

  #region Test Types

  private sealed record TestEvent(string Name) : IEvent;
  private sealed record TestCommand(string Name) : ICommand;

  #endregion
}
