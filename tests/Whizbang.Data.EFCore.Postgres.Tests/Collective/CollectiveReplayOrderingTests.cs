// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

#pragma warning disable CA1707

using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// A replay folds a key's collectives in the order the live sink applied them, commit order, even where their ids run
/// the other way (#963). Everything else keeps its place: a replay still interleaves by message id, and the collectives
/// sharing a key only change places among themselves, in the slots their ids gave them.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
[Category("Shard1")]
public class CollectiveReplayOrderingTests {
  private static readonly TenantCollectiveScope _scope = new("tenant-a");
  private static readonly Guid _keyStream = CollectiveOrdering.StreamIdFor(_scope, "family-7");

  // Minted in this order; committed in the opposite order for the two collectives.
  private static readonly Guid _perStreamId = Guid.Parse("00000000-0000-7000-8000-000000000001");
  private static readonly Guid _laterCommitId = Guid.Parse("00000000-0000-7000-8000-000000000002");
  private static readonly Guid _earlierCommitId = Guid.Parse("00000000-0000-7000-8000-000000000003");
  private static readonly Guid _unkeyedId = Guid.Parse("00000000-0000-7000-8000-000000000004");

  [Test]
  public async Task Interleave_AKeysCollectives_FoldInCommitOrder_WhenIdsRunBackwardAsync() {
    var (applier, _) = _applier(
      records: [
        _record(_keyStream, _earlierCommitId, typeof(FlipCollectiveEvent), commitSequence: 10),
        _record(_keyStream, _laterCommitId, typeof(FlipCollectiveEvent), commitSequence: 11),
      ],
      streams: new() { [_keyStream] = [_envelope(_laterCommitId, "b"), _envelope(_earlierCommitId, "a")] });
    var streamEvents = _perStream();

    var merged = await _interleaveAsync(applier, streamEvents);

    await Assert.That(merged.Select(e => e.MessageId.Value)).IsEquivalentTo(
      [_perStreamId, _earlierCommitId, _laterCommitId], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("The per-stream event keeps its place; the two collectives sharing a key swap into commit order.");
  }

  [Test]
  public async Task Fold_TwoCollectivesSharingAKey_EndsAtTheLaterCommitsResultAsync() {
    var (applier, _) = _applier(
      records: [
        _record(_keyStream, _earlierCommitId, typeof(FlipCollectiveEvent), commitSequence: 10),
        _record(_keyStream, _laterCommitId, typeof(FlipCollectiveEvent), commitSequence: 11),
      ],
      streams: new() { [_keyStream] = [_envelope(_laterCommitId, "b"), _envelope(_earlierCommitId, "a")] });
    var streamEvents = _perStream();

    var merged = await _interleaveAsync(applier, streamEvents);
    object model = new FlipModel();
    foreach (var envelope in merged.Where(e => e.Payload is ICollectiveEvent)) {
      model = applier.ApplyInMemory(typeof(FlipModel), model, Guid.CreateVersion7(), envelope.Payload);
    }

    await Assert.That(((FlipModel)model).Chosen).IsEqualTo("b")
      .Because("The live sink applied the key's collectives in commit order and ended at the later commit; the "
        + "rebuild has to end there too.");
  }

  [Test]
  public async Task Interleave_AnUnstampedCollective_FoldsAfterTheStampedOnesAsync() {
    var (applier, _) = _applier(
      records: [
        _record(_keyStream, _laterCommitId, typeof(FlipCollectiveEvent), commitSequence: null),
        _record(_keyStream, _earlierCommitId, typeof(FlipCollectiveEvent), commitSequence: 10),
      ],
      streams: new() { [_keyStream] = [_envelope(_laterCommitId, "b"), _envelope(_earlierCommitId, "a")] });

    var merged = await _interleaveAsync(applier, _perStream());

    await Assert.That(merged.Select(e => e.MessageId.Value)).IsEquivalentTo(
      [_perStreamId, _earlierCommitId, _laterCommitId], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("The sink queue orders an unstamped row last; the replay must do the same.");
  }

  [Test]
  public async Task Interleave_TwoUnstampedCollectives_FoldInEventIdOrderAsync() {
    var (applier, _) = _applier(
      records: [
        _record(_keyStream, _earlierCommitId, typeof(FlipCollectiveEvent), commitSequence: null),
        _record(_keyStream, _laterCommitId, typeof(FlipCollectiveEvent), commitSequence: null),
      ],
      streams: new() { [_keyStream] = [_envelope(_earlierCommitId, "a"), _envelope(_laterCommitId, "b")] });

    var merged = await _interleaveAsync(applier, _perStream());

    await Assert.That(merged.Select(e => e.MessageId.Value)).IsEquivalentTo(
      [_perStreamId, _laterCommitId, _earlierCommitId], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("With no commit sequence to go by, the sink queue breaks the tie on event_id, and so does the replay.");
  }

  [Test]
  public async Task Interleave_UnkeyedCollectives_KeepTheirMessageIdPlacesAsync() {
    var otherStream = Guid.CreateVersion7();
    var (applier, _) = _applier(
      records: [
        _record(_keyStream, _laterCommitId, typeof(FlipCollectiveEvent), commitSequence: 11),
        _record(otherStream, _unkeyedId, typeof(FlipCollectiveEvent), commitSequence: 5),
      ],
      streams: new() {
        [_keyStream] = [_envelope(_laterCommitId, "b")],
        [otherStream] = [_envelope(_unkeyedId, "u")],
      });

    var merged = await _interleaveAsync(applier, _perStream());

    await Assert.That(merged.Select(e => e.MessageId.Value)).IsEquivalentTo(
      [_perStreamId, _laterCommitId, _unkeyedId], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("Collectives on different streams are not ordered against each other; they keep the message-id order.");
  }

  // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

  private static async Task<IReadOnlyList<MessageEnvelope<IEvent>>> _interleaveAsync(
      CollectiveReplayApplier applier, IReadOnlyList<MessageEnvelope<IEvent>> streamEvents) {
    ProcessingModeAccessor.Current = ProcessingMode.Rebuild;
    try {
      return await applier.InterleaveForReplayAsync(typeof(FlipModel), streamEvents, CancellationToken.None);
    } finally {
      ProcessingModeAccessor.Current = null;
    }
  }

  private static (CollectiveReplayApplier Applier, StreamStore Store) _applier(
      List<EventStoreRecord> records, Dictionary<Guid, List<MessageEnvelope<IEvent>>> streams) {
    var entry = new CollectiveApplyEntry(
      ModelType: typeof(FlipModel),
      EventType: typeof(FlipCollectiveEvent),
      HandlerType: typeof(FlipHandler),
      MethodName: "Apply",
      ScopeHandling: default,
      SpecKind: default,
      Invoker: (_, evt, _) => ((FlipCollectiveEvent)evt).Chosen);
    var store = new StreamStore(streams);
    var services = new ServiceCollection().AddSingleton<FlipHandler>().BuildServiceProvider();
    var applier = new CollectiveReplayApplier([entry], services, store, new RecordQuery(records), [new FlipExecutor()]);
    return (applier, store);
  }

  private static EventStoreRecord _record(Guid streamId, Guid eventId, Type eventType, long? commitSequence) => new() {
    Id = eventId,
    StreamId = streamId,
    AggregateId = streamId,
    AggregateType = "collective",
    Version = 1,
    EventType = TypeNameFormatter.Format(eventType),
    EventData = null,
    Metadata = null,
    Scope = new PerspectiveScope { TenantId = _scope.TenantId },
    CommitSequence = commitSequence,
  };

  private static MessageEnvelope<IEvent> _envelope(Guid eventId, string chosen) => new(
    new MessageId(eventId), new FlipCollectiveEvent { Scope = _scope, Chosen = chosen }, []);

  // The row's own event, carrying the tenant the replay scopes its collective lookup to.
  private static List<MessageEnvelope<IEvent>> _perStream() => [
    new(new MessageId(_perStreamId), new ProbeEvent(), [
      new MessageHop {
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        Scope = Whizbang.Core.Security.ScopeDelta.FromSecurityContext(new Whizbang.Core.Observability.SecurityContext { TenantId = _scope.TenantId }),
      },
    ]),
  ];

  private sealed record FlipModel {
    public string Chosen { get; init; } = "";
  }

  private sealed record FlipCollectiveEvent : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
    public string Chosen { get; init; } = "";
  }

  private sealed record ProbeEvent : IEvent;

  private sealed class FlipHandler;

  // The "spec" the invoker hands back is the choice itself; applying it sets the model to it.
  private sealed class FlipExecutor : ICollectiveInMemoryExecutor {
    public Type ModelType => typeof(FlipModel);
    public object ApplyToRow(object spec, object currentModel, Guid streamId) =>
      (FlipModel)currentModel with { Chosen = (string)spec };
  }

  private sealed class RecordQuery(IReadOnlyList<EventStoreRecord> records) : IEventStoreQuery {
    public IQueryable<EventStoreRecord> Query => records.AsQueryable();
    public IQueryable<EventStoreRecord> GetStreamEvents(Guid streamId) => Query.Where(r => r.StreamId == streamId);
    public IQueryable<EventStoreRecord> GetEventsByType(string eventType) => Query.Where(r => r.EventType == eventType);
  }

  /// <summary>Reads each stream in the order given, as a store that reads by event id would.</summary>
  private sealed class StreamStore(Dictionary<Guid, List<MessageEnvelope<IEvent>>> streams) : IEventStore {
    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
        Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      foreach (var envelope in streams.GetValueOrDefault(streamId) ?? []) {
        yield return envelope;
      }
    }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      System.Linq.AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      System.Linq.AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<IEvent>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(0L);
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
  }
}
