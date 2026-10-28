// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.Cursors;

/// <summary>
/// #1363: the live completion path persists the stream's cursor at the position the run reports. In production mode
/// <c>complete_perspective</c> deletes the processed work rows before it updates cursors, so a cursor derived from those
/// rows never moved and was never created; a stream then had a perspective row and no cursor, and an existing cursor
/// stayed where it was. The cursor now follows the completion's own last event: created when missing, advanced when
/// behind, and never moved backwards by a completion that reports an earlier event.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers#perspective-cursors</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/016_UpdatePerspectiveCheckpoints.sql</code-under-test>
[Category("Shard2")]
public class PerspectiveCursorAdvanceSqlTests : EFCoreTestBase {
  private const string PERSPECTIVE = "Test.CursorPerspective";

  [Test]
  public async Task CompletePerspective_ProductionMode_CreatesTheMissingCursorAtTheCompletionsEventAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    var events = await _appendAsync(conn, stream, 3);

    await _completeAsync(_completion(stream, events[2]));

    await Assert.That(await _cursorAsync(conn, stream)).IsEqualTo(events[2])
      .Because("a stream processed on the live path gets a cursor at the event its run reached");
  }

  [Test]
  public async Task CompletePerspective_ProductionMode_AdvancesAnExistingCursorAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    var events = await _appendAsync(conn, stream, 3);
    await _completeAsync(_completion(stream, events[0]));

    await _completeAsync(_completion(stream, events[2]));

    await Assert.That(await _cursorAsync(conn, stream)).IsEqualTo(events[2])
      .Because("a cursor that does not move leaves every later batch judged against an old position");
  }

  [Test]
  public async Task CompletePerspective_AnEarlierEventThanTheCursor_LeavesTheCursorWhereItIsAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    var events = await _appendAsync(conn, stream, 3);
    await _completeAsync(_completion(stream, events[2]));

    await _completeAsync(_completion(stream, events[0]));

    await Assert.That(await _cursorAsync(conn, stream)).IsEqualTo(events[2])
      .Because("completions can be flushed out of order; a late one must not move the cursor back");
  }

  [Test]
  public async Task CompletePerspective_TwoCompletionsForOneStreamInABatch_TheLaterEventWinsAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    var events = await _appendAsync(conn, stream, 3);

    await _completeAsync(_completion(stream, events[2]), _completion(stream, events[1]));

    await Assert.That(await _cursorAsync(conn, stream)).IsEqualTo(events[2]);
  }

  [Test]
  public async Task CompletePerspective_ACompletionWithNoEvent_CreatesNoCursorAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    _ = await _appendAsync(conn, stream, 1);

    await _completeAsync(_completion(stream, Guid.Empty));

    await Assert.That(await _cursorAsync(conn, stream)).IsNull()
      .Because("a run that applied nothing has no position to record");
  }

  [Test]
  public async Task CompletePerspective_AnEventTheStoreDoesNotHold_IsSkippedAndTheRestOfTheBatchRecordedAsync() {
    await using var conn = await _openAsync();
    var unknownStream = TrackedGuid.New();
    var knownStream = TrackedGuid.New();
    var known = await _appendAsync(conn, knownStream, 1);

    await _completeAsync(_completion(unknownStream, TrackedGuid.New()), _completion(knownStream, known[0]));

    await Assert.That(await _cursorAsync(conn, unknownStream)).IsNull()
      .Because("a cursor must name an event the store holds");
    await Assert.That(await _cursorAsync(conn, knownStream)).IsEqualTo(known[0])
      .Because("one unusable completion must not lose the rest of the batch");
  }

  [Test]
  public async Task CompletePerspective_AnEarlierEventStillPending_HoldsTheCursorBeforeItAsync() {
    // The gap invariant, in production mode: event 2 is still unprocessed for this perspective, so a completion
    // reporting event 3 must not carry the cursor past event 2, or event 2 would never be applied.
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    var events = await _appendAsync(conn, stream, 3);
    await _completeAsync(_completion(stream, events[0]));
    await using (var pending = new NpgsqlCommand(
        "INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at) "
        + "VALUES (@w, @s, @p, @e, 0, 0, NOW())", conn)) {
      pending.Parameters.AddWithValue("w", (Guid)TrackedGuid.New());
      pending.Parameters.AddWithValue("s", (Guid)stream);
      pending.Parameters.AddWithValue("p", PERSPECTIVE);
      pending.Parameters.AddWithValue("e", events[1]);
      await pending.ExecuteNonQueryAsync();
    }

    await _completeAsync(_completion(stream, events[2]));

    await Assert.That(await _cursorAsync(conn, stream)).IsEqualTo(events[0])
      .Because("the cursor never passes an event still pending for the perspective");
  }

  [Test]
  public async Task CompletePerspective_ADroppedStragglersWork_RetiresItClearsTheRewindFlagAndLeavesTheCursorAsync() {
    // #1862: what the worker sends once an ephemeral perspective drops a late event it cannot rewind for: the event's
    // work id, and a completion for the pair that names no event. On the state an older build left behind (the
    // cursor flagged for a rewind, the straggler's work still pending) the flush deletes the work, leaves the cursor
    // where it was, and clears the flag, so nothing is left to find the straggler again.
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    var events = await _appendAsync(conn, stream, 2);
    var straggler = events[0];
    await _completeAsync(_completion(stream, events[1]));
    var stragglerWork = (Guid)TrackedGuid.New();
    await using (var flagged = new NpgsqlCommand(
        "UPDATE wh_perspective_cursors SET status = status | 32, rewind_trigger_event_id = @e, rewind_flagged_at = NOW(), "
        + "rewind_first_flagged_at = NOW() WHERE stream_id = @s AND perspective_name = @p; "
        + "INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at) "
        + "VALUES (@w, @s, @p, @e, 0, 0, NOW())", conn)) {
      flagged.Parameters.AddWithValue("w", stragglerWork);
      flagged.Parameters.AddWithValue("s", (Guid)stream);
      flagged.Parameters.AddWithValue("p", PERSPECTIVE);
      flagged.Parameters.AddWithValue("e", straggler);
      await flagged.ExecuteNonQueryAsync();
    }

    await using (var dbContext = CreateDbContext()) {
      var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
      await coordinator.CompletePerspectiveAsync(
        cursors: [_completion(stream, Guid.Empty) with { Status = PerspectiveProcessingStatus.None, DroppedStragglers = [straggler] }],
        eventWorkIds: [stragglerWork],
        debugMode: false);
    }

    await using var pending = new NpgsqlCommand(
      "SELECT COUNT(*) FROM wh_perspective_events WHERE event_work_id = @w", conn);
    pending.Parameters.AddWithValue("w", stragglerWork);
    await Assert.That((long)(await pending.ExecuteScalarAsync())!).IsEqualTo(0L)
      .Because("the dropped straggler's work is retired, so no claim finds it again");
    await Assert.That(await _cursorAsync(conn, stream)).IsEqualTo(events[1])
      .Because("the cursor is never moved backward to the dropped event");
    await using var status = new NpgsqlCommand(
      "SELECT status FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p", conn);
    status.Parameters.AddWithValue("s", (Guid)stream);
    status.Parameters.AddWithValue("p", PERSPECTIVE);
    await Assert.That(Convert.ToInt32(await status.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) & 32).IsEqualTo(0)
      .Because("with nothing left pending the rewind flag clears, so the rewind work-item path stops asking for it");
  }

  private static PerspectiveCursorCompletion _completion(Guid stream, Guid lastEventId) => new() {
    StreamId = stream,
    PerspectiveName = PERSPECTIVE,
    LastEventId = lastEventId,
    Status = PerspectiveProcessingStatus.Completed
  };

  private async Task _completeAsync(params PerspectiveCursorCompletion[] completions) {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.CompletePerspectiveAsync(cursors: completions, eventWorkIds: [], debugMode: false);
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<Guid[]> _appendAsync(NpgsqlConnection conn, Guid stream, int count) {
    var ids = new Guid[count];
    for (var version = 1; version <= count; version++) {
      ids[version - 1] = TrackedGuid.New();
      await using var cmd = new NpgsqlCommand(
        "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at) "
        + "VALUES (@e, @s, @s, 'TestAggregate', 'Test.Event, Test', @v, NOW())", conn);
      cmd.Parameters.AddWithValue("e", ids[version - 1]);
      cmd.Parameters.AddWithValue("s", stream);
      cmd.Parameters.AddWithValue("v", version);
      await cmd.ExecuteNonQueryAsync();
    }
    return ids;
  }

  private static async Task<Guid?> _cursorAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = new NpgsqlCommand(
      "SELECT last_event_id FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p", conn);
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("p", PERSPECTIVE);
    return await cmd.ExecuteScalarAsync() as Guid?;
  }
}
