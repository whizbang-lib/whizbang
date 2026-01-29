using System.Text.Json;
using TUnit.Core;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Tests for command auditing functionality.
/// Verifies that commands can be audited separately from events.
/// </summary>
public class CommandAuditTests {
  [Test]
  public async Task CommandAuditEnabled_DefaultsFalse_Async() {
    // Arrange
    var options = new SystemEventOptions();

    // Assert - Command audit is off by default
    await Assert.That(options.CommandAuditEnabled).IsFalse();
  }

  [Test]
  public async Task EnableCommandAudit_SetsCommandAuditEnabled_Async() {
    // Arrange
    var options = new SystemEventOptions();

    // Act
    options.EnableCommandAudit();

    // Assert
    await Assert.That(options.CommandAuditEnabled).IsTrue();
  }

  [Test]
  public async Task EnableCommandAudit_ReturnsThisForFluentApi_Async() {
    // Arrange
    var options = new SystemEventOptions();

    // Act
    var result = options.EnableCommandAudit();

    // Assert
    await Assert.That(result).IsSameReferenceAs(options);
  }

  [Test]
  public async Task EnableAudit_EnablesBothEventAndCommandAudit_Async() {
    // Arrange
    var options = new SystemEventOptions();

    // Act
    options.EnableAudit();

    // Assert - Both should be enabled
    await Assert.That(options.EventAuditEnabled).IsTrue();
    await Assert.That(options.CommandAuditEnabled).IsTrue();
  }

  [Test]
  public async Task EnableEventAudit_OnlyEnablesEventAudit_Async() {
    // Arrange
    var options = new SystemEventOptions();

    // Act
    options.EnableEventAudit();

    // Assert - Only event audit should be enabled
    await Assert.That(options.EventAuditEnabled).IsTrue();
    await Assert.That(options.CommandAuditEnabled).IsFalse();
  }

  [Test]
  public async Task EnableCommandAudit_OnlyEnablesCommandAudit_Async() {
    // Arrange
    var options = new SystemEventOptions();

    // Act
    options.EnableCommandAudit();

    // Assert - Only command audit should be enabled
    await Assert.That(options.CommandAuditEnabled).IsTrue();
    await Assert.That(options.EventAuditEnabled).IsFalse();
  }

  [Test]
  public async Task EnableAll_EnablesBothAudits_Async() {
    // Arrange
    var options = new SystemEventOptions();

    // Act
    options.EnableAll();

    // Assert - Both should be enabled
    await Assert.That(options.EventAuditEnabled).IsTrue();
    await Assert.That(options.CommandAuditEnabled).IsTrue();
  }

  [Test]
  public async Task IsEnabled_CommandAudited_ReturnsFalse_WhenNotEnabled_Async() {
    // Arrange
    var options = new SystemEventOptions();

    // Act & Assert
    await Assert.That(options.IsEnabled<CommandAudited>()).IsFalse();
  }

  [Test]
  public async Task IsEnabled_CommandAudited_ReturnsTrue_WhenCommandAuditEnabled_Async() {
    // Arrange
    var options = new SystemEventOptions().EnableCommandAudit();

    // Act & Assert
    await Assert.That(options.IsEnabled<CommandAudited>()).IsTrue();
  }

  [Test]
  public async Task IsEnabled_EventAudited_ReturnsTrue_WhenEventAuditEnabled_Async() {
    // Arrange
    var options = new SystemEventOptions().EnableEventAudit();

    // Act & Assert
    await Assert.That(options.IsEnabled<EventAudited>()).IsTrue();
    await Assert.That(options.IsEnabled<CommandAudited>()).IsFalse();
  }
}

/// <summary>
/// Tests for CommandAudited system event.
/// </summary>
public class CommandAuditedTests {
  [Test]
  public async Task CommandAudited_ImplementsISystemEvent_Async() {
    // Assert - CommandAudited should implement ISystemEvent
    await Assert.That(typeof(ISystemEvent).IsAssignableFrom(typeof(CommandAudited))).IsTrue();
  }

  [Test]
  public async Task CommandAudited_HasRequiredProperties_Async() {
    // Arrange
    var audited = new CommandAudited {
      Id = Guid.NewGuid(),
      CommandType = "CreateOrder",
      CommandBody = JsonSerializer.SerializeToElement(new { OrderId = Guid.NewGuid() }),
      Timestamp = DateTimeOffset.UtcNow,
      TenantId = "tenant-1",
      UserId = "user-1",
      UserName = "Test User",
      CorrelationId = "correlation-123"
    };

    // Assert - All properties should be set
    await Assert.That(audited.Id).IsNotEqualTo(Guid.Empty);
    await Assert.That(audited.CommandType).IsEqualTo("CreateOrder");
    await Assert.That(audited.TenantId).IsEqualTo("tenant-1");
    await Assert.That(audited.UserId).IsEqualTo("user-1");
    await Assert.That(audited.UserName).IsEqualTo("Test User");
    await Assert.That(audited.CorrelationId).IsEqualTo("correlation-123");
  }

  [Test]
  public async Task CommandAudited_HasAuditEventAttribute_WithExcludeTrue_Async() {
    // Act
    var attribute = typeof(CommandAudited)
        .GetCustomAttributes(typeof(Whizbang.Core.Attributes.AuditEventAttribute), false)
        .FirstOrDefault() as Whizbang.Core.Attributes.AuditEventAttribute;

    // Assert - Should have Exclude = true to prevent self-auditing
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Exclude).IsTrue();
  }
}
