// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Branch coverage for <see cref="SystemEventEmitter"/>'s constructor: an emitter built without a
/// logger still writes the audit record when the payload cannot be serialized, which is the one
/// path that logs.
/// </summary>
/// <code-under-test>src/Whizbang.Core/SystemEvents/SystemEventEmitter.cs</code-under-test>
[Category("SystemEvents")]
public class SystemEventEmitterBranchCoverageTests {

  private sealed record UnregisteredAuditedEvent(string Name);

  /// <summary>
  /// The unserializable payload is the path that warns. With no logger supplied the emitter
  /// must still append the audit record (with an empty body) rather than lose it.
  /// </summary>
  [Test]
  public async Task EmitEventAuditedAsync_NullLoggerAndUnserializablePayload_StillAppendsTheAuditRecordAsync() {
    var eventStore = new RecordingEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore,
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()), logger: null!);
    var envelope = new MessageEnvelope<UnregisteredAuditedEvent> {
      MessageId = MessageId.New(),
      Payload = new UnregisteredAuditedEvent("probe"),
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    await emitter.EmitEventAuditedAsync(Guid.CreateVersion7(), 3, envelope, CancellationToken.None);

    await Assert.That(eventStore.AppendedEnvelopes.Count).IsEqualTo(1)
      .Because("a missing logger must never cost the audit record itself");
    var audited = eventStore.AppendedEnvelopes[0] as MessageEnvelope<EventAudited>;
    await Assert.That(audited).IsNotNull();
    await Assert.That(audited!.Payload.OriginalBody.ValueKind).IsEqualTo(JsonValueKind.Object);
    await Assert.That(audited.Payload.OriginalBody.EnumerateObject().Any()).IsFalse()
      .Because("the payload type is in no registered context, so its body is persisted as {}");
  }

  private sealed class RecordingEventStore : IEventStore {
    public List<object> AppendedEnvelopes { get; } = [];

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
      AppendedEnvelopes.Add(envelope);
      return Task.CompletedTask;
    }

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default)
        where TMessage : notnull {
      AppendedEnvelopes.Add(message);
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

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
  }
}
