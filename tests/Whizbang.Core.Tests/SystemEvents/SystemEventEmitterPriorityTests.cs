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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// The band the emitter declares on the envelopes it builds. An audit record goes on the audit
/// band -- the idle band unless the application says otherwise -- and neither the audited
/// message's number nor the ambient handling may decide it.
/// </summary>
/// <remarks>
/// The band moved from background to idle when the idle band arrived: audit is most of what a bulk
/// load generates and is read days later if at all, so it is the clearest case of work to withhold
/// while a person is waiting. What these cases are really about did not change, which is that the
/// emitter builds its envelope by hand and must not let the audited work's urgency leak into it.
/// </remarks>
/// <docs>fundamentals/messaging/message-priority#the-idle-band</docs>
/// <code-under-test>src/Whizbang.Core/SystemEvents/SystemEventEmitter.cs</code-under-test>
[Category("SystemEvents")]
public class SystemEventEmitterPriorityTests {
  private sealed record _auditedEvent : IEvent { public string Name { get; init; } = ""; }
  private sealed record _auditedCommand(string Name);

  [Test]
  public async Task EmitEventAudited_TheAuditEnvelopeIsOnTheAuditBandAsync() {
    var (emitter, store) = _build();
    var source = _source();
    source.Priority = WorkPriority.INTERACTIVE;

    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, source);

    await Assert.That(store.Envelopes.Single().Priority).IsEqualTo(WorkPriority.IDLE)
      .Because("the audited event was interactive; its audit record is work nobody waits for and must not ride that number");
  }

  [Test]
  public async Task EmitCommandAudited_TheAuditEnvelopeIsOnTheAuditBandAsync() {
    var (emitter, store) = _build();

    await emitter.EmitCommandAuditedAsync(new _auditedCommand("c"), "ok", "TestReceptor", context: null);

    await Assert.That(store.Envelopes.Single().Priority).IsEqualTo(WorkPriority.IDLE)
      .Because("command audits share the emit path with event audits; one construction site, one band");
  }

  /// <summary>
  /// The band is configuration, not a constant: an application that needs its audit trail promptly
  /// says so, and the emitter follows.
  /// </summary>
  /// <remarks>
  /// This is the escape hatch for a deployment with something downstream reading the trail on a
  /// deadline. Without it, defaulting audit to a withheld band would be a decision made for every
  /// consumer of the framework with no way back.
  /// </remarks>
  [Test]
  public async Task WhenTheApplicationChoosesAnotherBand_TheEmitterFollowsItAsync() {
    var options = Options.Create(new SystemEventOptions { AuditPriority = WorkPriority.BACKGROUND }
      .EnableEventAudit().EnableCommandAudit());
    var store = new _captureStore();
    var emitter = new SystemEventEmitter(options, store, new Whizbang.Core.Observability.ServiceInstanceProvider());

    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, _source());

    await Assert.That(store.Envelopes.Single().Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the default is idle, but the application overrode it and nothing may quietly ignore that");
  }

  [Test]
  public async Task EmitEventAudited_InsideAnInteractiveHandling_StaysOnTheAuditBandAsync() {
    var (emitter, store) = _build();

    using (PriorityContext.Enter(WorkPriority.INTERACTIVE)) {
      await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, _source());
    }

    await Assert.That(store.Envelopes.Single().Priority).IsEqualTo(WorkPriority.IDLE)
      .Because("the emitter does not dispatch, so inheritance never runs for it; the ambient parent must not leak into a hand-built envelope");
  }

  private static (SystemEventEmitter Emitter, _captureStore Store) _build() {
    var store = new _captureStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit().EnableCommandAudit());
    return (new SystemEventEmitter(options, store, new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()), logger: NullLogger<SystemEventEmitter>.Instance), store);
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
