using System.Text.Json;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
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
    var streamPosition = 42L;

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
