using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Routing.MessageKindDetectorTestTypes.Commands;
using Whizbang.Core.Tests.Routing.MessageKindDetectorTestTypes.Events;
using Whizbang.Core.Tests.Routing.MessageKindDetectorTestTypes.Queries;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Tests for MessageKindDetector which classifies message types using a priority-based detection strategy.
/// </summary>
public class MessageKindDetectorTests {
  #region Null Handling

  [Test]
  public async Task Detect_NullType_ThrowsArgumentNullExceptionAsync() {
    // Act
    var act = () => MessageKindDetector.Detect(null!);

    // Assert
    await Assert.That(act).Throws<ArgumentNullException>();
  }

  #endregion

  #region Priority 1: Attribute Detection

  [Test]
  public async Task Detect_TypeWithCommandAttribute_ReturnsCommandAsync() {
    // Act
    var result = MessageKindDetector.Detect(typeof(AttributeCommandMessage));

    // Assert
    await Assert.That(result).IsEqualTo(MessageKind.Command);
  }

  [Test]
  public async Task Detect_TypeWithEventAttribute_ReturnsEventAsync() {
    // Act
    var result = MessageKindDetector.Detect(typeof(AttributeEventMessage));

    // Assert
    await Assert.That(result).IsEqualTo(MessageKind.Event);
  }

  [Test]
  public async Task Detect_TypeWithQueryAttribute_ReturnsQueryAsync() {
    // Act
    var result = MessageKindDetector.Detect(typeof(AttributeQueryMessage));

    // Assert
    await Assert.That(result).IsEqualTo(MessageKind.Query);
  }

  [Test]
  public async Task Detect_AttributeOverridesInterface_ReturnsAttributeKindAsync() {
    // Arrange - type implements IEvent but has [MessageKind(Command)] attribute
    // Act
    var result = MessageKindDetector.Detect(typeof(AttributeOverridesInterfaceMessage));

    // Assert - attribute takes priority
    await Assert.That(result).IsEqualTo(MessageKind.Command);
  }

  #endregion

  #region Priority 2: Interface Detection

  [Test]
  public async Task Detect_ICommandImplementation_ReturnsCommandAsync() {
    // Act
    var result = MessageKindDetector.Detect(typeof(MyCommandImpl));

    // Assert
    await Assert.That(result).IsEqualTo(MessageKind.Command);
  }

  [Test]
  public async Task Detect_IEventImplementation_ReturnsEventAsync() {
    // Act
    var result = MessageKindDetector.Detect(typeof(MyEventImpl));

    // Assert
    await Assert.That(result).IsEqualTo(MessageKind.Event);
  }

  [Test]
  public async Task Detect_IQueryImplementation_ReturnsQueryAsync() {
    // Act
    var result = MessageKindDetector.Detect(typeof(MyQueryImpl));

    // Assert
    await Assert.That(result).IsEqualTo(MessageKind.Query);
  }

  [Test]
  public async Task Detect_InterfaceOverridesNamespace_ReturnsInterfaceKindAsync() {
    // Arrange - type is in "Events" namespace but implements ICommand
    // Act
    var result = MessageKindDetector.Detect(typeof(InterfaceOverridesNamespace));

    // Assert - interface takes priority over namespace
    await Assert.That(result).IsEqualTo(MessageKind.Command);
  }

  #endregion

  #region Priority 3: Namespace Detection

  [Test]
  public async Task Detect_CommandsNamespace_ReturnsCommandAsync() {
    // Act
    var result = MessageKindDetector.Detect(typeof(CommandsNamespaceMessage));

    // Assert
    await Assert.That(result).IsEqualTo(MessageKind.Command);
  }

  [Test]
  public async Task Detect_EventsNamespace_ReturnsEventAsync() {
    // Act
    var result = MessageKindDetector.Detect(typeof(EventsNamespaceMessage));

    // Assert
    await Assert.That(result).IsEqualTo(MessageKind.Event);
  }

  [Test]
  public async Task Detect_QueriesNamespace_ReturnsQueryAsync() {
    // Act
    var result = MessageKindDetector.Detect(typeof(QueriesNamespaceMessage));

    // Assert
    await Assert.That(result).IsEqualTo(MessageKind.Query);
  }

  [Test]
  public async Task Detect_NamespaceOverridesTypeSuffix_ReturnsNamespaceKindAsync() {
    // Arrange - type ends with "Event" but is in "Commands" namespace
    // Act
    var result = MessageKindDetector.Detect(typeof(ConfusingEvent));

    // Assert - namespace takes priority over type name
    await Assert.That(result).IsEqualTo(MessageKind.Command);
  }

  #endregion

  #region Priority 4: Type Name Suffix Detection

  [Test]
  public async Task Detect_CommandSuffix_ReturnsCommandAsync() {
    // Act
    var result = MessageKindDetector.Detect(typeof(CreateOrderCommand));

    // Assert
    await Assert.That(result).IsEqualTo(MessageKind.Command);
  }

  [Test]
  public async Task Detect_EventSuffix_ReturnsEventAsync() {
    // Act
    var result = MessageKindDetector.Detect(typeof(OrderSubmittedEvent));

    // Assert
    await Assert.That(result).IsEqualTo(MessageKind.Event);
  }

  [Test]
  public async Task Detect_CreatedSuffix_ReturnsEventAsync() {
    // Act
    var result = MessageKindDetector.Detect(typeof(OrderCreated));

    // Assert
    await Assert.That(result).IsEqualTo(MessageKind.Event);
  }

  [Test]
  public async Task Detect_UpdatedSuffix_ReturnsEventAsync() {
    // Act
    var result = MessageKindDetector.Detect(typeof(OrderUpdated));

    // Assert
    await Assert.That(result).IsEqualTo(MessageKind.Event);
  }

  [Test]
  public async Task Detect_DeletedSuffix_ReturnsEventAsync() {
    // Act
    var result = MessageKindDetector.Detect(typeof(OrderDeleted));

    // Assert
    await Assert.That(result).IsEqualTo(MessageKind.Event);
  }

  [Test]
  public async Task Detect_QuerySuffix_ReturnsQueryAsync() {
    // Act
    var result = MessageKindDetector.Detect(typeof(GetOrdersQuery));

    // Assert
    await Assert.That(result).IsEqualTo(MessageKind.Query);
  }

  #endregion

  #region Unknown Detection

  [Test]
  public async Task Detect_UnclassifiableType_ReturnsUnknownAsync() {
    // Act
    var result = MessageKindDetector.Detect(typeof(UnclassifiableMessage));

    // Assert
    await Assert.That(result).IsEqualTo(MessageKind.Unknown);
  }

  [Test]
  public async Task Detect_EmptyNamespaceType_UsesOtherDetectionAsync() {
    // Note: We can't easily create a type with empty namespace in tests,
    // but we can verify the type suffix detection works for types in root namespace
    // Act
    var result = MessageKindDetector.Detect(typeof(string));

    // Assert - string has no namespace but doesn't match any suffix, so Unknown
    await Assert.That(result).IsEqualTo(MessageKind.Unknown);
  }

  #endregion

  #region Test Message Types

  // Attribute-based test types
  [MessageKind(MessageKind.Command)]
  private sealed record AttributeCommandMessage;

  [MessageKind(MessageKind.Event)]
  private sealed record AttributeEventMessage;

  [MessageKind(MessageKind.Query)]
  private sealed record AttributeQueryMessage;

  [MessageKind(MessageKind.Command)] // Attribute says Command
  private sealed record AttributeOverridesInterfaceMessage : IEvent; // but implements IEvent

  // Interface-based test types
  private sealed record MyCommandImpl : ICommand;
  private sealed record MyEventImpl : IEvent;
  private sealed record MyQueryImpl : IQuery;

  // Type suffix test types
  private sealed record CreateOrderCommand;
  private sealed record OrderSubmittedEvent;
  private sealed record OrderCreated;
  private sealed record OrderUpdated;
  private sealed record OrderDeleted;
  private sealed record GetOrdersQuery;

  // Unknown type
  private sealed record UnclassifiableMessage;

  #endregion
}
