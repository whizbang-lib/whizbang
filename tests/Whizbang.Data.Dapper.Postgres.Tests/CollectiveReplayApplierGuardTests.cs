using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The guards on the in-memory collective replay path.
/// </summary>
/// <remarks>
/// A collective apply normally runs as SQL against many rows at once. During a perspective
/// rebuild there is no table to run it against yet, so the same spec is replayed in memory
/// instead — and the two paths have to agree, because the rebuild's whole purpose is to
/// reproduce what the live path produced.
///
/// <para>
/// Each guard here fails loudly rather than quietly producing a different answer. A rebuild that
/// silently skipped a model, or answered a cross-perspective query with stale data, would leave a
/// perspective that looks complete and is wrong — which is far harder to notice than a rebuild
/// that stopped.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Collective/CollectiveReplayApplier.cs</code-under-test>
[Category("Unit")]
public class CollectiveReplayApplierGuardTests {

  private sealed record ProbeModel {
    public Guid Id { get; init; }
    public int Applied { get; init; }
  }

  private sealed record OtherModel {
    public Guid Id { get; init; }
  }

  private sealed record ProbeCollectiveEvent : ICollectiveEvent {
    public Guid Id { get; init; }
    public CollectiveScope Scope { get; init; } = new TenantCollectiveScope("tenant-a");
  }

  private sealed record PlainEvent([property: StreamId] Guid Id) : IEvent;

  private sealed class ProbeHandler { }

  private sealed class RecordingExecutor : ICollectiveInMemoryExecutor {
    public Type ModelType => typeof(ProbeModel);
    public int Applications { get; private set; }

    public object ApplyToRow(object spec, object currentModel, Guid streamId) {
      Applications++;
      var model = (ProbeModel)currentModel;
      return model with { Applied = model.Applied + 1 };
    }
  }

  /// <summary>Captures the query handed to the apply so the replay guard can be exercised.</summary>
  private sealed class QueryCapturingHandler {
    public ICollectiveQuery? Seen { get; private set; }
    public object Invoke(ICollectiveEvent evt, ICollectiveQuery query) {
      Seen = query;
      return new object();
    }
  }

  private static CollectiveReplayApplier _applier(
      IReadOnlyList<CollectiveApplyEntry> entries,
      IReadOnlyList<ICollectiveInMemoryExecutor> executors,
      IServiceProvider? services = null) =>
    new(entries,
        services ?? new ServiceCollection().AddSingleton<ProbeHandler>().BuildServiceProvider(),
        new NoOpEventStore(),
        new NoOpEventStoreQuery(),
        executors);

  private static CollectiveApplyEntry _entry(
      Func<object, ICollectiveEvent, ICollectiveQuery, object> invoker,
      Type? modelType = null,
      Type? eventType = null) =>
    new(
      ModelType: modelType ?? typeof(ProbeModel),
      EventType: eventType ?? typeof(ProbeCollectiveEvent),
      HandlerType: typeof(ProbeHandler),
      MethodName: "Apply",
      ScopeHandling: default,
      SpecKind: default,
      Invoker: invoker);

  // ============================================================

  [Test]
  public async Task ANonCollectiveEvent_LeavesTheModelUntouchedAsync() {
    // The replay walks whatever the event store holds for the stream, which includes ordinary
    // events. Treating one as collective would run an apply that was never meant for it.
    var executor = new RecordingExecutor();
    var applier = _applier([_entry((_, _, _) => new object())], [executor]);
    var current = new ProbeModel { Id = Guid.CreateVersion7() };

    var result = applier.ApplyInMemory(typeof(ProbeModel), current, current.Id, new PlainEvent(current.Id));

    await Assert.That(result).IsEqualTo(current);
    await Assert.That(executor.Applications).IsEqualTo(0);
  }

  [Test]
  public async Task WithNoExecutorForTheModel_TheFailureNamesTheMissingRegistrationAsync() {
    // A model whose executor was never registered cannot be rebuilt. Failing quietly here would
    // produce a perspective that looks complete and is missing every collective effect — so the
    // message has to name the call that fixes it.
    var applier = _applier([_entry((_, _, _) => new object())], executors: []);
    var current = new ProbeModel { Id = Guid.CreateVersion7() };

    await Assert.That(() => applier.ApplyInMemory(
        typeof(ProbeModel), current, current.Id, new ProbeCollectiveEvent()))
      .Throws<InvalidOperationException>()
      .WithMessageContaining("AddCollectiveExecutor");
  }

  [Test]
  public async Task TheExecutorIsMatchedByModelTypeAsync() {
    // Several models each register their own executor. Picking the wrong one applies the spec
    // to a row shape it was not written for.
    var probeExecutor = new RecordingExecutor();
    var applier = _applier([_entry((_, _, _) => new object())], [probeExecutor]);
    var current = new ProbeModel { Id = Guid.CreateVersion7() };

    _ = applier.ApplyInMemory(typeof(ProbeModel), current, current.Id, new ProbeCollectiveEvent());

    await Assert.That(probeExecutor.Applications).IsEqualTo(1);
  }

  [Test]
  public async Task EntriesForOtherModelsOrEventsAreSkippedAsync() {
    // The entry table is global. Running an entry registered for a different model or event
    // would apply an unrelated spec during the rebuild.
    var executor = new RecordingExecutor();
    var applier = _applier([
      _entry((_, _, _) => new object(), modelType: typeof(OtherModel)),
      _entry((_, _, _) => new object(), eventType: typeof(PlainEvent)),
    ], [executor]);
    var current = new ProbeModel { Id = Guid.CreateVersion7() };

    _ = applier.ApplyInMemory(typeof(ProbeModel), current, current.Id, new ProbeCollectiveEvent());

    await Assert.That(executor.Applications).IsEqualTo(0);
  }

  [Test]
  public async Task EveryMatchingEntryRunsAsync() {
    // Mirrors the live dispatcher, which fans out to all matching entries — two applies on one
    // model is a supported shape, and replaying only the first would rebuild a different model.
    var executor = new RecordingExecutor();
    var applier = _applier([
      _entry((_, _, _) => new object()),
      _entry((_, _, _) => new object()),
    ], [executor]);
    var current = new ProbeModel { Id = Guid.CreateVersion7() };

    var result = (ProbeModel)applier.ApplyInMemory(
      typeof(ProbeModel), current, current.Id, new ProbeCollectiveEvent());

    await Assert.That(executor.Applications).IsEqualTo(2);
    await Assert.That(result.Applied).IsEqualTo(2)
      .Because("each entry folds into the model the previous one returned");
  }

  [Test]
  public async Task ACrossPerspectiveQueryDuringReplay_FailsLoudlyAsync() {
    // WHIZ106 should have failed the build for an apply that queries siblings, because such a
    // spec is not replayable — there is no sibling perspective to read during a rebuild. This is
    // the runtime backstop, and it has to throw rather than answer with stale or empty data:
    // a wrong rebuild that completes is far harder to notice than one that stops.
    ICollectiveQuery? captured = null;
    var applier = _applier([_entry((_, _, query) => { captured = query; return new object(); })],
      [new RecordingExecutor()]);
    var current = new ProbeModel { Id = Guid.CreateVersion7() };

    _ = applier.ApplyInMemory(typeof(ProbeModel), current, current.Id, new ProbeCollectiveEvent());

    await Assert.That(captured).IsNotNull();
    await Assert.That(() => captured!.Of<OtherModel>())
      .Throws<NotSupportedException>()
      .WithMessageContaining("WHIZ106");
  }

  [Test]
  public async Task Constructor_RejectsEachRequiredCollaboratorAsync() {
    var services = new ServiceCollection().BuildServiceProvider();
    IReadOnlyList<CollectiveApplyEntry> entries = [];
    IReadOnlyList<ICollectiveInMemoryExecutor> executors = [];
    var store = new NoOpEventStore();
    var query = new NoOpEventStoreQuery();

    await Assert.That(() => new CollectiveReplayApplier(null!, services, store, query, executors))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new CollectiveReplayApplier(entries, null!, store, query, executors))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new CollectiveReplayApplier(entries, services, null!, query, executors))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new CollectiveReplayApplier(entries, services, store, null!, executors))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new CollectiveReplayApplier(entries, services, store, query, null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ApplyInMemory_RejectsItsRequiredArgumentsAsync() {
    var applier = _applier([], [new RecordingExecutor()]);
    var current = new ProbeModel { Id = Guid.CreateVersion7() };

    await Assert.That(() => applier.ApplyInMemory(null!, current, current.Id, new ProbeCollectiveEvent()))
      .Throws<ArgumentNullException>();
    await Assert.That(() => applier.ApplyInMemory(typeof(ProbeModel), null!, current.Id, new ProbeCollectiveEvent()))
      .Throws<ArgumentNullException>();
    await Assert.That(() => applier.ApplyInMemory(typeof(ProbeModel), current, current.Id, null!))
      .Throws<ArgumentNullException>();
  }

  /// <summary>Inert event store: the guards under test never reach it.</summary>
  private sealed class NoOpEventStore : IEventStore {
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

  /// <summary>Inert event-store query over an empty set.</summary>
  private sealed class NoOpEventStoreQuery : IEventStoreQuery {
    public IQueryable<EventStoreRecord> Query => Array.Empty<EventStoreRecord>().AsQueryable();
    public IQueryable<EventStoreRecord> GetStreamEvents(Guid streamId) => Query;
    public IQueryable<EventStoreRecord> GetEventsByType(string eventType) => Query;
  }
}
