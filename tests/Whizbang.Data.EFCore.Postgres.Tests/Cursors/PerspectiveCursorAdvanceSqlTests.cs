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
