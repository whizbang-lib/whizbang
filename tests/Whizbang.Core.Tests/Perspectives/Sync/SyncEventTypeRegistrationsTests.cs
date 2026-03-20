using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests for SyncEventTypeRegistrations static registration system.
/// These tests must run sequentially because they modify shared static state.
/// </summary>
[NotInParallel("SyncEventTypeRegistrations")]
public class SyncEventTypeRegistrationsTests {
  [Before(Test)]
  public void Setup() {
    // Clear registrations before each test
    SyncEventTypeRegistrations.Clear();
  }

  [After(Test)]
  public void Teardown() {
    // Clear registrations after each test
    SyncEventTypeRegistrations.Clear();
  }

  [Test]
  public async Task Register_SingleMapping_AddsMappingAsync() {
    // Arrange
    var eventType = typeof(RegistrationTestEvent);
    const string perspectiveName = "TestPerspective";

    // Act
    SyncEventTypeRegistrations.Register(eventType, perspectiveName);

    // Assert
    var mappings = SyncEventTypeRegistrations.GetMappings();
    await Assert.That(mappings).ContainsKey(eventType);
    await Assert.That(mappings[eventType]).Contains(perspectiveName);
  }

  [Test]
  public async Task Register_MultiplePerspectivesForSameEvent_AddsBothAsync() {
    // Arrange
    var eventType = typeof(RegistrationTestEvent);
    const string perspective1 = "Perspective1";
    const string perspective2 = "Perspective2";

    // Act
    SyncEventTypeRegistrations.Register(eventType, perspective1);
    SyncEventTypeRegistrations.Register(eventType, perspective2);

    // Assert
    var mappings = SyncEventTypeRegistrations.GetMappings();
    await Assert.That(mappings[eventType]).Contains(perspective1);
    await Assert.That(mappings[eventType]).Contains(perspective2);
    await Assert.That(mappings[eventType].Length).IsEqualTo(2);
  }

  [Test]
  public async Task Register_SamePerspectiveTwice_DoesNotDuplicateAsync() {
    // Arrange
    var eventType = typeof(RegistrationTestEvent);
    const string perspectiveName = "TestPerspective";

    // Act
    SyncEventTypeRegistrations.Register(eventType, perspectiveName);
    SyncEventTypeRegistrations.Register(eventType, perspectiveName);

    // Assert
    var mappings = SyncEventTypeRegistrations.GetMappings();
    await Assert.That(mappings[eventType].Length).IsEqualTo(1);
  }

  [Test]
  public async Task GetMappings_NoRegistrations_ReturnsEmptyDictionaryAsync() {
    // Act
    var mappings = SyncEventTypeRegistrations.GetMappings();

    // Assert
    await Assert.That(mappings.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Clear_WithRegistrations_RemovesAllAsync() {
    // Arrange
    SyncEventTypeRegistrations.Register(typeof(RegistrationTestEvent), "Perspective1");
    SyncEventTypeRegistrations.Register(typeof(RegistrationTestEvent2), "Perspective2");

    // Act
    SyncEventTypeRegistrations.Clear();

    // Assert
    var mappings = SyncEventTypeRegistrations.GetMappings();
    await Assert.That(mappings.Count).IsEqualTo(0);
  }

  [Test]
  public void Register_NullEventType_ThrowsArgumentNullException() {
    // Act & Assert
    Assert.Throws<ArgumentNullException>(() =>
        SyncEventTypeRegistrations.Register(null!, "Perspective"));
  }

  [Test]
  public void Register_NullPerspectiveName_ThrowsArgumentNullException() {
    // Act & Assert
    Assert.Throws<ArgumentNullException>(() =>
        SyncEventTypeRegistrations.Register(typeof(RegistrationTestEvent), null!));
  }
}

// Test types
internal sealed class RegistrationTestEvent { }
internal sealed class RegistrationTestEvent2 { }
