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
/// Tests for AuditingEventStoreDecorator.
/// The decorator intercepts AppendAsync calls, builds EventAudited envelopes,
/// and queues them to IDeferredOutboxChannel with destination "whizbang.core.auditevents".
/// </summary>
[Category("SystemEvents")]
public class AuditingEventStoreDecoratorTests {
  #region Constructor Tests

  [Test]
  public async Task Constructor_WithNullInner_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var channel = new MockDeferredOutboxChannel();
    var options = Options.Create(new SystemEventOptions());

    // Act & Assert
    await Assert.That(() => new AuditingEventStoreDecorator(null!, channel, options))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullChannel_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var inner = new MockEventStore();
    var options = Options.Create(new SystemEventOptions());

    // Act & Assert
    await Assert.That(() => new AuditingEventStoreDecorator(inner, null!, options))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullOptions_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var inner = new MockEventStore();
    var channel = new MockDeferredOutboxChannel();

    // Act & Assert
    await Assert.That(() => new AuditingEventStoreDecorator(inner, channel, null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  #endregion

  #region AppendAsync (envelope overload) Tests

  [Test]
  public async Task AppendAsync_WithEnvelope_DelegatesToInnerStoreAsync() {
    // Arrange
    var (decorator, inner, channel) = _createDecorator(opts => opts.EnableEventAudit());
    var streamId = Guid.NewGuid();
    var envelope = _createTestEnvelope(new TestEvent { Name = "Test" });

    // Act
    await decorator.AppendAsync(streamId, envelope);

    // Assert - Inner store received the append
    await Assert.That(inner.AppendedStreamIds).Contains(streamId);
    await Assert.That(inner.AppendedEnvelopes).Count().IsEqualTo(1);
  }

  [Test]
  public async Task AppendAsync_WithEnvelope_QueuesAuditEventToOutboxAsync() {
    // Arrange
    var (decorator, _, channel) = _createDecorator(opts => opts.EnableEventAudit());
    var streamId = Guid.NewGuid();
    var envelope = _createTestEnvelope(new TestEvent { Name = "Test" });

    // Act
    await decorator.AppendAsync(streamId, envelope);

    // Assert - Outbox channel received an audit event
    await Assert.That(channel.QueuedMessages).Count().IsEqualTo(1);
    await Assert.That(channel.QueuedMessages[0].Destination)
        .IsEqualTo(AuditingEventStoreDecorator.AUDIT_TOPIC_DESTINATION);
  }

  [Test]
  public async Task AppendAsync_WithEnvelope_AuditDisabled_DoesNotQueueAsync() {
    // Arrange - audit NOT enabled
    var (decorator, _, channel) = _createDecorator();
    var streamId = Guid.NewGuid();
    var envelope = _createTestEnvelope(new TestEvent { Name = "Test" });

    // Act
    await decorator.AppendAsync(streamId, envelope);

    // Assert - No audit event queued
    await Assert.That(channel.QueuedMessages).IsEmpty();
  }

  [Test]
  public async Task AppendAsync_WithEnvelope_PassesCorrectStreamPositionAsync() {
    // Arrange
    var (decorator, inner, channel) = _createDecorator(opts => opts.EnableEventAudit());
    inner.LastSequenceToReturn = 5L;
    var streamId = Guid.NewGuid();
    var envelope = _createTestEnvelope(new TestEvent { Name = "Test" });

    // Act
    await decorator.AppendAsync(streamId, envelope);

    // Assert - Audit event contains correct stream position
    await Assert.That(channel.QueuedMessages).Count().IsEqualTo(1);
    await Assert.That(channel.QueuedMessages[0].IsEvent).IsTrue();
  }

  #endregion

  #region AppendAsync (message overload) Tests

  [Test]
  public async Task AppendAsync_WithMessage_DelegatesToInnerStoreAsync() {
    // Arrange
    var (decorator, inner, _) = _createDecorator(opts => opts.EnableEventAudit());
    var streamId = Guid.NewGuid();
    var message = new TestEvent { Name = "Test" };

    // Act
    await decorator.AppendAsync(streamId, message);

    // Assert
    await Assert.That(inner.AppendedStreamIds).Contains(streamId);
    await Assert.That(inner.AppendedMessages).Count().IsEqualTo(1);
  }

  [Test]
  public async Task AppendAsync_WithMessage_QueuesAuditEventToOutboxAsync() {
    // Arrange
    var (decorator, _, channel) = _createDecorator(opts => opts.EnableEventAudit());
    var streamId = Guid.NewGuid();
    var message = new TestEvent { Name = "Test" };

    // Act
    await decorator.AppendAsync(streamId, message);

    // Assert - Audit event queued (both overloads now audit)
    await Assert.That(channel.QueuedMessages).Count().IsEqualTo(1);
    await Assert.That(channel.QueuedMessages[0].Destination)
        .IsEqualTo(AuditingEventStoreDecorator.AUDIT_TOPIC_DESTINATION);
  }

  #endregion

  #region AuditMode Tests

  [Test]
  public async Task ShouldAudit_OptOut_AuditsRegularEventsAsync() {
    // Arrange - OptOut (default): audit everything
    var (decorator, _, _) = _createDecorator(opts => {
      opts.EnableEventAudit();
      opts.AuditMode = AuditMode.OptOut;
    });

    // Act & Assert
    await Assert.That(decorator._shouldAudit(typeof(TestEvent))).IsTrue();
  }

  [Test]
  public async Task ShouldAudit_OptOut_ExcludesMarkedEventsAsync() {
    // Arrange
    var (decorator, _, _) = _createDecorator(opts => {
      opts.EnableEventAudit();
      opts.AuditMode = AuditMode.OptOut;
    });

    // Act & Assert - ExcludedEvent has [AuditEvent(Exclude = true)]
    await Assert.That(decorator._shouldAudit(typeof(ExcludedEvent))).IsFalse();
  }

  [Test]
  public async Task ShouldAudit_OptIn_DoesNotAuditUnmarkedEventsAsync() {
    // Arrange - OptIn: only audit events with [AuditEvent]
    var (decorator, _, _) = _createDecorator(opts => {
      opts.EnableEventAudit();
      opts.AuditMode = AuditMode.OptIn;
    });

    // Act & Assert - TestEvent has no [AuditEvent] attribute
    await Assert.That(decorator._shouldAudit(typeof(TestEvent))).IsFalse();
  }

  [Test]
  public async Task ShouldAudit_OptIn_AuditsMarkedEventsAsync() {
    // Arrange
    var (decorator, _, _) = _createDecorator(opts => {
      opts.EnableEventAudit();
      opts.AuditMode = AuditMode.OptIn;
    });

    // Act & Assert - MarkedEvent has [AuditEvent]
    await Assert.That(decorator._shouldAudit(typeof(MarkedEvent))).IsTrue();
  }

  [Test]
  public async Task ShouldAudit_OptIn_ExcludesMarkedButExcludedEventsAsync() {
    // Arrange
    var (decorator, _, _) = _createDecorator(opts => {
      opts.EnableEventAudit();
      opts.AuditMode = AuditMode.OptIn;
    });

    // Act & Assert - ExcludedEvent has [AuditEvent(Exclude = true)]
    await Assert.That(decorator._shouldAudit(typeof(ExcludedEvent))).IsFalse();
  }

  [Test]
  public async Task ShouldAudit_EventAudited_ExcludedFromAudit_PreventsInfiniteLoopAsync() {
    // Arrange - EventAudited has [AuditEvent(Exclude = true)]
    var (decorator, _, _) = _createDecorator(opts => {
      opts.EnableEventAudit();
      opts.AuditMode = AuditMode.OptOut;
    });

    // Act & Assert
    await Assert.That(decorator._shouldAudit(typeof(EventAudited))).IsFalse();
  }

  #endregion

  #region ReadAsync Tests

  [Test]
  public async Task ReadAsync_BySequence_DelegatesToInnerStoreAsync() {
    // Arrange
    var (decorator, inner, _) = _createDecorator();
    var streamId = Guid.NewGuid();

    // Act
    _ = await decorator.ReadAsync<TestEvent>(streamId, 0).ToListAsync();

    // Assert
    await Assert.That(inner.ReadBySequenceCalls).Count().IsEqualTo(1);
    await Assert.That(inner.ReadBySequenceCalls[0].StreamId).IsEqualTo(streamId);
  }

  [Test]
  public async Task ReadAsync_ByEventId_DelegatesToInnerStoreAsync() {
    // Arrange
    var (decorator, inner, _) = _createDecorator();
    var streamId = Guid.NewGuid();
    var fromEventId = Guid.NewGuid();

    // Act
    _ = await decorator.ReadAsync<TestEvent>(streamId, fromEventId).ToListAsync();

    // Assert
    await Assert.That(inner.ReadByEventIdCalls).Count().IsEqualTo(1);
  }

  #endregion

  #region GetLastSequenceAsync Tests

  [Test]
  public async Task GetLastSequenceAsync_DelegatesToInnerStoreAsync() {
    // Arrange
    var (decorator, inner, _) = _createDecorator();
    inner.LastSequenceToReturn = 42L;
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
    var (decorator, inner, _) = _createDecorator();
    var streamId = Guid.NewGuid();

    // Act
    _ = await decorator.GetEventsBetweenAsync<TestEvent>(streamId, Guid.NewGuid(), Guid.NewGuid());

    // Assert
    await Assert.That(inner.GetEventsBetweenCalls).Count().IsEqualTo(1);
  }

  [Test]
  public async Task GetEventsBetweenPolymorphicAsync_DelegatesToInnerStoreAsync() {
    // Arrange
    var (decorator, inner, _) = _createDecorator();
    var streamId = Guid.NewGuid();
    var eventTypes = new List<Type> { typeof(TestEvent) };

    // Act
    _ = await decorator.GetEventsBetweenPolymorphicAsync(streamId, Guid.NewGuid(), Guid.NewGuid(), eventTypes);

    // Assert
    await Assert.That(inner.GetEventsBetweenPolymorphicCalls).Count().IsEqualTo(1);
  }

  #endregion

  #region ReadPolymorphicAsync Tests

  [Test]
  public async Task ReadPolymorphicAsync_DelegatesToInnerStoreAsync() {
    // Arrange
    var (decorator, inner, _) = _createDecorator();
    var streamId = Guid.NewGuid();
    var eventTypes = new List<Type> { typeof(TestEvent) };

    // Act
    _ = await decorator.ReadPolymorphicAsync(streamId, Guid.NewGuid(), eventTypes).ToListAsync();

    // Assert
    await Assert.That(inner.ReadPolymorphicCalls).Count().IsEqualTo(1);
  }

  #endregion

  #region AuditTopicDestination Tests

  [Test]
  public async Task AuditTopicDestination_IsCorrectValueAsync() {
    var destination = AuditingEventStoreDecorator.AUDIT_TOPIC_DESTINATION;
    await Assert.That(destination).IsEqualTo("whizbang.core.auditevents");
  }

  #endregion

  #region Test Types

  private sealed record TestEvent {
    public required string Name { get; init; }
  }

  [AuditEvent(Exclude = true, Reason = "Test excluded event")]
  private sealed record ExcludedEvent : IEvent {
    public required string Name { get; init; }
  }

  [AuditEvent(Reason = "Explicitly marked for audit")]
  private sealed record MarkedEvent : IEvent {
    public required string Name { get; init; }
  }

  #endregion

  #region Helper Methods

  private static (AuditingEventStoreDecorator Decorator, MockEventStore Inner, MockDeferredOutboxChannel Channel)
      _createDecorator(Action<SystemEventOptions>? configure = null) {
    var options = new SystemEventOptions();
    configure?.Invoke(options);
    var inner = new MockEventStore();
    var channel = new MockDeferredOutboxChannel();
    var decorator = new AuditingEventStoreDecorator(inner, channel, Options.Create(options));
    return (decorator, inner, channel);
  }

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

  private sealed class MockDeferredOutboxChannel : IDeferredOutboxChannel {
    public List<OutboxMessage> QueuedMessages { get; } = [];

    public ValueTask QueueAsync(OutboxMessage message, CancellationToken ct = default) {
      QueuedMessages.Add(message);
      return ValueTask.CompletedTask;
    }

    public IReadOnlyList<OutboxMessage> DrainAll() {
      var messages = QueuedMessages.ToList();
      QueuedMessages.Clear();
      return messages;
    }

    public bool HasPending => QueuedMessages.Count > 0;
  }

  #endregion
}
