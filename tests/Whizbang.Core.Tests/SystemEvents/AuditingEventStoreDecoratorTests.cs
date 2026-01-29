using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Tests for AuditingEventStoreDecorator.
/// The decorator emits EventAudited system events when domain events are appended.
/// </summary>
[Category("SystemEvents")]
public class AuditingEventStoreDecoratorTests {
  #region Constructor Tests

  [Test]
  public async Task Constructor_WithNullInner_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var emitter = new MockSystemEventEmitter();

    // Act & Assert
    await Assert.That(() => new AuditingEventStoreDecorator(null!, emitter))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullEmitter_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var inner = new MockEventStore();

    // Act & Assert
    await Assert.That(() => new AuditingEventStoreDecorator(inner, null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  #endregion

  #region AppendAsync (envelope overload) Tests

  [Test]
  public async Task AppendAsync_WithEnvelope_DelegatesToInnerStoreAsync() {
    // Arrange
    var inner = new MockEventStore();
    var emitter = new MockSystemEventEmitter();
    var decorator = new AuditingEventStoreDecorator(inner, emitter);

    var streamId = Guid.NewGuid();
    var envelope = _createTestEnvelope(new TestEvent { Name = "Test" });

    // Act
    await decorator.AppendAsync(streamId, envelope);

    // Assert - Inner store received the append
    await Assert.That(inner.AppendedStreamIds).Contains(streamId);
    await Assert.That(inner.AppendedEnvelopes).Count().IsEqualTo(1);
  }

  [Test]
  public async Task AppendAsync_WithEnvelope_EmitsEventAuditedAsync() {
    // Arrange
    var inner = new MockEventStore();
    var emitter = new MockSystemEventEmitter();
    var decorator = new AuditingEventStoreDecorator(inner, emitter);

    var streamId = Guid.NewGuid();
    var envelope = _createTestEnvelope(new TestEvent { Name = "Test" });

    // Act
    await decorator.AppendAsync(streamId, envelope);

    // Assert - Emitter was called
    await Assert.That(emitter.EmitEventAuditedCalls).Count().IsEqualTo(1);
    var call = emitter.EmitEventAuditedCalls[0];
    await Assert.That(call.StreamId).IsEqualTo(streamId);
  }

  [Test]
  public async Task AppendAsync_WithEnvelope_PassesCorrectStreamPositionAsync() {
    // Arrange
    var inner = new MockEventStore {
      LastSequenceToReturn = 5L // Simulate position after append
    };
    var emitter = new MockSystemEventEmitter();
    var decorator = new AuditingEventStoreDecorator(inner, emitter);

    var streamId = Guid.NewGuid();
    var envelope = _createTestEnvelope(new TestEvent { Name = "Test" });

    // Act
    await decorator.AppendAsync(streamId, envelope);

    // Assert - Correct position passed to emitter
    await Assert.That(emitter.EmitEventAuditedCalls).Count().IsEqualTo(1);
    var call = emitter.EmitEventAuditedCalls[0];
    await Assert.That(call.StreamPosition).IsEqualTo(5L);
  }

  #endregion

  #region AppendAsync (message overload) Tests

  [Test]
  public async Task AppendAsync_WithMessage_DelegatesToInnerStoreAsync() {
    // Arrange
    var inner = new MockEventStore();
    var emitter = new MockSystemEventEmitter();
    var decorator = new AuditingEventStoreDecorator(inner, emitter);

    var streamId = Guid.NewGuid();
    var message = new TestEvent { Name = "Test" };

    // Act
    await decorator.AppendAsync(streamId, message);

    // Assert - Inner store received the append
    await Assert.That(inner.AppendedStreamIds).Contains(streamId);
    await Assert.That(inner.AppendedMessages).Count().IsEqualTo(1);
  }

  [Test]
  public async Task AppendAsync_WithMessage_DoesNotEmitAudit_ToAvoidDoubleAuditingAsync() {
    // Arrange
    var inner = new MockEventStore();
    var emitter = new MockSystemEventEmitter();
    var decorator = new AuditingEventStoreDecorator(inner, emitter);

    var streamId = Guid.NewGuid();
    var message = new TestEvent { Name = "Test" };

    // Act
    await decorator.AppendAsync(streamId, message);

    // Assert - Emitter was NOT called (to avoid double auditing)
    await Assert.That(emitter.EmitEventAuditedCalls).IsEmpty();
  }

  #endregion

  #region ReadAsync Tests

  [Test]
  public async Task ReadAsync_BySequence_DelegatesToInnerStoreAsync() {
    // Arrange
    var inner = new MockEventStore();
    var emitter = new MockSystemEventEmitter();
    var decorator = new AuditingEventStoreDecorator(inner, emitter);

    var streamId = Guid.NewGuid();

    // Act
    _ = await decorator.ReadAsync<TestEvent>(streamId, 0).ToListAsync();

    // Assert - Delegated to inner
    await Assert.That(inner.ReadBySequenceCalls).Count().IsEqualTo(1);
    await Assert.That(inner.ReadBySequenceCalls[0].StreamId).IsEqualTo(streamId);
    await Assert.That(inner.ReadBySequenceCalls[0].FromSequence).IsEqualTo(0);
  }

  [Test]
  public async Task ReadAsync_ByEventId_DelegatesToInnerStoreAsync() {
    // Arrange
    var inner = new MockEventStore();
    var emitter = new MockSystemEventEmitter();
    var decorator = new AuditingEventStoreDecorator(inner, emitter);

    var streamId = Guid.NewGuid();
    var fromEventId = Guid.NewGuid();

    // Act
    _ = await decorator.ReadAsync<TestEvent>(streamId, fromEventId).ToListAsync();

    // Assert - Delegated to inner
    await Assert.That(inner.ReadByEventIdCalls).Count().IsEqualTo(1);
    await Assert.That(inner.ReadByEventIdCalls[0].StreamId).IsEqualTo(streamId);
    await Assert.That(inner.ReadByEventIdCalls[0].FromEventId).IsEqualTo(fromEventId);
  }

  #endregion

  #region GetLastSequenceAsync Tests

  [Test]
  public async Task GetLastSequenceAsync_DelegatesToInnerStoreAsync() {
    // Arrange
    var inner = new MockEventStore {
      LastSequenceToReturn = 42L
    };
    var emitter = new MockSystemEventEmitter();
    var decorator = new AuditingEventStoreDecorator(inner, emitter);

    var streamId = Guid.NewGuid();

    // Act
    var result = await decorator.GetLastSequenceAsync(streamId);

    // Assert
    await Assert.That(result).IsEqualTo(42L);
    await Assert.That(inner.GetLastSequenceCalls).Contains(streamId);
  }

  #endregion

  #region GetEventsBetweenAsync Tests

  [Test]
  public async Task GetEventsBetweenAsync_DelegatesToInnerStoreAsync() {
    // Arrange
    var inner = new MockEventStore();
    var emitter = new MockSystemEventEmitter();
    var decorator = new AuditingEventStoreDecorator(inner, emitter);

    var streamId = Guid.NewGuid();
    var afterEventId = Guid.NewGuid();
    var upToEventId = Guid.NewGuid();

    // Act
    _ = await decorator.GetEventsBetweenAsync<TestEvent>(streamId, afterEventId, upToEventId);

    // Assert
    await Assert.That(inner.GetEventsBetweenCalls).Count().IsEqualTo(1);
  }

  [Test]
  public async Task GetEventsBetweenPolymorphicAsync_DelegatesToInnerStoreAsync() {
    // Arrange
    var inner = new MockEventStore();
    var emitter = new MockSystemEventEmitter();
    var decorator = new AuditingEventStoreDecorator(inner, emitter);

    var streamId = Guid.NewGuid();
    var afterEventId = Guid.NewGuid();
    var upToEventId = Guid.NewGuid();
    var eventTypes = new List<Type> { typeof(TestEvent) };

    // Act
    _ = await decorator.GetEventsBetweenPolymorphicAsync(streamId, afterEventId, upToEventId, eventTypes);

    // Assert
    await Assert.That(inner.GetEventsBetweenPolymorphicCalls).Count().IsEqualTo(1);
  }

  #endregion

  #region ReadPolymorphicAsync Tests

  [Test]
  public async Task ReadPolymorphicAsync_DelegatesToInnerStoreAsync() {
    // Arrange
    var inner = new MockEventStore();
    var emitter = new MockSystemEventEmitter();
    var decorator = new AuditingEventStoreDecorator(inner, emitter);

    var streamId = Guid.NewGuid();
    var fromEventId = Guid.NewGuid();
    var eventTypes = new List<Type> { typeof(TestEvent) };

    // Act
    _ = await decorator.ReadPolymorphicAsync(streamId, fromEventId, eventTypes).ToListAsync();

    // Assert
    await Assert.That(inner.ReadPolymorphicCalls).Count().IsEqualTo(1);
  }

  #endregion

  #region Test Types

  private sealed record TestEvent {
    public required string Name { get; init; }
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

  #region Mock Implementations

  private sealed class MockEventStore : IEventStore {
    public List<object> AppendedEnvelopes { get; } = [];
    public List<object> AppendedMessages { get; } = [];
    public List<Guid> AppendedStreamIds { get; } = [];
    public List<(Guid StreamId, long FromSequence)> ReadBySequenceCalls { get; } = [];
    public List<(Guid StreamId, Guid? FromEventId)> ReadByEventIdCalls { get; } = [];
    public List<Guid> ReadPolymorphicCalls { get; } = [];
    public List<Guid> GetLastSequenceCalls { get; } = [];
    public List<Guid> GetEventsBetweenCalls { get; } = [];
    public List<Guid> GetEventsBetweenPolymorphicCalls { get; } = [];

    public long LastSequenceToReturn { get; set; }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
      AppendedStreamIds.Add(streamId);
      AppendedEnvelopes.Add(envelope!);
      return Task.CompletedTask;
    }

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default)
        where TMessage : notnull {
      AppendedStreamIds.Add(streamId);
      AppendedMessages.Add(message!);
      return Task.CompletedTask;
    }

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) {
      ReadBySequenceCalls.Add((streamId, fromSequence));
      return AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();
    }

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) {
      ReadByEventIdCalls.Add((streamId, fromEventId));
      return AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();
    }

    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      ReadPolymorphicCalls.Add(streamId);
      return AsyncEnumerable.Empty<MessageEnvelope<IEvent>>();
    }

    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) {
      GetEventsBetweenCalls.Add(streamId);
      return Task.FromResult(new List<MessageEnvelope<TMessage>>());
    }

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      GetEventsBetweenPolymorphicCalls.Add(streamId);
      return Task.FromResult(new List<MessageEnvelope<IEvent>>());
    }

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) {
      GetLastSequenceCalls.Add(streamId);
      return Task.FromResult(LastSequenceToReturn);
    }
  }

  private sealed class MockSystemEventEmitter : ISystemEventEmitter {
    public List<(Guid StreamId, long StreamPosition, object Envelope)> EmitEventAuditedCalls { get; } = [];
    public List<(object Command, object Response, string ReceptorName)> EmitCommandAuditedCalls { get; } = [];
    public List<object> EmitAsyncCalls { get; } = [];

    public Task EmitEventAuditedAsync<TEvent>(Guid streamId, long streamPosition, MessageEnvelope<TEvent> envelope, CancellationToken cancellationToken = default) {
      EmitEventAuditedCalls.Add((streamId, streamPosition, envelope!));
      return Task.CompletedTask;
    }

    public Task EmitCommandAuditedAsync<TCommand, TResponse>(TCommand command, TResponse response, string receptorName, IMessageContext? context, CancellationToken cancellationToken = default)
        where TCommand : notnull {
      EmitCommandAuditedCalls.Add((command!, response!, receptorName));
      return Task.CompletedTask;
    }

    public Task EmitAsync<TSystemEvent>(TSystemEvent systemEvent, CancellationToken cancellationToken = default)
        where TSystemEvent : ISystemEvent {
      EmitAsyncCalls.Add(systemEvent!);
      return Task.CompletedTask;
    }

    public bool ShouldExcludeFromAudit(Type type) => false;
  }

  #endregion
}
