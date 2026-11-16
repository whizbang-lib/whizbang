// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.Cursors;

/// <summary>
/// #2366: a perspective cursor follows one order for its stream, the version, with the commit sequence as a replay's
/// frontier. Each test seeds a stream whose ids disagree with its versions, so a rule that compared ids would answer
/// differently from one that compares versions.
/// </summary>
/// <docs>fundamentals/perspectives/rewind#one-order-for-a-stream</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/455_StaleCopiesAboveTheNewestAreRewalked.sql</code-under-test>
[Category("Shard2")]
public class PerspectiveCursorOneOrderSqlTests : EFCoreTestBase {
  private const string PERSPECTIVE = "Test.OneOrderPerspective";
  private const int COMPLETED = 2;
  private const int REWIND_REQUIRED = 32;
  private static readonly DateTimeOffset _minted = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task CompleteCursorWork_AnEarlierVersionThanTheCursor_LeavesTheCursorWhereItIsAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    // Version order e1, e2, e3; e1 carries the highest id.
    var (e1, e2, e3) = (_id(30), _id(10), _id(20));
    await _appendAsync(conn, stream, e1, e2, e3);
    await _cursorAsync(conn, stream, e3, COMPLETED, trigger: null);

    await _reportAsync(_completion(stream, e1));

    await Assert.That((await _readAsync(conn, stream)).LastEventId).IsEqualTo(e3)
      .Because("the cursor never moves back in the stream's order, whatever the reported id sorts as");
  }

  /// <summary>
  /// The immediate completion path creates the cursor of a stream it has none for, as the batched path does: without
  /// it, a new stream's cursor was never written, so every apply read from the stream's start and no inversion or
  /// late arrival could be judged against a position.
  /// </summary>
  [Test]
  public async Task CompleteCursorWork_AStreamWithNoCursor_GetsOneAtTheReportedEventAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    var (e1, e2) = (_id(10), _id(20));
    await _appendAsync(conn, stream, e1, e2);

    await _reportAsync(_completion(stream, e2));

    var cursor = await _readAsync(conn, stream);
    await Assert.That(cursor.LastEventId).IsEqualTo(e2).Because("the cursor is created at the event the completion reports");
    await Assert.That(cursor.Status).IsEqualTo(COMPLETED);
    await Assert.That(cursor.Trigger).IsNull();
  }

  /// <summary>
  /// The design choice, locked: both completion paths create a missing cursor, and create the same one. A stream whose
  /// work is completed only on the immediate path (the instant completion strategy) must get the cursor the batched path
  /// would have written; if either path stopped creating it, or created it elsewhere, the two streams below differ.
  /// </summary>
  [Test]
  public async Task CompleteCursor_AStreamWithNoCursor_GetsTheSameCursorOnTheImmediateAndTheBatchedPathAsync() {
    await using var conn = await _openAsync();
    var (immediate, batched) = (TrackedGuid.New(), TrackedGuid.New());
    var (i1, i2) = (_id(110), _id(120));
    var (b1, b2) = (_id(210), _id(220));
    await _appendAsync(conn, immediate, i1, i2);
    await _appendAsync(conn, batched, b1, b2);

    await _reportAsync(_completion(immediate, i2));
    await _flushAsync(_completion(batched, b2));

    var (onImmediate, onBatched) = (await _readAsync(conn, immediate), await _readAsync(conn, batched));
    await Assert.That(onImmediate.LastEventId).IsEqualTo(i2)
      .Because("the immediate path creates the cursor at the reported event, as the batched path does");
    await Assert.That(onBatched.LastEventId).IsEqualTo(b2);
    await Assert.That(onImmediate.Status).IsEqualTo(onBatched.Status)
      .Because("the two paths write the same cursor for a stream that had none");
    await Assert.That(onImmediate.Trigger).IsEqualTo(onBatched.Trigger);
  }

  [Test]
  public async Task CompleteCursorWork_AnEventTheStoreDoesNotHold_CreatesNoCursorAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    await _appendAsync(conn, stream, _id(10));

    await _reportAsync(_completion(stream, _id(99)));

    await using var cmd = new NpgsqlCommand("SELECT count(*) FROM wh_perspective_cursors WHERE stream_id = @s", conn);
    cmd.Parameters.AddWithValue("s", (Guid)stream);
    await Assert.That(Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture)).IsEqualTo(0L)
      .Because("a position the store cannot place is never written as a new cursor");
  }

  [Test]
  public async Task CompleteCursorWork_AReplayAtTheLastVersion_MovesTheCursorStoresItsFrontierAndClearsTheFlagAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    // The stuck stream: the newest event (highest id) at version 1, three older copies stored after it.
    var (terminal, c0, c1, c2) = (_id(100), _id(1), _id(2), _id(3));
    await _appendAsync(conn, stream, terminal, c0, c1, c2);
    await _cursorAsync(conn, stream, terminal, COMPLETED | REWIND_REQUIRED, trigger: c0);
    var frontier = await _commitSequenceAsync(conn, c2);

    await _reportAsync(_completion(stream, c2) with { LastCommitSequence = frontier, ReplayedThroughCommitSequence = frontier });

    var cursor = await _readAsync(conn, stream);
    await Assert.That(cursor.LastEventId).IsEqualTo(c2).Because("the replay read the stream through its last version");
    await Assert.That(cursor.Status).IsEqualTo(COMPLETED).Because("the cursor is past the trigger, so the rewind is done");
    await Assert.That(cursor.Trigger).IsNull();
    await Assert.That(cursor.ReplayedThrough).IsEqualTo(frontier);
  }

  [Test]
  public async Task CompleteCursorWork_APositionBelowTheTrigger_KeepsTheFlagAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    var (e1, e2, e3) = (_id(30), _id(20), _id(10));
    await _appendAsync(conn, stream, e1, e2, e3);
    await _cursorAsync(conn, stream, e1, COMPLETED | REWIND_REQUIRED, trigger: e3);

    // A live apply reaching version 2: the trigger at version 3 was not replayed.
    await _reportAsync(_completion(stream, e2));

    var cursor = await _readAsync(conn, stream);
    await Assert.That(cursor.LastEventId).IsEqualTo(e2);
    await Assert.That(cursor.Status & REWIND_REQUIRED).IsEqualTo(REWIND_REQUIRED)
      .Because("a completion that never reached the trigger cannot settle the rewind it asks for");
    await Assert.That(cursor.Trigger).IsEqualTo(e3);
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task CompleteCursorWork_APendingRowBelowTheCursorByVersion_IsFlagged_UnlessAReplayReadItAsync(bool replayed) {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    // e2 sorts above e3 by id, so an id comparison would never call it a straggler; by version it is one.
    var (e1, e2, e3) = (_id(10), _id(30), _id(20));
    await _appendAsync(conn, stream, e1, e2, e3);
    await _cursorAsync(conn, stream, e1, COMPLETED, trigger: null);
    await _pendingAsync(conn, stream, e2);
    var frontier = await _commitSequenceAsync(conn, e3);

    await _reportAsync(_completion(stream, e3) with { ReplayedThroughCommitSequence = replayed ? frontier : null });

    var cursor = await _readAsync(conn, stream);
    await Assert.That(cursor.LastEventId).IsEqualTo(e3);
    if (replayed) {
      await Assert.That(cursor.Status & REWIND_REQUIRED).IsEqualTo(0)
        .Because("a replay from the stream's start read every event through its frontier; the pending row is a copy queued again");
    } else {
      await Assert.That(cursor.Status & REWIND_REQUIRED).IsEqualTo(REWIND_REQUIRED)
        .Because("the pending event sits below the cursor in the stream's order and the run never saw it");
      await Assert.That(cursor.Trigger).IsEqualTo(e2);
    }
  }

  [Test]
  public async Task CompletePerspective_AReplayFrontier_IsStoredOnTheCursor_AndNeverLoweredAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    var (e1, e2) = (_id(10), _id(20));
    await _appendAsync(conn, stream, e1, e2);

    await _flushAsync(_completion(stream, e2) with { ReplayedThroughCommitSequence = 900 });
    await Assert.That((await _readAsync(conn, stream)).ReplayedThrough).IsEqualTo(900L)
      .Because("the batched path creates the cursor with the frontier the replay reported");

    await _flushAsync(_completion(stream, e2) with { ReplayedThroughCommitSequence = 400 });
    await _flushAsync(_completion(stream, e2));
    await Assert.That((await _readAsync(conn, stream)).ReplayedThrough).IsEqualTo(900L)
      .Because("what a replay read stays read; a lower frontier or a live apply never lowers it");
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Repair_ACursorInTheStuckShape_MovesToTheLastVersionBeforePendingWork_AndKeepsItsFlagAsync(bool copiesPending) {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    var (terminal, c0, c1, c2) = (_id(100), _id(1), _id(2), _id(3));
    await _appendAsync(conn, stream, terminal, c0, c1, c2);
    // The trigger's own row is retired; the two later copies are pending or retired too.
    await _cursorAsync(conn, stream, terminal, COMPLETED | REWIND_REQUIRED, trigger: c0);
    if (copiesPending) {
      await _pendingAsync(conn, stream, c1);
      await _pendingAsync(conn, stream, c2);
    }

    var repaired = await _settleAsync(conn);

    var cursor = await _readAsync(conn, stream);
    await Assert.That(repaired).IsEqualTo(1);
    await Assert.That(cursor.LastEventId).IsEqualTo(copiesPending ? c0 : c2)
      .Because("the cursor moves to the last version before the first pending event, never past pending work");
    await Assert.That(cursor.Status).IsEqualTo(COMPLETED | REWIND_REQUIRED)
      .Because("the repair only places the cursor: the rewind the flag asks for is the fixed code's to run, once");
    await Assert.That(cursor.Trigger).IsEqualTo(c0);
    await Assert.That(await _pendingCountAsync(conn, stream)).IsEqualTo(copiesPending ? 2 : 1)
      .Because("a flagged pair with no pending work gets its trigger queued, so the rewind runs instead of waiting for the next event");
    await Assert.That(await _settleAsync(conn)).IsEqualTo(0).Because("the repair is idempotent: a repaired cursor is not in the shape");
    await Assert.That(await _pendingCountAsync(conn, stream)).IsEqualTo(copiesPending ? 2 : 1).Because("nothing is queued twice");
  }

  /// <summary>
  /// A genuine late arrival has the stuck shape too: the receive chain flagged the cursor for an event stored after it
  /// (a higher version) whose id sorts below it. When an older build retired its row by a live apply the idempotency
  /// filter dropped, the rewind it asks for is still owed, and the repair must not clear it.
  /// </summary>
  [Test]
  public async Task Repair_AGenuineLateArrivalWhoseRowIsRetired_KeepsItsFlag_AndQueuesItsRewindAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    var (applied, late) = (_id(100), _id(1));
    await _appendAsync(conn, stream, applied, late);
    await _cursorAsync(conn, stream, applied, COMPLETED | REWIND_REQUIRED, trigger: late);

    _ = await _settleAsync(conn);

    var cursor = await _readAsync(conn, stream);
    await Assert.That(cursor.Status & REWIND_REQUIRED).IsEqualTo(REWIND_REQUIRED)
      .Because("a flag the repair cannot tell from an owed rewind is left for the rewind");
    await Assert.That(cursor.Trigger).IsEqualTo(late);
    await Assert.That(await _pendingCountAsync(conn, stream)).IsEqualTo(1).Because("the trigger is queued, so its rewind runs");
    await Assert.That(await _settleAsync(conn)).IsEqualTo(0).Because("a re-run finds the placed cursor out of the shape");
    await Assert.That(await _pendingCountAsync(conn, stream)).IsEqualTo(1).Because("a re-run never queues the trigger twice");
  }

  /// <summary>
  /// The trigger's row can still be there, processed and not yet purged (debug mode keeps it; otherwise until the next
  /// purge). The repair queues the trigger again on that row rather than failing the migration on the row's unique key.
  /// </summary>
  [Test]
  public async Task Repair_ATriggerWhoseProcessedRowIsNotYetPurged_IsQueuedAgainOnThatRowAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    var (terminal, c0, c1) = (_id(100), _id(1), _id(2));
    await _appendAsync(conn, stream, terminal, c0, c1);
    await _cursorAsync(conn, stream, terminal, COMPLETED | REWIND_REQUIRED, trigger: c0);
    await _pendingAsync(conn, stream, c0);
    await using (var processed = new NpgsqlCommand(
        "UPDATE wh_perspective_events SET processed_at = NOW(), status = 2, attempts = 1 WHERE stream_id = @s AND event_id = @e", conn)) {
      processed.Parameters.AddWithValue("s", (Guid)stream);
      processed.Parameters.AddWithValue("e", c0);
      await processed.ExecuteNonQueryAsync();
    }

    var repaired = await _settleAsync(conn);

    await Assert.That(repaired).IsEqualTo(1);
    await Assert.That(await _pendingCountAsync(conn, stream)).IsEqualTo(1)
      .Because("the trigger's processed row is made pending again, so its rewind runs");
    await Assert.That(await _settleAsync(conn)).IsEqualTo(0).Because("a re-run finds the placed cursor out of the shape");
    await Assert.That(await _pendingCountAsync(conn, stream)).IsEqualTo(1);
  }

  /// <summary>
  /// The deploy repair's queueing is bounded: one work row for each flagged pair in the stuck shape with no pending
  /// work, none for a pair that has pending work, a flag outside the shape or a cursor with no flag, and a re-run, as
  /// a re-applied migration makes, queues nothing more.
  /// </summary>
  [Test]
  public async Task Repair_QueuesOneRowPerStuckPairWithNoPendingWork_NothingElse_AndARerunQueuesNothingAsync() {
    await using var conn = await _openAsync();
    var idle = new List<Guid>();
    for (var i = 0; i < 3; i++) {
      var stream = (Guid)TrackedGuid.New();
      var (terminal, c0, c1) = (_id(100 + i), _id((10 * i) + 1), _id((10 * i) + 2));
      await _appendAsync(conn, stream, terminal, c0, c1);
      await _cursorAsync(conn, stream, terminal, COMPLETED | REWIND_REQUIRED, trigger: c0);
      idle.Add(stream);
    }
    var busy = (Guid)TrackedGuid.New();
    var (busyTerminal, busyC0, busyC1) = (_id(200), _id(51), _id(52));
    await _appendAsync(conn, busy, busyTerminal, busyC0, busyC1);
    await _cursorAsync(conn, busy, busyTerminal, COMPLETED | REWIND_REQUIRED, trigger: busyC0);
    await _pendingAsync(conn, busy, busyC1);
    var outside = (Guid)TrackedGuid.New();
    var (straggler, cursorAt) = (_id(61), _id(300));
    await _appendAsync(conn, outside, straggler, cursorAt);
    await _cursorAsync(conn, outside, cursorAt, COMPLETED | REWIND_REQUIRED, trigger: straggler);
    var unflagged = (Guid)TrackedGuid.New();
    var (u1, u2) = (_id(400), _id(71));
    await _appendAsync(conn, unflagged, u1, u2);
    await _cursorAsync(conn, unflagged, u1, COMPLETED, trigger: null);

    var repaired = await _settleAsync(conn);
    var queued = await _pendingByStreamAsync(conn);

    await Assert.That(repaired).IsEqualTo(4).Because("the three idle pairs and the busy one are placed");
    await Assert.That(queued).IsEquivalentTo(new Dictionary<Guid, long> { [idle[0]] = 1, [idle[1]] = 1, [idle[2]] = 1, [busy] = 1 })
      .Because("one row per idle pair (its trigger), the busy pair's own pending row only, nothing for the others");
    await Assert.That(await _settleAsync(conn)).IsEqualTo(0);
    await Assert.That(await _pendingByStreamAsync(conn)).IsEquivalentTo(queued).Because("a re-run queues nothing more");
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Repair_AFlagWhoseTriggerIsStillPending_OrIsBelowTheCursorByVersion_IsLeftAsync(bool triggerPending) {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    if (triggerPending) {
      // A late arrival the receive chain flagged and no rewind has replayed yet: its row is still pending.
      var (applied, late) = (_id(100), _id(1));
      await _appendAsync(conn, stream, applied, late);
      await _cursorAsync(conn, stream, applied, COMPLETED | REWIND_REQUIRED, trigger: late);
      await _pendingAsync(conn, stream, late);
    } else {
      // A straggler below the cursor by version: the flag is the rewind's to settle, not the repair's.
      var (straggler, cursorAt) = (_id(1), _id(100));
      await _appendAsync(conn, stream, straggler, cursorAt);
      await _cursorAsync(conn, stream, cursorAt, COMPLETED | REWIND_REQUIRED, trigger: straggler);
    }

    _ = await _settleAsync(conn);

    await Assert.That((await _readAsync(conn, stream)).Status & REWIND_REQUIRED).IsEqualTo(REWIND_REQUIRED);
  }

  // ===== Helpers =====

  /// <summary>An id minted <paramref name="seconds"/> after a fixed instant: a larger number sorts later.</summary>
  private static Guid _id(int seconds) => Guid.CreateVersion7(_minted.AddSeconds(seconds));

  private static PerspectiveCursorCompletion _completion(Guid stream, Guid lastEventId) => new() {
    StreamId = stream,
    PerspectiveName = PERSPECTIVE,
    LastEventId = lastEventId,
    Status = PerspectiveProcessingStatus.Completed
  };

  private async Task _reportAsync(PerspectiveCursorCompletion completion) {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.ReportPerspectiveCompletionAsync(completion);
  }

  private async Task _flushAsync(PerspectiveCursorCompletion completion) {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.CompletePerspectiveAsync(cursors: [completion], eventWorkIds: [], debugMode: false);
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  /// <summary>Appends the events at versions 1, 2, ... in the order given, then stamps them in that order.</summary>
  private static async Task _appendAsync(NpgsqlConnection conn, Guid stream, params Guid[] ids) {
    for (var i = 0; i < ids.Length; i++) {
      await using var cmd = new NpgsqlCommand(
        "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at) "
        + "VALUES (@e, @s, @s, 'TestAggregate', 'Test.Event, Test', @v, NOW())", conn);
      cmd.Parameters.AddWithValue("e", ids[i]);
      cmd.Parameters.AddWithValue("s", stream);
      cmd.Parameters.AddWithValue("v", i + 1);
      await cmd.ExecuteNonQueryAsync();
    }
    await using var stamp = new NpgsqlCommand("SELECT stamp_pending_commit_sequences(10000, false)", conn);
    _ = await stamp.ExecuteScalarAsync();
  }

  private static async Task _cursorAsync(NpgsqlConnection conn, Guid stream, Guid lastEventId, int status, Guid? trigger) {
    await using var cmd = new NpgsqlCommand(
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at, "
      + "rewind_trigger_event_id, rewind_flagged_at, rewind_first_flagged_at) "
      + "VALUES (@s, @p, @e, @st, NOW(), @t, CASE WHEN @t IS NULL THEN NULL ELSE NOW() END, CASE WHEN @t IS NULL THEN NULL ELSE NOW() END)", conn);
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("p", PERSPECTIVE);
    cmd.Parameters.AddWithValue("e", lastEventId);
    cmd.Parameters.AddWithValue("st", (short)status);
    cmd.Parameters.Add(new NpgsqlParameter<Guid?>("t", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = trigger });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _pendingAsync(NpgsqlConnection conn, Guid stream, Guid eventId) {
    await using var cmd = new NpgsqlCommand(
      "INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at) "
      + "VALUES (@w, @s, @p, @e, 0, 0, NOW())", conn);
    cmd.Parameters.AddWithValue("w", (Guid)TrackedGuid.New());
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("p", PERSPECTIVE);
    cmd.Parameters.AddWithValue("e", eventId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _commitSequenceAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = new NpgsqlCommand("SELECT commit_sequence FROM wh_event_store WHERE event_id = @e", conn);
    cmd.Parameters.AddWithValue("e", eventId);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task<int> _settleAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand("SELECT wh_settle_id_ordered_rewind_flags()", conn);
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task<long> _pendingCountAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = new NpgsqlCommand(
      "SELECT count(*) FROM wh_perspective_events WHERE stream_id = @s AND perspective_name = @p AND processed_at IS NULL", conn);
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("p", PERSPECTIVE);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task<Dictionary<Guid, long>> _pendingByStreamAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand(
      "SELECT stream_id, count(*) FROM wh_perspective_events WHERE perspective_name = @p AND processed_at IS NULL GROUP BY stream_id", conn);
    cmd.Parameters.AddWithValue("p", PERSPECTIVE);
    var counts = new Dictionary<Guid, long>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      counts[reader.GetGuid(0)] = reader.GetInt64(1);
    }
    return counts;
  }

  private sealed record CursorRow(Guid LastEventId, int Status, Guid? Trigger, long? ReplayedThrough);

  private static async Task<CursorRow> _readAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = new NpgsqlCommand(
      "SELECT last_event_id, status, rewind_trigger_event_id, replayed_through_commit_sequence "
      + "FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p", conn);
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("p", PERSPECTIVE);
    await using var reader = await cmd.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue().Because("the cursor exists");
    return new CursorRow(
      reader.GetGuid(0),
      reader.GetInt16(1),
      await reader.IsDBNullAsync(2) ? null : reader.GetGuid(2),
      await reader.IsDBNullAsync(3) ? null : reader.GetInt64(3));
  }
}
