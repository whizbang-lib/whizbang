using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for EventTypeMatchingHelper which handles type name normalization and event matching.
/// </summary>
public class EventTypeMatchingHelperTests {
  // ========================================
  // NormalizeTypeName Tests
  // ========================================

  [Test]
  public async Task NormalizeTypeName_WithNullOrEmpty_ReturnsAsIsAsync() {
    // Act & Assert
    await Assert.That(EventTypeMatchingHelper.NormalizeTypeName(null!)).IsNull();
    await Assert.That(EventTypeMatchingHelper.NormalizeTypeName("")).IsEqualTo("");
  }

  [Test]
  public async Task NormalizeTypeName_WithSimpleTypeName_ReturnsUnchangedAsync() {
    // Arrange
    var typeName = "MyApp.Events.OrderCreated, MyApp.Contracts";

    // Act
    var result = EventTypeMatchingHelper.NormalizeTypeName(typeName);

    // Assert
    await Assert.That(result).IsEqualTo("MyApp.Events.OrderCreated, MyApp.Contracts");
  }

  [Test]
  public async Task NormalizeTypeName_WithVersionInfo_StripsVersionAsync() {
    // Arrange
    var typeName = "MyApp.Events.OrderCreated, MyApp.Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    // Act
    var result = EventTypeMatchingHelper.NormalizeTypeName(typeName);

    // Assert
    await Assert.That(result).IsEqualTo("MyApp.Events.OrderCreated, MyApp.Contracts");
  }

  [Test]
  public async Task NormalizeTypeName_WithPartialVersionInfo_StripsAllMetadataAsync() {
    // Arrange - Version only
    var typeName1 = "MyApp.OrderCreated, MyApp, Version=1.0.0.0";

    // Act
    var result1 = EventTypeMatchingHelper.NormalizeTypeName(typeName1);

    // Assert
    await Assert.That(result1).IsEqualTo("MyApp.OrderCreated, MyApp");
  }

  [Test]
  public async Task NormalizeTypeName_WithGenericType_StripsVersionFromBothAsync() {
    // Arrange - Generic type with nested version info
    var typeName = "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.OrderCreated, MyApp, Version=1.0.0.0]], Whizbang.Core, Version=2.0.0.0";

    // Act
    var result = EventTypeMatchingHelper.NormalizeTypeName(typeName);

    // Assert - Both inner and outer versions should be stripped
    await Assert.That(result).DoesNotContain("Version=");
    await Assert.That(result).Contains("MessageEnvelope`1[[MyApp.OrderCreated, MyApp]]");
    await Assert.That(result).Contains("Whizbang.Core");
  }

  // ========================================
  // IsEventType Tests
  // ========================================

  [Test]
  public async Task IsEventType_WithNullOrEmptyMessageType_ReturnsFalseAsync() {
    // Arrange
    var eventTypes = new List<Type> { typeof(TestEvent) };

    // Act & Assert
    await Assert.That(EventTypeMatchingHelper.IsEventType(null!, eventTypes)).IsFalse();
    await Assert.That(EventTypeMatchingHelper.IsEventType("", eventTypes)).IsFalse();
  }

  [Test]
  public async Task IsEventType_WithMatchingType_ReturnsTrueAsync() {
    // Arrange
    var eventTypes = new List<Type> { typeof(TestEvent) };
    var messageTypeName = typeof(TestEvent).FullName + ", " + typeof(TestEvent).Assembly.GetName().Name;

    // Act
    var result = EventTypeMatchingHelper.IsEventType(messageTypeName, eventTypes);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsEventType_WithAssemblyQualifiedName_ReturnsTrueAsync() {
    // Arrange
    var eventTypes = new List<Type> { typeof(TestEvent) };
    var messageTypeName = typeof(TestEvent).AssemblyQualifiedName!;

    // Act
    var result = EventTypeMatchingHelper.IsEventType(messageTypeName, eventTypes);

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsEventType_WithNonMatchingType_ReturnsFalseAsync() {
    // Arrange
    var eventTypes = new List<Type> { typeof(TestEvent) };
    var messageTypeName = "Some.Other.Event, OtherAssembly";

    // Act
    var result = EventTypeMatchingHelper.IsEventType(messageTypeName, eventTypes);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task IsEventType_WithEmptyEventTypes_ReturnsFalseAsync() {
    // Arrange
    var eventTypes = new List<Type>();
    var messageTypeName = typeof(TestEvent).FullName + ", " + typeof(TestEvent).Assembly.GetName().Name;

    // Act
    var result = EventTypeMatchingHelper.IsEventType(messageTypeName, eventTypes);

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task IsEventType_WithVersionMismatch_StillMatchesAsync() {
    // Arrange
    var eventTypes = new List<Type> { typeof(TestEvent) };
    // Create a type name with different version than actual
    var normalizedName = typeof(TestEvent).FullName + ", " + typeof(TestEvent).Assembly.GetName().Name;
    var messageTypeName = normalizedName + ", Version=9.9.9.9, Culture=neutral, PublicKeyToken=null";

    // Act
    var result = EventTypeMatchingHelper.IsEventType(messageTypeName, eventTypes);

    // Assert - Should match because normalization strips version info
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsEventType_WithMultipleEventTypes_MatchesCorrectOneAsync() {
    // Arrange
    var eventTypes = new List<Type> { typeof(TestEvent), typeof(AnotherTestEvent) };
    var messageTypeName = typeof(AnotherTestEvent).FullName + ", " + typeof(AnotherTestEvent).Assembly.GetName().Name;

    // Act
    var result = EventTypeMatchingHelper.IsEventType(messageTypeName, eventTypes);

    // Assert
    await Assert.That(result).IsTrue();
  }

  // Test types for event matching
  private sealed record TestEvent : IEvent;
  private sealed record AnotherTestEvent : IEvent;
}
