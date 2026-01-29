using System.Text.Json;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Audit;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Tests for EventAudited system event.
/// EventAudited captures metadata about domain events for audit trail purposes.
/// </summary>
public class EventAuditedTests {
  [Test]
  public async Task EventAudited_ImplementsISystemEvent_ForSystemStreamRoutingAsync() {
    // Arrange & Act
    var isSystemEvent = typeof(EventAudited).IsAssignableTo(typeof(ISystemEvent));

    // Assert
    await Assert.That(isSystemEvent).IsTrue();
  }

  [Test]
  public async Task EventAudited_CapturesOriginalEventType_ForAuditTrailAsync() {
    // Arrange
    var audited = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "OrderCreated",
      OriginalStreamId = "Order-abc123",
      OriginalStreamPosition = 1,
      OriginalBody = JsonSerializer.SerializeToElement(new { OrderId = "abc123" }),
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert
    await Assert.That(audited.OriginalEventType).IsEqualTo("OrderCreated");
  }

  [Test]
  public async Task EventAudited_CapturesStreamInfo_ForEventLocationAsync() {
    // Arrange
    var streamId = "Order-abc123";
    var position = 42L;

    var audited = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "OrderShipped",
      OriginalStreamId = streamId,
      OriginalStreamPosition = position,
      OriginalBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert
    await Assert.That(audited.OriginalStreamId).IsEqualTo(streamId);
    await Assert.That(audited.OriginalStreamPosition).IsEqualTo(position);
  }

  [Test]
  public async Task EventAudited_CapturesOriginalBody_ForFullEventDataAsync() {
    // Arrange
    var originalEvent = new { OrderId = "abc123", Amount = 99.99m, Currency = "USD" };
    var body = JsonSerializer.SerializeToElement(originalEvent);

    var audited = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "PaymentProcessed",
      OriginalStreamId = "Payment-xyz",
      OriginalStreamPosition = 1,
      OriginalBody = body,
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert
    await Assert.That(audited.OriginalBody.GetProperty("OrderId").GetString()).IsEqualTo("abc123");
    await Assert.That(audited.OriginalBody.GetProperty("Amount").GetDecimal()).IsEqualTo(99.99m);
  }

  [Test]
  public async Task EventAudited_CapturesScopeInfo_ForComplianceQueriesAsync() {
    // Arrange
    var audited = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "CustomerDataViewed",
      OriginalStreamId = "Customer-123",
      OriginalStreamPosition = 5,
      OriginalBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow,
      TenantId = "tenant-acme",
      UserId = "user-john",
      UserName = "John Doe",
      CorrelationId = "corr-abc",
      CausationId = "cause-xyz"
    };

    // Assert
    await Assert.That(audited.TenantId).IsEqualTo("tenant-acme");
    await Assert.That(audited.UserId).IsEqualTo("user-john");
    await Assert.That(audited.UserName).IsEqualTo("John Doe");
    await Assert.That(audited.CorrelationId).IsEqualTo("corr-abc");
    await Assert.That(audited.CausationId).IsEqualTo("cause-xyz");
  }

  [Test]
  public async Task EventAudited_CapturesAuditReason_WhenAttributePresentAsync() {
    // Arrange - Event had [AuditEvent(Reason = "PII access")]
    var audited = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "CustomerDataViewed",
      OriginalStreamId = "Customer-123",
      OriginalStreamPosition = 1,
      OriginalBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow,
      AuditReason = "PII access",
      AuditLevel = AuditLevel.Warning
    };

    // Assert
    await Assert.That(audited.AuditReason).IsEqualTo("PII access");
    await Assert.That(audited.AuditLevel).IsEqualTo(AuditLevel.Warning);
  }

  [Test]
  public async Task EventAudited_HasUniqueId_ForDeduplicationAsync() {
    // Arrange
    var id1 = Guid.NewGuid();
    var id2 = Guid.NewGuid();

    var audited1 = new EventAudited {
      Id = id1,
      OriginalEventType = "Test",
      OriginalStreamId = "Test-1",
      OriginalStreamPosition = 1,
      OriginalBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };

    var audited2 = new EventAudited {
      Id = id2,
      OriginalEventType = "Test",
      OriginalStreamId = "Test-1",
      OriginalStreamPosition = 1,
      OriginalBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert
    await Assert.That(audited1.Id).IsNotEqualTo(audited2.Id);
  }
}
