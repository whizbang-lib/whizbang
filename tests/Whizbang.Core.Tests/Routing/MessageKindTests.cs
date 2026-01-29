using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Tests for MessageKind enum and detection logic.
/// MessageKind classifies messages as Command, Event, or Query for routing decisions.
/// </summary>
public class MessageKindTests {
  #region Enum Values

  [Test]
  public async Task MessageKind_HasCommand_ValueAsync() {
    // Arrange & Act
    var kind = MessageKind.Command;

    // Assert
    await Assert.That(kind).IsEqualTo(MessageKind.Command);
  }

  [Test]
  public async Task MessageKind_HasEvent_ValueAsync() {
    // Arrange & Act
    var kind = MessageKind.Event;

    // Assert
    await Assert.That(kind).IsEqualTo(MessageKind.Event);
  }

  [Test]
  public async Task MessageKind_HasQuery_ValueAsync() {
    // Arrange & Act
    var kind = MessageKind.Query;

    // Assert
    await Assert.That(kind).IsEqualTo(MessageKind.Query);
  }

  #endregion

  #region Attribute Detection (Priority 1)

  [Test]
  public async Task Detect_WithMessageKindAttribute_ReturnsAttributeValueAsync() {
    // Arrange & Act
    var kind = MessageKindDetector.Detect(typeof(AttributeMarkedCommand));

    // Assert
    await Assert.That(kind).IsEqualTo(MessageKind.Command);
  }

  [Test]
  public async Task Detect_WithMessageKindAttributeEvent_ReturnsEventAsync() {
    // Arrange & Act
    var kind = MessageKindDetector.Detect(typeof(AttributeMarkedEvent));

    // Assert
    await Assert.That(kind).IsEqualTo(MessageKind.Event);
  }

  [Test]
  public async Task Detect_WithMessageKindAttributeQuery_ReturnsQueryAsync() {
    // Arrange & Act
    var kind = MessageKindDetector.Detect(typeof(AttributeMarkedQuery));

    // Assert
    await Assert.That(kind).IsEqualTo(MessageKind.Query);
  }

  [Test]
  public async Task Detect_AttributeTakesPriorityOverInterface_WhenBothPresentAsync() {
    // Arrange - type has ICommand interface but is marked as Event via attribute
    var type = typeof(AttributeOverridesInterface);

    // Act
    var kind = MessageKindDetector.Detect(type);

    // Assert - attribute should win
    await Assert.That(kind).IsEqualTo(MessageKind.Event);
  }

  #endregion

  #region Interface Detection (Priority 2)

  [Test]
  public async Task Detect_ImplementsICommand_ReturnsCommandAsync() {
    // Arrange & Act
    var kind = MessageKindDetector.Detect(typeof(InterfaceCommand));

    // Assert
    await Assert.That(kind).IsEqualTo(MessageKind.Command);
  }

  [Test]
  public async Task Detect_ImplementsIEvent_ReturnsEventAsync() {
    // Arrange & Act
    var kind = MessageKindDetector.Detect(typeof(InterfaceEvent));

    // Assert
    await Assert.That(kind).IsEqualTo(MessageKind.Event);
  }

  [Test]
  public async Task Detect_ImplementsIQuery_ReturnsQueryAsync() {
    // Arrange & Act
    var kind = MessageKindDetector.Detect(typeof(InterfaceQuery));

    // Assert
    await Assert.That(kind).IsEqualTo(MessageKind.Query);
  }

  [Test]
  public async Task Detect_InterfaceTakesPriorityOverNamespace_WhenBothPresentAsync() {
    // Arrange - type in "Events" namespace but implements ICommand
    var type = typeof(MessageKindTestTypes.Events.InterfaceInWrongNamespace);

    // Act
    var kind = MessageKindDetector.Detect(type);

    // Assert - interface should win over namespace
    await Assert.That(kind).IsEqualTo(MessageKind.Command);
  }

  #endregion

  #region Namespace Convention Detection (Priority 3)

  [Test]
  public async Task Detect_NamespaceContainsCommands_ReturnsCommandAsync() {
    // Arrange
    var type = typeof(MessageKindTestTypes.Commands.PlainCommand);

    // Act
    var kind = MessageKindDetector.Detect(type);

    // Assert
    await Assert.That(kind).IsEqualTo(MessageKind.Command);
  }

  [Test]
  public async Task Detect_NamespaceContainsEvents_ReturnsEventAsync() {
    // Arrange
    var type = typeof(MessageKindTestTypes.Events.PlainEvent);

    // Act
    var kind = MessageKindDetector.Detect(type);

    // Assert
    await Assert.That(kind).IsEqualTo(MessageKind.Event);
  }

  [Test]
  public async Task Detect_NamespaceContainsQueries_ReturnsQueryAsync() {
    // Arrange
    var type = typeof(MessageKindTestTypes.Queries.PlainQuery);

    // Act
    var kind = MessageKindDetector.Detect(type);

    // Assert
    await Assert.That(kind).IsEqualTo(MessageKind.Query);
  }

  [Test]
  public async Task Detect_NamespaceTakesPriorityOverTypeName_WhenBothPresentAsync() {
    // Arrange - type named "CreateOrderEvent" in "Commands" namespace
    var type = typeof(MessageKindTestTypes.Commands.CreateOrderEvent);

    // Act
    var kind = MessageKindDetector.Detect(type);

    // Assert - namespace should win over type name suffix
    await Assert.That(kind).IsEqualTo(MessageKind.Command);
  }

  #endregion

  #region Type Name Suffix Detection (Priority 4 - Fallback)

  [Test]
  public async Task Detect_TypeNameEndsWithCommand_ReturnsCommandAsync() {
    // Arrange
    var type = typeof(CreateOrderCommand);

    // Act
    var kind = MessageKindDetector.Detect(type);

    // Assert
    await Assert.That(kind).IsEqualTo(MessageKind.Command);
  }

  [Test]
  public async Task Detect_TypeNameEndsWithEvent_ReturnsEventAsync() {
    // Arrange
    var type = typeof(OrderCreatedEvent);

    // Act
    var kind = MessageKindDetector.Detect(type);

    // Assert
    await Assert.That(kind).IsEqualTo(MessageKind.Event);
  }

  [Test]
  public async Task Detect_TypeNameEndsWithQuery_ReturnsQueryAsync() {
    // Arrange
    var type = typeof(GetOrderByIdQuery);

    // Act
    var kind = MessageKindDetector.Detect(type);

    // Assert
    await Assert.That(kind).IsEqualTo(MessageKind.Query);
  }

  [Test]
  public async Task Detect_TypeNameEndsWithCreated_ReturnsEventAsync() {
    // Arrange - "Created" suffix indicates an event
    var type = typeof(OrderCreated);

    // Act
    var kind = MessageKindDetector.Detect(type);

    // Assert
    await Assert.That(kind).IsEqualTo(MessageKind.Event);
  }

  [Test]
  public async Task Detect_TypeNameEndsWithUpdated_ReturnsEventAsync() {
    // Arrange
    var type = typeof(OrderUpdated);

    // Act
    var kind = MessageKindDetector.Detect(type);

    // Assert
    await Assert.That(kind).IsEqualTo(MessageKind.Event);
  }

  [Test]
  public async Task Detect_TypeNameEndsWithDeleted_ReturnsEventAsync() {
    // Arrange
    var type = typeof(OrderDeleted);

    // Act
    var kind = MessageKindDetector.Detect(type);

    // Assert
    await Assert.That(kind).IsEqualTo(MessageKind.Event);
  }

  #endregion

  #region Unknown/Default Behavior

  [Test]
  public async Task Detect_NoIndicators_ReturnsUnknownAsync() {
    // Arrange - type with no indicators
    var type = typeof(UnclassifiedMessage);

    // Act
    var kind = MessageKindDetector.Detect(type);

    // Assert - should return Unknown when can't be determined
    await Assert.That(kind).IsEqualTo(MessageKind.Unknown);
  }

  [Test]
  public async Task Detect_NullType_ThrowsArgumentNullExceptionAsync() {
    // Arrange, Act & Assert
    await Assert.That(() => MessageKindDetector.Detect(null!))
      .Throws<ArgumentNullException>();
  }

  #endregion
}
