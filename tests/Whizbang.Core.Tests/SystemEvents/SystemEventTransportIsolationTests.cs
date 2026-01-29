using TUnit.Core;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Tests for system event transport isolation.
/// Verifies LocalOnly behavior prevents duplicate auditing across services.
/// </summary>
public class SystemEventTransportIsolationTests {
  [Test]
  public async Task ShouldPublishToOutbox_ReturnsFalse_WhenLocalOnlyAsync() {
    // Arrange
    var options = new SystemEventOptions().EnableAudit();
    // LocalOnly defaults to true

    // Act - Helper method that would be used by outbox publishing logic
    var shouldPublish = _shouldPublishSystemEventToOutbox(typeof(EventAudited), options);

    // Assert - Should NOT publish to outbox when LocalOnly
    await Assert.That(shouldPublish).IsFalse();
  }

  [Test]
  public async Task ShouldPublishToOutbox_ReturnsTrue_WhenBroadcastEnabledAsync() {
    // Arrange
    var options = new SystemEventOptions()
        .EnableAudit()
        .Broadcast(); // Explicitly enable broadcasting

    // Act
    var shouldPublish = _shouldPublishSystemEventToOutbox(typeof(EventAudited), options);

    // Assert - Should publish to outbox when Broadcast()
    await Assert.That(shouldPublish).IsTrue();
  }

  [Test]
  public async Task ShouldReceiveFromInbox_ReturnsFalse_WhenLocalOnlyAsync() {
    // Arrange
    var options = new SystemEventOptions().EnableAudit();
    // LocalOnly defaults to true

    // Act - Helper method that would be used by inbox filtering logic
    var shouldReceive = _shouldReceiveSystemEventFromInbox(typeof(EventAudited), options);

    // Assert - Should NOT receive from inbox when LocalOnly
    await Assert.That(shouldReceive).IsFalse();
  }

  [Test]
  public async Task ShouldReceiveFromInbox_ReturnsTrue_WhenBroadcastEnabledAsync() {
    // Arrange
    var options = new SystemEventOptions()
        .EnableAudit()
        .Broadcast();

    // Act
    var shouldReceive = _shouldReceiveSystemEventFromInbox(typeof(EventAudited), options);

    // Assert - Should receive from inbox when Broadcast()
    await Assert.That(shouldReceive).IsTrue();
  }

  [Test]
  public async Task LocalOnly_PreventsNetworkTraffic_ForSystemEventsAsync() {
    // Arrange
    var options = new SystemEventOptions()
        .EnableAudit()
        .EnableErrorEvents()
        .EnablePerspectiveEvents();
    // LocalOnly defaults to true

    // Assert - All system events should be local-only by default
    await Assert.That(options.LocalOnly).IsTrue();
    await Assert.That(_shouldPublishSystemEventToOutbox(typeof(EventAudited), options)).IsFalse();
  }

  [Test]
  public async Task CentralizedCollection_Scenario_RequiresBroadcastAsync() {
    // Scenario: Dedicated monitoring service collects system events from all hosts
    // This requires Broadcast() to enable transport

    // Arrange - Monitoring service configuration
    var monitoringServiceOptions = new SystemEventOptions()
        .EnableAll()
        .Broadcast(); // Required for centralized collection

    // Act & Assert
    await Assert.That(monitoringServiceOptions.LocalOnly).IsFalse();
    await Assert.That(_shouldPublishSystemEventToOutbox(typeof(EventAudited), monitoringServiceOptions)).IsTrue();
    await Assert.That(_shouldReceiveSystemEventFromInbox(typeof(EventAudited), monitoringServiceOptions)).IsTrue();
  }

  [Test]
  public async Task TypicalBffScenario_UsesLocalOnly_PreventsDuplicatesAsync() {
    // Scenario: BFF receives events from multiple services, audits locally
    // Each service also audits its own events locally
    // LocalOnly prevents duplicate EventAudited from flowing between services

    // Arrange - BFF configuration
    var bffOptions = new SystemEventOptions().EnableAudit();
    // LocalOnly = true by default

    // Arrange - User service configuration
    var userServiceOptions = new SystemEventOptions().EnableAudit();
    // LocalOnly = true by default

    // Assert - Both services audit locally, neither broadcasts
    await Assert.That(bffOptions.LocalOnly).IsTrue();
    await Assert.That(userServiceOptions.LocalOnly).IsTrue();

    // Neither should publish EventAudited to outbox
    await Assert.That(_shouldPublishSystemEventToOutbox(typeof(EventAudited), bffOptions)).IsFalse();
    await Assert.That(_shouldPublishSystemEventToOutbox(typeof(EventAudited), userServiceOptions)).IsFalse();
  }

  // Helper methods that represent the logic that would be used in transport layer

  private static bool _shouldPublishSystemEventToOutbox(Type eventType, SystemEventOptions options) {
    // Only system events are affected by this logic
    if (!typeof(ISystemEvent).IsAssignableFrom(eventType)) {
      return true; // Domain events always publish
    }

    // Check if this system event type is enabled
    if (!options.IsEnabled(eventType)) {
      return false; // Not enabled, don't publish
    }

    // LocalOnly prevents publishing
    return !options.LocalOnly;
  }

  private static bool _shouldReceiveSystemEventFromInbox(Type eventType, SystemEventOptions options) {
    // Only system events are affected by this logic
    if (!typeof(ISystemEvent).IsAssignableFrom(eventType)) {
      return true; // Domain events always received
    }

    // Check if this system event type is enabled
    if (!options.IsEnabled(eventType)) {
      return false; // Not enabled, don't receive
    }

    // LocalOnly prevents receiving
    return !options.LocalOnly;
  }
}
