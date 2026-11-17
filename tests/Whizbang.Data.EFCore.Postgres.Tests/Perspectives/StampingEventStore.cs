// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The in-memory store with the commit sequences a settled database answers with: the checkpoint reads them through
/// <c>GetCommitSequenceAsync</c> and the replay read carries them on each envelope. It also answers the history probe
/// a missing row is checked with, from the stream it holds, which the in-memory store leaves unanswered.
/// </summary>
internal sealed class StampingEventStore(InMemoryEventStore inner, IReadOnlyDictionary<Guid, long?> stamps) : IEventStore {
  public long? StampOf(Guid eventId) => stamps.TryGetValue(eventId, out var seq) ? seq : null;
  public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) =>
    inner.AppendAsync(streamId, envelope, cancellationToken);
  public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull =>
    inner.AppendAsync(streamId, message, cancellationToken);
  public Task<long?> GetCommitSequenceAsync(Guid eventId, CancellationToken cancellationToken = default) =>
    Task.FromResult(StampOf(eventId));
  public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
    inner.ReadAsync<TMessage>(streamId, fromSequence, cancellationToken);
  public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
    inner.ReadAsync<TMessage>(streamId, fromEventId, cancellationToken);
  public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
      Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes,
      [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
    await foreach (var envelope in inner.ReadPolymorphicAsync(streamId, fromEventId, eventTypes, cancellationToken)) {
      envelope.LocalCommitSequence = StampOf(envelope.MessageId.Value);
      yield return envelope;
    }
  }
  public async Task<bool> HasStreamEventsBeforeAsync(
      Guid streamId, Guid beforeEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
    await foreach (var envelope in inner.ReadPolymorphicAsync(streamId, null, eventTypes, cancellationToken)) {
      if (string.CompareOrdinal(envelope.MessageId.Value.ToString("D"), beforeEventId.ToString("D")) < 0) {
        return true;
      }
    }
    return false;
  }
  public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
    inner.GetEventsBetweenAsync<TMessage>(streamId, afterEventId, upToEventId, cancellationToken);
  public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
    inner.GetEventsBetweenPolymorphicAsync(streamId, afterEventId, upToEventId, eventTypes, cancellationToken);
  public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
    inner.GetLastSequenceAsync(streamId, cancellationToken);
  public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
    inner.DeserializeStreamEvents(streamEvents, eventTypes);
}
