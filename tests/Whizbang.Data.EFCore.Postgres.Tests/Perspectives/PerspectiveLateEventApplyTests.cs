// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Every stored event of a stream is applied to a perspective that handles it, however the apply workers that carry
/// a stream's events interleave (#1608).
/// </summary>
/// <remarks>
/// <para>
/// Each stream holds Started then Completed. Two apply workers each carry one of them, and the one carrying Started
/// reads the row first but writes last: the worker carrying Completed reads, folds and commits in between. That is the
/// burst shape a fan-out produces, two workers on one row with overlapping transactions, and the framework's answer
/// to it is that the worker writing last re-reads the row it lost to and re-folds, rewinding the stream when its event
/// sits below the row's recorded position.
/// </para>
/// <para>
/// The interleaving is forced, never timed: the Started worker's store runs the whole Completed apply, committed on its
/// own context, after the Started worker's read and before its first write. The streams run concurrently, each with
/// its own pair of workers.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/versioned-apply</docs>
[Category("Integration")]
[Category("Shard4")]
public class PerspectiveLateEventApplyTests : EFCoreTestBase {
  private const string PERSPECTIVE = "item_progress";
  private const string TABLE = "wh_per_item_progress";
  private const int STREAM_COUNT = 16;

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task TheLaterEventCommitsFirst_EveryRowStillHoldsBothEventsAsync(bool stamped) {
    var eventStore = new InMemoryEventStore();
    var stamps = new Dictionary<Guid, long?>();
    var streams = new List<(Guid StreamId, MessageEnvelope<IEvent> Started, MessageEnvelope<IEvent> Completed)>();
    for (var i = 0; i < STREAM_COUNT; i++) {
      var streamId = Guid.CreateVersion7();
      // Ids in the order the stream holds the events, as one producer mints them.
      var started = _envelope(Guid.Parse($"019f0000-0000-7aaa-8000-{i:D12}"), new ItemProgressStartedEvent { StreamId = streamId });
      var completed = _envelope(Guid.Parse($"019f0001-0000-7aaa-8000-{i:D12}"), new ItemProgressCompletedEvent { StreamId = streamId });
      await eventStore.AppendAsync(streamId, started);
      await eventStore.AppendAsync(streamId, completed);
      // Stamped: commit order follows the stream's versions. Unstamped: the stamper has not reached either yet.
      stamps[started.MessageId.Value] = stamped ? 1000 + i : null;
      stamps[completed.MessageId.Value] = stamped ? 2000 + i : null;
      streams.Add((streamId, started, completed));
    }
    var store = new StampingEventStore(eventStore, stamps);

    await Task.WhenAll(streams.Select(s => _applyInterleavedAsync(store, s.StreamId, s.Started, s.Completed)));

    await using var verify = CreateDbContext();
    var ids = streams.Select(s => s.StreamId).ToList();
    var rows = await verify.Set<PerspectiveRow<ItemProgress>>().AsNoTracking()
      .Where(r => ids.Contains(r.Id))
      .ToListAsync();
    var missingStarted = new List<Guid>();
    var missingCompleted = new List<Guid>();
    foreach (var row in rows) {
      var progress = row.Data;
      if (!progress.Started || progress.Attempts != 1) {
        missingStarted.Add(row.Id);
      }
      if (!progress.Completed) {
        missingCompleted.Add(row.Id);
      }
    }

    await Assert.That(rows.Count).IsEqualTo(STREAM_COUNT);
    await Assert.That(missingCompleted).IsEmpty();
    await Assert.That(missingStarted).IsEmpty()
      .Because("Started is stored before Completed and was applied by its own worker; the worker that wrote last "
             + "must fold it onto the row the other worker committed, not report it applied while the row lacks it");
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task ARowThatLostItsEarlierEvent_IsRepairedByARebuildInPlaceAsync(bool stamped) {
    var streamId = Guid.CreateVersion7();
    var started = _envelope(Guid.Parse("019f0000-0000-7aaa-8000-000000000001"), new ItemProgressStartedEvent { StreamId = streamId });
    var completed = _envelope(Guid.Parse("019f0001-0000-7aaa-8000-000000000001"), new ItemProgressCompletedEvent { StreamId = streamId });
    var inner = new InMemoryEventStore();
    await inner.AppendAsync(streamId, started);
    await inner.AppendAsync(streamId, completed);
    var eventStore = new StampingEventStore(inner, new Dictionary<Guid, long?> {
      [started.MessageId.Value] = stamped ? 1000 : null,
      [completed.MessageId.Value] = stamped ? 2000 : null,
    });
    // The row as an affected release left it: Completed applied, Started never folded.
    await using (var seedContext = CreateDbContext()) {
      await _runner(eventStore, new EFCorePostgresPerspectiveStore<ItemProgress>(seedContext, TABLE)).RunWithEventsAsync(
        streamId, PERSPECTIVE, null, [_copy(completed, eventStore)], CancellationToken.None);
    }

    var previousMode = ProcessingModeAccessor.Current;
    ProcessingModeAccessor.Current = ProcessingMode.Rebuild;
    try {
      await using var rebuildContext = CreateDbContext();
      await _runner(eventStore, new EFCorePostgresPerspectiveStore<ItemProgress>(rebuildContext, TABLE)).RunWithEventsAsync(
        streamId, PERSPECTIVE, null, [_copy(started, eventStore), _copy(completed, eventStore)], CancellationToken.None);
    } finally {
      ProcessingModeAccessor.Current = previousMode;
    }

    await using var verify = CreateDbContext();
    var row = await verify.Set<PerspectiveRow<ItemProgress>>().AsNoTracking().SingleAsync(r => r.Id == streamId);
    await Assert.That(row.Data.Started).IsTrue()
      .Because("a rebuild re-derives the row from every stored event and records the row's own newest event, "
             + "a write the strict ordering must admit or the row can never be repaired in place");
    await Assert.That(row.Data.Completed).IsTrue();
    await Assert.That(row.Data.Attempts).IsEqualTo(1);
  }

  /// <summary>
  /// One stream's two workers: the Started worker reads, the Completed worker runs to commit, then the Started
  /// worker writes.
  /// </summary>
  private async Task _applyInterleavedAsync(
      StampingEventStore eventStore, Guid streamId, MessageEnvelope<IEvent> started, MessageEnvelope<IEvent> completed) {
    await using var startedContext = CreateDbContext();
    var startedStore = new InterleavingStore(new EFCorePostgresPerspectiveStore<ItemProgress>(startedContext, TABLE)) {
      BeforeFirstWrite = async () => {
        await using var completedContext = CreateDbContext();
        var completedStore = new EFCorePostgresPerspectiveStore<ItemProgress>(completedContext, TABLE);
        await _runner(eventStore, completedStore).RunWithEventsAsync(
          streamId, PERSPECTIVE, null, [_copy(completed, eventStore)], CancellationToken.None);
      },
    };

    await _runner(eventStore, startedStore).RunWithEventsAsync(
      streamId, PERSPECTIVE, null, [_copy(started, eventStore)], CancellationToken.None);
  }

  private static IPerspectiveRunner _runner(IEventStore eventStore, IPerspectiveStore<ItemProgress> perspectiveStore) {
    var services = new ServiceCollection();
    services.AddTransient<ItemProgressPerspective>();
    services.AddLogging();
    var sp = services.BuildServiceProvider();
    var runnerType = typeof(PerspectiveLateEventApplyTests).Assembly.GetTypes()
      .Single(t => t.Name == "ItemProgressPerspectiveRunner");
    var logger = typeof(LoggerFactoryExtensions).GetMethods()
      .Single(m => m.Name == "CreateLogger" && m.IsGenericMethod)
      .MakeGenericMethod(runnerType)
      .Invoke(null, [sp.GetRequiredService<ILoggerFactory>()])!;
    return (IPerspectiveRunner)runnerType.GetConstructors().Single().Invoke([
      sp, logger, eventStore, perspectiveStore, sp.GetRequiredService<IServiceScopeFactory>(),
      null, null, null, null, null]);
  }

  private static MessageEnvelope<IEvent> _envelope(Guid id, IEvent payload) => new() {
    MessageId = MessageId.From(id),
    Payload = payload,
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    Hops = [],
  };

  /// <summary>The envelope a worker's batch carries: the stored event with the commit sequence its claim read.</summary>
  private static MessageEnvelope<IEvent> _copy(MessageEnvelope<IEvent> stored, StampingEventStore eventStore) {
    var copy = _envelope(stored.MessageId.Value, stored.Payload);
    copy.LocalCommitSequence = eventStore.StampOf(stored.MessageId.Value);
    return copy;
  }

  /// <summary>
  /// The in-memory store with the commit sequences a settled database answers with: the checkpoint reads them through
  /// <c>GetCommitSequenceAsync</c> and the replay read carries them on each envelope.
  /// </summary>
  private sealed class StampingEventStore(InMemoryEventStore inner, IReadOnlyDictionary<Guid, long?> stamps) : IEventStore {
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
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      inner.GetEventsBetweenAsync<TMessage>(streamId, afterEventId, upToEventId, cancellationToken);
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      inner.GetEventsBetweenPolymorphicAsync(streamId, afterEventId, upToEventId, eventTypes, cancellationToken);
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      inner.GetLastSequenceAsync(streamId, cancellationToken);
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
      inner.DeserializeStreamEvents(streamEvents, eventTypes);
  }

  /// <summary>
  /// Forwards to the real store and runs <see cref="BeforeFirstWrite"/> after the worker's first read and before its
  /// first write: the deterministic seam the other worker commits into.
  /// </summary>
  private sealed class InterleavingStore(IPerspectiveStore<ItemProgress> inner) : IPerspectiveStore<ItemProgress> {
    private bool _interleaved;
    public Func<Task>? BeforeFirstWrite { get; init; }

    public Task<PerspectiveApplyRead> ReadForApplyAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      inner.ReadForApplyAsync(streamId, cancellationToken);

    public Task<ItemProgress?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      inner.GetByStreamIdAsync(streamId, cancellationToken);

    public Task<PerspectiveMetadata?> GetMetadataByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      inner.GetMetadataByStreamIdAsync(streamId, cancellationToken);

    public async Task<bool> TryUpsertAsync(Guid streamId, ItemProgress model, PerspectiveScope scope, bool forceUpdateScope,
        PerspectiveMetadata metadata, PerspectiveRowVersion expectedVersion, CancellationToken cancellationToken = default) {
      if (!_interleaved && BeforeFirstWrite is not null) {
        _interleaved = true;
        await BeforeFirstWrite();
      }
      return await inner.TryUpsertAsync(streamId, model, scope, forceUpdateScope, metadata, expectedVersion, cancellationToken);
    }

    public Task UpsertAsync(Guid streamId, ItemProgress model, CancellationToken cancellationToken = default) =>
      inner.UpsertAsync(streamId, model, cancellationToken);

    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, ItemProgress model, IDictionary<string, object?> physicalFieldValues,
        PerspectiveScope? scope = null, CancellationToken cancellationToken = default) =>
      inner.UpsertWithPhysicalFieldsAsync(streamId, model, physicalFieldValues, scope, cancellationToken);

    public Task<ItemProgress?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => inner.GetByPartitionKeyAsync(partitionKey, cancellationToken);

    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, ItemProgress model, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => inner.UpsertByPartitionKeyAsync(partitionKey, model, cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
    public Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) => inner.PurgeAsync(streamId, cancellationToken);
    public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => inner.PurgeByPartitionKeyAsync(partitionKey, cancellationToken);
  }
}
