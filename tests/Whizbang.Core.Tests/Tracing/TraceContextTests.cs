using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tracing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Tracing;

/// <summary>
/// Tests for TraceContext which contains metadata for trace operations.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tracing/TraceContext.cs</code-under-test>
public class TraceContextTests {
  #region Required Property Tests

  [Test]
  public async Task MessageId_RequiredProperty_CanBeSetAsync() {
    // Arrange
    var messageId = Guid.NewGuid();

    // Act
    var context = _createContext(messageId: messageId);

    // Assert
    await Assert.That(context.MessageId).IsEqualTo(messageId);
  }

  [Test]
  public async Task CorrelationId_RequiredProperty_CanBeSetAsync() {
    // Arrange
    const string correlationId = "corr-123-456";

    // Act
    var context = _createContext(correlationId: correlationId);

    // Assert
    await Assert.That(context.CorrelationId).IsEqualTo(correlationId);
  }

  [Test]
  public async Task MessageType_RequiredProperty_CanBeSetAsync() {
    // Arrange
    const string messageType = "OrderCreatedEvent";

    // Act
    var context = _createContext(messageType: messageType);

    // Assert
    await Assert.That(context.MessageType).IsEqualTo(messageType);
  }

  [Test]
  public async Task Component_RequiredProperty_CanBeSetAsync() {
    // Arrange
    const TraceComponents component = TraceComponents.Handlers;

    // Act
    var context = _createContext(component: component);

    // Assert
    await Assert.That(context.Component).IsEqualTo(component);
  }

  [Test]
  public async Task Verbosity_RequiredProperty_CanBeSetAsync() {
    // Arrange
    const TraceVerbosity verbosity = TraceVerbosity.Debug;

    // Act
    var context = _createContext(verbosity: verbosity);

    // Assert
    await Assert.That(context.Verbosity).IsEqualTo(verbosity);
  }

  #endregion

  #region Optional Property Tests

  [Test]
  public async Task CausationId_OptionalProperty_CanBeNullAsync() {
    // Arrange & Act
    var context = _createContext();

    // Assert
    await Assert.That(context.CausationId).IsNull();
  }

  [Test]
  public async Task CausationId_OptionalProperty_CanBeSetAsync() {
    // Arrange
    const string causationId = "cause-789";

    // Act
    var context = _createContext() with { CausationId = causationId };

    // Assert
    await Assert.That(context.CausationId).IsEqualTo(causationId);
  }

  [Test]
  public async Task HandlerName_OptionalProperty_CanBeNullAsync() {
    // Arrange & Act - For message-level traces
    var context = _createContext();

    // Assert
    await Assert.That(context.HandlerName).IsNull();
  }

  [Test]
  public async Task HandlerName_OptionalProperty_CanBeSetAsync() {
    // Arrange
    const string handlerName = "OrderReceptor";

    // Act
    var context = _createContext() with { HandlerName = handlerName };

    // Assert
    await Assert.That(context.HandlerName).IsEqualTo(handlerName);
  }

  [Test]
  public async Task ExplicitSource_OptionalProperty_CanBeNullAsync() {
    // Arrange & Act
    var context = _createContext();

    // Assert
    await Assert.That(context.ExplicitSource).IsNull();
  }

  [Test]
  public async Task ExplicitSource_OptionalProperty_CanBeSetAsync() {
    // Arrange & Act
    var context = _createContext() with {
      IsExplicit = true,
      ExplicitSource = "attribute"
    };

    // Assert
    await Assert.That(context.ExplicitSource).IsEqualTo("attribute");
  }

  #endregion

  #region Default Value Tests

  [Test]
  public async Task IsExplicit_DefaultValue_IsFalseAsync() {
    // Arrange & Act
    var context = _createContext();

    // Assert
    await Assert.That(context.IsExplicit).IsFalse();
  }

  [Test]
  public async Task HopCount_DefaultValue_IsZeroAsync() {
    // Arrange & Act
    var context = _createContext();

    // Assert
    await Assert.That(context.HopCount).IsEqualTo(0);
  }

  [Test]
  public async Task Properties_DefaultValue_IsEmptyDictionaryAsync() {
    // Arrange & Act
    var context = _createContext();

    // Assert
    await Assert.That(context.Properties).IsNotNull();
    await Assert.That(context.Properties.Count).IsEqualTo(0);
  }

  #endregion

  #region Properties Dictionary Tests

  [Test]
  public async Task Properties_CanBePopulatedAsync() {
    // Arrange
    var context = _createContext();

    // Act
    context.Properties["customKey"] = "customValue";
    context.Properties["numericKey"] = 42;

    // Assert
    await Assert.That(context.Properties.Count).IsEqualTo(2);
    await Assert.That(context.Properties["customKey"]).IsEqualTo("customValue");
    await Assert.That(context.Properties["numericKey"]).IsEqualTo(42);
  }

  #endregion

  #region Record Behavior Tests

  [Test]
  public async Task TraceContext_IsRecordAsync() {
    // Arrange
    var type = typeof(TraceContext);

    // Assert - Records support with expressions
    await Assert.That(type.IsClass).IsTrue();
  }

  [Test]
  public async Task TraceContext_SupportsWith_ExpressionAsync() {
    // Arrange
    var original = _createContext(messageType: "Original");

    // Act
    var modified = original with { MessageType = "Modified" };

    // Assert
    await Assert.That(original.MessageType).IsEqualTo("Original");
    await Assert.That(modified.MessageType).IsEqualTo("Modified");
    await Assert.That(original.MessageId).IsEqualTo(modified.MessageId);
  }

  #endregion

  #region Helper Methods

  private static TraceContext _createContext(
      Guid? messageId = null,
      string? correlationId = null,
      string? messageType = null,
      TraceComponents? component = null,
      TraceVerbosity? verbosity = null) {
    return new TraceContext {
      MessageId = messageId ?? Guid.NewGuid(),
      CorrelationId = correlationId ?? "test-correlation",
      MessageType = messageType ?? "TestMessage",
      Component = component ?? TraceComponents.Handlers,
      Verbosity = verbosity ?? TraceVerbosity.Normal,
      StartTime = DateTimeOffset.UtcNow
    };
  }

  #endregion
}
