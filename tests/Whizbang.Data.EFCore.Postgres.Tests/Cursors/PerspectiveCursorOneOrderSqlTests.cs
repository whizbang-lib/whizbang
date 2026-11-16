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
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/452_StaleCopiesAboveTheNewestAreRewalked.sql</code-under-test>
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
  public async Task Repair_ACursorInTheStuckShape_MovesToTheLastVersionBeforePendingWork_AndItsFlagIsClearedAsync(bool copiesPending) {
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

    var settled = await _settleAsync(conn);

    var cursor = await _readAsync(conn, stream);
    await Assert.That(settled).IsEqualTo(1);
    await Assert.That(cursor.LastEventId).IsEqualTo(copiesPending ? c0 : c2)
      .Because("the cursor moves to the last version before the first pending event, never past pending work");
    await Assert.That(cursor.Status).IsEqualTo(COMPLETED);
    await Assert.That(cursor.Trigger).IsNull();
    await Assert.That(await _settleAsync(conn)).IsEqualTo(0).Because("the repair is idempotent: a settled cursor is not in the shape");
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
