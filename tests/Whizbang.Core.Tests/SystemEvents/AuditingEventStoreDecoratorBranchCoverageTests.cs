// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// Branch coverage for <see cref="AuditingEventStoreDecorator"/>'s constructor: a decorator built
/// without a logger still queues the audit record when the payload cannot be serialized, which
/// is the one path that logs.
/// </summary>
/// <code-under-test>src/Whizbang.Core/SystemEvents/AuditingEventStoreDecorator.cs</code-under-test>
[Category("SystemEvents")]
public class AuditingEventStoreDecoratorBranchCoverageTests {

  private sealed record UnregisteredAuditedEvent(string Name) : IEvent;

  /// <summary>
  /// The unserializable payload is the path that warns. With no logger supplied the decorator
  /// must still append the event and queue its audit record rather than lose either.
  /// </summary>
  [Test]
  public async Task AppendAsync_NullLoggerAndUnserializablePayload_StillQueuesTheAuditRecordAsync() {
    var inner = new RecordingEventStore();
    var channel = new RecordingOutboxChannel();
    var decorator = new AuditingEventStoreDecorator(
      inner, channel, Options.Create(new SystemEventOptions().EnableEventAudit()),
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      NoOpinionAuditDecisionHook.Instance, logger: null!);
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

    await decorator.AppendAsync(Guid.CreateVersion7(), envelope, CancellationToken.None);

    await Assert.That(inner.AppendCount).IsEqualTo(1);
    await Assert.That(channel.QueuedMessages.Count).IsEqualTo(1)
      .Because("a missing logger must never cost the audit record itself");
    await Assert.That(channel.QueuedMessages[0].Destination).IsEqualTo(AuditingEventStoreDecorator.AUDIT_TOPIC_DESTINATION);
  }

  private sealed class RecordingEventStore : IEventStore {
    public int AppendCount { get; private set; }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
      AppendCount++;
      return Task.CompletedTask;
    }

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default)
        where TMessage : notnull {
      AppendCount++;
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

  private sealed class RecordingOutboxChannel : IDeferredOutboxChannel {
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
