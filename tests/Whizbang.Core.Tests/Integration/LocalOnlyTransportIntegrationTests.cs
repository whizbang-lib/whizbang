using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.Integration;

/// <summary>
/// Integration tests for LocalOnly transport filtering.
/// Verifies that system events respect the LocalOnly setting and don't
/// get published to transport when LocalOnly is true.
/// </summary>
[Category("Integration")]
public class LocalOnlyTransportIntegrationTests {
  // Test domain event (should always be published regardless of LocalOnly) - unique name
  public sealed record TransportTestOrderCreated([property: StreamKey] Guid OrderId, string CustomerId) : IEvent;

  // Test system event without Exclude attribute (should respect LocalOnly)
  public sealed record TransportTestRebuildStarted([property: StreamKey] Guid PerspectiveId, string PerspectiveName) : ISystemEvent;

  [Test]
  public async Task LocalOnly_WhenTrue_SystemEventsNotPublishedToTransport_Async() {
    // Arrange
    var services = _createServices(options => {
      options.EnableAudit();
      // LocalOnly = true is the default
    });
    var filter = services.GetRequiredService<ITransportPublishFilter>();
    var systemEvent = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "OrderCreated",
      OriginalStreamId = "stream-1",
      OriginalStreamPosition = 1,
      OriginalBody = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act
    var shouldPublish = filter.ShouldPublishToTransport(systemEvent);

    // Assert - LocalOnly means don't publish system events
    await Assert.That(shouldPublish).IsFalse();
  }

  [Test]
  public async Task LocalOnly_WhenFalse_SystemEventsPublishedToTransport_Async() {
    // Arrange
    var services = _createServices(options => {
      options.EnableAudit();
      options.Broadcast(); // Sets LocalOnly = false
    });
    var filter = services.GetRequiredService<ITransportPublishFilter>();
    var systemEvent = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "OrderCreated",
      OriginalStreamId = "stream-1",
      OriginalStreamPosition = 1,
      OriginalBody = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act
    var shouldPublish = filter.ShouldPublishToTransport(systemEvent);

    // Assert - Broadcast means publish system events
    await Assert.That(shouldPublish).IsTrue();
  }

  [Test]
  public async Task LocalOnly_DomainEventsAlwaysPublished_Async() {
    // Arrange - LocalOnly should NOT affect domain events
    var services = _createServices(options => {
      options.EnableAudit();
      // LocalOnly = true is the default
    });
    var filter = services.GetRequiredService<ITransportPublishFilter>();
    var domainEvent = new TransportTestOrderCreated(Guid.NewGuid(), "cust-123");

    // Act
    var shouldPublish = filter.ShouldPublishToTransport(domainEvent);

    // Assert - domain events always published
    await Assert.That(shouldPublish).IsTrue();
  }

  [Test]
  public async Task LocalOnly_CommandAuditedNotPublished_WhenLocalOnly_Async() {
    // Arrange
    var services = _createServices(options => {
      options.EnableCommandAudit();
      // LocalOnly = true is the default
    });
    var filter = services.GetRequiredService<ITransportPublishFilter>();
    var systemEvent = new CommandAudited {
      Id = Guid.NewGuid(),
      CommandType = "CreateOrder",
      CommandBody = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act
    var shouldPublish = filter.ShouldPublishToTransport(systemEvent);

    // Assert
    await Assert.That(shouldPublish).IsFalse();
  }

  [Test]
  public async Task LocalOnly_OtherSystemEventsRespectLocalOnly_Async() {
    // Arrange - Other system events (like TransportTestRebuildStarted) should also respect LocalOnly
    var services = _createServices(options => {
      options.EnablePerspectiveEvents();
      // LocalOnly = true
    });
    var filter = services.GetRequiredService<ITransportPublishFilter>();
    var systemEvent = new TransportTestRebuildStarted(Guid.NewGuid(), "OrderPerspective");

    // Act
    var shouldPublish = filter.ShouldPublishToTransport(systemEvent);

    // Assert - LocalOnly applies to all system events
    await Assert.That(shouldPublish).IsFalse();
  }

  [Test]
  public async Task LocalOnly_OtherSystemEventsPublished_WhenBroadcast_Async() {
    // Arrange
    var services = _createServices(options => {
      options.EnablePerspectiveEvents();
      options.Broadcast();
    });
    var filter = services.GetRequiredService<ITransportPublishFilter>();
    var systemEvent = new TransportTestRebuildStarted(Guid.NewGuid(), "OrderPerspective");

    // Act
    var shouldPublish = filter.ShouldPublishToTransport(systemEvent);

    // Assert
    await Assert.That(shouldPublish).IsTrue();
  }

  [Test]
  public async Task LocalOnly_ShouldReceiveFromTransport_ReturnsFalse_WhenLocalOnly_Async() {
    // Arrange - Also test the receive side
    var services = _createServices(options => {
      options.EnableAudit();
      // LocalOnly = true
    });
    var filter = services.GetRequiredService<ITransportPublishFilter>();

    // Act
    var shouldReceive = filter.ShouldReceiveFromTransport(typeof(EventAudited));

    // Assert - Don't receive system events when LocalOnly
    await Assert.That(shouldReceive).IsFalse();
  }

  [Test]
  public async Task LocalOnly_ShouldReceiveFromTransport_ReturnsTrue_WhenBroadcast_Async() {
    // Arrange
    var services = _createServices(options => {
      options.EnableAudit();
      options.Broadcast();
    });
    var filter = services.GetRequiredService<ITransportPublishFilter>();

    // Act
    var shouldReceive = filter.ShouldReceiveFromTransport(typeof(EventAudited));

    // Assert
    await Assert.That(shouldReceive).IsTrue();
  }

  [Test]
  public async Task LocalOnly_DomainEventsAlwaysReceived_Async() {
    // Arrange
    var services = _createServices(options => {
      options.EnableAudit();
      // LocalOnly = true
    });
    var filter = services.GetRequiredService<ITransportPublishFilter>();

    // Act
    var shouldReceive = filter.ShouldReceiveFromTransport(typeof(TransportTestOrderCreated));

    // Assert - domain events always received
    await Assert.That(shouldReceive).IsTrue();
  }

  [Test]
  public async Task LocalOnly_DefaultsToTrue_Async() {
    // Arrange
    var options = new SystemEventOptions();

    // Assert
    await Assert.That(options.LocalOnly).IsTrue();
  }

  [Test]
  public async Task Broadcast_SetsLocalOnlyFalse_Async() {
    // Arrange
    var options = new SystemEventOptions();

    // Act
    options.Broadcast();

    // Assert
    await Assert.That(options.LocalOnly).IsFalse();
  }

  [Test]
  public async Task LocalOnly_BffScenario_EachHostAuditsLocally_Async() {
    // Scenario: BFF and User service both have audit enabled with LocalOnly
    // Events flow: User service → BFF
    // Each service audits locally, no duplicate broadcast

    // Arrange - BFF configuration
    var bffServices = _createServices(options => {
      options.EnableAudit();
      // LocalOnly = true (default)
    });
    var bffFilter = bffServices.GetRequiredService<ITransportPublishFilter>();

    // Arrange - User service configuration
    var userServices = _createServices(options => {
      options.EnableAudit();
      // LocalOnly = true (default)
    });
    var userFilter = userServices.GetRequiredService<ITransportPublishFilter>();

    // Act - User service creates an audit event
    var userAuditEvent = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "UserCreated",
      OriginalStreamId = "user-123",
      OriginalStreamPosition = 1,
      OriginalBody = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };

    // Assert - User service won't broadcast its audit event
    await Assert.That(userFilter.ShouldPublishToTransport(userAuditEvent)).IsFalse();

    // Assert - BFF also won't broadcast its audit events
    var bffAuditEvent = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "UserCreated",
      OriginalStreamId = "user-123",
      OriginalStreamPosition = 1,
      OriginalBody = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };
    await Assert.That(bffFilter.ShouldPublishToTransport(bffAuditEvent)).IsFalse();

    // But domain events flow normally
    var domainEvent = new TransportTestOrderCreated(Guid.NewGuid(), "cust-1");
    await Assert.That(userFilter.ShouldPublishToTransport(domainEvent)).IsTrue();
    await Assert.That(bffFilter.ShouldPublishToTransport(domainEvent)).IsTrue();
  }

  [Test]
  public async Task Broadcast_CentralizedMonitoringScenario_Async() {
    // Scenario: Dedicated monitoring service collects all system events
    // Requires Broadcast() to enable cross-service system event transport

    // Arrange - Monitoring service with Broadcast enabled
    var monitoringServices = _createServices(options => {
      options.EnableAll();
      options.Broadcast(); // Enable cross-service transport
    });
    var filter = monitoringServices.GetRequiredService<ITransportPublishFilter>();

    // Act & Assert - All system events should be publishable and receivable
    var auditEvent = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "OrderCreated",
      OriginalStreamId = "order-1",
      OriginalStreamPosition = 1,
      OriginalBody = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };

    await Assert.That(filter.ShouldPublishToTransport(auditEvent)).IsTrue();
    await Assert.That(filter.ShouldReceiveFromTransport(typeof(EventAudited))).IsTrue();

    var perspectiveEvent = new TransportTestRebuildStarted(Guid.NewGuid(), "OrderPerspective");
    await Assert.That(filter.ShouldPublishToTransport(perspectiveEvent)).IsTrue();
    await Assert.That(filter.ShouldReceiveFromTransport(typeof(TransportTestRebuildStarted))).IsTrue();
  }

  // Helper methods

  private static ServiceProvider _createServices(Action<SystemEventOptions> configureOptions) {
    var services = new ServiceCollection();

    // Configure options
    var options = new SystemEventOptions();
    configureOptions(options);
    services.AddSingleton(Options.Create(options));

    // Add the transport filter (now from production code)
    services.AddSingleton<ITransportPublishFilter, SystemEventTransportFilter>();

    return services.BuildServiceProvider();
  }
}
