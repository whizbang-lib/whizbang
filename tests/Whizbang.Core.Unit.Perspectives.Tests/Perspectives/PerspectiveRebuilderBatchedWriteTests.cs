// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Events.System;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tests.Perspectives.RunnerBase;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// A rebuild in place writes its rows a batch at a time (#2522): each batch of streams it read together is prepared,
/// written through the host's write sink with the rest of the batch, and completed, the set-based write of the live path
/// (#2246), instead of one write per row. Every result is compared with the per-row rebuild of the same streams: rows,
/// stamps, cursors, counts and progress, over a stale row, a current one, a new one, a stream with no events, one with
/// only events the perspective does not fold, a purge, a row the guard refuses, a row that moves under its write and a
/// stream whose events an upcaster re-keyed.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild#batched-writes</docs>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRebuilder.cs</code-under-test>
public class PerspectiveRebuilderBatchedWriteTests {
  // A perspective a generated runner would register: its rows may be written set-based.
  private const string NAME = "RbPerspective.BatchedWrites";

  private static readonly Guid _stale = TrackedGuid.New().Value;
  private static readonly Guid _current = TrackedGuid.New().Value;
  private static readonly Guid _new = TrackedGuid.New().Value;
  private static readonly Guid _empty = TrackedGuid.New().Value;
  private static readonly Guid _ignoredOnly = TrackedGuid.New().Value;
  private static readonly Guid[] _streams = [_stale, _current, _new, _empty, _ignoredOnly];
  private static readonly Guid _target = TrackedGuid.New().Value;
  private static readonly Guid _mover = TrackedGuid.New().Value;
  private static readonly Guid _moverFirst = TrackedGuid.New().Value;
  private static readonly Guid[] _rekeyStreams = [_mover, _moverFirst, _target, .. _streams];

  static PerspectiveRebuilderBatchedWriteTests() => PerspectiveWriteModes.Register(NAME, declared: null);

  [Test]
  public async Task Rebuild_InPlace_WritesEachBatchsRowsThroughTheSinkWithOneWrite_NotOneWritePerRowAsync() {
    var batched = _world(sink: true);
    var perRow = _world(sink: false);

    var batchedResult = await batched.Rebuilder.RebuildStreamsAsync(NAME, _streams);
    var perRowResult = await perRow.Rebuilder.RebuildStreamsAsync(NAME, _streams);

    await Assert.That(batchedResult.Success).IsTrue().Because(batchedResult.Error ?? "");
    await Assert.That(perRowResult.Success).IsTrue().Because(perRowResult.Error ?? "");
    await Assert.That(perRow.Rows.Writes).IsEqualTo(3).Because("the control: the per-row rebuild writes each folded row on its own");
    await Assert.That(perRow.Sink.Writes).IsEmpty();
    await Assert.That(batched.Rows.Writes).IsEqualTo(batched.Sink.Writes.Sum(w => w.Count))
      .Because("every row the batched rebuild wrote, the sink wrote");
    await Assert.That(batched.Sink.Writes.Select(w => w.Count)).IsEquivalentTo([2, 1])
      .Because("five streams in batches of two hold two, then one, folded rows: one sink write each");
    await Assert.That(batched.Sink.Writes.SelectMany(w => w)).IsEquivalentTo([_stale, _current, _new]);
    await _assertParityAsync(batched, batchedResult, perRow, perRowResult);
    await Assert.That(batched.Rows.Row(_stale).Marks).IsEqualTo("ab").Because("the stale row is re-derived, not folded onto");
    await Assert.That(batched.Rows.Row(_new).Marks).IsEqualTo("n");
    await Assert.That(batched.Rows.Has(_empty)).IsFalse();
    await Assert.That(batched.Rows.Has(_ignoredOnly)).IsFalse();
  }

  [Test]
  public async Task Rebuild_InPlace_ReadsEachBatchsRowsOnce_AndNoRowOnItsOwnAsync() {
    var world = _world(sink: true);

    var result = await world.Rebuilder.RebuildStreamsAsync(NAME, _streams);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    await Assert.That(world.Rows.RowReads).IsEmpty().Because("the batched write prepares each row from the batch's read");
    await Assert.That(world.Rows.BatchRowReads.Count).IsEqualTo(2);
  }

  [Test]
  public async Task Rebuild_InPlace_AWriteTheGuardRefuses_IsCountedAlreadyCorrectOrRefused_AsThePerRowWriteCountsItAsync() {
    // Each stored row is stamped above every event its stream holds, so the ordering guard refuses the rebuild's write:
    // the current one already holds the fold, the stale one does not (#2252).
    var batched = _world(sink: true, stampedAbove: true);
    var perRow = _world(sink: false, stampedAbove: true);

    var batchedResult = await batched.Rebuilder.RebuildStreamsAsync(NAME, _streams);
    var perRowResult = await perRow.Rebuilder.RebuildStreamsAsync(NAME, _streams);

    await Assert.That(batched.Sink.Refused).IsEquivalentTo([_stale, _current]).Because("the sink is where the guard refused");
    await Assert.That(perRowResult.StreamsAlreadyCorrect).IsEqualTo(1);
    await Assert.That(perRowResult.StreamsWriteRefused).IsEqualTo(1);
    await Assert.That(batchedResult.StreamsAlreadyCorrect).IsEqualTo(perRowResult.StreamsAlreadyCorrect);
    await Assert.That(batchedResult.StreamsWriteRefused).IsEqualTo(perRowResult.StreamsWriteRefused);
    await Assert.That(batchedResult.Success).IsEqualTo(perRowResult.Success);
    await Assert.That(batchedResult.Error).IsEqualTo(perRowResult.Error);
    await _assertParityAsync(batched, batchedResult, perRow, perRowResult);
  }

  [Test]
  public async Task Rebuild_InPlace_AWriteTheGuardRefuses_NeverRewindsTheStream_AsALiveRefusalDoesAsync() {
    var world = _world(sink: true, stampedAbove: true);

    await world.Rebuilder.RebuildStreamsAsync(NAME, _streams);

    await Assert.That(world.Sink.Refused).IsNotEmpty().Because("the refusal this test is about happened");
    await Assert.That(world.Log.Count(LogLevel.Warning, "[diag.write.refused]")).IsEqualTo(0)
      .Because("a rebuild already folded the whole stream, so its refused write is reported, never rewound");
    await Assert.That(world.Events.StreamReads).IsEmpty().Because("a rewind would read the stream again");
    await Assert.That(world.Rows.Writes).IsEqualTo(world.Sink.Writes.Sum(w => w.Count)).Because("nothing was written after the sink");
  }

  [Test]
  public async Task Rebuild_InPlace_APurge_IsWrittenWithTheBatch_AndCountedAsThePerRowPurgeIsAsync() {
    var batched = _world(sink: true, purgeOn: "p");
    var perRow = _world(sink: false, purgeOn: "p");

    var batchedResult = await batched.Rebuilder.RebuildStreamsAsync(NAME, _streams);
    var perRowResult = await perRow.Rebuilder.RebuildStreamsAsync(NAME, _streams);

    await Assert.That(perRow.Rows.Purged).IsEquivalentTo([_current]);
    await Assert.That(batched.Sink.Purges).IsEquivalentTo([_current]).Because("the batch removed the purged row");
    await Assert.That(batched.Rows.Purged).IsEquivalentTo(perRow.Rows.Purged);
    await Assert.That(batched.PurgeMarkers.Marked).IsEquivalentTo(perRow.PurgeMarkers.Marked)
      .Because("the purge is recorded before the batch removes the row, as the per-row purge records it");
    await Assert.That(batchedResult.StreamsPurged).IsEqualTo(1);
    await Assert.That(batchedResult.StreamsPurged).IsEqualTo(perRowResult.StreamsPurged);
    await _assertParityAsync(batched, batchedResult, perRow, perRowResult);
  }

  [Test]
  public async Task Rebuild_InPlace_ARowThatMovesUnderTheBatchsWrite_IsReadAgainAndWrittenOnItsOwnAsync() {
    var world = _world(sink: true);
    var moved = false;
    world.Rows.BeforeWrite = id => {
      if (id == _stale && !moved) {
        moved = true;
        world.Rows.Touch(_stale);
      }
    };

    var result = await world.Rebuilder.RebuildStreamsAsync(NAME, _streams);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    await Assert.That(world.Sink.Conflicts).IsEquivalentTo([_stale]).Because("the conflict this test is about happened");
    await Assert.That(world.Rows.RowReads).Contains(_stale)
      .Because("the batch's read is older than the row now; the retry reads the row as it is");
    await Assert.That(world.Rows.RowReads.Distinct()).IsEquivalentTo([_stale]).Because("only the row that moved is read again");
    await Assert.That(world.Rows.Row(_stale).Marks).IsEqualTo("ab");
    await Assert.That(result.StreamsProcessed).IsEqualTo(3);
  }

  [Test]
  public async Task Rebuild_InPlace_AStreamWhoseEventsWereRekeyed_FoldsEveryRowAsThePerRowRebuildDoesAsync() {
    var batched = _world(sink: true, rekeyed: true);
    var perRow = _world(sink: false, rekeyed: true);

    var batchedResult = await batched.Rebuilder.RebuildStreamsAsync(NAME, _rekeyStreams);
    var perRowResult = await perRow.Rebuilder.RebuildStreamsAsync(NAME, _rekeyStreams);

    await Assert.That(string.Concat(perRow.Rows.Row(_target).Marks.Order())).IsEqualTo("abt")
      .Because("the target's own event and the one each mover re-keyed onto it, before and after its own row");
    await Assert.That(perRow.Rows.Row(_mover).Marks).IsEqualTo("s").Because("the event that stayed on the stream it is stored on");
    await Assert.That(batched.Sink.Writes.SelectMany(w => w)).Contains(_mover).Because("the row its staying event folds onto is batched");
    await Assert.That(batched.Sink.Writes.SelectMany(w => w)).Contains(_moverFirst);
    await _assertRekeyParityAsync(batched, batchedResult, perRow, perRowResult);
  }

  [Test]
  public async Task Rebuild_InPlace_AStreamWhoseOwnRowFails_HandsOverTheEventsRekeyedAfterIt_AsItsOwnRebuildDoesAsync() {
    var batched = _world(sink: true, rekeyed: true);
    var perRow = _world(sink: false, rekeyed: true);
    batched.Sink.Fail = _mover;
    batched.Rows.BeforeWrite = id => {
      if (id == _mover) {
        throw new InvalidOperationException("the row cannot be written");
      }
    };
    perRow.Rows.BeforeWrite = batched.Rows.BeforeWrite;

    var batchedResult = await batched.Rebuilder.RebuildStreamsAsync(NAME, _rekeyStreams);
    var perRowResult = await perRow.Rebuilder.RebuildStreamsAsync(NAME, _rekeyStreams);

    await Assert.That(batched.Sink.Rejected).IsEquivalentTo([_mover]).Because("the failure this test is about happened");
    await Assert.That(string.Concat(perRow.Rows.Row(_target).Marks.Order())).IsEqualTo("bt")
      .Because("the event re-keyed after the failed row is never handed over; the one before it is");
    await _assertRekeyParityAsync(batched, batchedResult, perRow, perRowResult);
  }

  [Test]
  public async Task Rebuild_InPlace_AStreamTheBatchedReadLeftOut_IsRebuiltOnItsOwnAsync() {
    var world = _world(sink: true);
    world.Events.BatchOmits.Add(_stale);

    var result = await world.Rebuilder.RebuildStreamsAsync(NAME, _streams);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    await Assert.That(world.Sink.Writes.SelectMany(w => w)).IsEquivalentTo([_current, _new]);
    await Assert.That(world.Events.StreamReads).IsEquivalentTo([_stale]);
    await Assert.That(world.Rows.Row(_stale).Marks).IsEqualTo("ab");
  }

  [Test]
  public async Task Rebuild_InPlace_ARowTheSinkRejects_IsWrittenOnItsOwn_AndAFailureThereFailsOnlyItsStreamAsync() {
    var world = _world(sink: true);
    world.Sink.Fail = _stale;
    world.Rows.BeforeWrite = id => {
      if (id == _stale && world.Sink.Rejected.Count > 0) {
        throw new InvalidOperationException("the row cannot be written on its own either");
      }
    };

    var result = await world.Rebuilder.RebuildStreamsAsync(NAME, _streams);

    await Assert.That(world.Sink.Rejected).IsEquivalentTo([_stale]);
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error).Contains("1 of 5 stream(s) failed to replay");
    await Assert.That(result.StreamsProcessed).IsEqualTo(2).Because("the batch's other rows were written");
    await Assert.That(world.Rows.Row(_current).Marks).IsEqualTo("c");
  }

  [Test]
  public async Task Rebuild_InPlace_WhenTheBatchCannotTakeItsLocks_LogsIt_AndRebuildsEachStreamOnItsOwnAsync() {
    var world = _world(sink: true, coordinator: new RefusingCoordinator());

    var result = await world.Rebuilder.RebuildStreamsAsync(NAME, _streams);

    await Assert.That(result.Success).IsTrue().Because("a batch that cannot be written costs its round trips, never its streams");
    await Assert.That(world.Sink.Writes).IsEmpty();
    await Assert.That(world.Rows.Writes).IsEqualTo(3);
    await Assert.That(world.Rows.Row(_stale).Marks).IsEqualTo("ab");
    await Assert.That(world.Log.Count(LogLevel.Warning, "rows of")).IsEqualTo(2).Because("each batch's failed write is logged, once");
  }

  [Test]
  public async Task Rebuild_InPlace_OfAServiceThatWritesPerStream_WritesEachRowOnItsOwnAsync() {
    var world = _world(sink: true, writeMode: PerspectiveWriteMode.PerStream);

    var result = await world.Rebuilder.RebuildStreamsAsync(NAME, _streams);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(world.Sink.Writes).IsEmpty().Because("the rebuild writes its rows as the service's write mode says");
    await Assert.That(world.Rows.Writes).IsEqualTo(3);
  }

  [Test]
  public async Task Rebuild_InPlace_OfAPerspectiveWithNoGeneratedRunner_WritesEachRowOnItsOwnAsync() {
    var world = _world(sink: true);

    var result = await world.Rebuilder.RebuildStreamsAsync(RbHarness.NAME + ".HandWritten", _streams);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(world.Sink.Writes).IsEmpty().Because("a hand-written runner prepares nothing, so it keeps its own rebuild");
    await Assert.That(world.Rows.Writes).IsEqualTo(3);
  }

  [Test]
  public async Task Rebuild_InPlace_ReadingEachStreamOnItsOwn_WritesEachRowOnItsOwnAsync() {
    var world = _world(sink: true, batchSize: 1);

    var result = await world.Rebuilder.RebuildStreamsAsync(NAME, _streams);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(world.Sink.Writes).IsEmpty().Because("only the streams a batch read together are written together");
    await Assert.That(world.Rows.Writes).IsEqualTo(3);
  }

  // ===== Fixtures =====

  private static async Task _assertParityAsync(World batched, RebuildResult batchedResult, World perRow, RebuildResult perRowResult) {
    foreach (var stream in _streams) {
      await Assert.That(batched.Rows.Has(stream)).IsEqualTo(perRow.Rows.Has(stream));
      if (perRow.Rows.Has(stream)) {
        await Assert.That(batched.Rows.Row(stream)).IsEqualTo(perRow.Rows.Row(stream));
        await Assert.That(batched.Rows.MetadataOf(stream).EventId).IsEqualTo(perRow.Rows.MetadataOf(stream).EventId)
          .Because("the row is stamped with the same last event");
        await Assert.That(batched.Rows.MetadataOf(stream).CommitSequence).IsEqualTo(perRow.Rows.MetadataOf(stream).CommitSequence);
      }
    }
    await Assert.That(_cursors(batched)).IsEquivalentTo(_cursors(perRow));
    await Assert.That(_counts(batchedResult)).IsEqualTo(_counts(perRowResult));
    await Assert.That(_progress(batched)).IsEquivalentTo(_progress(perRow));
  }

  private static async Task _assertRekeyParityAsync(World batched, RebuildResult batchedResult, World perRow, RebuildResult perRowResult) {
    foreach (var stream in _rekeyStreams) {
      await Assert.That(batched.Rows.Has(stream)).IsEqualTo(perRow.Rows.Has(stream));
      if (perRow.Rows.Has(stream)) {
        await Assert.That(batched.Rows.Row(stream)).IsEqualTo(perRow.Rows.Row(stream));
      }
    }
    await Assert.That(_cursors(batched)).IsEquivalentTo(_cursors(perRow));
    await Assert.That(_counts(batchedResult)).IsEqualTo(_counts(perRowResult));
    await Assert.That(batchedResult.Success).IsEqualTo(perRowResult.Success);
  }

  private static List<(Guid StreamId, Guid LastEventId, int EventsProcessed, bool RowChanged, PerspectiveRowUnchangedReason Reason)> _cursors(World world) =>
    [.. world.Completer.Completed.Select(c => (c.StreamId, c.LastEventId, c.EventsProcessed, c.RowChanged, c.UnchangedReason))];

  private static (int, int, int, int, int, int, int) _counts(RebuildResult result) =>
    (result.StreamsProcessed, result.EventsReplayed, result.StreamsWithoutEvents, result.StreamsAlreadyCorrect,
      result.StreamsWriteRefused, result.StreamsPurged, result.StreamsRequested);

  private static List<(int, int, int)> _progress(World world) =>
    [.. world.Dispatcher.Published.OfType<PerspectiveRebuildProgress>().Select(p => (p.ProcessedStreams, p.TotalStreams, p.EventsReplayed))];

  /// <summary>
  /// The same streams in every world: a stale stored row, a current one, a new stream, one with no events and one with
  /// only events the perspective does not fold, read two at a time with a state source, as the Entity Framework driver
  /// reads them.
  /// </summary>
  private static World _world(bool sink, bool stampedAbove = false, string? purgeOn = null, bool rekeyed = false,
      PerspectiveWriteMode? writeMode = null, IPerspectiveApplyCoordinator? coordinator = null, int batchSize = 2) {
    var harness = new RbHarness();
    var events = harness.Events;
    events.ReadsInBatches = true;
    events.ReadsCommitSequencesInBatches = true;
    harness.Rows.ReadsInBatches = true;
    harness.Rows.Prepares = true;
    var purgeMarkers = new RecordingPurgeMarkers();
    harness.Services.AddSingleton<IPerspectivePurgeMarkerStore>(purgeMarkers);
    // A stamp above every event a stream holds makes the ordering guard refuse the rebuild's write (#2252).
    var stamp = stampedAbove ? "ffffffff-ffff-7fff-bfff-ffffffffffff" : null;
    harness.Rows.Seed(_stale, new RbModel { Id = _stale, Marks = "zz" },
      stampEventId: new Guid("00000000-0000-7000-8000-000000000000"), stampText: stamp);
    harness.Rows.Seed(_current, new RbModel { Id = _current, Marks = "c" },
      stampEventId: new Guid("00000000-0000-7000-8000-000000000011"), stampText: stamp);
    events.Add(_stale, new RbMarked { StreamId = _stale, Mark = "a" }, id: new Guid("00000000-0000-7000-8000-000000000001"));
    events.Add(_stale, new RbMarked { StreamId = _stale, Mark = "b" }, id: new Guid("00000000-0000-7000-8000-000000000002"));
    events.Add(_current, new RbMarked { StreamId = _current, Mark = purgeOn ?? "c" }, id: new Guid("00000000-0000-7000-8000-000000000011"));
    events.Add(_new, new RbMarked { StreamId = _new, Mark = "n" }, id: new Guid("00000000-0000-7000-8000-000000000021"));
    events.Add(_ignoredOnly, new RbIgnored { StreamId = _ignoredOnly }, id: new Guid("00000000-0000-7000-8000-000000000031"));
    if (purgeOn is not null) {
      harness.Runner.Apply = (model, @event) => @event is RbMarked marked && marked.Mark == purgeOn
        ? (null, ModelAction.Purge)
        : (model! with { Marks = model.Marks + ((RbMarked)@event).Mark }, ModelAction.None);
    }
    if (rekeyed) {
      // One mover keeps its first event and re-keys its second onto the target; the other re-keys its first and keeps its
      // second, so a re-keyed partition comes after the stream's own row once and before it once.
      events.Add(_target, new RbMarked { StreamId = _target, Mark = "t" }, id: new Guid("00000000-0000-7000-8000-000000000041"));
      events.Add(_mover, new RbMarked { StreamId = _mover, Mark = "s" }, id: new Guid("00000000-0000-7000-8000-000000000042"));
      var after = events.Add(_mover, new RbMarked { StreamId = _mover, Mark = "a" }, id: new Guid("00000000-0000-7000-8000-000000000043"));
      EventRekeys.Record(after.Payload, _target);
      var before = events.Add(_moverFirst, new RbMarked { StreamId = _moverFirst, Mark = "b" }, id: new Guid("00000000-0000-7000-8000-000000000044"));
      EventRekeys.Record(before.Payload, _target);
      events.Add(_moverFirst, new RbMarked { StreamId = _moverFirst, Mark = "f" }, id: new Guid("00000000-0000-7000-8000-000000000045"));
    }

    var log = new RbLog();
    var completer = new RecordingCompleter();
    var dispatcher = new RebuildEventDispatcherDouble();
    var recordingSink = new RecordingSink(harness.Rows);
    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(new Registry(harness.Runner));
    services.AddSingleton<IPerspectiveCheckpointCompleter>(completer);
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton(Options.Create(new PerspectiveRebuildOptions { StreamReadBatchSize = batchSize }));
    services.AddSingleton<IPerspectiveStateSource>(new BatchedPerspectiveStateSource());
    if (sink) {
      services.AddScoped<IPerspectiveWriteSink>(_ => recordingSink);
    }
    if (writeMode is { } mode) {
      services.AddSingleton(Options.Create(new PerspectiveWorkerOptions { WriteMode = mode }));
    }
    if (coordinator is not null) {
      services.AddSingleton(coordinator);
    }
    var sp = services.BuildServiceProvider();
    var rebuilder = new PerspectiveRebuilder(sp.GetRequiredService<IServiceScopeFactory>(), new TypedLog(log));
    return new World(rebuilder, events, harness.Rows, completer, dispatcher, log, purgeMarkers, recordingSink);
  }

  private sealed record World(
    PerspectiveRebuilder Rebuilder, RbEvents Events, RbRows Rows, RecordingCompleter Completer,
    RebuildEventDispatcherDouble Dispatcher, RbLog Log, RecordingPurgeMarkers PurgeMarkers, RecordingSink Sink);

  /// <summary>A write sink over the test rows: each call's rows, and the ones it refused, found moved, rejected or purged.</summary>
  private sealed class RecordingSink(RbRows rows) : IPerspectiveWriteSink {
    public List<List<Guid>> Writes { get; } = [];
    public List<Guid> Refused { get; } = [];
    public List<Guid> Conflicts { get; } = [];
    public List<Guid> Rejected { get; } = [];
    public List<Guid> Purges { get; } = [];

    /// <summary>A row the sink rejects, as the database rejects a value a column cannot take.</summary>
    public Guid? Fail { get; set; }

    public Task<IReadOnlyList<PerspectiveWriteOutcome>> WriteAsync(
        IReadOnlyList<PendingPerspectiveWrite> writes, CancellationToken cancellationToken = default) {
      Writes.Add([.. writes.Select(w => w.StreamId)]);
      var outcomes = new List<PerspectiveWriteOutcome>(writes.Count);
      foreach (var write in writes) {
        if (write.StreamId == Fail) {
          Rejected.Add(write.StreamId);
          outcomes.Add(PerspectiveWriteOutcome.Failed(new InvalidOperationException("rejected")));
          continue;
        }
        if (write.Operation == PerspectiveWriteOperation.Purge) {
          Purges.Add(write.StreamId);
        }
        var outcome = rows.ApplyPrepared(write);
        if (outcome.Status == PerspectiveWriteStatus.Refused) {
          Refused.Add(write.StreamId);
        } else if (outcome.Status == PerspectiveWriteStatus.Conflict) {
          Conflicts.Add(write.StreamId);
        }
        outcomes.Add(outcome);
      }
      return Task.FromResult<IReadOnlyList<PerspectiveWriteOutcome>>(outcomes);
    }
  }

  /// <summary>An apply coordinator that cannot take any lock.</summary>
  private sealed class RefusingCoordinator : IPerspectiveApplyCoordinator {
    public Task<IAsyncDisposable> AcquireAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      throw new InvalidOperationException("no lock");
  }

  /// <summary>Purge markers that hold the ones the rebuild records.</summary>
  private sealed class RecordingPurgeMarkers : IPerspectivePurgeMarkerStore {
    public List<Guid> Marked { get; } = [];

    public Task<bool> IsPurgedAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult(Marked.Contains(streamId));

    public Task<IReadOnlySet<Guid>> PurgedAmongAsync(
        IReadOnlyCollection<Guid> streamIds, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlySet<Guid>>(streamIds.Where(Marked.Contains).ToHashSet());

    public Task MarkPurgedAsync(Guid streamId, string perspectiveName, Guid? purgeEventId, CancellationToken cancellationToken = default) {
      Marked.Add(streamId);
      return Task.CompletedTask;
    }

    public Task ClearAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
      Marked.Remove(streamId);
      return Task.CompletedTask;
    }
  }

  private sealed class RecordingCompleter : IPerspectiveCheckpointCompleter {
    public List<PerspectiveCursorCompletion> Completed { get; } = [];

    public Task CompleteAsync(IReadOnlyList<PerspectiveCursorCompletion> completions, CancellationToken cancellationToken = default) {
      Completed.AddRange(completions);
      return Task.CompletedTask;
    }
  }

  private sealed class TypedLog(RbLog inner) : ILogger<PerspectiveRebuilder> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => inner.Log(logLevel, eventId, state, exception, formatter);
  }

  private sealed class Registry(IPerspectiveRunner runner) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => runner;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }
}
