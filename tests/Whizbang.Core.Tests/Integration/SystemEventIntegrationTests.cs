using System.Text.Json;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Audit;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.Integration;

/// <summary>
/// Integration tests for system events.
/// Verifies that system events are correctly excluded from tag discovery
/// and that the full configuration stack works together.
/// </summary>
[Category("Integration")]
public class SystemEventIntegrationTests {
  [Test]
  public async Task SystemEventOptions_FullConfiguration_WorksCorrectly_Async() {
    // Integration test: configure all options and verify state

    // Arrange & Act
    var options = new SystemEventOptions()
        .EnableEventAudit()
        .EnableCommandAudit()
        .EnablePerspectiveEvents()
        .EnableErrorEvents()
        .Broadcast();

    // Assert - All settings should be correct
    await Assert.That(options.EventAuditEnabled).IsTrue();
    await Assert.That(options.CommandAuditEnabled).IsTrue();
    await Assert.That(options.PerspectiveEventsEnabled).IsTrue();
    await Assert.That(options.ErrorEventsEnabled).IsTrue();
    await Assert.That(options.LocalOnly).IsFalse();
    await Assert.That(options.AuditEnabled).IsTrue(); // Convenience property

    // IsEnabled should work for both audit types
    await Assert.That(options.IsEnabled<EventAudited>()).IsTrue();
    await Assert.That(options.IsEnabled<CommandAudited>()).IsTrue();
  }

  [Test]
  public async Task SystemEventOptions_SelectiveConfiguration_WorksCorrectly_Async() {
    // Integration test: configure only event audit, verify command audit is off

    // Arrange & Act
    var options = new SystemEventOptions()
        .EnableEventAudit();

    // Assert
    await Assert.That(options.EventAuditEnabled).IsTrue();
    await Assert.That(options.CommandAuditEnabled).IsFalse();
    await Assert.That(options.AuditEnabled).IsTrue(); // True because event audit is on
    await Assert.That(options.LocalOnly).IsTrue(); // Default

    await Assert.That(options.IsEnabled<EventAudited>()).IsTrue();
    await Assert.That(options.IsEnabled<CommandAudited>()).IsFalse();
  }

  [Test]
  public async Task EventAudited_CanSerializeAndDeserialize_Async() {
    // Integration test: verify EventAudited works with JSON serialization

    // Arrange
    var original = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "OrderCreated",
      OriginalStreamId = "Order-123",
      OriginalStreamPosition = 5,
      OriginalBody = JsonSerializer.SerializeToElement(new { OrderId = "123", Total = 99.99m }),
      Timestamp = DateTimeOffset.UtcNow,
      TenantId = "tenant-1",
      UserId = "user-1",
      UserName = "Test User",
      CorrelationId = "corr-123",
      CausationId = "cause-456",
      AuditReason = "Compliance",
      AuditLevel = AuditLevel.Info
    };

    // Act
    var json = JsonSerializer.Serialize(original);
    var deserialized = JsonSerializer.Deserialize<EventAudited>(json);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Id).IsEqualTo(original.Id);
    await Assert.That(deserialized.OriginalEventType).IsEqualTo(original.OriginalEventType);
    await Assert.That(deserialized.OriginalStreamId).IsEqualTo(original.OriginalStreamId);
    await Assert.That(deserialized.TenantId).IsEqualTo(original.TenantId);
    await Assert.That(deserialized.AuditLevel).IsEqualTo(original.AuditLevel);
  }

  [Test]
  public async Task CommandAudited_CanSerializeAndDeserialize_Async() {
    // Integration test: verify CommandAudited works with JSON serialization

    // Arrange
    var original = new CommandAudited {
      Id = Guid.NewGuid(),
      CommandType = "CreateOrder",
      CommandBody = JsonSerializer.SerializeToElement(new { CustomerId = "cust-1", ItemCount = 1 }),
      Timestamp = DateTimeOffset.UtcNow,
      TenantId = "tenant-1",
      UserId = "user-1",
      UserName = "Test User",
      CorrelationId = "corr-123",
      ReceptorName = "OrderReceptor",
      ResponseType = "OrderCreated"
    };

    // Act
    var json = JsonSerializer.Serialize(original);
    var deserialized = JsonSerializer.Deserialize<CommandAudited>(json);

    // Assert
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Id).IsEqualTo(original.Id);
    await Assert.That(deserialized.CommandType).IsEqualTo(original.CommandType);
    await Assert.That(deserialized.ReceptorName).IsEqualTo(original.ReceptorName);
    await Assert.That(deserialized.ResponseType).IsEqualTo(original.ResponseType);
  }

  [Test]
  public async Task SystemEvents_ImplementISystemEvent_Async() {
    // Integration test: verify both system events implement the marker interface

    // Assert
    await Assert.That(typeof(ISystemEvent).IsAssignableFrom(typeof(EventAudited))).IsTrue();
    await Assert.That(typeof(ISystemEvent).IsAssignableFrom(typeof(CommandAudited))).IsTrue();
  }

  [Test]
  public async Task SystemEventStreams_ProvidesDedicatedStreamName_Async() {
    // Integration test: verify system events go to dedicated stream

    // Assert
    await Assert.That(SystemEventStreams.Name).IsEqualTo("$wb-system");
    await Assert.That(SystemEventStreams.Prefix).IsEqualTo("$wb-");
  }

  [Test]
  public async Task AuditEventAttribute_OnSystemEvents_HasExcludeTrue_Async() {
    // Integration test: verify both system events have Exclude = true

    // Act
    var eventAuditedAttr = typeof(EventAudited)
        .GetCustomAttributes(typeof(AuditEventAttribute), false)
        .FirstOrDefault() as AuditEventAttribute;

    var commandAuditedAttr = typeof(CommandAudited)
        .GetCustomAttributes(typeof(AuditEventAttribute), false)
        .FirstOrDefault() as AuditEventAttribute;

    // Assert
    await Assert.That(eventAuditedAttr).IsNotNull();
    await Assert.That(eventAuditedAttr!.Exclude).IsTrue();

    await Assert.That(commandAuditedAttr).IsNotNull();
    await Assert.That(commandAuditedAttr!.Exclude).IsTrue();
  }
}
