// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The operator re-sequence of received streams stored out of their origin's order (#1629), against a real database:
/// <see cref="PostgresReceivedStreamResequencer"/> finds them with <c>wh_received_stream_order_window</c>, plans each by
/// the replay's own rule and applies the plan with <c>wh_resequence_stream</c> (migration 555), which rewinds the stream's
/// perspectives from its start. Every seeded stream is stored against its origin's order, so a run that changed nothing
/// fails, and the already-ordered and dry-run cases check that nothing is written.
/// </summary>
/// <docs>operations/infrastructure/resequencing-received-streams</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/555_ReceivedStreamsAreResequencedInTheirOriginsOrder.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresReceivedStreamResequencer.cs</code-under-test>
[Category("Integration")]
[Category("Shard4")]
public class ReceivedStreamResequenceSqlTests : EFCoreTestBase {
  private const string P1 = "Test.ResequencedPerspectiveOne";
  private const string P2 = "Test.ResequencedPerspectiveTwo";
  private const string COLLECTIVE_SINK = "__collective__";
  private const int REWIND_REQUIRED = 32;
  private static readonly Guid _originA = _newId();
  private static readonly Guid _originB = _newId();

  /// <summary>One event to seed: its id, version, origin (null for local), origin sequence and flags.</summary>
  private sealed record Seed(Guid Id, int Version, Guid? Origin = null, long? Sequence = null, int Flags = 0);

  // ---------------------------------------------------------------------------------------------
  // The order
  // ---------------------------------------------------------------------------------------------

  [Test]
  public async Task Resequence_ASingleOriginStoredOutOfOrder_IsStoredInTheOriginsOrderAsync() {
    await using var conn = await _openAsync();
    var stream = _newId();
    var (a, b, c) = _threeIds();
    // #1607's shape: fan-out children carry no origin sequence, and the initializer (the lowest id) was stored last.
    await _seedAsync(conn, stream, new Seed(b, 1, _originA), new Seed(c, 2, _originA), new Seed(a, 3, _originA));

    var report = await _resequencer().ResequenceAsync(_request());

    await Assert.That(await _orderAsync(conn, stream)).IsEquivalentTo([a, b, c], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(await _versionsAsync(conn, stream)).IsEquivalentTo([1, 2, 3], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the versions are only permuted");
    await Assert.That(report.Resequenced).Count().IsEqualTo(1);
    await Assert.That(report.Resequenced[0].StreamId).IsEqualTo(stream);
    await Assert.That(report.Resequenced[0].Moves).IsEquivalentTo(
      [new ResequencedEvent(a, 3, 1), new ResequencedEvent(b, 1, 2), new ResequencedEvent(c, 2, 3)]);
    await Assert.That(report.DryRun).IsFalse();
    await Assert.That(report.StreamsExamined).IsEqualTo(1);
    await Assert.That(report.ResumeAfterStreamId).IsNull();
  }

  [Test]
  public async Task Resequence_AMixedStream_LeavesLocalEventsInPlace_AndOrdersTheOriginsEventsAmongTheirOwnVersionsAsync() {
    await using var conn = await _openAsync();
    var stream = _newId();
    var ids = _ids(4);
    var (local1, late, local2, early) = (ids[0], ids[1], ids[2], ids[3]);
    await _seedAsync(conn, stream,
      new Seed(local1, 1), new Seed(late, 2, _originA, 20), new Seed(local2, 3), new Seed(early, 4, _originA, 10));

    await _resequencer().ResequenceAsync(_request());

    await Assert.That(await _orderAsync(conn, stream)).IsEquivalentTo([local1, early, local2, late], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the local events keep versions 1 and 3; the origin's events take 2 and 4 in its order");
  }

  [Test]
  public async Task Resequence_SeveralOrigins_FixesEachOnesOrderIndependently_AndKeepsTheirInterleavingAsync() {
    await using var conn = await _openAsync();
    var stream = _newId();
    var ids = _ids(4);
    // A holds versions 1 and 3, B holds 2 and 4, each stored against its own sequences.
    await _seedAsync(conn, stream,
      new Seed(ids[0], 1, _originA, 2), new Seed(ids[1], 2, _originB, 9),
      new Seed(ids[2], 3, _originA, 1), new Seed(ids[3], 4, _originB, 8));

    await _resequencer().ResequenceAsync(_request());

    await Assert.That(await _orderAsync(conn, stream)).IsEquivalentTo([ids[2], ids[3], ids[0], ids[1]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("versions 1 and 3 stay A's and 2 and 4 stay B's; within each, the origin's order");
  }

  [Test]
  public async Task Resequence_StreamsAlreadyInOrder_AreExaminedAndLeftUntouchedAsync() {
    await using var conn = await _openAsync();
    var received = _newId();
    var local = _newId();
    var ids = _ids(5);
    await _seedAsync(conn, received, new Seed(ids[0], 1, _originA, 1), new Seed(ids[1], 2), new Seed(ids[2], 3, _originA, 2));
    await _seedAsync(conn, local, new Seed(ids[4], 1), new Seed(ids[3], 2));
    await _cursorAsync(conn, received, P1, ids[2]);

    var report = await _resequencer().ResequenceAsync(_request());

    await Assert.That(report.Resequenced).IsEmpty();
    await Assert.That(report.Skipped).IsEmpty();
    await Assert.That(report.StreamsExamined).IsEqualTo(2);
    await Assert.That(await _orderAsync(conn, local)).IsEquivalentTo([ids[4], ids[3]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a local stream's order is never an origin's to correct, whatever its ids");
    await Assert.That(await _cursorStatusAsync(conn, received, P1) & REWIND_REQUIRED).IsEqualTo(0);
  }

  [Test]
  public async Task Resequence_AStreamTheWindowMarksWhoseOrderIsRight_IsNotMovedAsync() {
    // Positioned events in their origin's order whose ids run backward: the window's cheap check marks the stream, and
    // the planner, which decides, moves nothing.
    await using var conn = await _openAsync();
    var stream = _newId();
    var (a, b, _) = _threeIds();
    await _seedAsync(conn, stream, new Seed(b, 1, _originA, 1), new Seed(a, 2, _originA, 2));

    var dry = await _resequencer().ResequenceAsync(_request() with { DryRun = true });
    var real = await _resequencer().ResequenceAsync(_request());

    await Assert.That(dry.Resequenced).IsEmpty();
    await Assert.That(real.Resequenced).IsEmpty();
    await Assert.That(await _orderAsync(conn, stream)).IsEquivalentTo([b, a], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  // ---------------------------------------------------------------------------------------------
  // Dry run and the perspectives
  // ---------------------------------------------------------------------------------------------

  [Test]
  public async Task DryRun_ReportsWhatItWouldChange_AndChangesNothingAsync() {
    await using var conn = await _openAsync();
    var first = _newId();
    var second = _newId();
    var ids = _ids(4);
    await _seedAsync(conn, first, new Seed(ids[1], 1, _originA), new Seed(ids[0], 2, _originA));
    await _seedAsync(conn, second, new Seed(ids[3], 1, _originB), new Seed(ids[2], 2, _originB));
    await _cursorAsync(conn, first, P1, ids[0]);
    await _cursorAsync(conn, first, COLLECTIVE_SINK, ids[0]);
    await _snapshotAsync(conn, first, P1, ids[1], 1);

    var report = await _resequencer().ResequenceAsync(_request() with { DryRun = true });

    await Assert.That(report.DryRun).IsTrue();
    await Assert.That(report.Resequenced.Select(s => s.StreamId)).IsEquivalentTo([first, second]);
    var firstReport = report.Resequenced.Single(s => s.StreamId == first);
    await Assert.That(firstReport.Moves).IsEquivalentTo([new ResequencedEvent(ids[0], 2, 1), new ResequencedEvent(ids[1], 1, 2)]);
    await Assert.That(firstReport.PerspectivesRewound).IsEqualTo(1).Because("the collective sink is never rewound");
    await Assert.That(firstReport.SnapshotsRemoved).IsEqualTo(1);
    await Assert.That(await _orderAsync(conn, first)).IsEquivalentTo([ids[1], ids[0]], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(await _cursorStatusAsync(conn, first, P1) & REWIND_REQUIRED).IsEqualTo(0);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_perspective_snapshots WHERE stream_id = $1", first)).IsEqualTo(1L);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_perspective_events WHERE stream_id = $1", first)).IsEqualTo(0L);
  }

  [Test]
  public async Task Resequence_RewindsEveryPerspectiveOfTheStreamFromItsStart_AndLeavesTheCollectiveSinkAloneAsync() {
    await using var conn = await _openAsync();
    var stream = _newId();
    var (a, b, c) = _threeIds();
    await _seedAsync(conn, stream, new Seed(b, 1, _originA), new Seed(c, 2, _originA), new Seed(a, 3, _originA));
    await _cursorAsync(conn, stream, P1, a, replayedThrough: 99);
    await _cursorAsync(conn, stream, P2, a);
    await _cursorAsync(conn, stream, COLLECTIVE_SINK, a);
    await _snapshotAsync(conn, stream, P1, c, 2);
    await _snapshotAsync(conn, stream, P2, b, 1);
    // P2 already applied the event that becomes the stream's first, and debug mode kept its processed row.
    await _executeAsync(conn,
      "INSERT INTO wh_perspective_events (stream_id, perspective_name, event_id, status, attempts, created_at, processed_at) "
      + "VALUES ($1, $2, $3, 2, 1, NOW(), NOW())", stream, P2, a);

    var report = await _resequencer().ResequenceAsync(_request());

    await Assert.That(report.Resequenced[0].PerspectivesRewound).IsEqualTo(2);
    await Assert.That(report.Resequenced[0].SnapshotsRemoved).IsEqualTo(2);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_perspective_snapshots WHERE stream_id = $1", stream))
      .IsEqualTo(0L).Because("every snapshot held a fold in the old order");
    foreach (var perspective in new[] { P1, P2 }) {
      await Assert.That(await _cursorStatusAsync(conn, stream, perspective) & REWIND_REQUIRED).IsEqualTo(REWIND_REQUIRED);
      await Assert.That(await _scalarAsync<Guid>(conn,
        "SELECT rewind_trigger_event_id FROM wh_perspective_cursors WHERE stream_id = $1 AND perspective_name = $2", stream, perspective))
        .IsEqualTo(a).Because("the trigger is the stream's first event now, so no snapshot can sit below it: a full replay");
      await Assert.That(await _scalarAsync<long>(conn,
        "SELECT count(*) FROM wh_perspective_cursors WHERE stream_id = $1 AND perspective_name = $2 AND replayed_through_commit_sequence IS NULL",
        stream, perspective)).IsEqualTo(1L).Because("a frontier read in the old order covers nothing in the new one");
      await Assert.That(await _scalarAsync<long>(conn,
        "SELECT count(*) FROM wh_perspective_events WHERE stream_id = $1 AND perspective_name = $2 AND event_id = $3 AND processed_at IS NULL AND instance_id IS NULL",
        stream, perspective, a)).IsEqualTo(1L).Because("the trigger is queued, unleased, so the rewind runs at once");
    }
    await Assert.That(await _cursorStatusAsync(conn, stream, COLLECTIVE_SINK) & REWIND_REQUIRED).IsEqualTo(0);
  }

  [Test]
  public async Task Apply_ACompletionThatRacesTheFlag_LeavesTheRewindAskedForAsync() {
    // A worker that claimed work before the re-sequence committed completes after it, at the stream's last event. The
    // completion clears a flag whose trigger it passed; the queued trigger below it flags the cursor again.
    await using var conn = await _openAsync();
    var stream = _newId();
    var (a, b, c) = _threeIds();
    await _seedAsync(conn, stream, new Seed(b, 1, _originA), new Seed(c, 2, _originA), new Seed(a, 3, _originA));
    await _cursorAsync(conn, stream, P1, c);
    await _resequencer().ResequenceAsync(_request());

    await _executeAsync(conn, "SELECT complete_perspective_cursor_work($1, $2, $3, $4::jsonb, 2::smallint)",
      stream, P1, c, $"[\"{c}\"]");

    await Assert.That(await _cursorStatusAsync(conn, stream, P1) & REWIND_REQUIRED).IsEqualTo(REWIND_REQUIRED);
    await Assert.That(await _scalarAsync<Guid>(conn,
      "SELECT rewind_trigger_event_id FROM wh_perspective_cursors WHERE stream_id = $1 AND perspective_name = $2", stream, P1)).IsEqualTo(a);
  }

  // ---------------------------------------------------------------------------------------------
  // A live system
  // ---------------------------------------------------------------------------------------------

  [Test]
  public async Task Resequence_AStreamBeingWritten_IsReportedBusy_AndARerunFixesItAsync() {
    await using var conn = await _openAsync();
    var stream = _newId();
    var (a, b, _) = _threeIds();
    await _seedAsync(conn, stream, new Seed(b, 1, _originA), new Seed(a, 2, _originA));

    ReceivedStreamResequenceReport busy;
    await using (var writer = await _holdStreamLockAsync(stream)) {
      busy = await _resequencer().ResequenceAsync(_request());
      await writer.Transaction.CommitAsync();
    }
    var rerun = await _resequencer().ResequenceAsync(_request());

    await Assert.That(busy.Skipped).IsEquivalentTo([new StreamLeftAlone(stream, ResequenceSkipReason.Busy)]);
    await Assert.That(busy.Resequenced).IsEmpty().Because("the run never waits for a writer");
    await Assert.That(rerun.Resequenced.Select(s => s.StreamId)).IsEquivalentTo([stream]);
    await Assert.That(await _orderAsync(conn, stream)).IsEquivalentTo([a, b], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Apply_AnAppendDuringTheRun_WaitsForIt_AndLandsAfterTheStreamsLastVersionAsync() {
    await using var conn = await _openAsync();
    var stream = _newId();
    var (a, b, c) = _threeIds();
    await _seedAsync(conn, stream, new Seed(c, 1, _originA), new Seed(b, 2, _originA), new Seed(a, 3, _originA));

    await using var runner = await _openAsync();
    await using var run = await runner.BeginTransactionAsync();
    await _applyAsync(runner, run, stream, [a, c], [3, 1], [1, 3]);
    await using var context = CreateDbContext();
    var append = new EFCoreEventStore<WorkCoordinationDbContext>(context).AppendAsync(stream, _envelope());
    var waited = await _untilWaitingAsync("advisory", append);
    await run.CommitAsync();
    await append;

    await Assert.That(waited).IsTrue().Because("an append takes the lock the re-sequence holds");
    await Assert.That(await _versionsAsync(conn, stream)).IsEquivalentTo([1, 2, 3, 4], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the append numbered after the run committed, from a last version the run never changes");
    await Assert.That((await _orderAsync(conn, stream))[..3]).IsEquivalentTo([a, b, c], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Resequence_AnEventRemovedByAPathThatTakesNoStreamLock_LeavesTheStreamForARerunAsync() {
    // A remover that holds a row lock but not the stream's lock (as a reaper may) deletes one of the stream's events
    // between the run's read and its write. The write finds the plan no longer matches and changes nothing.
    await using var conn = await _openAsync();
    var stream = _newId();
    var (a, b, c) = _threeIds();
    await _seedAsync(conn, stream, new Seed(c, 1, _originA), new Seed(b, 2, _originA), new Seed(a, 3, _originA));

    await using var remover = await _openAsync();
    await using var removal = await remover.BeginTransactionAsync();
    await using (var delete = new NpgsqlCommand("DELETE FROM wh_event_store WHERE event_id = $1", remover, removal)) {
      delete.Parameters.AddWithValue(c);
      await delete.ExecuteNonQueryAsync();
    }
    var run = _resequencer().ResequenceAsync(_request());
    var waited = await _untilWaitingAsync("transactionid", run);
    await removal.CommitAsync();
    var report = await run;

    await Assert.That(waited).IsTrue();
    await Assert.That(report.Skipped).IsEquivalentTo([new StreamLeftAlone(stream, ResequenceSkipReason.Changed)]);
    await Assert.That(await _orderAsync(conn, stream)).IsEquivalentTo([b, a], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("nothing was written, so the survivors keep their versions");
    var rerun = await _resequencer().ResequenceAsync(_request());
    await Assert.That(rerun.Resequenced.Select(s => s.StreamId)).IsEquivalentTo([stream]);
    await Assert.That(await _orderAsync(conn, stream)).IsEquivalentTo([a, b], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Resequence_ACrashMidRun_RollsBackItsBatchWhole_AndARerunFinishesAsync() {
    await using var conn = await _openAsync();
    var streams = _ids(2);
    var ids = _ids(4);
    await _seedAsync(conn, streams[0], new Seed(ids[1], 1, _originA), new Seed(ids[0], 2, _originA));
    await _seedAsync(conn, streams[1], new Seed(ids[3], 1, _originA), new Seed(ids[2], 2, _originA));
    await _cursorAsync(conn, streams[1], P1, ids[2]);
    await _snapshotAsync(conn, streams[1], P1, ids[3], 1);
    // The second batch fails part way through its write: after the renumber, at the cursor flag.
    await _executeAsync(conn, $"""
      CREATE FUNCTION wh_test_crash() RETURNS TRIGGER AS $f$ BEGIN RAISE EXCEPTION 'crash'; END; $f$ LANGUAGE plpgsql;
      CREATE TRIGGER wh_test_crash_trg BEFORE UPDATE ON wh_perspective_cursors FOR EACH ROW
        WHEN (NEW.stream_id = '{streams[1]}'::uuid) EXECUTE FUNCTION wh_test_crash();
      """);

    await Assert.That(async () => await _resequencer().ResequenceAsync(_request() with { BatchSize = 1 })).Throws<PostgresException>();

    await Assert.That(await _orderAsync(conn, streams[0])).IsEquivalentTo([ids[0], ids[1]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the first batch committed before the crash");
    await Assert.That(await _orderAsync(conn, streams[1])).IsEquivalentTo([ids[3], ids[2]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the crashed batch rolled back whole, its renumber included");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_perspective_snapshots WHERE stream_id = $1", streams[1])).IsEqualTo(1L);

    await _executeAsync(conn, "DROP TRIGGER wh_test_crash_trg ON wh_perspective_cursors");
    var rerun = await _resequencer().ResequenceAsync(_request() with { BatchSize = 1 });

    await Assert.That(rerun.Resequenced.Select(s => s.StreamId)).IsEquivalentTo([streams[1]])
      .Because("the stream the first run finished is in order now, and only the other is touched");
    await Assert.That(await _orderAsync(conn, streams[1])).IsEquivalentTo([ids[2], ids[3]], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(await _cursorStatusAsync(conn, streams[1], P1) & REWIND_REQUIRED).IsEqualTo(REWIND_REQUIRED);
  }

  [Test]
  public async Task Resequence_ABoundedRun_StopsWhereItSays_AndResumesFromThereAsync() {
    await using var conn = await _openAsync();
    var streams = _ids(3);
    var ids = _ids(6);
    for (var i = 0; i < 3; i++) {
      await _seedAsync(conn, streams[i], new Seed(ids[(2 * i) + 1], 1, _originA), new Seed(ids[2 * i], 2, _originA));
    }

    var first = await _resequencer().ResequenceAsync(_request() with { BatchSize = 1, MaxStreams = 2 });
    await Assert.That(await _orderAsync(conn, streams[2])).IsEquivalentTo([ids[5], ids[4]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the bounded run stopped before it");
    var second = await _resequencer().ResequenceAsync(_request() with { StartAfterStreamId = first.ResumeAfterStreamId });

    await Assert.That(first.StreamsExamined).IsEqualTo(2);
    await Assert.That(first.ResumeAfterStreamId).IsEqualTo(streams[1]);
    await Assert.That(first.Resequenced.Select(s => s.StreamId)).IsEquivalentTo([streams[0], streams[1]]);
    await Assert.That(second.StreamsExamined).IsEqualTo(1);
    await Assert.That(second.ResumeAfterStreamId).IsNull();
    await Assert.That(second.Resequenced.Select(s => s.StreamId)).IsEquivalentTo([streams[2]]);
  }

  [Test]
  public async Task Resequence_EphemeralCollectiveAndArchivedStreams_AreLeftAloneAndReportedAsync() {
    await using var conn = await _openAsync();
    var streams = _ids(3);
    var ids = _ids(6);
    var (ephemeral, collective, archived) = (streams[0], streams[1], streams[2]);
    await _seedAsync(conn, ephemeral, new Seed(ids[1], 1, _originA, Flags: 8), new Seed(ids[0], 2, _originA, Flags: 8));
    await _seedAsync(conn, collective, new Seed(ids[3], 1, _originA, Flags: 1), new Seed(ids[2], 2, _originA));
    await _seedAsync(conn, archived, new Seed(ids[5], 2, _originA), new Seed(ids[4], 3, _originA));
    await _executeAsync(conn,
      "INSERT INTO wh_event_archive (event_id, stream_id, event_type, version) VALUES ($1, $2, 'Test.Event, Test', 1)", _newId(), archived);

    var report = await _resequencer().ResequenceAsync(_request());

    await Assert.That(report.Resequenced).IsEmpty();
    await Assert.That(report.Skipped).IsEquivalentTo([
      new StreamLeftAlone(ephemeral, ResequenceSkipReason.Ephemeral),
      new StreamLeftAlone(collective, ResequenceSkipReason.Collective),
      new StreamLeftAlone(archived, ResequenceSkipReason.Archived)]);
    await Assert.That(await _orderAsync(conn, archived)).IsEquivalentTo([ids[5], ids[4]], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  // ---------------------------------------------------------------------------------------------
  // The window and the apply's guards
  // ---------------------------------------------------------------------------------------------

  [Test]
  public async Task Window_MarksEveryStreamThePlannerWouldMove_AndNoStreamInOrder_OverEveryArrangementAsync() {
    // Two origins with and without sequences and a local event, whose ids agree with their sequences, stored at every
    // permutation of their versions: one stream per arrangement. The window's check must mark exactly the streams the
    // planner moves; a stream it missed would never be read.
    await using var conn = await _openAsync();
    var ids = _ids(5);
    Seed[] events = [
      new(ids[0], 0, _originA, 1), new(ids[1], 0, _originB), new(ids[2], 0, _originA),
      new(ids[3], 0, _originA, 2), new(ids[4], 0)];
    var expected = new Dictionary<Guid, bool>();
    foreach (var versions in _permutations([1, 2, 3, 4, 5])) {
      var stream = _newId();
      // Fresh ids per stream, in the same relative order as the template's.
      var fresh = _ids(5);
      var seeds = events.Select((e, i) => e with { Id = fresh[i], Version = versions[i] }).ToArray();
      await _seedAsync(conn, stream, seeds);
      var planned = ReceivedStreamOrder.Resequence([.. seeds.Select(s => new StoredStreamEvent(s.Id, s.Version, s.Origin ?? Guid.Empty, s.Sequence ?? 0))]);
      expected[stream] = planned.Count > 0;
    }

    var marked = new Dictionary<Guid, bool>();
    await using (var window = new NpgsqlCommand("SELECT stream_id, out_of_order FROM wh_received_stream_order_window($1, 1000)", conn)) {
      window.Parameters.AddWithValue(Guid.Empty);
      await using var reader = await window.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        marked[reader.GetGuid(0)] = reader.GetBoolean(1);
      }
    }

    await Assert.That(marked.Count).IsEqualTo(120);
    await Assert.That(expected.Count(e => e.Value)).IsGreaterThan(0).Because("a check over no out-of-order stream proves nothing");
    await Assert.That(expected.Where(e => marked[e.Key] != e.Value).Select(e => e.Key)).IsEmpty();
  }

  [Test]
  public async Task Apply_APlanTheStreamNoLongerMatches_ChangesNothingAsync() {
    await using var conn = await _openAsync();
    var stream = _newId();
    var (a, b, _) = _threeIds();
    await _seedAsync(conn, stream, new Seed(b, 1, _originA), new Seed(a, 2, _originA));
    await _cursorAsync(conn, stream, P1, a);

    await using var tx = await conn.BeginTransactionAsync();
    var outcome = await _applyAsync(conn, tx, stream, [a, b], [5, 1], [1, 5]);
    await tx.CommitAsync();

    await Assert.That(outcome).IsEqualTo("changed");
    await Assert.That(await _orderAsync(conn, stream)).IsEquivalentTo([b, a], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(await _cursorStatusAsync(conn, stream, P1) & REWIND_REQUIRED).IsEqualTo(0);
  }

  [Test]
  public async Task Apply_APlanThatIsNotAPermutationOfItsVersions_IsRefusedAsync() {
    await using var conn = await _openAsync();
    var stream = _newId();
    var (a, b, _) = _threeIds();
    await _seedAsync(conn, stream, new Seed(b, 1, _originA), new Seed(a, 2, _originA));

    foreach (var (events, from, to) in new (Guid[], int[], int[])[] {
      ([a, b], [2, 1], [1, 3]),   // a version the stream does not hold
      ([a, b], [2, 1], [1]),      // arrays of different lengths
      ([a, a], [2, 2], [2, 2]),   // one event twice
      ([], [], []),               // nothing
    }) {
      await using var tx = await conn.BeginTransactionAsync();
      var refused = await Assert.That(async () => await _applyAsync(conn, tx, stream, events, from, to)).Throws<PostgresException>();
      await Assert.That(refused!.SqlState).IsEqualTo("22023");
      await tx.RollbackAsync();
    }
    await Assert.That(await _orderAsync(conn, stream)).IsEquivalentTo([b, a], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Apply_AStreamStartingAtVersionZero_IsRenumberedWithoutMeetingItselfAsync() {
    // A stream written before #1913 starts at version 0; renumbering through plain negatives would leave 0 in place
    // while another event takes it.
    await using var conn = await _openAsync();
    var stream = _newId();
    var (a, b, _) = _threeIds();
    await _seedAsync(conn, stream, new Seed(b, 0, _originA), new Seed(a, 1, _originA));

    await _resequencer().ResequenceAsync(_request());

    await Assert.That(await _orderAsync(conn, stream)).IsEquivalentTo([a, b], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(await _versionsAsync(conn, stream)).IsEquivalentTo([0, 1], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  // ---------------------------------------------------------------------------------------------
  // The request and the report
  // ---------------------------------------------------------------------------------------------

  [Test]
  public async Task Request_WithoutWhoOrWhyOrAPositiveBound_IsRefusedAsync() {
    var resequencer = _resequencer();
    await Assert.That(async () => await resequencer.ResequenceAsync(null!)).Throws<ArgumentNullException>();
    foreach (var request in new[] {
      _request() with { RequestedBy = " " },
      _request() with { Reason = "" },
      _request() with { BatchSize = 0 },
      _request() with { MaxStreams = 0 },
    }) {
      await Assert.That(async () => await resequencer.ResequenceAsync(request)).Throws<ArgumentException>();
    }
  }

  [Test]
  public async Task Report_Format_SaysWhatChangedAndWhatWasLeftAloneAsync() {
    var run = _newId();
    var (moved, left, resume) = (_newId(), _newId(), _newId());
    var done = new ReceivedStreamResequenceReport(run, false, 7,
      [new StreamResequence(moved, [new ResequencedEvent(_newId(), 2, 1), new ResequencedEvent(_newId(), 1, 2)], 3, 1)],
      [new StreamLeftAlone(left, ResequenceSkipReason.Busy)], null);
    var dry = done with { DryRun = true, ResumeAfterStreamId = resume };

    var lines = done.Format().Split(Environment.NewLine);

    await Assert.That(lines).Count().IsEqualTo(3);
    await Assert.That(lines[0]).IsEqualTo($"Re-sequenced 1 stream(s) of 7 examined (2 event(s) moved); 1 left alone; whole store walked; run {run}");
    await Assert.That(lines[1]).IsEqualTo($"  {moved}: 2 moved, 3 perspective(s) rewound, 1 snapshot(s) removed");
    await Assert.That(lines[2]).IsEqualTo($"  {left}: left alone (Busy)");
    await Assert.That(dry.Format()).StartsWith($"Dry run: would re-sequence 1 stream(s) of 7 examined (2 event(s) moved); 1 left alone; stopped after {resume}");
  }

  [Test]
  public async Task Resequence_WithALogger_RecordsTheRunAndEachBatch_AndAnEmptyStoreWalksNothingAsync() {
    await using var conn = await _openAsync();
    var stream = _newId();
    var (a, b, _) = _threeIds();
    await _seedAsync(conn, stream, new Seed(b, 1, _originA), new Seed(a, 2, _originA));
    var logger = new ListLogger();

    var report = await _resequencer(logger).ResequenceAsync(_request());
    var nothingAfter = await _resequencer(logger).ResequenceAsync(_request() with { StartAfterStreamId = stream });

    await Assert.That(report.Resequenced).Count().IsEqualTo(1);
    await Assert.That(nothingAfter.StreamsExamined).IsEqualTo(0);
    await Assert.That(logger.Messages.Count(m => m.Contains(report.RunId.ToString(), StringComparison.Ordinal))).IsEqualTo(3)
      .Because("the start, the one batch and the run are each recorded");
  }

  [Test]
  public async Task Resequence_WithANamedSchema_ReadsAndWritesThatSchemasTablesAsync() {
    await using var conn = await _openAsync();
    var schema = await _scalarAsync<string>(conn, "SELECT current_schema()");
    var stream = _newId();
    var (a, b, _) = _threeIds();
    await _seedAsync(conn, stream, new Seed(b, 1, _originA), new Seed(a, 2, _originA));

    var report = await new PostgresReceivedStreamResequencer(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync, schema)
      .ResequenceAsync(_request());

    await Assert.That(report.Resequenced.Select(s => s.StreamId)).IsEquivalentTo([stream]);
    await Assert.That(await _orderAsync(conn, stream)).IsEquivalentTo([a, b], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  // ---------------------------------------------------------------------------------------------
  // Helpers
  // ---------------------------------------------------------------------------------------------

  private static ReceivedStreamResequenceRequest _request() => new() { RequestedBy = "operator-1", Reason = "streams stored before the receive-order fix" };

  private PostgresReceivedStreamResequencer _resequencer(Microsoft.Extensions.Logging.ILogger<PostgresReceivedStreamResequencer>? logger = null) =>
    new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync, logger: logger);

  private static Guid _newId() => TrackedGuid.New().Value;

  /// <summary>Fresh ids sorted in the database's uuid order, which is the canonical text's.</summary>
  private static Guid[] _ids(int count) =>
    [.. Enumerable.Range(0, count).Select(_ => _newId()).OrderBy(g => g.ToString("D"), StringComparer.Ordinal)];

  private static (Guid, Guid, Guid) _threeIds() {
    var ids = _ids(3);
    return (ids[0], ids[1], ids[2]);
  }

  private static async Task _seedAsync(NpgsqlConnection conn, Guid stream, params Seed[] events) {
    foreach (var e in events) {
      await using var insert = new NpgsqlCommand("""
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at, flags,
                                    origin_service_id, origin_commit_sequence, commit_sequence)
        VALUES ($1, $2, $2, 'Test', 'Test.Event, Test', $3, NOW(), $4, $5, $6,
                (SELECT COALESCE(MAX(commit_sequence), 0) + 1 FROM wh_event_store))
        """, conn);
      insert.Parameters.AddWithValue(e.Id);
      insert.Parameters.AddWithValue(stream);
      insert.Parameters.AddWithValue(e.Version);
      insert.Parameters.AddWithValue(e.Flags);
      insert.Parameters.Add(new NpgsqlParameter { Value = (object?)e.Origin ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
      insert.Parameters.Add(new NpgsqlParameter { Value = (object?)e.Sequence ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
      await insert.ExecuteNonQueryAsync();
    }
  }

  private static Task _cursorAsync(NpgsqlConnection conn, Guid stream, string perspective, Guid lastEventId, long? replayedThrough = null) =>
    _executeAsync(conn,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, replayed_through_commit_sequence) VALUES ($1, $2, $3, 2, $4)",
      stream, perspective, lastEventId, (object?)replayedThrough ?? DBNull.Value);

  private static Task _snapshotAsync(NpgsqlConnection conn, Guid stream, string perspective, Guid eventId, int version) =>
    _executeAsync(conn,
      "INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_version) "
      + "VALUES ($1, $2, $3, '{}'::jsonb, $4, $4)",
      stream, perspective, eventId, version);

  private static async Task<int> _cursorStatusAsync(NpgsqlConnection conn, Guid stream, string perspective) =>
    await _scalarAsync<short>(conn, "SELECT status FROM wh_perspective_cursors WHERE stream_id = $1 AND perspective_name = $2", stream, perspective);

  private static async Task<string> _applyAsync(
      NpgsqlConnection conn, NpgsqlTransaction tx, Guid stream, Guid[] events, int[] from, int[] to) {
    await using var apply = new NpgsqlCommand("SELECT outcome FROM wh_resequence_stream($1, $2, $3, $4, NOW())", conn, tx);
    apply.Parameters.AddWithValue(stream);
    apply.Parameters.Add(new NpgsqlParameter { Value = events, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
    apply.Parameters.Add(new NpgsqlParameter { Value = from, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
    apply.Parameters.Add(new NpgsqlParameter { Value = to, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
    return (string)(await apply.ExecuteScalarAsync())!;
  }

  private static async Task<Guid[]> _orderAsync(NpgsqlConnection conn, Guid stream) {
    await using var command = new NpgsqlCommand("SELECT event_id FROM wh_event_store WHERE stream_id = $1 ORDER BY version", conn);
    command.Parameters.AddWithValue(stream);
    var ids = new List<Guid>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return [.. ids];
  }

  private static async Task<int[]> _versionsAsync(NpgsqlConnection conn, Guid stream) {
    await using var command = new NpgsqlCommand("SELECT version FROM wh_event_store WHERE stream_id = $1 ORDER BY version", conn);
    command.Parameters.AddWithValue(stream);
    var versions = new List<int>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      versions.Add(reader.GetInt32(0));
    }
    return [.. versions];
  }

  private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger<PostgresReceivedStreamResequencer> {
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
  }

  private sealed class LockHolder(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable {
    public NpgsqlTransaction Transaction { get; } = transaction;

    public async ValueTask DisposeAsync() {
      await Transaction.DisposeAsync();
      await connection.DisposeAsync();
    }
  }

  /// <summary>Takes the emit chains' lock for the stream in an open transaction, as a writer does.</summary>
  private async Task<LockHolder> _holdStreamLockAsync(Guid stream) {
    var connection = await _openAsync();
    var transaction = await connection.BeginTransactionAsync();
    await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('wh_event_store:' || $1::text))", connection, transaction);
    command.Parameters.AddWithValue(stream);
    await command.ExecuteNonQueryAsync();
    return new LockHolder(connection, transaction);
  }

  /// <summary>Waits until a session of this database waits on a lock of the kind named, or the call ends first.</summary>
  private async Task<bool> _untilWaitingAsync(string lockType, Task call) {
    await using var observer = await _openAsync();
    await using var command = new NpgsqlCommand("""
      SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
      WHERE l.locktype = $1 AND NOT l.granted AND a.datname = current_database()
      """, observer);
    command.Parameters.AddWithValue(lockType);
    while (!call.IsCompleted) {
      if (Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0) {
        return true;
      }
      await Task.Yield();
    }
    return false;
  }

  private static MessageEnvelope<OrderCreatedEvent> _envelope() => new() {
    MessageId = MessageId.New(),
    Payload = new OrderCreatedEvent { OrderId = _newId(), CustomerName = "Appended during a re-sequence" },
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params object[] args) {
    await using var command = new NpgsqlCommand(sql, conn);
    foreach (var arg in args) {
      command.Parameters.AddWithValue(arg);
    }
    await command.ExecuteNonQueryAsync();
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params object[] args) {
    await using var command = new NpgsqlCommand(sql, conn);
    foreach (var arg in args) {
      command.Parameters.AddWithValue(arg);
    }
    return (T)(await command.ExecuteScalarAsync())!;
  }

  private static IEnumerable<int[]> _permutations(int[] items) {
    if (items.Length <= 1) {
      yield return items;
      yield break;
    }
    for (var i = 0; i < items.Length; i++) {
      var rest = items.Where((_, j) => j != i).ToArray();
      foreach (var tail in _permutations(rest)) {
        yield return [items[i], .. tail];
      }
    }
  }
}
