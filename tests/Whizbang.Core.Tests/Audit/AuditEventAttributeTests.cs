using System;
using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Audit;

namespace Whizbang.Core.Tests.Audit;

/// <summary>
/// Tests for <see cref="AuditEventAttribute"/>.
/// Validates the selective audit marker attribute.
/// </summary>
/// <tests>Whizbang.Core/Attributes/AuditEventAttribute.cs</tests>
[Category("Core")]
[Category("Audit")]
public class AuditEventAttributeTests {

  [Test]
  public async Task AuditEventAttribute_InheritsFromMessageTagAttributeAsync() {
    // Assert
    await Assert.That(typeof(AuditEventAttribute).IsSubclassOf(typeof(MessageTagAttribute))).IsTrue();
  }

  [Test]
  public async Task AuditEventAttribute_Tag_DefaultsToAuditAsync() {
    // Arrange & Act
    var attr = new AuditEventAttribute();

    // Assert
    await Assert.That(attr.Tag).IsEqualTo("audit");
  }

  [Test]
  public async Task AuditEventAttribute_IncludeEvent_DefaultsToTrueAsync() {
    // Arrange & Act
    var attr = new AuditEventAttribute();

    // Assert
    await Assert.That(attr.IncludeEvent).IsTrue();
  }

  [Test]
  public async Task AuditEventAttribute_AttributeUsage_AllowsClassTargetAsync() {
    // Arrange
    var usage = typeof(AuditEventAttribute).GetCustomAttribute<AttributeUsageAttribute>();

    // Assert
    await Assert.That(usage).IsNotNull();
    await Assert.That((usage!.ValidOn & AttributeTargets.Class) != 0).IsTrue();
  }

  [Test]
  public async Task AuditEventAttribute_AttributeUsage_AllowsStructTargetAsync() {
    // Arrange
    var usage = typeof(AuditEventAttribute).GetCustomAttribute<AttributeUsageAttribute>();

    // Assert
    await Assert.That(usage).IsNotNull();
    await Assert.That((usage!.ValidOn & AttributeTargets.Struct) != 0).IsTrue();
  }

  [Test]
  public async Task AuditEventAttribute_AttributeUsage_DoesNotAllowMultipleAsync() {
    // Arrange
    var usage = typeof(AuditEventAttribute).GetCustomAttribute<AttributeUsageAttribute>();

    // Assert
    await Assert.That(usage).IsNotNull();
    await Assert.That(usage!.AllowMultiple).IsFalse();
  }

  [Test]
  public async Task AuditEventAttribute_AttributeUsage_AllowsInheritedAsync() {
    // Arrange
    var usage = typeof(AuditEventAttribute).GetCustomAttribute<AttributeUsageAttribute>();

    // Assert
    await Assert.That(usage).IsNotNull();
    await Assert.That(usage!.Inherited).IsTrue();
  }

  [Test]
  public async Task AuditEventAttribute_Reason_CanBeSetAsync() {
    // Arrange & Act
    var attr = new AuditEventAttribute { Reason = "PII access" };

    // Assert
    await Assert.That(attr.Reason).IsEqualTo("PII access");
  }

  [Test]
  public async Task AuditEventAttribute_Reason_IsNullByDefaultAsync() {
    // Arrange & Act
    var attr = new AuditEventAttribute();

    // Assert
    await Assert.That(attr.Reason is null).IsTrue();
  }

  [Test]
  public async Task AuditEventAttribute_Level_DefaultsToInfoAsync() {
    // Arrange & Act
    var attr = new AuditEventAttribute();

    // Assert
    await Assert.That(attr.Level).IsEqualTo(AuditLevel.Info);
  }

  [Test]
  public async Task AuditEventAttribute_Level_CanBeSetAsync() {
    // Arrange & Act
    var attr = new AuditEventAttribute { Level = AuditLevel.Critical };

    // Assert
    await Assert.That(attr.Level).IsEqualTo(AuditLevel.Critical);
  }

  [Test]
  public async Task AuditEventAttribute_CanBeAppliedToEventRecordAsync() {
    // Arrange
    var type = typeof(TestAuditedEvent);

    // Act
    var attr = type.GetCustomAttribute<AuditEventAttribute>();

    // Assert
    await Assert.That(attr).IsNotNull();
    await Assert.That(attr!.Reason).IsEqualTo("Customer data viewed");
    await Assert.That(attr.Level).IsEqualTo(AuditLevel.Warning);
  }

  [Test]
  public async Task AuditEventAttribute_NotPresentOnNonAuditedEventsAsync() {
    // Arrange
    var type = typeof(TestNonAuditedEvent);

    // Act
    var attr = type.GetCustomAttribute<AuditEventAttribute>();

    // Assert
    await Assert.That(attr is null).IsTrue();
  }

  // Test event types
  [AuditEvent(Reason = "Customer data viewed", Level = AuditLevel.Warning)]
  private sealed record TestAuditedEvent(Guid CustomerId, string ViewedBy);

  private sealed record TestNonAuditedEvent(Guid OrderId);
}
