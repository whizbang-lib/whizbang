// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// Coverage for the <see cref="CollectiveReplayApplier"/> early-return branches and the
/// <c>_resolveExecutor</c> / <c>_toListAsync</c> private helpers that
/// <c>CollectiveReplayRebuildIntegrationTests</c> (the live-DB happy path) and
/// <c>Whizbang.Data.Dapper.Postgres.Tests.CollectiveReplayApplierGuardTests</c> (the
/// <c>ApplyInMemory</c> guards) don't reach. No database anywhere in this file — every case here is
/// either an ambient-mode / empty-registration short circuit, or an in-memory
/// <see cref="IQueryable{T}"/> standing in for the event store query.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
[Category("Shard1")]
public class CollectiveReplayApplierCoverageTests {

  private sealed record ProbeModel {
    public Guid Id { get; init; }
    public int Applied { get; init; }
  }

  private sealed record OtherModel {
  }

  private sealed record ProbeCollectiveEvent : ICollectiveEvent {
    public CollectiveScope Scope { get; init; } = new TenantCollectiveScope("tenant-a");
  }

  private sealed record ProbeEvent : IEvent;

  private sealed class ProbeHandler;

  private sealed class OtherModelInMemoryExecutor : ICollectiveInMemoryExecutor {
    public Type ModelType => typeof(OtherModel);
    public object ApplyToRow(object spec, object currentModel, Guid streamId) => currentModel;
  }

  private sealed class RecordingExecutor : ICollectiveInMemoryExecutor {
    public Type ModelType => typeof(ProbeModel);
    public int Applications { get; private set; }

    public object ApplyToRow(object spec, object currentModel, Guid streamId) {
      Applications++;
      var model = (ProbeModel)currentModel;
      return model with { Applied = model.Applied + 1 };
    }
  }

  /// <summary>Inert event store, unless a case hands it collective streams to read back.</summary>
  private sealed class NoOpEventStore(IReadOnlyDictionary<Guid, List<MessageEnvelope<IEvent>>>? streams = null) : IEventStore {
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message,
      CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
      Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      System.Linq.AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
      Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      System.Linq.AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
      Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken = default) =>
      (streams?.GetValueOrDefault(streamId) ?? []).ToAsyncEnumerable();

    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(
      Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<TMessage>>());

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
      Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<IEvent>>());

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(0L);

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(
      IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
  }

  /// <summary>Event-store query over whatever in-memory rows the case supplies (empty by default).</summary>
  private sealed class FakeEventStoreQuery(IReadOnlyList<EventStoreRecord>? records = null) : IEventStoreQuery {
    public IQueryable<EventStoreRecord> Query => (records ?? []).AsQueryable();
    public IQueryable<EventStoreRecord> GetStreamEvents(Guid streamId) => Query;
    public IQueryable<EventStoreRecord> GetEventsByType(string eventType) => Query;
  }

  private static List<MessageEnvelope<IEvent>> _oneStreamEvent() =>
    [new MessageEnvelope<IEvent>(MessageId.New(), new ProbeEvent(), [])];

  // ============================================================

  // A rebuild that folds collectives when it shouldn't would double-apply a mutation the live
  // drain already made via the set-based SQL path. Outside Rebuild/Replay mode the stream events
  // must come back completely untouched — not merely equal, but the same list — proving no folding
  // work (entry lookup, event-store query) ran at all.
  [Test]
  public async Task InterleaveForReplayAsync_NotInRebuildOrReplayMode_ReturnsStreamEventsUntouchedAsync() {
    var applier = new CollectiveReplayApplier(
      [], new ServiceCollection().BuildServiceProvider(),
      new NoOpEventStore(), new FakeEventStoreQuery(), []);
    var streamEvents = _oneStreamEvent();

    var result = await applier.InterleaveForReplayAsync(typeof(ProbeModel), streamEvents, CancellationToken.None);

    await Assert.That(result).IsSameReferenceAs(streamEvents)
      .Because("ProcessingModeAccessor.Current defaults to null (live processing) in this test, so "
             + "the ambient-mode guard must return the original list untouched before ever looking "
             + "at the entry table");
  }

  // A model with no [CollectiveApplyFor] entries has no collective events to fold in the first
  // place — the entry-lookup short circuit has to recognize that and hand the stream back as-is
  // rather than running an event-store query that could only ever come back empty.
  [Test]
  public async Task InterleaveForReplayAsync_NoEntriesRegisteredForTheModel_ReturnsStreamEventsUntouchedAsync() {
    var applier = new CollectiveReplayApplier(
      [], new ServiceCollection().BuildServiceProvider(),
      new NoOpEventStore(), new FakeEventStoreQuery(), []);
    var streamEvents = _oneStreamEvent();

    ProcessingModeAccessor.Current = ProcessingMode.Rebuild;
    try {
      var result = await applier.InterleaveForReplayAsync(typeof(ProbeModel), streamEvents, CancellationToken.None);

      await Assert.That(result).IsSameReferenceAs(streamEvents)
        .Because("no registered entry targets this model, so there is no collective event type to "
               + "look for and the stream must come back exactly as it went in");
    } finally {
      ProcessingModeAccessor.Current = null;
    }
  }

  // The model has a registered collective apply, but this tenant's event store holds none of that
  // event type — the query legitimately comes back empty. That must still hand the stream back
  // untouched rather than merging in an empty list's worth of nothing incorrectly, and it exercises
  // the in-memory (non-IAsyncEnumerable) materialization path every Dapper-style query provider uses.
  [Test]
  public async Task InterleaveForReplayAsync_NoMatchingCollectiveStreamsForTheTenant_ReturnsStreamEventsUntouchedAsync() {
    var entry = new CollectiveApplyEntry(
      ModelType: typeof(ProbeModel),
      EventType: typeof(ProbeCollectiveEvent),
      HandlerType: typeof(ProbeHandler),
      MethodName: "Apply",
      ScopeHandling: default,
      SpecKind: default,
      Invoker: (_, _, _) => new object());
    var applier = new CollectiveReplayApplier(
      [entry], new ServiceCollection().BuildServiceProvider(),
      new NoOpEventStore(), new FakeEventStoreQuery(records: []), executors: []);
    var streamEvents = _oneStreamEvent();

    ProcessingModeAccessor.Current = ProcessingMode.Rebuild;
    try {
      var result = await applier.InterleaveForReplayAsync(typeof(ProbeModel), streamEvents, CancellationToken.None);

      await Assert.That(result).IsSameReferenceAs(streamEvents)
        .Because("an entry is registered for this model, but the event store has zero matching "
               + "collective streams, so folding must still be a no-op rather than fabricating a merge");
    } finally {
      ProcessingModeAccessor.Current = null;
    }
  }

  // _resolveExecutor walks every registered executor looking for the ModelType match. A model
  // registered after some other model's executor still has to resolve correctly — if the search
  // stopped at (or was thrown off by) the first mismatch, a rebuild would apply the wrong model's
  // executor, or fail, for every model that doesn't happen to be first in the registration list.
  [Test]
  public async Task ApplyInMemory_SkipsNonMatchingExecutorsBeforeFindingTheRightOneAsync() {
    var recordingExecutor = new RecordingExecutor();
    var entry = new CollectiveApplyEntry(
      ModelType: typeof(ProbeModel),
      EventType: typeof(ProbeCollectiveEvent),
      HandlerType: typeof(ProbeHandler),
      MethodName: "Apply",
      ScopeHandling: default,
      SpecKind: default,
      Invoker: (_, _, _) => new object());
    var services = new ServiceCollection().AddSingleton<ProbeHandler>().BuildServiceProvider();
    var applier = new CollectiveReplayApplier(
      [entry], services, new NoOpEventStore(), new FakeEventStoreQuery(),
      [new OtherModelInMemoryExecutor(), recordingExecutor]);
    var current = new ProbeModel { Id = Guid.CreateVersion7() };

    var result = (ProbeModel)applier.ApplyInMemory(
      typeof(ProbeModel), current, current.Id, new ProbeCollectiveEvent());

    await Assert.That(recordingExecutor.Applications).IsEqualTo(1)
      .Because("the second executor's ModelType matches, and the mismatched first one must not "
             + "block or short-circuit the search");
    await Assert.That(result.Applied).IsEqualTo(1);
  }
  // #1956: a rewind folds the row's collectives committed after where it starts. It runs in the live processing mode, so
  // the merge must not wait for a rebuild, and the collectives committed before the start are already in its snapshot.
  [Test]
  public async Task InterleaveAfterAsync_OutsideARebuild_MergesTheCollectivesAfterTheStart_OnlyAsync() {
    var (applier, before, after) = _twoCollectives();
    var streamEvent = new MessageEnvelope<IEvent>(MessageId.New(), new ProbeEvent(), []) { LocalCommitSequence = 10 };

    var merged = await applier.InterleaveAfterAsync(
      typeof(ProbeModel), [streamEvent], new CollectiveReplayStart("tenant-a", Guid.CreateVersion7(), AfterCommitSequence: 7),
      CancellationToken.None);

    await Assert.That(merged.Select(e => e.MessageId)).IsEquivalentTo([streamEvent.MessageId, after.MessageId], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the collective committed at 5 is before the start at 7, and the one at 15 goes after the stream event at 10");
    await Assert.That(merged.Select(e => e.MessageId)).DoesNotContain(before.MessageId);
  }

  [Test]
  public async Task InterleaveAfterAsync_WithNoStreamEvents_ReturnsTheCollectivesAloneAsync() {
    var (applier, before, after) = _twoCollectives();

    var merged = await applier.InterleaveAfterAsync(typeof(ProbeModel), [], new CollectiveReplayStart("tenant-a"), CancellationToken.None);

    await Assert.That(merged.Select(e => e.MessageId)).IsEquivalentTo([before.MessageId, after.MessageId], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a replay whose only news since its snapshot is a collective still folds it");
  }

  [Test]
  public async Task InterleaveAfterAsync_RefusesNullArgumentsAsync() {
    var (applier, _, _) = _twoCollectives();
    var start = new CollectiveReplayStart("tenant-a");

    await Assert.That(() => applier.InterleaveAfterAsync(null!, [], start, CancellationToken.None)).ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => applier.InterleaveAfterAsync(typeof(ProbeModel), null!, start, CancellationToken.None)).ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => applier.InterleaveAfterAsync(typeof(ProbeModel), [], null!, CancellationToken.None)).ThrowsExactly<ArgumentNullException>();
  }

  // One registered collective for ProbeModel in tenant-a, twice: committed at 5 and at 15, each its own stream.
  private static (CollectiveReplayApplier Applier, MessageEnvelope<IEvent> Before, MessageEnvelope<IEvent> After) _twoCollectives() {
    var before = new MessageEnvelope<IEvent>(MessageId.New(), new ProbeCollectiveEvent(), []) { LocalCommitSequence = 5 };
    var after = new MessageEnvelope<IEvent>(MessageId.New(), new ProbeCollectiveEvent(), []) { LocalCommitSequence = 15 };
    var streams = new Dictionary<Guid, List<MessageEnvelope<IEvent>>> {
      [Guid.CreateVersion7()] = [before],
      [Guid.CreateVersion7()] = [after],
    };
    var records = streams.Select(s => new EventStoreRecord {
      Id = s.Value[0].MessageId.Value,
      StreamId = s.Key,
      AggregateId = s.Key,
      AggregateType = "Collective",
      Version = 1,
      EventType = TypeNameFormatter.Format(typeof(ProbeCollectiveEvent)),
      EventData = null,
      Metadata = null,
      Scope = new Whizbang.Core.Lenses.PerspectiveScope { TenantId = "tenant-a" },
      CommitSequence = s.Value[0].LocalCommitSequence,
    }).ToList();
    var entry = new CollectiveApplyEntry(
      ModelType: typeof(ProbeModel),
      EventType: typeof(ProbeCollectiveEvent),
      HandlerType: typeof(ProbeHandler),
      MethodName: "Apply",
      ScopeHandling: default,
      SpecKind: default,
      Invoker: (_, _, _) => new object());
    var applier = new CollectiveReplayApplier(
      [entry], new ServiceCollection().BuildServiceProvider(), new NoOpEventStore(streams), new FakeEventStoreQuery(records), []);
    return (applier, before, after);
  }

  // A blue-green rebuild reads these names to notice a collective that committed while it ran, so the lookup
  // must name exactly this model's collective event types: once each, with or without the global:: prefix the
  // perspective registry may record, and none of another model's.
  [Test]
  public async Task CollectiveEventTypeNamesFor_NamesTheModelsCollectivesOnlyAsync() {
    static CollectiveApplyEntry entry(Type model, Type evt) => new(
      ModelType: model, EventType: evt, HandlerType: typeof(ProbeHandler), MethodName: "Apply",
      ScopeHandling: default, SpecKind: default, Invoker: (_, _, _) => new object());
    var applier = new CollectiveReplayApplier(
      [entry(typeof(ProbeModel), typeof(ProbeCollectiveEvent)),
       entry(typeof(ProbeModel), typeof(ProbeCollectiveEvent)),
       entry(typeof(OtherModel), typeof(ProbeEvent))],
      new ServiceCollection().BuildServiceProvider(), new NoOpEventStore(), new FakeEventStoreQuery(), []);
    var modelName = typeof(ProbeModel).FullName!.Replace('+', '.');
    var expected = new[] { TypeNameFormatter.Format(typeof(ProbeCollectiveEvent)) };

    await Assert.That(applier.CollectiveEventTypeNamesFor(modelName)).IsEquivalentTo(expected);
    await Assert.That(applier.CollectiveEventTypeNamesFor("global::" + modelName)).IsEquivalentTo(expected);
    await Assert.That(applier.CollectiveEventTypeNamesFor("Unknown.Model")).IsEmpty();
    await Assert.That(() => applier.CollectiveEventTypeNamesFor(null!)).Throws<ArgumentNullException>();
  }

  // An applier written against the interface before the lookup existed names no collectives, so a blue-green
  // rebuild over it simply has nothing to watch for.
  [Test]
  public async Task CollectiveEventTypeNamesFor_DefaultsToNoneAsync() {
    ICollectiveReplayApplier minimal = new MinimalApplier();

    await Assert.That(minimal.CollectiveEventTypeNamesFor("Any.Model")).IsEmpty();
  }

  private sealed class MinimalApplier : ICollectiveReplayApplier {
    public Task<IReadOnlyList<MessageEnvelope<IEvent>>> InterleaveForReplayAsync(
        Type modelType, IReadOnlyList<MessageEnvelope<IEvent>> streamEvents, CancellationToken cancellationToken) =>
      Task.FromResult(streamEvents);

    public object ApplyInMemory(Type modelType, object currentModel, Guid streamId, IEvent collectiveEvent) => currentModel;
  }
}
