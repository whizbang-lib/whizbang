using TUnit.Core;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Tests for SystemEventOptions configuration.
/// </summary>
public class SystemEventOptionsTests {
  [Test]
  public async Task LocalOnly_DefaultsToTrue_PreventsAccidentalBroadcastAsync() {
    // Arrange
    var options = new SystemEventOptions();

    // Assert - Default should be LocalOnly to prevent duplicate auditing
    await Assert.That(options.LocalOnly).IsTrue();
  }

  [Test]
  public async Task EnableAudit_SetsAuditEnabled_ReturnsThisForFluentApiAsync() {
    // Arrange
    var options = new SystemEventOptions();

    // Act
    var result = options.EnableAudit();

    // Assert
    await Assert.That(options.AuditEnabled).IsTrue();
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task EnableAll_SetsAllFlags_ReturnsThisForFluentApiAsync() {
    // Arrange
    var options = new SystemEventOptions();

    // Act
    var result = options.EnableAll();

    // Assert
    await Assert.That(options.AuditEnabled).IsTrue();
    await Assert.That(options.PerspectiveEventsEnabled).IsTrue();
    await Assert.That(options.ErrorEventsEnabled).IsTrue();
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task Broadcast_SetsLocalOnlyFalse_ForCentralizedCollectionAsync() {
    // Arrange
    var options = new SystemEventOptions();

    // Act
    var result = options.Broadcast();

    // Assert - LocalOnly should be false to allow system event transport
    await Assert.That(options.LocalOnly).IsFalse();
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task FluentApi_CanChainMultipleCalls_Async() {
    // Arrange & Act
    var options = new SystemEventOptions()
        .EnableAudit()
        .EnableErrorEvents()
        .Broadcast();

    // Assert
    await Assert.That(options.AuditEnabled).IsTrue();
    await Assert.That(options.ErrorEventsEnabled).IsTrue();
    await Assert.That(options.LocalOnly).IsFalse();
    await Assert.That(options.PerspectiveEventsEnabled).IsFalse(); // Not enabled
  }

  [Test]
  public async Task IsEnabled_EventAudited_ReturnsTrue_WhenAuditEnabledAsync() {
    // Arrange
    var options = new SystemEventOptions().EnableAudit();

    // Act & Assert
    await Assert.That(options.IsEnabled<EventAudited>()).IsTrue();
  }

  [Test]
  public async Task IsEnabled_EventAudited_ReturnsFalse_WhenNotEnabledAsync() {
    // Arrange
    var options = new SystemEventOptions();

    // Act & Assert
    await Assert.That(options.IsEnabled<EventAudited>()).IsFalse();
  }

  [Test]
  public async Task IsEnabled_NonSystemEvent_ReturnsFalse_Async() {
    // Arrange
    var options = new SystemEventOptions().EnableAll();

    // Act - Check with a non-system event type
    var result = options.IsEnabled(typeof(TestNonSystemEvent));

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task EnablePerspectiveEvents_SetsPerspectiveEventsEnabled_Async() {
    // Arrange
    var options = new SystemEventOptions();

    // Act
    options.EnablePerspectiveEvents();

    // Assert
    await Assert.That(options.PerspectiveEventsEnabled).IsTrue();
    await Assert.That(options.AuditEnabled).IsFalse(); // Others not affected
  }

  [Test]
  public async Task EnableErrorEvents_SetsErrorEventsEnabled_Async() {
    // Arrange
    var options = new SystemEventOptions();

    // Act
    options.EnableErrorEvents();

    // Assert
    await Assert.That(options.ErrorEventsEnabled).IsTrue();
    await Assert.That(options.AuditEnabled).IsFalse(); // Others not affected
  }
}

// Test helper - not a system event
internal sealed record TestNonSystemEvent(Guid Id) : Whizbang.Core.IEvent;
