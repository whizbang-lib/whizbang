#pragma warning disable CA1707

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Covers <see cref="IEventStore.HasStreamEventsBeforeAsync"/>'s default interface implementation
/// (returns <c>false</c>) — the sibling <c>IEventStoreDefaultMethodTests</c> exercises the
/// <c>AppendAndWaitAsync</c> defaults through its own <c>MinimalEventStore</c>, but never probes
/// this one.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IEventStore.cs</code-under-test>
[Category("EventStore")]
public class IEventStoreCoverageTests {

  [Test]
  public async Task HasStreamEventsBeforeAsync_NotOverridden_ReturnsFalseAsync() {
    // A store that predates row-retention support (or an in-memory test store) does not override
    // this probe. Its default MUST be false: "no resurrection" is the safe behavior for a store
    // that cannot answer the question, so the runner keeps applying the batch onto the current
    // model instead of assuming history it can't confirm exists.
    IEventStore store = new _minimalEventStore();

    var result = await store.HasStreamEventsBeforeAsync(Guid.NewGuid(), Guid.NewGuid());

    await Assert.That(result).IsFalse();
  }

  private sealed class _minimalEventStore : IEventStore {
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default)
        where TMessage : notnull =>
      Task.CompletedTask;

    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
        Guid streamId, long fromSequence, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }

    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
        Guid streamId, Guid? fromEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }

    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
        Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }

    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(
        Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<TMessage>>());

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
        Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes,
        CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<IEvent>>());

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(-1L);

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
  }
}
