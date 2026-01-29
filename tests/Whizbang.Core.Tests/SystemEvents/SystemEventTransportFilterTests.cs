using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Tests for SystemEventTransportFilter.
/// The filter controls whether system events are published to/received from transport.
/// </summary>
/// <tests>Whizbang.Core/SystemEvents/SystemEventTransportFilter.cs</tests>
[Category("SystemEvents")]
[Category("Transport")]
public class SystemEventTransportFilterTests {
  #region Constructor Tests

  [Test]
  public async Task Constructor_WithNullOptions_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    await Assert.That(() => new SystemEventTransportFilter(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithValidOptions_DoesNotThrowAsync() {
    // Arrange
    var options = Options.Create(new SystemEventOptions());

    // Act & Assert
    await Assert.That(() => new SystemEventTransportFilter(options))
      .ThrowsNothing();
  }

  #endregion

  #region ShouldPublishToTransport Tests

  [Test]
  public async Task ShouldPublishToTransport_DomainEvent_ReturnsTrue_WhenLocalOnlyAsync() {
    // Arrange
    var options = Options.Create(new SystemEventOptions()); // LocalOnly = true by default
    var filter = new SystemEventTransportFilter(options);
    var domainEvent = new TestDomainEvent { OrderId = Guid.NewGuid() };

    // Act
    var result = filter.ShouldPublishToTransport(domainEvent);

    // Assert - Domain events always publish regardless of LocalOnly
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ShouldPublishToTransport_DomainEvent_ReturnsTrue_WhenBroadcastEnabledAsync() {
    // Arrange
    var options = Options.Create(new SystemEventOptions().Broadcast()); // LocalOnly = false
    var filter = new SystemEventTransportFilter(options);
    var domainEvent = new TestDomainEvent { OrderId = Guid.NewGuid() };

    // Act
    var result = filter.ShouldPublishToTransport(domainEvent);

    // Assert - Domain events always publish
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ShouldPublishToTransport_SystemEvent_ReturnsFalse_WhenLocalOnlyAsync() {
    // Arrange
    var options = Options.Create(new SystemEventOptions()); // LocalOnly = true by default
    var filter = new SystemEventTransportFilter(options);
    var systemEvent = new TestSystemEvent { Name = "Test" };

    // Act
    var result = filter.ShouldPublishToTransport(systemEvent);

    // Assert - System events respect LocalOnly setting
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task ShouldPublishToTransport_SystemEvent_ReturnsTrue_WhenBroadcastEnabledAsync() {
    // Arrange
    var options = Options.Create(new SystemEventOptions().Broadcast()); // LocalOnly = false
    var filter = new SystemEventTransportFilter(options);
    var systemEvent = new TestSystemEvent { Name = "Test" };

    // Act
    var result = filter.ShouldPublishToTransport(systemEvent);

    // Assert - System events publish when Broadcast() is called
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ShouldPublishToTransport_EventAudited_ReturnsFalse_WhenLocalOnlyAsync() {
    // Arrange
    var options = Options.Create(new SystemEventOptions().EnableAudit()); // LocalOnly = true
    var filter = new SystemEventTransportFilter(options);
    var auditEvent = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "TestEvent",
      OriginalStreamId = "stream-1",
      OriginalStreamPosition = 1,
      OriginalBody = default,
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act
    var result = filter.ShouldPublishToTransport(auditEvent);

    // Assert - Audit events don't publish when LocalOnly
    await Assert.That(result).IsFalse();
  }

  #endregion

  #region ShouldReceiveFromTransport Tests

  [Test]
  public async Task ShouldReceiveFromTransport_NullMessageType_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var options = Options.Create(new SystemEventOptions());
    var filter = new SystemEventTransportFilter(options);

    // Act & Assert
    await Assert.That(() => filter.ShouldReceiveFromTransport(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task ShouldReceiveFromTransport_DomainEventType_ReturnsTrue_WhenLocalOnlyAsync() {
    // Arrange
    var options = Options.Create(new SystemEventOptions()); // LocalOnly = true
    var filter = new SystemEventTransportFilter(options);

    // Act
    var result = filter.ShouldReceiveFromTransport(typeof(TestDomainEvent));

    // Assert - Domain events always received regardless of LocalOnly
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ShouldReceiveFromTransport_DomainEventType_ReturnsTrue_WhenBroadcastEnabledAsync() {
    // Arrange
    var options = Options.Create(new SystemEventOptions().Broadcast()); // LocalOnly = false
    var filter = new SystemEventTransportFilter(options);

    // Act
    var result = filter.ShouldReceiveFromTransport(typeof(TestDomainEvent));

    // Assert - Domain events always received
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ShouldReceiveFromTransport_SystemEventType_ReturnsFalse_WhenLocalOnlyAsync() {
    // Arrange
    var options = Options.Create(new SystemEventOptions()); // LocalOnly = true
    var filter = new SystemEventTransportFilter(options);

    // Act
    var result = filter.ShouldReceiveFromTransport(typeof(TestSystemEvent));

    // Assert - System events respect LocalOnly setting
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task ShouldReceiveFromTransport_SystemEventType_ReturnsTrue_WhenBroadcastEnabledAsync() {
    // Arrange
    var options = Options.Create(new SystemEventOptions().Broadcast()); // LocalOnly = false
    var filter = new SystemEventTransportFilter(options);

    // Act
    var result = filter.ShouldReceiveFromTransport(typeof(TestSystemEvent));

    // Assert - System events received when Broadcast() is called
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ShouldReceiveFromTransport_EventAuditedType_ReturnsFalse_WhenLocalOnlyAsync() {
    // Arrange
    var options = Options.Create(new SystemEventOptions().EnableAudit()); // LocalOnly = true
    var filter = new SystemEventTransportFilter(options);

    // Act
    var result = filter.ShouldReceiveFromTransport(typeof(EventAudited));

    // Assert - Audit event types don't receive when LocalOnly
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task ShouldReceiveFromTransport_CommandAuditedType_ReturnsFalse_WhenLocalOnlyAsync() {
    // Arrange
    var options = Options.Create(new SystemEventOptions().EnableAudit()); // LocalOnly = true
    var filter = new SystemEventTransportFilter(options);

    // Act
    var result = filter.ShouldReceiveFromTransport(typeof(CommandAudited));

    // Assert - Audit event types don't receive when LocalOnly
    await Assert.That(result).IsFalse();
  }

  #endregion

  #region Test Types

  private sealed record TestDomainEvent {
    public required Guid OrderId { get; init; }
  }

  private sealed record TestSystemEvent : ISystemEvent {
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
  }

  #endregion
}
