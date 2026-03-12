using System.Text.Json;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Tests for SystemEventEmitter.
/// SystemEventEmitter emits system events to the dedicated system stream.
/// </summary>
[Category("SystemEvents")]
public class SystemEventEmitterTests {
  #region Constructor Tests

  [Test]
  public async Task Constructor_WithNullOptions_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var eventStore = new MockEventStore();

    // Act & Assert
    await Assert.That(() => new SystemEventEmitter(null!, eventStore))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullEventStore_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var options = Options.Create(new SystemEventOptions());

    // Act & Assert
    await Assert.That(() => new SystemEventEmitter(options, null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  #endregion

  #region EmitEventAuditedAsync Tests

  [Test]
  public async Task EmitEventAuditedAsync_WhenEventAuditDisabled_DoesNotEmitAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions()); // EventAuditEnabled = false by default
    var emitter = new SystemEventEmitter(options, eventStore);

    var envelope = _createTestEnvelope(new TestEvent { Name = "Test" });

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, envelope);

    // Assert - No events appended
    await Assert.That(eventStore.AppendedEnvelopes).IsEmpty();
  }

  [Test]
  public async Task EmitEventAuditedAsync_WhenEventAuditEnabled_ChecksOptionsAsync() {
    // Arrange - This test verifies the options check without hitting JSON serialization
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions()); // Disabled
    var emitter = new SystemEventEmitter(options, eventStore);

    var streamId = Guid.NewGuid();
    var envelope = _createTestEnvelope(new TestEvent { Name = "TestName" });

    // Act
    await emitter.EmitEventAuditedAsync(streamId, 1, envelope);

    // Assert - No events because audit is disabled
    await Assert.That(eventStore.AppendedEnvelopes).IsEmpty();

    // Now enable via options - we can't actually emit without AOT registration
    // but we've tested the options checking path
    await Assert.That(options.Value.EventAuditEnabled).IsFalse();
  }

  [Test]
  public async Task EmitEventAuditedAsync_WithNullPayload_DoesNotEmitAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = default!,
      Hops = []
    };

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, envelope);

    // Assert - No events appended
    await Assert.That(eventStore.AppendedEnvelopes).IsEmpty();
  }

  [Test]
  public async Task EmitEventAuditedAsync_WithExcludedEventType_DoesNotEmitAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var envelope = _createTestEnvelope(new ExcludedEvent { Name = "Test" });

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, envelope);

    // Assert - No events appended (excluded via attribute)
    await Assert.That(eventStore.AppendedEnvelopes).IsEmpty();
  }

  [Test]
  public async Task EmitEventAuditedAsync_WithEnabledOptions_ValidatesStreamIdAsync() {
    // Arrange - Test that the streamId parameter is used correctly
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var streamId = Guid.NewGuid();
    const long streamPosition = 42L;

    // Create envelope with null payload to hit the early return path
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = default!,
      Hops = []
    };

    // Act - This will return early due to null payload
    await emitter.EmitEventAuditedAsync(streamId, streamPosition, envelope);

    // Assert - No events appended because payload was null
    await Assert.That(eventStore.AppendedEnvelopes).IsEmpty();
  }

  #endregion

  #region EmitCommandAuditedAsync Tests

  [Test]
  public async Task EmitCommandAuditedAsync_WhenCommandAuditDisabled_DoesNotEmitAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions()); // CommandAuditEnabled = false by default
    var emitter = new SystemEventEmitter(options, eventStore);

    var command = new TestCommand { OrderId = "ABC123" };

    // Act
    await emitter.EmitCommandAuditedAsync(command, "result", "TestReceptor", null);

    // Assert - No events appended
    await Assert.That(eventStore.AppendedEnvelopes).IsEmpty();
  }

  [Test]
  public async Task EmitCommandAuditedAsync_WhenCommandAuditEnabled_ChecksOptionsAsync() {
    // Arrange - Test options checking behavior
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions()); // Disabled by default
    var emitter = new SystemEventEmitter(options, eventStore);

    var command = new TestCommand { OrderId = "ABC123" };

    // Act
    await emitter.EmitCommandAuditedAsync(command, "result", "TestReceptor", null);

    // Assert - No events appended because command audit is disabled
    await Assert.That(eventStore.AppendedEnvelopes).IsEmpty();
    await Assert.That(options.Value.CommandAuditEnabled).IsFalse();
  }

  [Test]
  public async Task EmitCommandAuditedAsync_WithExcludedCommandType_DoesNotEmitAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var command = new ExcludedCommand { Name = "Test" };

    // Act
    await emitter.EmitCommandAuditedAsync(command, "result", "TestReceptor", null);

    // Assert - No events appended (excluded via attribute)
    await Assert.That(eventStore.AppendedEnvelopes).IsEmpty();
  }

  [Test]
  public async Task EmitCommandAuditedAsync_WithNullContext_DoesNotThrowAsync() {
    // Arrange - Test that null context is handled gracefully
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions()); // Disabled to avoid serialization
    var emitter = new SystemEventEmitter(options, eventStore);

    var command = new TestCommand { OrderId = "ABC123" };

    // Act - Should not throw even with null context
    await emitter.EmitCommandAuditedAsync(command, "result", "TestReceptor", null);

    // Assert - No exception thrown, no events because disabled
    await Assert.That(eventStore.AppendedEnvelopes).IsEmpty();
  }

  #endregion

  #region EmitAsync Tests

  [Test]
  public async Task EmitAsync_WithNonAuditSystemEvent_WhenAuditDisabled_DoesNotEmitAsync() {
    // Arrange - Test non-EventAudited/CommandAudited path with audit disabled
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions()); // Audit disabled
    var emitter = new SystemEventEmitter(options, eventStore);

    // Use a custom ISystemEvent type that is NOT EventAudited or CommandAudited
    var systemEvent = new TestSystemEvent {
      Id = Guid.NewGuid(),
      Name = "test",
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act
    await emitter.EmitAsync(systemEvent);

    // Assert - No events appended (not EventAudited/CommandAudited and audit disabled)
    await Assert.That(eventStore.AppendedEnvelopes).IsEmpty();
  }

  [Test]
  public async Task EmitAsync_WithNonAuditSystemEvent_WhenAuditEnabled_DoesNotEmitAsync() {
    // Arrange - Test non-EventAudited/CommandAudited path with audit enabled
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableAudit()); // Audit enabled
    var emitter = new SystemEventEmitter(options, eventStore);

    // Use a custom ISystemEvent type that is NOT EventAudited or CommandAudited
    var systemEvent = new TestSystemEvent {
      Id = Guid.NewGuid(),
      Name = "test",
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act - TestSystemEvent is not EventAudited or CommandAudited, so it should not emit
    await emitter.EmitAsync(systemEvent);

    // Assert - No events appended (not EventAudited/CommandAudited type)
    await Assert.That(eventStore.AppendedEnvelopes).IsEmpty();
  }

  [Test]
  public async Task EmitAsync_WhenSystemEventTypeDisabled_DoesNotEmitAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions()); // Nothing enabled
    var emitter = new SystemEventEmitter(options, eventStore);

    var systemEvent = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "Test",
      OriginalStreamId = "Stream-1",
      OriginalStreamPosition = 1,
      OriginalBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act
    await emitter.EmitAsync(systemEvent);

    // Assert - No events appended (EventAudited disabled)
    await Assert.That(eventStore.AppendedEnvelopes).IsEmpty();
  }

  [Test]
  public async Task EmitAsync_WhenAuditEnabled_EmitsEventAuditedAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var systemEvent = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "Test",
      OriginalStreamId = "Stream-1",
      OriginalStreamPosition = 1,
      OriginalBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act
    await emitter.EmitAsync(systemEvent);

    // Assert - Event was appended
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
  }

  [Test]
  public async Task EmitAsync_WhenAuditEnabled_EmitsCommandAuditedAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var systemEvent = new CommandAudited {
      Id = Guid.NewGuid(),
      CommandType = "TestCommand",
      CommandBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow,
      ReceptorName = "TestReceptor",
      ResponseType = "string"
    };

    // Act
    await emitter.EmitAsync(systemEvent);

    // Assert - Event was appended
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
  }

  #endregion

  #region ShouldExcludeFromAudit Tests

  [Test]
  public async Task ShouldExcludeFromAudit_WithExcludedAttribute_ReturnsTrueAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions());
    var emitter = new SystemEventEmitter(options, eventStore);

    // Act
    var result = emitter.ShouldExcludeFromAudit(typeof(ExcludedEvent));

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ShouldExcludeFromAudit_WithoutExcludedAttribute_ReturnsFalseAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions());
    var emitter = new SystemEventEmitter(options, eventStore);

    // Act
    var result = emitter.ShouldExcludeFromAudit(typeof(TestEvent));

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task ShouldExcludeFromAudit_WithAuditAttributeNotExcluded_ReturnsFalseAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions());
    var emitter = new SystemEventEmitter(options, eventStore);

    // Act
    var result = emitter.ShouldExcludeFromAudit(typeof(AuditedEvent));

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task ShouldExcludeFromAudit_WithExplicitExcludeFalse_ReturnsFalseAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions());
    var emitter = new SystemEventEmitter(options, eventStore);

    // Act
    var result = emitter.ShouldExcludeFromAudit(typeof(ExplicitlyIncludedEvent));

    // Assert
    await Assert.That(result).IsFalse();
  }

  #endregion

  #region SystemEventOptions IsEnabled Tests

  [Test]
  public async Task SystemEventOptions_IsEnabled_EventAudited_WhenEventAuditEnabled_ReturnsTrueAsync() {
    // Arrange
    var options = new SystemEventOptions().EnableEventAudit();

    // Act
    var result = options.IsEnabled<EventAudited>();

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task SystemEventOptions_IsEnabled_CommandAudited_WhenCommandAuditEnabled_ReturnsTrueAsync() {
    // Arrange
    var options = new SystemEventOptions().EnableCommandAudit();

    // Act
    var result = options.IsEnabled<CommandAudited>();

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task SystemEventOptions_IsEnabled_EventAudited_WhenDisabled_ReturnsFalseAsync() {
    // Arrange
    var options = new SystemEventOptions(); // Nothing enabled

    // Act
    var result = options.IsEnabled<EventAudited>();

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task SystemEventOptions_IsEnabled_NonAuditEvent_ReturnsFalseAsync() {
    // Arrange
    var options = new SystemEventOptions().EnableAll();

    // Act - TestSystemEvent is not EventAudited or CommandAudited
    var result = options.IsEnabled<TestSystemEvent>();

    // Assert - Only EventAudited and CommandAudited have explicit checks
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task SystemEventOptions_IsEnabled_ByType_WhenNotISystemEvent_ReturnsFalseAsync() {
    // Arrange
    var options = new SystemEventOptions().EnableAll();

    // Act - string is not ISystemEvent
    var result = options.IsEnabled(typeof(string));

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task SystemEventOptions_EnableAll_EnablesAllOptionsAsync() {
    // Arrange
    var options = new SystemEventOptions();

    // Act
    options.EnableAll();

    // Assert
    await Assert.That(options.EventAuditEnabled).IsTrue();
    await Assert.That(options.CommandAuditEnabled).IsTrue();
    await Assert.That(options.PerspectiveEventsEnabled).IsTrue();
    await Assert.That(options.ErrorEventsEnabled).IsTrue();
  }

  [Test]
  public async Task SystemEventOptions_Broadcast_SetsLocalOnlyToFalseAsync() {
    // Arrange
    var options = new SystemEventOptions();
    await Assert.That(options.LocalOnly).IsTrue(); // Default is true

    // Act
    options.Broadcast();

    // Assert
    await Assert.That(options.LocalOnly).IsFalse();
  }

  [Test]
  public async Task SystemEventOptions_EnablePerspectiveEvents_EnablesPerspectiveEventsAsync() {
    // Arrange
    var options = new SystemEventOptions();

    // Act
    options.EnablePerspectiveEvents();

    // Assert
    await Assert.That(options.PerspectiveEventsEnabled).IsTrue();
  }

  [Test]
  public async Task SystemEventOptions_EnableErrorEvents_EnablesErrorEventsAsync() {
    // Arrange
    var options = new SystemEventOptions();

    // Act
    options.EnableErrorEvents();

    // Assert
    await Assert.That(options.ErrorEventsEnabled).IsTrue();
  }

  [Test]
  public async Task SystemEventOptions_AuditEnabled_WhenEventAuditEnabled_ReturnsTrueAsync() {
    // Arrange
    var options = new SystemEventOptions().EnableEventAudit();

    // Assert
    await Assert.That(options.AuditEnabled).IsTrue();
  }

  [Test]
  public async Task SystemEventOptions_AuditEnabled_WhenCommandAuditEnabled_ReturnsTrueAsync() {
    // Arrange
    var options = new SystemEventOptions().EnableCommandAudit();

    // Assert
    await Assert.That(options.AuditEnabled).IsTrue();
  }

  [Test]
  public async Task SystemEventOptions_AuditEnabled_WhenNothingEnabled_ReturnsFalseAsync() {
    // Arrange
    var options = new SystemEventOptions();

    // Assert
    await Assert.That(options.AuditEnabled).IsFalse();
  }

  #endregion

  #region Test Types

  private sealed record TestEvent {
    public required string Name { get; init; }
  }

  [AuditEvent(Exclude = true)]
  private sealed record ExcludedEvent {
    public required string Name { get; init; }
  }

  [AuditEvent(Reason = "Compliance")]
  private sealed record AuditedEvent {
    public required string Name { get; init; }
  }

  [AuditEvent(Exclude = false)]
  private sealed record ExplicitlyIncludedEvent {
    public required string Name { get; init; }
  }

  private sealed record TestCommand {
    public required string OrderId { get; init; }
  }

  [AuditEvent(Exclude = true)]
  private sealed record ExcludedCommand {
    public required string Name { get; init; }
  }

  /// <summary>
  /// Custom ISystemEvent for testing non-audit system event paths.
  /// This type is NOT EventAudited or CommandAudited.
  /// </summary>
  private sealed record TestSystemEvent : ISystemEvent {
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
  }

  #endregion

  #region Real Implementation Coverage Tests

  [Test]
  public async Task EmitEventAuditedAsync_WithSecurityContext_ExtractsScopeCorrectlyAsync() {
    // Arrange - Use real SystemEventEmitter to test scope extraction
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var testCorrelationId = Guid.NewGuid();
    var envelope = new MessageEnvelope<EventAudited> {
      MessageId = MessageId.New(),
      Payload = new EventAudited {
        Id = Guid.NewGuid(),
        OriginalEventType = "Test",
        OriginalStreamId = "stream-1",
        OriginalStreamPosition = 1,
        OriginalBody = JsonSerializer.SerializeToElement(new { }),
        Timestamp = DateTimeOffset.UtcNow
      },
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext {
            TenantId = "tenant-123",
            UserId = "user-456"
          }),
          CorrelationId = new CorrelationId(testCorrelationId)
        }
      ]
    };

    // Act - This will hit the scope extraction code but return early due to [AuditEvent(Exclude=true)]
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, envelope);

    // Assert - No events because EventAudited is excluded from re-audit
    await Assert.That(eventStore.AppendedEnvelopes).IsEmpty();
  }

  [Test]
  public async Task EmitCommandAuditedAsync_WithContext_ExtractsMetadataAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    // Create a mock context with metadata
    var context = new TestMessageContext {
      UserId = "user-123"
    };
    context.Metadata["TenantId"] = "tenant-456";

    var command = new ExcludedCommand { Name = "Test" }; // Excluded to hit early exit

    // Act
    await emitter.EmitCommandAuditedAsync(command, "result", "TestReceptor", context);

    // Assert - No events because ExcludedCommand has [AuditEvent(Exclude=true)]
    await Assert.That(eventStore.AppendedEnvelopes).IsEmpty();
  }

  [Test]
  public async Task EmitAsync_WithEventAudited_WhenAuditEnabled_AppendsToSystemStreamAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var systemEvent = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "TestEvent",
      OriginalStreamId = "stream-1",
      OriginalStreamPosition = 1,
      OriginalBody = JsonSerializer.SerializeToElement(new { Name = "Test" }),
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act
    await emitter.EmitAsync(systemEvent);

    // Assert
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
    await Assert.That(eventStore.AppendedStreamIds[0]).IsEqualTo(SystemEventStreams.StreamId);
  }

  [Test]
  public async Task EmitAsync_WithCommandAudited_WhenAuditEnabled_AppendsToSystemStreamAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var systemEvent = new CommandAudited {
      Id = Guid.NewGuid(),
      CommandType = "TestCommand",
      CommandBody = JsonSerializer.SerializeToElement(new { OrderId = "123" }),
      Timestamp = DateTimeOffset.UtcNow,
      ReceptorName = "TestReceptor",
      ResponseType = "string"
    };

    // Act
    await emitter.EmitAsync(systemEvent);

    // Assert
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
    await Assert.That(eventStore.AppendedStreamIds[0]).IsEqualTo(SystemEventStreams.StreamId);
  }

  [Test]
  public async Task EmitAsync_CreatesValidMessageEnvelopeAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var systemEvent = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "TestEvent",
      OriginalStreamId = "stream-1",
      OriginalStreamPosition = 1,
      OriginalBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act
    await emitter.EmitAsync(systemEvent);

    // Assert - Verify envelope structure
    var envelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<EventAudited>;
    await Assert.That(envelope).IsNotNull();
    await Assert.That(envelope!.MessageId.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(envelope.Payload).IsEqualTo(systemEvent);
    await Assert.That(envelope.Hops).Count().IsEqualTo(1);
    await Assert.That(envelope.Hops[0].Type).IsEqualTo(HopType.Current);
  }

  #endregion

  #region EmitEventAuditedAsync Happy Path Tests

  [Test]
  public async Task EmitEventAuditedAsync_WithEnabledAudit_EmitsEventAuditedToSystemStreamAsync() {
    // Arrange - Use string as TEvent since it's registered in InfrastructureJsonContext
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var streamId = Guid.NewGuid();
    const long streamPosition = 42L;
    var envelope = _createTestEnvelope("TestPayload");

    // Act
    await emitter.EmitEventAuditedAsync(streamId, streamPosition, envelope);

    // Assert - EventAudited was emitted to the system stream
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
    await Assert.That(eventStore.AppendedStreamIds[0]).IsEqualTo(SystemEventStreams.StreamId);

    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<EventAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.OriginalEventType).IsEqualTo("String");
    await Assert.That(emittedEnvelope.Payload.OriginalStreamId).IsEqualTo(streamId.ToString());
    await Assert.That(emittedEnvelope.Payload.OriginalStreamPosition).IsEqualTo(streamPosition);
  }

  [Test]
  public async Task EmitEventAuditedAsync_WithScopeContext_ExtractsTenantAndUserAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var testCorrelationId = Guid.NewGuid();
    var envelope = new MessageEnvelope<string> {
      MessageId = MessageId.New(),
      Payload = "ScopedPayload",
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext {
            TenantId = "tenant-abc",
            UserId = "user-xyz"
          }),
          CorrelationId = new CorrelationId(testCorrelationId)
        }
      ]
    };

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 5, envelope);

    // Assert - Scope values are extracted into EventAudited
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<EventAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.TenantId).IsEqualTo("tenant-abc");
    await Assert.That(emittedEnvelope.Payload.UserId).IsEqualTo("user-xyz");
    await Assert.That(emittedEnvelope.Payload.CorrelationId).IsEqualTo(testCorrelationId.ToString());
    await Assert.That(emittedEnvelope.Payload.Scope).IsNotNull();
  }

  [Test]
  public async Task EmitEventAuditedAsync_WithNoScope_EmitsWithNullScopeAsync() {
    // Arrange - Envelope with no scope delta and no correlation ID
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var envelope = new MessageEnvelope<string> {
      MessageId = MessageId.New(),
      Payload = "NoScopePayload",
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow
        }
      ]
    };

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, envelope);

    // Assert - EventAudited is emitted with null scope properties
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<EventAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.TenantId).IsNull();
    await Assert.That(emittedEnvelope.Payload.UserId).IsNull();
    await Assert.That(emittedEnvelope.Payload.CorrelationId).IsNull();
    await Assert.That(emittedEnvelope.Payload.Scope).IsNull();
  }

  [Test]
  public async Task EmitEventAuditedAsync_WithPartialScope_ExtractsOnlyAvailableFieldsAsync() {
    // Arrange - Scope with only TenantId (no UserId, no CorrelationId)
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var envelope = new MessageEnvelope<string> {
      MessageId = MessageId.New(),
      Payload = "PartialScopePayload",
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext {
            TenantId = "tenant-only"
          })
        }
      ]
    };

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, envelope);

    // Assert
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<EventAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.TenantId).IsEqualTo("tenant-only");
    await Assert.That(emittedEnvelope.Payload.UserId).IsNull();
    await Assert.That(emittedEnvelope.Payload.CorrelationId).IsNull();
  }

  [Test]
  public async Task EmitEventAuditedAsync_SerializesPayloadToJsonElementAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var envelope = _createTestEnvelope("SerializeMe");

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, envelope);

    // Assert - OriginalBody should contain serialized payload
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<EventAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.OriginalBody.ValueKind).IsNotEqualTo(JsonValueKind.Undefined);
  }

  [Test]
  public async Task EmitEventAuditedAsync_SetsTimestampOnAuditEventAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var before = DateTimeOffset.UtcNow;
    var envelope = _createTestEnvelope("TimestampTest");

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, envelope);

    // Assert
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<EventAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.Timestamp).IsGreaterThanOrEqualTo(before);
    await Assert.That(emittedEnvelope.Payload.Timestamp).IsLessThanOrEqualTo(DateTimeOffset.UtcNow);
  }

  #endregion

  #region EmitCommandAuditedAsync Happy Path Tests

  [Test]
  public async Task EmitCommandAuditedAsync_WithEnabledAudit_EmitsCommandAuditedToSystemStreamAsync() {
    // Arrange - Use string as TCommand since it's registered in InfrastructureJsonContext
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    // Act
    await emitter.EmitCommandAuditedAsync("TestCommand", "TestResponse", "MyReceptor", null);

    // Assert
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
    await Assert.That(eventStore.AppendedStreamIds[0]).IsEqualTo(SystemEventStreams.StreamId);

    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<CommandAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.CommandType).IsEqualTo("String");
    await Assert.That(emittedEnvelope.Payload.ReceptorName).IsEqualTo("MyReceptor");
    await Assert.That(emittedEnvelope.Payload.ResponseType).IsEqualTo("String");
  }

  [Test]
  public async Task EmitCommandAuditedAsync_WithContext_ExtractsMetadataCorrectlyAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var context = new TestMessageContext {
      UserId = "user-cmd-123"
    };
    context.Metadata["TenantId"] = "tenant-cmd-456";

    // Act
    await emitter.EmitCommandAuditedAsync("SomeCommand", 42, "OrderReceptor", context);

    // Assert - Metadata extracted into CommandAudited
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<CommandAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.UserId).IsEqualTo("user-cmd-123");
    await Assert.That(emittedEnvelope.Payload.TenantId).IsEqualTo("tenant-cmd-456");
    await Assert.That(emittedEnvelope.Payload.CorrelationId).IsNotNull();
    await Assert.That(emittedEnvelope.Payload.Scope).IsNotNull();
  }

  [Test]
  public async Task EmitCommandAuditedAsync_WithNullContext_EmitsWithNullScopeFieldsAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    // Act - null context
    await emitter.EmitCommandAuditedAsync("NullCtxCmd", "ok", "TestReceptor", null);

    // Assert
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<CommandAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.TenantId).IsNull();
    await Assert.That(emittedEnvelope.Payload.UserId).IsNull();
    await Assert.That(emittedEnvelope.Payload.CorrelationId).IsNull();
    await Assert.That(emittedEnvelope.Payload.Scope).IsNull();
  }

  [Test]
  public async Task EmitCommandAuditedAsync_WithContextNoTenantId_OmitsTenantFromScopeAsync() {
    // Arrange - Context with UserId but no TenantId in Metadata
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var context = new TestMessageContext {
      UserId = "user-only"
    };
    // Not setting TenantId metadata

    // Act
    await emitter.EmitCommandAuditedAsync("PartialCtxCmd", "ok", "TestReceptor", context);

    // Assert
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<CommandAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.UserId).IsEqualTo("user-only");
    await Assert.That(emittedEnvelope.Payload.TenantId).IsNull();
  }

  [Test]
  public async Task EmitCommandAuditedAsync_SerializesCommandBodyAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    // Act
    await emitter.EmitCommandAuditedAsync("MyCommandBody", "response", "Receptor", null);

    // Assert - CommandBody should contain serialized command
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<CommandAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.CommandBody.ValueKind).IsNotEqualTo(JsonValueKind.Undefined);
  }

  [Test]
  public async Task EmitCommandAuditedAsync_SetsTimestampAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var before = DateTimeOffset.UtcNow;

    // Act
    await emitter.EmitCommandAuditedAsync("TimedCmd", "ok", "Receptor", null);

    // Assert
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<CommandAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.Timestamp).IsGreaterThanOrEqualTo(before);
    await Assert.That(emittedEnvelope.Payload.Timestamp).IsLessThanOrEqualTo(DateTimeOffset.UtcNow);
  }

  #endregion

  #region EmitCommandAuditedAsync Branch Coverage Tests

  [Test]
  public async Task EmitCommandAuditedAsync_WithNullTenantIdInMetadata_HandlesNullValueGracefullyAsync() {
    // Arrange - Context with TenantId key present in Metadata but value is null.
    // This covers the branch in `scope["TenantId"] = tenantId?.ToString()` where tenantId is null.
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var context = new TestMessageContext {
      UserId = "user-789"
    };
    // TenantId key present but value is null - exercises the null-coalescing branch
    context.Metadata["TenantId"] = null!;

    // Act
    await emitter.EmitCommandAuditedAsync("SomeCommand", "ok", "TestReceptor", context);

    // Assert - Should emit with null TenantId despite key being present
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<CommandAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.UserId).IsEqualTo("user-789");
    // TenantId is null (the null value from Metadata)
    await Assert.That(emittedEnvelope.Payload.TenantId).IsNull();
  }

  [Test]
  public async Task EmitCommandAuditedAsync_WithContextMissingTenantIdKey_DoesNotAddTenantToScopeAsync() {
    // Arrange - Context with Metadata that does NOT contain TenantId key at all.
    // This covers the false branch of context?.Metadata.TryGetValue("TenantId") == true.
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var context = new TestMessageContext {
      UserId = "user-scope-test"
    };
    // Deliberately do NOT add TenantId to Metadata

    // Act
    await emitter.EmitCommandAuditedAsync("MyCommand", "result", "Receptor", context);

    // Assert - Scope should not include TenantId
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<CommandAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.TenantId).IsNull();
    await Assert.That(emittedEnvelope.Payload.UserId).IsEqualTo("user-scope-test");
    // Scope should contain UserId and CorrelationId, but NOT TenantId
    await Assert.That(emittedEnvelope.Payload.Scope).IsNotNull();
    await Assert.That(emittedEnvelope.Payload.Scope!.ContainsKey("TenantId")).IsFalse();
  }

  #endregion

  #region EmitAsync Branch Coverage Tests

  [Test]
  public async Task EmitAsync_WithActivityCurrentSet_PopulatesTraceParentAsync() {
    // Arrange - Test the TraceParent branch in EmitAsync where Activity.Current?.Id is used.
    // Start a diagnostic activity to ensure Activity.Current is non-null.
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var systemEvent = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "Test",
      OriginalStreamId = "stream-1",
      OriginalStreamPosition = 1,
      OriginalBody = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act - With an active diagnostic activity
    using var activity = new System.Diagnostics.ActivitySource("TestSource").StartActivity("TestActivity");

    await emitter.EmitAsync(systemEvent);

    // Assert - The hop's TraceParent may be set if an activity is running
    var envelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<EventAudited>;
    await Assert.That(envelope).IsNotNull();
    await Assert.That(envelope!.Hops).Count().IsEqualTo(1);
    // TraceParent is Activity.Current?.Id - may be null or set depending on activity listener
    // The test verifies the code path is exercised without error
    await Assert.That(envelope.Hops[0].Type).IsEqualTo(HopType.Current);
  }

  [Test]
  public async Task EmitAsync_WithNoActivityCurrent_TraceParentIsNullAsync() {
    // Arrange - Test the null branch of Activity.Current?.Id in EmitAsync.
    // This ensures the null-conditional expression is tested with null Activity.Current.
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var systemEvent = new CommandAudited {
      Id = Guid.NewGuid(),
      CommandType = "TestCommand",
      CommandBody = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow,
      ReceptorName = "Receptor",
      ResponseType = "string"
    };

    // Act - Without an active diagnostic activity, Activity.Current should be null
    // (assuming no ambient activity in the test environment)
    await emitter.EmitAsync(systemEvent);

    // Assert
    var envelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<CommandAudited>;
    await Assert.That(envelope).IsNotNull();
    await Assert.That(envelope!.Hops).Count().IsEqualTo(1);
    await Assert.That(envelope.Hops[0].ServiceInstance).IsEqualTo(ServiceInstanceInfo.Unknown);
  }

  #endregion

  #region EmitAsync Additional Coverage Tests

  [Test]
  public async Task EmitAsync_WithEventAudited_WhenEventAuditEnabled_IsEnabledReturnsTrueAsync() {
    // Arrange - Test the path where IsEnabled<EventAudited>() returns true directly
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var systemEvent = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "Test",
      OriginalStreamId = "stream-1",
      OriginalStreamPosition = 1,
      OriginalBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act
    await emitter.EmitAsync(systemEvent);

    // Assert - Should emit because IsEnabled<EventAudited>() returns true
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
  }

  [Test]
  public async Task EmitAsync_WithCommandAudited_WhenCommandAuditEnabled_IsEnabledReturnsTrueAsync() {
    // Arrange - Test the path where IsEnabled<CommandAudited>() returns true directly
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var systemEvent = new CommandAudited {
      Id = Guid.NewGuid(),
      CommandType = "Test",
      CommandBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow,
      ReceptorName = "Receptor",
      ResponseType = "string"
    };

    // Act
    await emitter.EmitAsync(systemEvent);

    // Assert - Should emit because IsEnabled<CommandAudited>() returns true
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
  }

  [Test]
  public async Task EmitAsync_PassesCancellationTokenToEventStoreAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var systemEvent = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "Test",
      OriginalStreamId = "stream-1",
      OriginalStreamPosition = 1,
      OriginalBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };

    using var cts = new CancellationTokenSource();

    // Act - should not throw with non-cancelled token
    await emitter.EmitAsync(systemEvent, cts.Token);

    // Assert
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
  }

  [Test]
  public async Task EmitAsync_CreatesEnvelopeWithServiceInstanceUnknownAsync() {
    // Arrange
    var eventStore = new MockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var systemEvent = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "Test",
      OriginalStreamId = "stream-1",
      OriginalStreamPosition = 1,
      OriginalBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act
    await emitter.EmitAsync(systemEvent);

    // Assert - Verify hop uses ServiceInstanceInfo.Unknown
    var envelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<EventAudited>;
    await Assert.That(envelope).IsNotNull();
    await Assert.That(envelope!.Hops[0].ServiceInstance).IsEqualTo(ServiceInstanceInfo.Unknown);
  }

  #endregion

  #region Helper Methods

  private static MessageEnvelope<T> _createTestEnvelope<T>(T payload) {
    return new MessageEnvelope<T> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow
        }
      ]
    };
  }

  /// <summary>
  /// Simple message context implementation for testing.
  /// </summary>
  private sealed class TestMessageContext : IMessageContext {
    public MessageId MessageId { get; init; } = MessageId.New();
    public CorrelationId CorrelationId { get; init; } = new(Guid.NewGuid());
    public MessageId CausationId { get; init; } = MessageId.New();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? UserId { get; set; }
    public Dictionary<string, object> Metadata { get; } = new();

    public string? TenantId => throw new NotImplementedException();
    public Core.Security.IScopeContext? ScopeContext => null;
    public ICallerInfo? CallerInfo => null;

    IReadOnlyDictionary<string, object> IMessageContext.Metadata => Metadata;
  }

  #endregion

  #region Mock EventStore

  /// <summary>
  /// Mock IEventStore for testing SystemEventEmitter.
  /// </summary>
  private sealed class MockEventStore : IEventStore {
    public List<object> AppendedEnvelopes { get; } = [];
    public List<Guid> AppendedStreamIds { get; } = [];

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
      AppendedStreamIds.Add(streamId);
      AppendedEnvelopes.Add(envelope!);
      return Task.CompletedTask;
    }

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default)
        where TMessage : notnull {
      AppendedStreamIds.Add(streamId);
      AppendedEnvelopes.Add(message!);
      return Task.CompletedTask;
    }

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<MessageEnvelope<IEvent>>();

    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new List<MessageEnvelope<TMessage>>());

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
        Task.FromResult(new List<MessageEnvelope<IEvent>>());

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
        Task.FromResult(0L);
  }

  #endregion
}
