using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Priority step 1, background work: every system event the emitter appends (event audits, command audits, and
/// anything else routed through <c>EmitAsync</c>) is declared <see cref="WorkPriority.BACKGROUND"/> on the envelope
/// it builds. The emitter constructs the envelope by hand, so neither the audited message's number nor the
/// ambient handling can decide the band; the declaration has to be in the constructor.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#background-work</docs>
/// <code-under-test>src/Whizbang.Core/SystemEvents/SystemEventEmitter.cs</code-under-test>
[Category("SystemEvents")]
public class SystemEventEmitterPriorityTests {
  private sealed record _auditedEvent : IEvent { public string Name { get; init; } = ""; }
  private sealed record _auditedCommand(string Name);

  [Test]
  public async Task EmitEventAudited_TheAuditEnvelopeIsBackgroundAsync() {
    var (emitter, store) = _build();
    var source = _source();
    source.Priority = WorkPriority.INTERACTIVE;

    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, source);

    await Assert.That(store.Envelopes.Single().Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the audited event was interactive; its audit record is catch-up work and must not ride that number");
  }

  [Test]
  public async Task EmitCommandAudited_TheAuditEnvelopeIsBackgroundAsync() {
    var (emitter, store) = _build();

    await emitter.EmitCommandAuditedAsync(new _auditedCommand("c"), "ok", "TestReceptor", context: null);

    await Assert.That(store.Envelopes.Single().Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("command audits share the emit path with event audits; one construction site, one band");
  }

  [Test]
  public async Task EmitEventAudited_InsideAnInteractiveHandling_StaysBackgroundAsync() {
    var (emitter, store) = _build();

    using (PriorityContext.Enter(WorkPriority.INTERACTIVE)) {
      await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, _source());
    }

    await Assert.That(store.Envelopes.Single().Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the emitter does not dispatch, so inheritance never runs for it; the ambient parent must not leak into a hand-built envelope");
  }

  private static (SystemEventEmitter Emitter, _captureStore Store) _build() {
    var store = new _captureStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit().EnableCommandAudit());
    return (new SystemEventEmitter(options, store, new Whizbang.Core.Observability.ServiceInstanceProvider()), store);
  }

  private static MessageEnvelope<_auditedEvent> _source() => new() {
    MessageId = MessageId.New(),
    Payload = new _auditedEvent { Name = "x" },
    Hops = [new MessageHop {
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
    }],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private sealed class _captureStore : IEventStore {
    /// <summary>Every system event envelope appended, whatever its payload type, through the envelope's own number.</summary>
    public List<IMessageEnvelope> Envelopes { get; } = [];

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
      Envelopes.Add(envelope);
      return Task.CompletedTask;
    }
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default)
      where TMessage : notnull => Task.CompletedTask;
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<Whizbang.Core.Messaging.StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
      throw new NotSupportedException();
  }
}
