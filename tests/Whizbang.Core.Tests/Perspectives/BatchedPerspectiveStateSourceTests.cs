// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The default state source of a set-based batch (#2246): one read of the rows per perspective, none for a stream known
/// to be new, one purge-marker query and one history query for the rows found missing, and every question it cannot
/// answer left to the runner's per-stream read.
/// </summary>
/// <docs>fundamentals/perspectives/write-modes#state-source</docs>
public class BatchedPerspectiveStateSourceTests {
  private const string PERSPECTIVE = "orders";
  private const string OTHER = "invoices";

  [Test]
  public async Task IsKnownNew_OnlyWithoutACursor_AndWhenTheFirstEventStartsTheStreamAsync() {
    var first = _envelope(isFirst: true);
    var later = _envelope(isFirst: false);

    await Assert.That(PerspectiveStateRequest.IsKnownNew(null, [first, later])).IsTrue();
    await Assert.That(PerspectiveStateRequest.IsKnownNew(Guid.NewGuid(), [first])).IsFalse()
      .Because("a cursor means the stream has been processed before");
    await Assert.That(PerspectiveStateRequest.IsKnownNew(null, [later, first])).IsFalse()
      .Because("only the first event the batch folds decides it");
    await Assert.That(PerspectiveStateRequest.IsKnownNew(null, [])).IsFalse();
  }

  [Test]
  public async Task AKnownNewStream_IsNeverRead_AndTheOthersAreReadOnceForThePerspectiveAsync() {
    var known = Guid.NewGuid();
    var present = Guid.NewGuid();
    var missing = Guid.NewGuid();
    var store = new BatchStore { Rows = { [present] = _row("present") } };
    var batch = new BatchedPerspectiveStateSource().BeginBatch([
      new(known, PERSPECTIVE, Guid.NewGuid(), KnownNew: true),
      new(present, PERSPECTIVE, Guid.NewGuid(), KnownNew: false),
      new(missing, PERSPECTIVE, Guid.NewGuid(), KnownNew: false),
    ]);

    await Assert.That(batch.IsKnownNew(known, PERSPECTIVE)).IsTrue();
    await Assert.That(batch.IsKnownNew(present, PERSPECTIVE)).IsFalse();
    await Assert.That(batch.IsKnownNew(known, OTHER)).IsFalse().Because("the plan is per (stream, perspective)");
    await Assert.That(await batch.ReadRowAsync(store, known, PERSPECTIVE)).IsNull()
      .Because("a known-new stream is not part of the read; the runner never asks for it");

    var presentRow = await batch.ReadRowAsync(store, present, PERSPECTIVE);
    var missingRow = await batch.ReadRowAsync(store, missing, PERSPECTIVE);

    await Assert.That(store.Reads).Count().IsEqualTo(1).Because("one read serves every pair of the perspective");
    await Assert.That(store.Reads[0]).IsEquivalentTo(new[] { present, missing });
    await Assert.That(presentRow!.Model!.Name).IsEqualTo("present");
    await Assert.That(missingRow!.Model).IsNull();
    await Assert.That(missingRow.ApplyRead.Version).IsEqualTo(PerspectiveRowVersion.Absent);
  }

  [Test]
  public async Task APairTheBatchDidNotPlan_IsLeftToItsOwnReadAsync() {
    var store = new BatchStore();
    var batch = new BatchedPerspectiveStateSource().BeginBatch([new(Guid.NewGuid(), PERSPECTIVE, Guid.NewGuid(), false)]);

    await Assert.That(await batch.ReadRowAsync(store, Guid.NewGuid(), PERSPECTIVE)).IsNull();
    await Assert.That(await batch.ReadRowAsync(store, Guid.NewGuid(), OTHER)).IsNull();
    await Assert.That(store.Reads).IsEmpty();
  }

  [Test]
  public async Task AStoreThatDoesNotReadInBatches_LeavesEveryRowToItsOwnReadAsync() {
    var stream = Guid.NewGuid();
    var batch = new BatchedPerspectiveStateSource().BeginBatch([new(stream, PERSPECTIVE, Guid.NewGuid(), false)]);

    await Assert.That(await batch.ReadRowAsync(new BatchStore { Declines = true }, stream, PERSPECTIVE)).IsNull();
    await Assert.That(await batch.IsPurgedAsync(new Markers(), stream, PERSPECTIVE)).IsNull()
      .Because("with no batched read, nothing is known to be missing");
  }

  [Test]
  public async Task ABatchedReadThatFails_IsLogged_AndLeavesEveryRowToItsOwnReadAsync() {
    var stream = Guid.NewGuid();
    var logger = new RecordingLogger();
    var batch = new BatchedPerspectiveStateSource(logger).BeginBatch([new(stream, PERSPECTIVE, Guid.NewGuid(), false)]);

    await Assert.That(await batch.ReadRowAsync(new BatchStore { Fails = true }, stream, PERSPECTIVE)).IsNull();
    await Assert.That(logger.Warnings).Count().IsEqualTo(1);
    await Assert.That(logger.Warnings[0]).Contains("row read").And.Contains(PERSPECTIVE);
  }

  [Test]
  public async Task TheRowsOfADifferentModel_AreNotServedAsync() {
    // Two perspectives never share a name, but a plan cannot hand one model's rows to a runner of another.
    var stream = Guid.NewGuid();
    var batch = new BatchedPerspectiveStateSource().BeginBatch([new(stream, PERSPECTIVE, Guid.NewGuid(), false)]);
    await batch.ReadRowAsync(new BatchStore(), stream, PERSPECTIVE);

    await Assert.That(await batch.ReadRowAsync(new OtherStore(), stream, PERSPECTIVE)).IsNull();
  }

  [Test]
  public async Task PurgeMarkers_AreAskedOnce_ForTheRowsFoundMissingAsync() {
    var present = Guid.NewGuid();
    var purged = Guid.NewGuid();
    var live = Guid.NewGuid();
    var store = new BatchStore { Rows = { [present] = _row("present") } };
    var markers = new Markers { Purged = { purged } };
    var batch = new BatchedPerspectiveStateSource().BeginBatch([
      new(present, PERSPECTIVE, Guid.NewGuid(), false),
      new(purged, PERSPECTIVE, Guid.NewGuid(), false),
      new(live, PERSPECTIVE, Guid.NewGuid(), false),
    ]);
    foreach (var stream in new[] { present, purged, live }) {
      await batch.ReadRowAsync(store, stream, PERSPECTIVE);
    }

    await Assert.That(await batch.IsPurgedAsync(markers, purged, PERSPECTIVE)).IsEqualTo(true);
    await Assert.That(await batch.IsPurgedAsync(markers, live, PERSPECTIVE)).IsEqualTo(false);
    await Assert.That(await batch.IsPurgedAsync(markers, present, PERSPECTIVE)).IsNull()
      .Because("a row the read found is never probed");

    await Assert.That(markers.Queries).Count().IsEqualTo(1);
    await Assert.That(markers.Queries[0]).IsEquivalentTo(new[] { purged, live });
  }

  [Test]
  public async Task APurgeMarkerQueryThatFails_IsLogged_AndLeavesTheProbeToEachStreamAsync() {
    var stream = Guid.NewGuid();
    var logger = new RecordingLogger();
    var batch = new BatchedPerspectiveStateSource(logger).BeginBatch([new(stream, PERSPECTIVE, Guid.NewGuid(), false)]);
    await batch.ReadRowAsync(new BatchStore(), stream, PERSPECTIVE);

    await Assert.That(await batch.IsPurgedAsync(new Markers { Fails = true }, stream, PERSPECTIVE)).IsNull();
    await Assert.That(logger.Warnings).Count().IsEqualTo(1);
    await Assert.That(logger.Warnings[0]).Contains("purge-marker read");
  }

  [Test]
  public async Task TheHistoryProbe_IsAskedOnce_BeforeEachStreamsFirstEventAsync() {
    var woken = Guid.NewGuid();
    var fresh = Guid.NewGuid();
    var wokenFirst = Guid.NewGuid();
    var freshFirst = Guid.NewGuid();
    var events = new HistoryStore { WithHistory = { woken } };
    var batch = new BatchedPerspectiveStateSource().BeginBatch([
      new(woken, PERSPECTIVE, wokenFirst, false),
      new(fresh, PERSPECTIVE, freshFirst, false),
    ]);
    var store = new BatchStore();
    await batch.ReadRowAsync(store, woken, PERSPECTIVE);
    await batch.ReadRowAsync(store, fresh, PERSPECTIVE);
    Type[] types = [typeof(string)];

    await Assert.That(await batch.HasHistoryBeforeAsync(events, woken, PERSPECTIVE, wokenFirst, types)).IsEqualTo(true);
    await Assert.That(await batch.HasHistoryBeforeAsync(events, fresh, PERSPECTIVE, freshFirst, types)).IsEqualTo(false);
    await Assert.That(await batch.HasHistoryBeforeAsync(events, fresh, PERSPECTIVE, Guid.NewGuid(), types)).IsNull()
      .Because("the batch asked before the event it planned; any other question is the stream's own");

    await Assert.That(events.Queries).Count().IsEqualTo(1);
    await Assert.That(events.Queries[0]).IsEquivalentTo(new[] { (woken, wokenFirst), (fresh, freshFirst) });
    await Assert.That(events.TypesAsked[0]).IsSameReferenceAs(types);
  }

  [Test]
  public async Task AHistoryProbeThatFails_IsLogged_AndLeavesTheProbeToEachStreamAsync() {
    var stream = Guid.NewGuid();
    var first = Guid.NewGuid();
    var logger = new RecordingLogger();
    var batch = new BatchedPerspectiveStateSource(logger).BeginBatch([new(stream, PERSPECTIVE, first, false)]);
    await batch.ReadRowAsync(new BatchStore(), stream, PERSPECTIVE);

    await Assert.That(await batch.HasHistoryBeforeAsync(new HistoryStore { Fails = true }, stream, PERSPECTIVE, first, [])).IsNull();
    await Assert.That(logger.Warnings[0]).Contains("history probe");
  }

  [Test]
  public async Task AProbeOfAPerspectiveTheBatchDidNotPlan_IsLeftToItsOwnQueryAsync() {
    var batch = new BatchedPerspectiveStateSource().BeginBatch([]);

    await Assert.That(await batch.IsPurgedAsync(new Markers(), Guid.NewGuid(), PERSPECTIVE)).IsNull();
    await Assert.That(await batch.HasHistoryBeforeAsync(new HistoryStore(), Guid.NewGuid(), PERSPECTIVE, Guid.NewGuid(), [])).IsNull();
  }

  [Test]
  public async Task CommitSequences_AreReadOnce_ForEveryEventTheBatchPlannedAsync() {
    var first = Guid.NewGuid();
    var second = Guid.NewGuid();
    var unstamped = Guid.NewGuid();
    var events = new HistoryStore { Sequences = { [first] = 10, [second] = 20, [unstamped] = null } };
    var batch = new BatchedPerspectiveStateSource().BeginBatch([
      new(Guid.NewGuid(), PERSPECTIVE, first, KnownNew: true, EventIds: [first, second]),
      new(Guid.NewGuid(), OTHER, unstamped, KnownNew: false, EventIds: [unstamped]),
      new(Guid.NewGuid(), OTHER, Guid.NewGuid(), KnownNew: false),
    ]);

    await Assert.That(await batch.CommitSequenceOfAsync(events, second)).IsEqualTo(new PerspectiveCommitSequence(20));
    await Assert.That(await batch.CommitSequenceOfAsync(events, first)).IsEqualTo(new PerspectiveCommitSequence(10));
    await Assert.That(await batch.CommitSequenceOfAsync(events, unstamped)).IsEqualTo(new PerspectiveCommitSequence(null))
      .Because("an event not yet stamped has no commit sequence, which the batch answers as the per-event read does");
    await Assert.That(await batch.CommitSequenceOfAsync(events, Guid.NewGuid())).IsNull()
      .Because("an event the batch did not plan is the pair's own read");

    await Assert.That(events.SequenceQueries).Count().IsEqualTo(1).Because("one read serves every pair of the batch");
    await Assert.That(events.SequenceQueries[0]).IsEquivalentTo(new[] { first, second, unstamped });
  }

  [Test]
  public async Task ACommitSequenceReadThatFails_IsLogged_AndLeavesEachEventToItsOwnReadAsync() {
    var eventId = Guid.NewGuid();
    var logger = new RecordingLogger();
    var batch = new BatchedPerspectiveStateSource(logger).BeginBatch([new(Guid.NewGuid(), PERSPECTIVE, eventId, false, [eventId])]);

    await Assert.That(await batch.CommitSequenceOfAsync(new HistoryStore { Fails = true }, eventId)).IsNull();
    await Assert.That(logger.Warnings[0]).Contains("commit-sequence read");
    await Assert.That(async () => await batch.CommitSequenceOfAsync(null!, eventId)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task GetCommitSequencesAsync_Default_AsksEachEventAsync() {
    var eventId = Guid.NewGuid();
    IEventStore events = new SingleHistory(Guid.Empty) { Sequence = 42 };

    var answer = await events.GetCommitSequencesAsync([eventId]);

    await Assert.That(answer[eventId]).IsEqualTo(42L);
    await Assert.That(async () => await events.GetCommitSequencesAsync(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ReadPolymorphicManyAsync_Default_ReturnsNullAsync() {
    // A store that does not read streams in batches says so, and each pair of a claimed batch reads its own events.
    IEventStore events = new SingleHistory(Guid.Empty);

    await Assert.That(await events.ReadPolymorphicManyAsync([(Guid.NewGuid(), null)], [typeof(string)])).IsNull();
  }

  [Test]
  public async Task PurgedAmongAsync_Default_AsksEachStreamAsync() {
    var purged = Guid.NewGuid();
    IPerspectivePurgeMarkerStore markers = new SingleMarkers(purged);

    var answer = await markers.PurgedAmongAsync([purged, Guid.NewGuid()], PERSPECTIVE);

    await Assert.That(answer).IsEquivalentTo(new[] { purged });
  }

  [Test]
  public async Task StreamsWithEventsBeforeAsync_Default_AsksEachStreamAsync() {
    var withHistory = Guid.NewGuid();
    IEventStore events = new SingleHistory(withHistory);

    var answer = await events.StreamsWithEventsBeforeAsync(
      [(withHistory, Guid.NewGuid()), (Guid.NewGuid(), Guid.NewGuid())], [typeof(string)]);

    await Assert.That(answer).IsEquivalentTo(new[] { withHistory });
  }

  [Test]
  public async Task ArgumentsAreRequiredAsync() {
    var source = new BatchedPerspectiveStateSource();
    var batch = source.BeginBatch([]);

    await Assert.That(() => source.BeginBatch(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => PerspectiveStateRequest.IsKnownNew(null, null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await batch.ReadRowAsync<Model>(null!, Guid.NewGuid(), PERSPECTIVE)).Throws<ArgumentNullException>();
    await Assert.That(async () => await batch.IsPurgedAsync(null!, Guid.NewGuid(), PERSPECTIVE)).Throws<ArgumentNullException>();
    await Assert.That(async () => await batch.HasHistoryBeforeAsync(null!, Guid.NewGuid(), PERSPECTIVE, Guid.NewGuid(), [])).Throws<ArgumentNullException>();
    await Assert.That(async () => await batch.HasHistoryBeforeAsync(new HistoryStore(), Guid.NewGuid(), PERSPECTIVE, Guid.NewGuid(), null!)).Throws<ArgumentNullException>();
    IPerspectivePurgeMarkerStore markers = new SingleMarkers(Guid.Empty);
    await Assert.That(async () => await markers.PurgedAmongAsync(null!, PERSPECTIVE)).Throws<ArgumentNullException>();
    IEventStore events = new SingleHistory(Guid.Empty);
    await Assert.That(async () => await events.StreamsWithEventsBeforeAsync(null!, [])).Throws<ArgumentNullException>();
  }

  // ---------------------------------------------------------------------------------------------------------------

  public sealed class Model {
    public string Name { get; init; } = "";
  }

  private static PerspectiveStoredRow<Model> _row(string name) =>
    new(new PerspectiveApplyRead(PerspectiveRowVersion.Of(7), new PerspectiveMetadata { EventId = "e", EventType = "t" }),
      new Model { Name = name });

  private static MessageEnvelope<IEvent> _envelope(bool isFirst) => new() {
    MessageId = MessageId.New(),
    Payload = new SampleEvent(),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = global::Whizbang.Core.Dispatch.DispatchModes.Local, Source = MessageSource.Local },
    IsFirstInLocalStream = isFirst,
  };

  private sealed record SampleEvent : IEvent;

  /// <summary>A store whose only real member is the batched row read.</summary>
  private sealed class BatchStore : IPerspectiveStore<Model> {
    public Dictionary<Guid, PerspectiveStoredRow<Model>> Rows { get; } = [];
    public List<Guid[]> Reads { get; } = [];
    public bool Declines { get; init; }
    public bool Fails { get; init; }

    public Task<IReadOnlyDictionary<Guid, PerspectiveStoredRow<Model>>?> ReadForApplyManyAsync(
        IReadOnlyCollection<Guid> streamIds, CancellationToken cancellationToken = default) {
      Reads.Add([.. streamIds]);
      if (Fails) {
        throw new InvalidOperationException("the read failed");
      }
      IReadOnlyDictionary<Guid, PerspectiveStoredRow<Model>>? rows = Declines
        ? null
        : Rows.Where(r => streamIds.Contains(r.Key)).ToDictionary(r => r.Key, r => r.Value);
      return Task.FromResult(rows);
    }

    public Task<Model?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task UpsertAsync(Guid streamId, Model model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, Model model, IDictionary<string, object?> physicalFieldValues,
        PerspectiveScope? scope = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Model?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => throw new NotSupportedException();
    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, Model model, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => throw new NotSupportedException();
    public Task FlushAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => throw new NotSupportedException();
  }

  public sealed class OtherModel;

  private sealed class OtherStore : IPerspectiveStore<OtherModel> {
    public Task<OtherModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task UpsertAsync(Guid streamId, OtherModel model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, OtherModel model, IDictionary<string, object?> physicalFieldValues,
        PerspectiveScope? scope = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OtherModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => throw new NotSupportedException();
    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, OtherModel model, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => throw new NotSupportedException();
    public Task FlushAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => throw new NotSupportedException();
  }

  private sealed class Markers : IPerspectivePurgeMarkerStore {
    public HashSet<Guid> Purged { get; } = [];
    public List<Guid[]> Queries { get; } = [];
    public bool Fails { get; init; }

    public Task<IReadOnlySet<Guid>> PurgedAmongAsync(IReadOnlyCollection<Guid> streamIds, string perspectiveName, CancellationToken cancellationToken = default) {
      Queries.Add([.. streamIds]);
      if (Fails) {
        throw new InvalidOperationException("the query failed");
      }
      return Task.FromResult<IReadOnlySet<Guid>>(streamIds.Where(Purged.Contains).ToHashSet());
    }

    public Task<bool> IsPurgedAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task MarkPurgedAsync(Guid streamId, string perspectiveName, Guid? purgeEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task ClearAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  /// <summary>Answers only the per-stream question, so the default batched member is what is tested.</summary>
  private sealed class SingleMarkers(Guid purged) : IPerspectivePurgeMarkerStore {
    public Task<bool> IsPurgedAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult(streamId == purged);
    public Task MarkPurgedAsync(Guid streamId, string perspectiveName, Guid? purgeEventId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ClearAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  private sealed class HistoryStore() : ForwardingEventStoreDecorator(new InMemoryEventStore()) {
    public HashSet<Guid> WithHistory { get; } = [];
    public Dictionary<Guid, long?> Sequences { get; } = [];
    public List<Guid[]> SequenceQueries { get; } = [];

    public override Task<IReadOnlyDictionary<Guid, long?>> GetCommitSequencesAsync(
        IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default) {
      SequenceQueries.Add([.. eventIds]);
      if (Fails) {
        throw new InvalidOperationException("the read failed");
      }
      return Task.FromResult<IReadOnlyDictionary<Guid, long?>>(eventIds.Where(Sequences.ContainsKey).ToDictionary(id => id, id => Sequences[id]));
    }
    public List<(Guid, Guid)[]> Queries { get; } = [];
    public List<IReadOnlyList<Type>> TypesAsked { get; } = [];
    public bool Fails { get; init; }

    public override Task<IReadOnlySet<Guid>> StreamsWithEventsBeforeAsync(
        IReadOnlyList<(Guid StreamId, Guid BeforeEventId)> probes, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      Queries.Add([.. probes]);
      TypesAsked.Add(eventTypes);
      if (Fails) {
        throw new InvalidOperationException("the probe failed");
      }
      return Task.FromResult<IReadOnlySet<Guid>>(probes.Select(p => p.StreamId).Where(WithHistory.Contains).ToHashSet());
    }
  }

  /// <summary>
  /// Answers only the per-stream typed probe: it re-implements the interface, so the batched member is the interface's
  /// default, which asks this one per stream.
  /// </summary>
  private sealed class SingleHistory(Guid withHistory) : InMemoryEventStore, IEventStore {
    public long? Sequence { get; init; }

    public Task<long?> GetCommitSequenceAsync(Guid eventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(Sequence);

    public Task<bool> HasStreamEventsBeforeAsync(Guid streamId, Guid beforeEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(streamId == withHistory);
  }

  private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<BatchedPerspectiveStateSource> {
    public List<string> Warnings { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning) {
        Warnings.Add(formatter(state, exception));
      }
    }
  }
}
