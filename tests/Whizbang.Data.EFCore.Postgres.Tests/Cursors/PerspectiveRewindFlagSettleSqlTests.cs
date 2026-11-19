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
/// A perspective's cursor completion and the retirement of its work rows travel to <c>complete_perspective</c> on one
/// channel but not necessarily in one flush: the worker reports the cursor before it buffers the rows, every consumer
/// loop flushes both queues, and the flusher cuts batches by size and time. When the cursor lands first,
/// <c>update_perspective_cursors</c> still sees the rows pending, so it keeps the cursor's status, RewindRequired
/// included. The flush that later retires the rows carried no cursor for the pair, and nothing came back to the cursor:
/// it stayed flagged with no work, every later event on the stream took a full rewind, and the startup rewind scan
/// found it on every restart. Retiring a pair's last pending row now settles a flagged cursor the way a completion in
/// the same flush would have.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers#perspective-cursors</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/374_RetiredWorkSettlesTheRewindFlag.sql</code-under-test>
[Category("Shard2")]
public class PerspectiveRewindFlagSettleSqlTests : EFCoreTestBase {
  private const string PERSPECTIVE = "Test.RewindSettlePerspective";
  private const string OTHER_PERSPECTIVE = "Test.RewindSettleOtherPerspective";
  private const int REWIND_REQUIRED = 32;

  [Test]
  public async Task CompletePerspective_TheCursorFlushedBeforeItsWork_ClearsTheRewindFlagWhenTheWorkIsRetiredAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    var events = await _appendAsync(conn, stream, 3);
    await _completeAsync([_completion(stream, events[1])], [], debugMode: false);
    var stragglerWork = await _flagWithPendingStragglerAsync(conn, stream, PERSPECTIVE, events[0]);

    // The cursor's completion lands in a flush of its own, while the straggler's row is still pending.
    await _completeAsync([_completion(stream, events[2])], [], debugMode: false);
    await Assert.That(await _statusAsync(conn, stream, PERSPECTIVE) & REWIND_REQUIRED).IsEqualTo(REWIND_REQUIRED)
      .Because("with the straggler's row still pending, the cursor completion cannot settle the rewind");

    // The row's retirement arrives in a later flush that carries no cursor for the pair.
    await _completeAsync([], [stragglerWork], debugMode: false);

    await Assert.That(await _statusAsync(conn, stream, PERSPECTIVE) & REWIND_REQUIRED).IsEqualTo(0)
      .Because("the pair's last pending row is retired, so nothing is left for a rewind to replay");
  }

  [Test]
  public async Task CompletePerspective_RetiringOneOfTwoPendingRows_KeepsTheRewindFlagAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    var events = await _appendAsync(conn, stream, 3);
    await _completeAsync([_completion(stream, events[2])], [], debugMode: false);
    var firstWork = await _flagWithPendingStragglerAsync(conn, stream, PERSPECTIVE, events[0]);
    _ = await _flagWithPendingStragglerAsync(conn, stream, PERSPECTIVE, events[1]);

    await _completeAsync([], [firstWork], debugMode: false);

    await Assert.That(await _statusAsync(conn, stream, PERSPECTIVE) & REWIND_REQUIRED).IsEqualTo(REWIND_REQUIRED)
      .Because("a straggler still pending for the pair still needs the rewind");
  }

  [Test]
  public async Task CompletePerspective_DebugMode_ClearsTheRewindFlagWhenTheLastRowIsStampedAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    var events = await _appendAsync(conn, stream, 2);
    await _completeAsync([_completion(stream, events[1])], [], debugMode: true);
    var stragglerWork = await _flagWithPendingStragglerAsync(conn, stream, PERSPECTIVE, events[0]);

    await _completeAsync([], [stragglerWork], debugMode: true);

    await Assert.That(await _statusAsync(conn, stream, PERSPECTIVE) & REWIND_REQUIRED).IsEqualTo(0)
      .Because("debug mode keeps the row stamped processed; it is no longer pending either way");
  }

  [Test]
  public async Task CompletePerspective_AFlaggedCursorOfAnotherPerspective_IsLeftAloneAsync() {
    await using var conn = await _openAsync();
    var stream = TrackedGuid.New();
    var events = await _appendAsync(conn, stream, 2);
    await _completeAsync(
      [_completion(stream, events[1]), _completion(stream, events[1]) with { PerspectiveName = OTHER_PERSPECTIVE }],
      [], debugMode: false);
    var stragglerWork = await _flagWithPendingStragglerAsync(conn, stream, PERSPECTIVE, events[0]);
    _ = await _flagWithPendingStragglerAsync(conn, stream, OTHER_PERSPECTIVE, events[0]);

    await _completeAsync([], [stragglerWork], debugMode: false);

    await Assert.That(await _statusAsync(conn, stream, OTHER_PERSPECTIVE) & REWIND_REQUIRED).IsEqualTo(REWIND_REQUIRED)
      .Because("only the pairs whose rows this flush retired are settled");
  }

  [Test]
  public async Task SettleStaleRewindFlags_ClearsFlagsWithNoPendingWork_AndKeepsFlagsThatStillHaveSomeAsync() {
    await using var conn = await _openAsync();
    var staleStream = TrackedGuid.New();
    var liveStream = TrackedGuid.New();
    var stale = await _appendAsync(conn, staleStream, 2);
    var live = await _appendAsync(conn, liveStream, 2);
    await _completeAsync([_completion(staleStream, stale[1]), _completion(liveStream, live[1])], [], debugMode: false);
    var staleWork = await _flagWithPendingStragglerAsync(conn, staleStream, PERSPECTIVE, stale[0]);
    _ = await _flagWithPendingStragglerAsync(conn, liveStream, PERSPECTIVE, live[0]);
    // The state the race left behind: the straggler's row retired by an older build, the flag never cleared.
    await using (var retire = new NpgsqlCommand("DELETE FROM wh_perspective_events WHERE event_work_id = @w", conn)) {
      retire.Parameters.AddWithValue("w", staleWork);
      await retire.ExecuteNonQueryAsync();
    }

    await using var settle = new NpgsqlCommand("SELECT wh_settle_stale_rewind_flags()", conn);
    var settled = Convert.ToInt32(await settle.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

    await Assert.That(settled).IsEqualTo(1);
    await Assert.That(await _statusAsync(conn, staleStream, PERSPECTIVE) & REWIND_REQUIRED).IsEqualTo(0)
      .Because("a flag with no pending work has nothing to replay and only costs a needless rewind");
    await Assert.That(await _statusAsync(conn, liveStream, PERSPECTIVE) & REWIND_REQUIRED).IsEqualTo(REWIND_REQUIRED)
      .Because("a flag whose straggler is still pending is the rewind the worker has yet to run");
  }

  private static PerspectiveCursorCompletion _completion(Guid stream, Guid lastEventId) => new() {
    StreamId = stream,
    PerspectiveName = PERSPECTIVE,
    LastEventId = lastEventId,
    Status = PerspectiveProcessingStatus.Completed
  };

  private async Task _completeAsync(PerspectiveCursorCompletion[] cursors, Guid[] workIds, bool debugMode) {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.CompletePerspectiveAsync(cursors: cursors, eventWorkIds: workIds, debugMode: debugMode);
  }

  /// <summary>What the store's emit chain does for a straggler: a pending work row, and the cursor flagged for it.</summary>
  private static async Task<Guid> _flagWithPendingStragglerAsync(
      NpgsqlConnection conn, Guid stream, string perspective, Guid straggler) {
    var work = (Guid)TrackedGuid.New();
    await using var cmd = new NpgsqlCommand(
      "UPDATE wh_perspective_cursors SET status = status | 32, rewind_trigger_event_id = @e, rewind_flagged_at = NOW(), "
      + "rewind_first_flagged_at = COALESCE(rewind_first_flagged_at, NOW()) WHERE stream_id = @s AND perspective_name = @p; "
      + "INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at) "
      + "VALUES (@w, @s, @p, @e, 0, 0, NOW())", conn);
    cmd.Parameters.AddWithValue("w", work);
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("p", perspective);
    cmd.Parameters.AddWithValue("e", straggler);
    await cmd.ExecuteNonQueryAsync();
    return work;
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

  private static async Task<int> _statusAsync(NpgsqlConnection conn, Guid stream, string perspective) {
    await using var cmd = new NpgsqlCommand(
      "SELECT status FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p", conn);
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("p", perspective);
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }
}
