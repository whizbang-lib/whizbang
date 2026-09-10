using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Priority step 1, background work: the audit record the decorator queues is catch-up work nobody waits on. It
/// declares <see cref="WorkPriority.BACKGROUND"/> by construction, on the row the store reads and on the envelope
/// the wire carries, whatever number the audited event or the ambient handling carried. A record left at the
/// standard number sits in the outbox ahead of live work, which is exactly what a flood of them did.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#background-work</docs>
/// <code-under-test>src/Whizbang.Core/SystemEvents/AuditingEventStoreDecorator.cs</code-under-test>
[Category("SystemEvents")]
public class AuditingEventStoreDecoratorPriorityTests {
  [Test]
  public async Task AppendAsync_WithEnvelope_QueuesTheAuditRecordAsBackground_OnTheRowAndTheEnvelopeAsync() {
    var (decorator, channel) = _createDecorator();
    var source = _createTestEnvelope(new _auditedEvent { Name = "interactive-source" });
    source.Priority = WorkPriority.INTERACTIVE;

    await decorator.AppendAsync(Guid.NewGuid(), source);

    var queued = channel.QueuedMessages.Single();
    await Assert.That(queued.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the outbox row's number is what the store keeps and the drain claims by; audit records never compete with the work they describe");
    await Assert.That(queued.Envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the envelope's number crosses the wire; the consumer's audit projection must arrive declared background");
  }

  [Test]
  public async Task AppendAsync_WithBareMessage_QueuesTheAuditRecordAsBackgroundAsync() {
    var (decorator, channel) = _createDecorator();

    await decorator.AppendAsync(Guid.NewGuid(), new _auditedEvent { Name = "bare" });

    var queued = channel.QueuedMessages.Single();
    await Assert.That(queued.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the message overload builds its own audit record; both append paths declare the same band");
    await Assert.That(queued.Envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND);
  }

  [Test]
  public async Task AppendAsync_TheAuditedEventsInteractiveNumber_DoesNotReachTheAuditRecordAsync() {
    var (decorator, channel) = _createDecorator();
    var source = _createTestEnvelope(new _auditedEvent { Name = "urgent" });
    source.Priority = WorkPriority.INTERACTIVE;

    await decorator.AppendAsync(Guid.NewGuid(), source);

    await Assert.That(channel.QueuedMessages.Single().Priority).IsNotEqualTo(WorkPriority.INTERACTIVE)
      .Because("a person waits on the audited command, not on its audit trail; copying the source's number would put every audit record in the urgent lane");
  }

  [Test]
  public async Task AppendAsync_InsideAnInteractiveHandling_TheAuditRecordStaysBackgroundAsync() {
    var (decorator, channel) = _createDecorator();

    using (PriorityContext.Enter(WorkPriority.INTERACTIVE)) {
      await decorator.AppendAsync(Guid.NewGuid(), _createTestEnvelope(new _auditedEvent { Name = "in-handler" }));
    }

    var queued = channel.QueuedMessages.Single();
    await Assert.That(queued.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the decorator builds the record by hand, outside the dispatcher's inheritance; the ambient parent must not leak into it");
    await Assert.That(queued.Envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND);
  }

  private static (AuditingEventStoreDecorator Decorator, _captureChannel Channel) _createDecorator() {
    var options = new SystemEventOptions().EnableEventAudit();
    var channel = new _captureChannel();
    var decorator = new AuditingEventStoreDecorator(
      new _inertStore(), channel, Options.Create(options),
      new Whizbang.Core.Observability.ServiceInstanceProvider(),
      Whizbang.Core.SystemEvents.NoOpinionAuditDecisionHook.Instance);
    return (decorator, channel);
  }

  private static MessageEnvelope<T> _createTestEnvelope<T>(T payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [
      new MessageHop {
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  private sealed record _auditedEvent {
    public required string Name { get; init; }
  }

  private sealed class _inertStore : IEventStore {
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
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
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(3L);
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
  }

  private sealed class _captureChannel : IDeferredOutboxChannel {
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
}
