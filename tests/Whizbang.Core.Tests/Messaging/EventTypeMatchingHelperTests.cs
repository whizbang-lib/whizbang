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
    const string typeName = "MyApp.Events.OrderCreated, MyApp.Contracts";

    // Act
    var result = EventTypeMatchingHelper.NormalizeTypeName(typeName);

    // Assert
    await Assert.That(result).IsEqualTo("MyApp.Events.OrderCreated, MyApp.Contracts");
  }

  [Test]
  public async Task NormalizeTypeName_WithVersionInfo_StripsVersionAsync() {
    // Arrange
    const string typeName = "MyApp.Events.OrderCreated, MyApp.Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    // Act
    var result = EventTypeMatchingHelper.NormalizeTypeName(typeName);

    // Assert
    await Assert.That(result).IsEqualTo("MyApp.Events.OrderCreated, MyApp.Contracts");
  }

  [Test]
  public async Task NormalizeTypeName_WithPartialVersionInfo_StripsAllMetadataAsync() {
    // Arrange - Version only
    const string typeName1 = "MyApp.OrderCreated, MyApp, Version=1.0.0.0";

    // Act
    var result1 = EventTypeMatchingHelper.NormalizeTypeName(typeName1);

    // Assert
    await Assert.That(result1).IsEqualTo("MyApp.OrderCreated, MyApp");
  }

  [Test]
  public async Task NormalizeTypeName_WithGenericType_StripsVersionFromBothAsync() {
    // Arrange - Generic type with nested version info
    const string typeName = "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.OrderCreated, MyApp, Version=1.0.0.0]], Whizbang.Core, Version=2.0.0.0";

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
    const string messageTypeName = "Some.Other.Event, OtherAssembly";

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

  // ========================================
  // TryResolveType / BuildTypeLookup Tests
  //
  // The canonical stored-EventType -> concrete Type resolver that replaces the
  // hand-rolled per-store type maps (EFCore _resolveConcreteType / _buildEventTypeLookup
  // / inline; Dapper _buildTypeLookup). One normalized strategy for every read path.
  // ========================================

  [Test]
  public async Task TryResolveType_WithStorageForm_ResolvesAsync() {
    // Arrange — "FullName, AssemblyName" is exactly what TypeNameFormatter.Format / AppendAsync writes.
    var lookup = EventTypeMatchingHelper.BuildTypeLookup([typeof(TestEvent)]);
    var stored = TypeNameFormatter.Format(typeof(TestEvent));

    // Act
    var ok = EventTypeMatchingHelper.TryResolveType(lookup, stored, out var resolved);

    // Assert
    await Assert.That(ok).IsTrue();
    await Assert.That(resolved).IsEqualTo(typeof(TestEvent));
  }

  [Test]
  public async Task TryResolveType_WithAssemblyQualifiedNameWithVersion_ResolvesAsync() {
    // Arrange — old rows / cross-build rows may carry the full AQN with version.
    var lookup = EventTypeMatchingHelper.BuildTypeLookup([typeof(TestEvent)]);
    var stored = typeof(TestEvent).AssemblyQualifiedName!;

    // Act
    var ok = EventTypeMatchingHelper.TryResolveType(lookup, stored, out var resolved);

    // Assert — normalization strips Version/Culture/PublicKeyToken before matching.
    await Assert.That(ok).IsTrue();
    await Assert.That(resolved).IsEqualTo(typeof(TestEvent));
  }

  [Test]
  public async Task TryResolveType_WithFullNameOnly_ResolvesAsync() {
    // Arrange
    var lookup = EventTypeMatchingHelper.BuildTypeLookup([typeof(TestEvent)]);
    var stored = typeof(TestEvent).FullName!;

    // Act
    var ok = EventTypeMatchingHelper.TryResolveType(lookup, stored, out var resolved);

    // Assert
    await Assert.That(ok).IsTrue();
    await Assert.That(resolved).IsEqualTo(typeof(TestEvent));
  }

  [Test]
  public async Task TryResolveType_WithSimpleName_ResolvesAsync() {
    // Arrange
    var lookup = EventTypeMatchingHelper.BuildTypeLookup([typeof(TestEvent)]);

    // Act
    var ok = EventTypeMatchingHelper.TryResolveType(lookup, nameof(TestEvent), out var resolved);

    // Assert
    await Assert.That(ok).IsTrue();
    await Assert.That(resolved).IsEqualTo(typeof(TestEvent));
  }

  [Test]
  public async Task TryResolveType_WithNestedTypeStorageForm_ResolvesAsync() {
    // Arrange — nested types serialize with '+' in their FullName; Format must round-trip.
    // Plain nested type (not IEvent) so the message-context generator doesn't try to register it.
    var lookup = EventTypeMatchingHelper.BuildTypeLookup([typeof(NestedSample)]);
    var stored = TypeNameFormatter.Format(typeof(NestedSample));

    // Act
    var ok = EventTypeMatchingHelper.TryResolveType(lookup, stored, out var resolved);

    // Assert
    await Assert.That(ok).IsTrue();
    await Assert.That(resolved).IsEqualTo(typeof(NestedSample));
    await Assert.That(stored).Contains("+"); // confirms we exercised the nested-type path
  }

  [Test]
  public async Task TryResolveType_WithUnknownType_ReturnsFalseAsync() {
    // Arrange — a perspective only materializes its own candidate types; anything else is skipped.
    var lookup = EventTypeMatchingHelper.BuildTypeLookup([typeof(TestEvent)]);

    // Act
    var ok = EventTypeMatchingHelper.TryResolveType(lookup, "Some.Other.Event, OtherAssembly", out var resolved);

    // Assert
    await Assert.That(ok).IsFalse();
    await Assert.That(resolved).IsNull();
  }

  [Test]
  public async Task TryResolveType_WithNullOrEmptyStoredName_ReturnsFalseAsync() {
    // Arrange
    var lookup = EventTypeMatchingHelper.BuildTypeLookup([typeof(TestEvent)]);

    // Act & Assert
    await Assert.That(EventTypeMatchingHelper.TryResolveType(lookup, null!, out _)).IsFalse();
    await Assert.That(EventTypeMatchingHelper.TryResolveType(lookup, "", out _)).IsFalse();
  }

  [Test]
  public async Task TryResolveType_WithMultipleCandidates_ResolvesCorrectOneAsync() {
    // Arrange
    var lookup = EventTypeMatchingHelper.BuildTypeLookup([typeof(TestEvent), typeof(AnotherTestEvent)]);
    var stored = TypeNameFormatter.Format(typeof(AnotherTestEvent));

    // Act
    var ok = EventTypeMatchingHelper.TryResolveType(lookup, stored, out var resolved);

    // Assert
    await Assert.That(ok).IsTrue();
    await Assert.That(resolved).IsEqualTo(typeof(AnotherTestEvent));
  }

  // Test types for event matching
  private sealed record TestEvent : IEvent;
  private sealed record AnotherTestEvent : IEvent;

  // Plain nested type (deliberately NOT IEvent) to exercise the '+' nested-name path
  // in TypeNameFormatter.Format without the message-context generator registering it.
  private sealed record NestedSample;
}
