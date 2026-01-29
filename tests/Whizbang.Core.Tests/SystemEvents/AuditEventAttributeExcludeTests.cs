using TUnit.Core;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Tests for AuditEventAttribute.Exclude property.
/// When system audit is enabled, all events are audited by default.
/// Use Exclude=true to opt-out specific event types from auditing.
/// </summary>
public class AuditEventAttributeExcludeTests {
  [Test]
  public async Task AuditEventAttribute_ExcludeDefaultsFalse_AllEventsIncludedByDefaultAsync() {
    // Arrange
    var attribute = new AuditEventAttribute();

    // Assert - By default, events are NOT excluded (included in audit)
    await Assert.That(attribute.Exclude).IsFalse();
  }

  [Test]
  public async Task AuditEventAttribute_ExcludeTrue_ExcludesEventFromAuditAsync() {
    // Arrange - Mark event to be excluded from system audit
    var attribute = new AuditEventAttribute { Exclude = true };

    // Assert
    await Assert.That(attribute.Exclude).IsTrue();
  }

  [Test]
  public async Task AuditEventAttribute_ExcludeWithReason_DocumentsWhyExcludedAsync() {
    // Arrange - Exclude with documented reason
    var attribute = new AuditEventAttribute {
      Exclude = true,
      Reason = "High-frequency event, would overwhelm audit log"
    };

    // Assert
    await Assert.That(attribute.Exclude).IsTrue();
    await Assert.That(attribute.Reason).IsEqualTo("High-frequency event, would overwhelm audit log");
  }

  [Test]
  public async Task AuditEventAttribute_OnEventType_CanBeReadViaReflectionAsync() {
    // Arrange - Get attribute from a test event type
    var attribute = typeof(TestExcludedEvent)
        .GetCustomAttributes(typeof(AuditEventAttribute), true)
        .Cast<AuditEventAttribute>()
        .FirstOrDefault();

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Exclude).IsTrue();
  }

  [Test]
  public async Task AuditEventAttribute_NotExcluded_HasReasonForAuditingAsync() {
    // Arrange - Event that IS audited with reason
    var attribute = typeof(TestAuditedEvent)
        .GetCustomAttributes(typeof(AuditEventAttribute), true)
        .Cast<AuditEventAttribute>()
        .FirstOrDefault();

    // Assert
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Exclude).IsFalse();
    await Assert.That(attribute.Reason).IsEqualTo("Financial transaction");
  }
}

// Test event types

[AuditEvent(Exclude = true)]
internal sealed record TestExcludedEvent(Guid Id) : Whizbang.Core.IEvent;

[AuditEvent(Reason = "Financial transaction")]
internal sealed record TestAuditedEvent(Guid Id) : Whizbang.Core.IEvent;
