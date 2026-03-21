using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Internal;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Internal;

/// <summary>
/// Tests for MessageExtractor's non-message value callback (onNonMessageValue parameter).
/// Verifies that the callback is invoked for non-IMessage return types and NOT invoked for
/// null, IMessage, tuples, or RoutedNone values.
/// </summary>
public class MessageExtractorNonMessageLoggingTests {
  #region ExtractMessages - Non-message callback

  [Test]
  public async Task ExtractMessages_WithNonMessageType_InvokesCallbackAsync() {
    // Arrange
    Type? callbackType = null;
    void callback(Type t) => callbackType = t;

    // Act - int is not IMessage, should invoke callback
    // IMPORTANT: ExtractMessages is an iterator method (yield return).
    // The callback is only invoked when the iterator is consumed.
    var messages = MessageExtractor.ExtractMessages(42, callback).ToList();

    // Assert
    await Assert.That(messages).IsEmpty();
    await Assert.That(callbackType).IsNotNull();
    await Assert.That(callbackType).IsEqualTo(typeof(int));
  }

  [Test]
  public async Task ExtractMessages_WithStringNonMessageType_InvokesCallbackAsync() {
    // Arrange
    Type? callbackType = null;
    void callback(Type t) => callbackType = t;

    // Act - string is not IMessage (and is excluded from IEnumerable processing)
    var messages = MessageExtractor.ExtractMessages("hello", callback).ToList();

    // Assert
    await Assert.That(messages).IsEmpty();
    await Assert.That(callbackType).IsNotNull();
    await Assert.That(callbackType).IsEqualTo(typeof(string));
  }

  [Test]
  public async Task ExtractMessages_WithNull_DoesNotInvokeCallbackAsync() {
    // Arrange
    Type? callbackType = null;
    void callback(Type t) => callbackType = t;

    // Act - null should not trigger callback
    var messages = MessageExtractor.ExtractMessages(null, callback).ToList();

    // Assert
    await Assert.That(messages).IsEmpty();
    await Assert.That(callbackType).IsNull();
  }

  [Test]
  public async Task ExtractMessages_WithIMessage_DoesNotInvokeCallbackAsync() {
    // Arrange
    Type? callbackType = null;
    void callback(Type t) => callbackType = t;
    var testEvent = new TestEvent("Event1");

    // Act - IMessage should be extracted, not trigger callback
    var messages = MessageExtractor.ExtractMessages(testEvent, callback).ToList();

    // Assert
    await Assert.That(messages).Count().IsEqualTo(1);
    await Assert.That(messages[0]).IsEqualTo(testEvent);
    await Assert.That(callbackType).IsNull();
  }

  [Test]
  public async Task ExtractMessages_WithTupleContainingNonMessage_DoesNotInvokeCallbackForNestedItemsAsync() {
    // Arrange
    var callbackTypes = new List<Type>();
    void callback(Type t) => callbackTypes.Add(t);
    var testEvent = new TestEvent("Event1");

    // Act - tuple with an event and a non-message int
    // Note: The recursive call within tuple processing does NOT propagate the callback.
    // The callback is only invoked at the top level when the result itself is non-message.
    // Tuples are handled by the ITuple branch which recurses WITHOUT the callback.
    var tuple = (Event: testEvent, Number: 42);
    var messages = MessageExtractor.ExtractMessages(tuple, callback).ToList();

    // Assert - event is extracted, but callback is NOT invoked for nested int (by design)
    await Assert.That(messages).Count().IsEqualTo(1);
    await Assert.That(messages[0]).IsEqualTo(testEvent);
    await Assert.That(callbackTypes).IsEmpty();
  }

  [Test]
  public async Task ExtractMessages_WithCommand_DoesNotInvokeCallbackAsync() {
    // Arrange
    Type? callbackType = null;
    void callback(Type t) => callbackType = t;
    var testCommand = new TestCommand("Cmd1");

    // Act - ICommand implements IMessage, should not trigger callback
    var messages = MessageExtractor.ExtractMessages(testCommand, callback).ToList();

    // Assert
    await Assert.That(messages).Count().IsEqualTo(1);
    await Assert.That(callbackType).IsNull();
  }

  #endregion

  #region ExtractMessagesWithRouting - Non-message callback

  [Test]
  public async Task ExtractMessagesWithRouting_WithNonMessageType_InvokesCallbackAsync() {
    // Arrange
    Type? callbackType = null;
    void callback(Type t) => callbackType = t;

    // Act - int is not IMessage
    var messages = MessageExtractor.ExtractMessagesWithRouting(42, onNonMessageValue: callback).ToList();

    // Assert
    await Assert.That(messages).IsEmpty();
    await Assert.That(callbackType).IsNotNull();
    await Assert.That(callbackType).IsEqualTo(typeof(int));
  }

  [Test]
  public async Task ExtractMessagesWithRouting_WithRoutedNone_DoesNotInvokeCallbackAsync() {
    // Arrange
    Type? callbackType = null;
    void callback(Type t) => callbackType = t;

    // Act - Route.None() should be skipped entirely via IRouted with Mode==None
    var routedNone = Route.None();
    var messages = MessageExtractor.ExtractMessagesWithRouting(routedNone, onNonMessageValue: callback).ToList();

    // Assert - RoutedNone should not trigger callback (it's handled by the IRouted branch)
    await Assert.That(messages).IsEmpty();
    await Assert.That(callbackType).IsNull();
  }

  [Test]
  public async Task ExtractMessagesWithRouting_WithNull_DoesNotInvokeCallbackAsync() {
    // Arrange
    Type? callbackType = null;
    void callback(Type t) => callbackType = t;

    // Act
    var messages = MessageExtractor.ExtractMessagesWithRouting(null, onNonMessageValue: callback).ToList();

    // Assert
    await Assert.That(messages).IsEmpty();
    await Assert.That(callbackType).IsNull();
  }

  [Test]
  public async Task ExtractMessagesWithRouting_WithIMessage_DoesNotInvokeCallbackAsync() {
    // Arrange
    Type? callbackType = null;
    void callback(Type t) => callbackType = t;
    var testEvent = new TestEvent("Event1");

    // Act
    var messages = MessageExtractor.ExtractMessagesWithRouting(testEvent, onNonMessageValue: callback).ToList();

    // Assert
    await Assert.That(messages).Count().IsEqualTo(1);
    await Assert.That(callbackType).IsNull();
  }

  [Test]
  public async Task ExtractMessagesWithRouting_WithTupleContainingNonMessage_InvokesCallbackForNonMessageItemAsync() {
    // Arrange
    var callbackTypes = new List<Type>();
    void callback(Type t) => callbackTypes.Add(t);
    var testEvent = new TestEvent("Event1");

    // Act - tuple with event and non-message int
    var tuple = (Event: testEvent, Number: 42);
    var messages = MessageExtractor.ExtractMessagesWithRouting(tuple, onNonMessageValue: callback).ToList();

    // Assert
    await Assert.That(messages).Count().IsEqualTo(1);
    await Assert.That(callbackTypes).Count().IsEqualTo(1);
    await Assert.That(callbackTypes[0]).IsEqualTo(typeof(int));
  }

  [Test]
  public async Task ExtractMessagesWithRouting_WithRoutedEvent_DoesNotInvokeCallbackAsync() {
    // Arrange
    Type? callbackType = null;
    void callback(Type t) => callbackType = t;
    var testEvent = new TestEvent("Event1");

    // Act - Routed<IEvent> wrapping a valid message
    var routed = Route.Local(testEvent);
    var messages = MessageExtractor.ExtractMessagesWithRouting(routed, onNonMessageValue: callback).ToList();

    // Assert - the routed event should be extracted, not trigger callback
    await Assert.That(messages).Count().IsEqualTo(1);
    await Assert.That(callbackType).IsNull();
  }

  #endregion

  #region Test Types

  private sealed record TestEvent(string Name) : IEvent;
  private sealed record TestCommand(string Name) : ICommand;

  #endregion
}
