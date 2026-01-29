using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Tests that verify system events are excluded from auditing to prevent infinite loops.
/// When system audit is enabled, EventAudited should NOT trigger another EventAudited.
/// </summary>
public class SystemEventSelfAuditPreventionTests {
  [Test]
  public async Task EventAudited_HasExcludeAttribute_PreventsSelfAuditingAsync() {
    // Arrange - Get the AuditEvent attribute from EventAudited
    var attribute = typeof(EventAudited)
        .GetCustomAttributes(typeof(AuditEventAttribute), true)
        .Cast<AuditEventAttribute>()
        .FirstOrDefault();

    // Assert - Must have attribute with Exclude = true
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Exclude).IsTrue();
  }

  [Test]
  public async Task EventAudited_ExcludeReason_DocumentsWhyAsync() {
    // Arrange
    var attribute = typeof(EventAudited)
        .GetCustomAttributes(typeof(AuditEventAttribute), true)
        .Cast<AuditEventAttribute>()
        .FirstOrDefault();

    // Assert - Reason should explain the exclusion
    await Assert.That(attribute).IsNotNull();
    await Assert.That(attribute!.Reason).IsNotNull();
    await Assert.That(attribute.Reason).Contains("self-audit");
  }

  [Test]
  public async Task ISystemEvent_Implementations_ShouldBeExcludedFromAudit_ConventionAsync() {
    // This test documents the convention: all ISystemEvent implementations
    // should have [AuditEvent(Exclude = true)] to prevent audit loops.

    // Arrange - Find all types implementing ISystemEvent in Core
    var systemEventTypes = typeof(ISystemEvent).Assembly
        .GetTypes()
        .Where(t => typeof(ISystemEvent).IsAssignableFrom(t)
                    && t != typeof(ISystemEvent)
                    && !t.IsAbstract);

    // Assert - All should have Exclude = true
    foreach (var type in systemEventTypes) {
      var attribute = type
          .GetCustomAttributes(typeof(AuditEventAttribute), true)
          .Cast<AuditEventAttribute>()
          .FirstOrDefault();

      // System events must either:
      // 1. Have [AuditEvent(Exclude = true)]
      // 2. Or we check at runtime via ISystemEvent interface
      // For now, we require the attribute for explicitness
      await Assert.That(attribute)
          .IsNotNull()
          .Because($"System event {type.Name} must have [AuditEvent] attribute");
      await Assert.That(attribute!.Exclude)
          .IsTrue()
          .Because($"System event {type.Name} must have Exclude = true to prevent self-auditing");
    }
  }

  [Test]
  public async Task ShouldAuditEvent_ReturnsFalse_ForSystemEventsAsync() {
    // Arrange - Helper method that would be used by audit emission logic
    static bool ShouldAuditEvent(Type eventType) {
      // Check 1: Is it a system event? (interface check - fast path)
      if (typeof(ISystemEvent).IsAssignableFrom(eventType)) {
        return false;
      }

      // Check 2: Does it have [AuditEvent(Exclude = true)]?
      var attribute = eventType
          .GetCustomAttributes(typeof(AuditEventAttribute), true)
          .Cast<AuditEventAttribute>()
          .FirstOrDefault();

      if (attribute?.Exclude == true) {
        return false;
      }

      // Default: audit the event
      return true;
    }

    // Assert
    await Assert.That(ShouldAuditEvent(typeof(EventAudited))).IsFalse();

    // Regular domain events should be audited
    await Assert.That(ShouldAuditEvent(typeof(TestDomainEvent))).IsTrue();

    // Excluded domain events should not be audited
    await Assert.That(ShouldAuditEvent(typeof(TestExcludedDomainEvent))).IsFalse();
  }
}

// Test helper types
internal sealed record TestDomainEvent(Guid Id) : Whizbang.Core.IEvent;

[AuditEvent(Exclude = true)]
internal sealed record TestExcludedDomainEvent(Guid Id) : Whizbang.Core.IEvent;
