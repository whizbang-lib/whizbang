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

  private sealed record _probeModel {
    public Guid Id { get; init; }
    public int Applied { get; init; }
  }

  private sealed record _otherModel {
    public Guid Id { get; init; }
  }

  private sealed record _probeCollectiveEvent : ICollectiveEvent {
    public CollectiveScope Scope { get; init; } = new TenantCollectiveScope("tenant-a");
  }

  private sealed record _probeEvent : IEvent;

  private sealed class _probeHandler { }

  private sealed class _otherModelExecutor : ICollectiveInMemoryExecutor {
    public Type ModelType => typeof(_otherModel);
    public object ApplyToRow(object spec, object currentModel, Guid streamId) => currentModel;
  }

  private sealed class _recordingExecutor : ICollectiveInMemoryExecutor {
    public Type ModelType => typeof(_probeModel);
    public int Applications { get; private set; }

    public object ApplyToRow(object spec, object currentModel, Guid streamId) {
      Applications++;
      var model = (_probeModel)currentModel;
      return model with { Applied = model.Applied + 1 };
    }
  }

  /// <summary>Inert event store: none of the cases here read from it.</summary>
  private sealed class _noOpEventStore : IEventStore {
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
      System.Linq.AsyncEnumerable.Empty<MessageEnvelope<IEvent>>();

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
  private sealed class _fakeEventStoreQuery(IReadOnlyList<EventStoreRecord>? records = null) : IEventStoreQuery {
    public IQueryable<EventStoreRecord> Query => (records ?? []).AsQueryable();
    public IQueryable<EventStoreRecord> GetStreamEvents(Guid streamId) => Query;
    public IQueryable<EventStoreRecord> GetEventsByType(string eventType) => Query;
  }

  private static List<MessageEnvelope<IEvent>> _oneStreamEvent() =>
    [new MessageEnvelope<IEvent>(MessageId.New(), new _probeEvent(), [])];

  // ============================================================

  // A rebuild that folds collectives when it shouldn't would double-apply a mutation the live
  // drain already made via the set-based SQL path. Outside Rebuild/Replay mode the stream events
  // must come back completely untouched — not merely equal, but the same list — proving no folding
  // work (entry lookup, event-store query) ran at all.
  [Test]
  public async Task InterleaveForReplayAsync_NotInRebuildOrReplayMode_ReturnsStreamEventsUntouchedAsync() {
    var applier = new CollectiveReplayApplier(
      [], new ServiceCollection().BuildServiceProvider(),
      new _noOpEventStore(), new _fakeEventStoreQuery(), []);
    var streamEvents = _oneStreamEvent();

    var result = await applier.InterleaveForReplayAsync(typeof(_probeModel), streamEvents, CancellationToken.None);

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
      new _noOpEventStore(), new _fakeEventStoreQuery(), []);
    var streamEvents = _oneStreamEvent();

    ProcessingModeAccessor.Current = ProcessingMode.Rebuild;
    try {
      var result = await applier.InterleaveForReplayAsync(typeof(_probeModel), streamEvents, CancellationToken.None);

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
      ModelType: typeof(_probeModel),
      EventType: typeof(_probeCollectiveEvent),
      HandlerType: typeof(_probeHandler),
      MethodName: "Apply",
      ScopeHandling: default,
      SpecKind: default,
      Invoker: (_, _, _) => new object());
    var applier = new CollectiveReplayApplier(
      [entry], new ServiceCollection().BuildServiceProvider(),
      new _noOpEventStore(), new _fakeEventStoreQuery(records: []), executors: []);
    var streamEvents = _oneStreamEvent();

    ProcessingModeAccessor.Current = ProcessingMode.Rebuild;
    try {
      var result = await applier.InterleaveForReplayAsync(typeof(_probeModel), streamEvents, CancellationToken.None);

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
    var recordingExecutor = new _recordingExecutor();
    var entry = new CollectiveApplyEntry(
      ModelType: typeof(_probeModel),
      EventType: typeof(_probeCollectiveEvent),
      HandlerType: typeof(_probeHandler),
      MethodName: "Apply",
      ScopeHandling: default,
      SpecKind: default,
      Invoker: (_, _, _) => new object());
    var services = new ServiceCollection().AddSingleton<_probeHandler>().BuildServiceProvider();
    var applier = new CollectiveReplayApplier(
      [entry], services, new _noOpEventStore(), new _fakeEventStoreQuery(),
      [new _otherModelExecutor(), recordingExecutor]);
    var current = new _probeModel { Id = Guid.CreateVersion7() };

    var result = (_probeModel)applier.ApplyInMemory(
      typeof(_probeModel), current, current.Id, new _probeCollectiveEvent());

    await Assert.That(recordingExecutor.Applications).IsEqualTo(1)
      .Because("the second executor's ModelType matches, and the mismatched first one must not "
             + "block or short-circuit the search");
    await Assert.That(result.Applied).IsEqualTo(1);
  }
}
