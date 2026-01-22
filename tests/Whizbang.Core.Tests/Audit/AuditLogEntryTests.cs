using System;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Audit;

namespace Whizbang.Core.Tests.Audit;

/// <summary>
/// Tests for <see cref="AuditLogEntry"/>.
/// Validates the audit log read model for compliance and auditing scenarios.
/// </summary>
/// <tests>Whizbang.Core/Audit/AuditLogEntry.cs</tests>
[Category("Core")]
[Category("Audit")]
public class AuditLogEntryTests {

  [Test]
  public async Task AuditLogEntry_Id_CanBeSetAndRetrievedAsync() {
    // Arrange
    var id = Guid.NewGuid();

    // Act
    var entry = _createEntry() with { Id = id };

    // Assert
    await Assert.That(entry.Id).IsEqualTo(id);
  }

  [Test]
  public async Task AuditLogEntry_StreamId_CanBeSetAndRetrievedAsync() {
    // Arrange & Act
    var entry = _createEntry() with { StreamId = "Order-abc123" };

    // Assert
    await Assert.That(entry.StreamId).IsEqualTo("Order-abc123");
  }

  [Test]
  public async Task AuditLogEntry_StreamPosition_CanBeSetAndRetrievedAsync() {
    // Arrange & Act
    var entry = _createEntry() with { StreamPosition = 42 };

    // Assert
    await Assert.That(entry.StreamPosition).IsEqualTo(42);
  }

  [Test]
  public async Task AuditLogEntry_EventType_CanBeSetAndRetrievedAsync() {
    // Arrange & Act
    var entry = _createEntry() with { EventType = "OrderCreated" };

    // Assert
    await Assert.That(entry.EventType).IsEqualTo("OrderCreated");
  }

  [Test]
  public async Task AuditLogEntry_Timestamp_CanBeSetAndRetrievedAsync() {
    // Arrange
    var timestamp = DateTimeOffset.UtcNow;

    // Act
    var entry = _createEntry() with { Timestamp = timestamp };

    // Assert
    await Assert.That(entry.Timestamp).IsEqualTo(timestamp);
  }

  [Test]
  public async Task AuditLogEntry_TenantId_CanBeSetAndRetrievedAsync() {
    // Arrange & Act
    var entry = _createEntry() with { TenantId = "tenant-123" };

    // Assert
    await Assert.That(entry.TenantId).IsEqualTo("tenant-123");
  }

  [Test]
  public async Task AuditLogEntry_TenantId_CanBeNullAsync() {
    // Arrange & Act
    var entry = _createEntry() with { TenantId = null };

    // Assert
    await Assert.That(entry.TenantId is null).IsTrue();
  }

  [Test]
  public async Task AuditLogEntry_UserId_CanBeSetAndRetrievedAsync() {
    // Arrange & Act
    var entry = _createEntry() with { UserId = "user-456" };

    // Assert
    await Assert.That(entry.UserId).IsEqualTo("user-456");
  }

  [Test]
  public async Task AuditLogEntry_UserName_CanBeSetAndRetrievedAsync() {
    // Arrange & Act
    var entry = _createEntry() with { UserName = "John Doe" };

    // Assert
    await Assert.That(entry.UserName).IsEqualTo("John Doe");
  }

  [Test]
  public async Task AuditLogEntry_CorrelationId_CanBeSetAndRetrievedAsync() {
    // Arrange & Act
    var entry = _createEntry() with { CorrelationId = "corr-789" };

    // Assert
    await Assert.That(entry.CorrelationId).IsEqualTo("corr-789");
  }

  [Test]
  public async Task AuditLogEntry_CausationId_CanBeSetAndRetrievedAsync() {
    // Arrange & Act
    var entry = _createEntry() with { CausationId = "cause-012" };

    // Assert
    await Assert.That(entry.CausationId).IsEqualTo("cause-012");
  }

  [Test]
  public async Task AuditLogEntry_Body_CanBeSetAndRetrievedAsync() {
    // Arrange
    var bodyData = new { OrderId = "ord-123", Status = "Created" };
    var body = JsonSerializer.SerializeToElement(bodyData);

    // Act
    var entry = _createEntry() with { Body = body };

    // Assert
    await Assert.That(entry.Body.ValueKind).IsEqualTo(JsonValueKind.Object);
    await Assert.That(entry.Body.GetProperty("OrderId").GetString()).IsEqualTo("ord-123");
    await Assert.That(entry.Body.GetProperty("Status").GetString()).IsEqualTo("Created");
  }

  [Test]
  public async Task AuditLogEntry_AuditReason_CanBeSetAndRetrievedAsync() {
    // Arrange & Act
    var entry = _createEntry() with { AuditReason = "PII access" };

    // Assert
    await Assert.That(entry.AuditReason).IsEqualTo("PII access");
  }

  [Test]
  public async Task AuditLogEntry_AuditReason_CanBeNullAsync() {
    // Arrange & Act
    var entry = _createEntry() with { AuditReason = null };

    // Assert
    await Assert.That(entry.AuditReason is null).IsTrue();
  }

  [Test]
  public async Task AuditLogEntry_IsRecord_SupportsWithExpressionAsync() {
    // Arrange
    var original = _createEntry();

    // Act
    var modified = original with {
      TenantId = "new-tenant",
      UserId = "new-user"
    };

    // Assert
    await Assert.That(modified.TenantId).IsEqualTo("new-tenant");
    await Assert.That(modified.UserId).IsEqualTo("new-user");
    await Assert.That(original.TenantId).IsNotEqualTo("new-tenant"); // Original unchanged
  }

  [Test]
  public async Task AuditLogEntry_Equality_WorksForSameValuesAsync() {
    // Arrange
    var id = Guid.NewGuid();
    var timestamp = DateTimeOffset.UtcNow;
    var body = JsonSerializer.SerializeToElement(new { Test = true });

    var entry1 = new AuditLogEntry {
      Id = id,
      StreamId = "Order-123",
      StreamPosition = 1,
      EventType = "OrderCreated",
      Timestamp = timestamp,
      Body = body
    };

    var entry2 = new AuditLogEntry {
      Id = id,
      StreamId = "Order-123",
      StreamPosition = 1,
      EventType = "OrderCreated",
      Timestamp = timestamp,
      Body = body
    };

    // Assert
    await Assert.That(entry1).IsEqualTo(entry2);
  }

  // Helper to create a valid entry with required properties
  private static AuditLogEntry _createEntry() {
    return new AuditLogEntry {
      Id = Guid.NewGuid(),
      StreamId = "Test-Stream",
      StreamPosition = 0,
      EventType = "TestEvent",
      Timestamp = DateTimeOffset.UtcNow,
      Body = JsonSerializer.SerializeToElement(new { })
    };
  }
}
