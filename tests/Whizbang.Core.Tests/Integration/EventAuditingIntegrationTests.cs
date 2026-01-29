using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Audit;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Integration;

/// <summary>
/// Integration tests for event auditing flow.
/// Verifies that domain events are audited correctly and system events
/// respect the Exclude attribute.
/// </summary>
[Category("Integration")]
public class EventAuditingIntegrationTests {
  // Test domain events (unique names to avoid generator conflicts)
  public sealed record AuditTestOrderCreated([property: StreamKey] Guid OrderId, string CustomerId, decimal Total) : IEvent;
  public sealed record AuditTestOrderShipped([property: StreamKey] Guid OrderId, DateTimeOffset ShippedAt) : IEvent;
  public sealed record AuditTestPaymentProcessed([property: StreamKey] Guid PaymentId, decimal Amount) : IEvent;

  // Test system event that SHOULD be audited (no Exclude attribute)
  public sealed record AuditTestSystemEvent([property: StreamKey] Guid Id, string Message) : ISystemEvent;

  [Test]
  public async Task EventAuditing_WhenEnabled_EmitsEventAuditedForDomainEvent_Async() {
    // Arrange
    var services = _createServices(options => options.EnableEventAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();
    var capturedEvents = services.GetRequiredService<CapturedSystemEvents>();

    var orderId = Guid.NewGuid();
    var envelope = _createEnvelope(new AuditTestOrderCreated(orderId, "cust-123", 99.99m));

    // Act
    await emitter.EmitEventAuditedAsync(
        streamId: orderId,
        streamPosition: 1,
        envelope: envelope,
        cancellationToken: default);

    // Assert
    await Assert.That(capturedEvents.Events.Count).IsEqualTo(1);

    var audited = capturedEvents.Events[0] as EventAudited;
    await Assert.That(audited).IsNotNull();
    await Assert.That(audited!.OriginalEventType).IsEqualTo("AuditTestOrderCreated");
    await Assert.That(audited.OriginalStreamId).IsEqualTo(orderId.ToString());
    await Assert.That(audited.OriginalStreamPosition).IsEqualTo(1);
  }

  [Test]
  public async Task EventAuditing_WhenDisabled_DoesNotEmitEventAudited_Async() {
    // Arrange - audit NOT enabled
    var services = _createServices(_ => { });
    var emitter = services.GetRequiredService<ISystemEventEmitter>();
    var capturedEvents = services.GetRequiredService<CapturedSystemEvents>();

    var envelope = _createEnvelope(new AuditTestOrderCreated(Guid.NewGuid(), "cust-123", 99.99m));

    // Act
    await emitter.EmitEventAuditedAsync(
        streamId: Guid.NewGuid(),
        streamPosition: 1,
        envelope: envelope,
        cancellationToken: default);

    // Assert - no events captured
    await Assert.That(capturedEvents.Events).IsEmpty();
  }

  [Test]
  public async Task EventAuditing_MultipleEvents_EmitsAuditForEach_Async() {
    // Arrange
    var services = _createServices(options => options.EnableEventAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();
    var capturedEvents = services.GetRequiredService<CapturedSystemEvents>();

    var orderId = Guid.NewGuid();

    // Act - emit 3 different events
    await emitter.EmitEventAuditedAsync(orderId, 1,
        _createEnvelope(new AuditTestOrderCreated(orderId, "cust-1", 100m)), default);
    await emitter.EmitEventAuditedAsync(orderId, 2,
        _createEnvelope(new AuditTestPaymentProcessed(Guid.NewGuid(), 100m)), default);
    await emitter.EmitEventAuditedAsync(orderId, 3,
        _createEnvelope(new AuditTestOrderShipped(orderId, DateTimeOffset.UtcNow)), default);

    // Assert - 3 audit events
    await Assert.That(capturedEvents.Events.Count).IsEqualTo(3);

    var eventTypes = capturedEvents.Events
        .Cast<EventAudited>()
        .Select(e => e.OriginalEventType)
        .ToList();

    await Assert.That(eventTypes).Contains("AuditTestOrderCreated");
    await Assert.That(eventTypes).Contains("AuditTestPaymentProcessed");
    await Assert.That(eventTypes).Contains("AuditTestOrderShipped");
  }

  [Test]
  public async Task EventAuditing_CapturesScopeFromEnvelope_Async() {
    // Arrange
    var services = _createServices(options => options.EnableEventAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();
    var capturedEvents = services.GetRequiredService<CapturedSystemEvents>();

    var testCorrelationId = Guid.NewGuid();
    var envelope = _createEnvelope(
        new AuditTestOrderCreated(Guid.NewGuid(), "cust-123", 99.99m),
        tenantId: "tenant-abc",
        userId: "user-456",
        correlationId: testCorrelationId);

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, envelope, default);

    // Assert
    var audited = capturedEvents.Events[0] as EventAudited;
    await Assert.That(audited).IsNotNull();
    await Assert.That(audited!.TenantId).IsEqualTo("tenant-abc");
    await Assert.That(audited.UserId).IsEqualTo("user-456");
    await Assert.That(audited.CorrelationId).IsEqualTo(testCorrelationId.ToString());
  }

  [Test]
  public async Task EventAuditing_EventAuditedEvent_IsNotReAudited_Async() {
    // Arrange - This is critical for preventing infinite loops
    var services = _createServices(options => options.EnableEventAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();
    var capturedEvents = services.GetRequiredService<CapturedSystemEvents>();

    // Create an EventAudited event (has [AuditEvent(Exclude = true)])
    var auditEvent = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "SomeEvent",
      OriginalStreamId = "stream-1",
      OriginalStreamPosition = 1,
      OriginalBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };

    var envelope = _createEnvelope(auditEvent);

    // Act - try to audit an EventAudited
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, envelope, default);

    // Assert - should NOT create another audit event
    await Assert.That(capturedEvents.Events).IsEmpty();
  }

  [Test]
  public async Task EventAuditing_CommandAuditedEvent_IsNotReAudited_Async() {
    // Arrange
    var services = _createServices(options => options.EnableEventAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();
    var capturedEvents = services.GetRequiredService<CapturedSystemEvents>();

    // Create a CommandAudited event (has [AuditEvent(Exclude = true)])
    var auditEvent = new CommandAudited {
      Id = Guid.NewGuid(),
      CommandType = "SomeCommand",
      CommandBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };

    var envelope = _createEnvelope(auditEvent);

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, envelope, default);

    // Assert - should NOT create another audit event
    await Assert.That(capturedEvents.Events).IsEmpty();
  }

  [Test]
  public async Task EventAuditing_OtherSystemEvents_AreAudited_Async() {
    // Arrange - Other system events (without Exclude=true) SHOULD be audited
    var services = _createServices(options => options.EnableEventAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();
    var capturedEvents = services.GetRequiredService<CapturedSystemEvents>();

    // AuditTestSystemEvent is a system event but does NOT have [AuditEvent(Exclude = true)]
    var systemEvent = new AuditTestSystemEvent(Guid.NewGuid(), "Test message");
    var envelope = _createEnvelope(systemEvent);

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, envelope, default);

    // Assert - SHOULD be audited
    await Assert.That(capturedEvents.Events.Count).IsEqualTo(1);
    var audited = capturedEvents.Events[0] as EventAudited;
    await Assert.That(audited!.OriginalEventType).IsEqualTo("AuditTestSystemEvent");
  }

  [Test]
  public async Task EventAuditing_SerializesOriginalEventBody_Async() {
    // Arrange
    var services = _createServices(options => options.EnableEventAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();
    var capturedEvents = services.GetRequiredService<CapturedSystemEvents>();

    var orderId = Guid.NewGuid();
    var originalEvent = new AuditTestOrderCreated(orderId, "cust-123", 99.99m);
    var envelope = _createEnvelope(originalEvent);

    // Act
    await emitter.EmitEventAuditedAsync(orderId, 1, envelope, default);

    // Assert - verify original body is serialized
    var audited = capturedEvents.Events[0] as EventAudited;
    await Assert.That(audited).IsNotNull();

    // Deserialize and verify
    var bodyJson = audited!.OriginalBody.GetRawText();
    await Assert.That(bodyJson).Contains(orderId.ToString());
    await Assert.That(bodyJson).Contains("cust-123");
    await Assert.That(bodyJson).Contains("99.99");
  }

  [Test]
  public async Task ShouldExcludeFromAudit_EventAudited_ReturnsTrue_Async() {
    // Arrange
    var services = _createServices(options => options.EnableEventAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();

    // Act & Assert
    await Assert.That(emitter.ShouldExcludeFromAudit(typeof(EventAudited))).IsTrue();
  }

  [Test]
  public async Task ShouldExcludeFromAudit_CommandAudited_ReturnsTrue_Async() {
    // Arrange
    var services = _createServices(options => options.EnableEventAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();

    // Act & Assert
    await Assert.That(emitter.ShouldExcludeFromAudit(typeof(CommandAudited))).IsTrue();
  }

  [Test]
  public async Task ShouldExcludeFromAudit_DomainEvent_ReturnsFalse_Async() {
    // Arrange
    var services = _createServices(options => options.EnableEventAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();

    // Act & Assert
    await Assert.That(emitter.ShouldExcludeFromAudit(typeof(AuditTestOrderCreated))).IsFalse();
  }

  [Test]
  public async Task ShouldExcludeFromAudit_SystemEventWithoutExclude_ReturnsFalse_Async() {
    // Arrange
    var services = _createServices(options => options.EnableEventAudit());
    var emitter = services.GetRequiredService<ISystemEventEmitter>();

    // Act & Assert - TestSystemEvent is ISystemEvent but no [AuditEvent(Exclude=true)]
    await Assert.That(emitter.ShouldExcludeFromAudit(typeof(AuditTestSystemEvent))).IsFalse();
  }

  // Helper methods

  private static ServiceProvider _createServices(Action<SystemEventOptions> configureOptions) {
    var services = new ServiceCollection();

    // Configure options
    var options = new SystemEventOptions();
    configureOptions(options);
    services.AddSingleton(Options.Create(options));

    // Add captured events for testing
    services.AddSingleton<CapturedSystemEvents>();

    // Add the system event emitter
    services.AddSingleton<ISystemEventEmitter, TestableSystemEventEmitter>();

    return services.BuildServiceProvider();
  }

  private static MessageEnvelope<TMessage> _createEnvelope<TMessage>(
      TMessage payload,
      string? tenantId = null,
      string? userId = null,
      Guid? correlationId = null) where TMessage : notnull {
    // Create minimal hop with required ServiceInstance
    var hop = new MessageHop {
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      SecurityContext = (tenantId != null || userId != null)
          ? new SecurityContext {
            TenantId = tenantId,
            UserId = userId
          }
          : null,
      CorrelationId = correlationId.HasValue ? new CorrelationId(correlationId.Value) : null
    };

    return new MessageEnvelope<TMessage> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = [hop]
    };
  }
}

/// <summary>
/// Captures system events for test verification.
/// </summary>
public class CapturedSystemEvents {
  public List<ISystemEvent> Events { get; } = [];
}

/// <summary>
/// Testable implementation of ISystemEventEmitter that captures events.
/// </summary>
public class TestableSystemEventEmitter : ISystemEventEmitter {
  private readonly CapturedSystemEvents _captured;
  private readonly SystemEventOptions _options;

  public TestableSystemEventEmitter(
      CapturedSystemEvents captured,
      IOptions<SystemEventOptions> options) {
    _captured = captured;
    _options = options.Value;
  }

  public Task EmitEventAuditedAsync<TEvent>(
      Guid streamId,
      long streamPosition,
      MessageEnvelope<TEvent> envelope,
      CancellationToken cancellationToken = default) {
    // Check if audit is enabled
    if (!_options.EventAuditEnabled) {
      return Task.CompletedTask;
    }

    // Check if this type should be excluded
    if (ShouldExcludeFromAudit(typeof(TEvent))) {
      return Task.CompletedTask;
    }

    // Extract scope from envelope - use SecurityContext from hops
    var securityContext = envelope.GetCurrentSecurityContext();
    var correlationId = envelope.GetCorrelationId();

    // Create EventAudited
    var audited = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = typeof(TEvent).Name,
      OriginalStreamId = streamId.ToString(),
      OriginalStreamPosition = streamPosition,
      OriginalBody = JsonSerializer.SerializeToElement(envelope.Payload),
      Timestamp = DateTimeOffset.UtcNow,
      TenantId = securityContext?.TenantId,
      UserId = securityContext?.UserId,
      CorrelationId = correlationId?.ToString()
    };

    _captured.Events.Add(audited);
    return Task.CompletedTask;
  }

  public Task EmitCommandAuditedAsync<TCommand, TResponse>(
      TCommand command,
      TResponse response,
      string receptorName,
      IMessageContext? context,
      CancellationToken cancellationToken = default) where TCommand : notnull {
    // Check if command audit is enabled
    if (!_options.CommandAuditEnabled) {
      return Task.CompletedTask;
    }

    // Check if this type should be excluded
    if (ShouldExcludeFromAudit(typeof(TCommand))) {
      return Task.CompletedTask;
    }

    var audited = new CommandAudited {
      Id = Guid.NewGuid(),
      CommandType = typeof(TCommand).Name,
      CommandBody = JsonSerializer.SerializeToElement(command),
      Timestamp = DateTimeOffset.UtcNow,
      ReceptorName = receptorName,
      ResponseType = typeof(TResponse).Name
    };

    _captured.Events.Add(audited);
    return Task.CompletedTask;
  }

  public Task EmitAsync<TSystemEvent>(
      TSystemEvent systemEvent,
      CancellationToken cancellationToken = default) where TSystemEvent : ISystemEvent {
    _captured.Events.Add(systemEvent);
    return Task.CompletedTask;
  }

  public bool ShouldExcludeFromAudit(Type type) {
    // Check for [AuditEvent(Exclude = true)] attribute
    var attribute = type
        .GetCustomAttributes(typeof(AuditEventAttribute), inherit: true)
        .FirstOrDefault() as AuditEventAttribute;

    return attribute?.Exclude == true;
  }
}
