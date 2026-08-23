using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Slice 3 of the bulk-import-saga-completion-race plan. Locks the
/// "cursor only advances past contiguously-processed events" invariant in
/// <c>update_perspective_cursors</c> (migration 016) so future SQL refactors
/// cannot accidentally weaken it. The production forensic that motivated the plan
/// concerned the rewind path's checkpoint (already addressed in Slice 2 via
/// the catch-up loop in <c>PerspectiveRunnerTemplate.cs</c>); this Slice 3
/// test holds the normal-completion path's invariant in place independently.
///
/// <para><strong>Invariant:</strong> when N events for a (stream, perspective)
/// pair are reported as completed and there is a GAP in the processed set
/// (i.e., an earlier event has <c>processed_at IS NULL</c>),
/// <c>wh_perspective_cursors.last_event_id</c> MUST stay at the latest
/// contiguously-processed event — NOT jump to the latest processed event
/// past the gap.</para>
///
/// <para>Concretely: if events 1, 2, 4 are processed and event 3 is NOT,
/// the cursor must advance to event 2 (max contiguous), never to event 4.
/// Otherwise event 3 is silently lost — exactly the symptom produced in
/// production at the projection level, with a handful of line numbers missing.</para>
/// </summary>
/// <docs>fundamentals/perspectives/rewind-invariants</docs>
[Category("Shard3")]
public class PerspectiveCursorAdvanceTests : EFCoreTestBase {

  [Test]
  public async Task UpdateCursors_GapInProcessedEvents_CursorStaysAtLastContiguousProcessedAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "BulkImportSagaProjection.GapInvariantTest";

    // Four events in strict UUIDv7 order (TrackedGuid.NewMedo guarantees monotonic).
    // Mirrors the production sequence: events arrive in order, but the projection's
    // Apply chain runs out of order under the rewind race — leaving a gap.
    var eventId1 = (Guid)TrackedGuid.NewMedo();
    var eventId2 = (Guid)TrackedGuid.NewMedo();
    var eventId3 = (Guid)TrackedGuid.NewMedo();
    var eventId4 = (Guid)TrackedGuid.NewMedo();

    // event 1 — processed.
    await _insertPerspectiveEventAsync(conn, streamId, perspectiveName, eventId1, processed: true);
    // event 2 — processed.
    await _insertPerspectiveEventAsync(conn, streamId, perspectiveName, eventId2, processed: true);
    // event 3 — NOT processed (THE GAP).
    await _insertPerspectiveEventAsync(conn, streamId, perspectiveName, eventId3, processed: false);
    // event 4 — processed (past the gap).
    await _insertPerspectiveEventAsync(conn, streamId, perspectiveName, eventId4, processed: true);

    // Seed a cursor row at event 1 so update_perspective_cursors exercises its
    // UPDATE path (the hot one during a saga's normal completion flow). The
    // FK on wh_perspective_cursors.last_event_id requires this to be a real
    // event_id present in wh_event_store — event 1 was inserted above.
    await _insertCursorAsync(conn, streamId, perspectiveName, lastEventId: eventId1);

    // Call update_perspective_cursors with the (stream, perspective) pair.
    // The function discovers the contiguously-processed range itself; it does
    // NOT take an event-list argument. It must NOT skip over the gap.
    await _callUpdatePerspectiveCursorsAsync(conn, streamId, perspectiveName);

    var cursorLastEventId = await _readCursorLastEventIdAsync(conn, streamId, perspectiveName);
    await Assert.That(cursorLastEventId).IsEqualTo(eventId2)
      .Because("Gap-free invariant: cursor MUST stay at the last contiguously-processed event (event 2). Event 3 has processed_at IS NULL, so the cursor cannot legally advance to event 4 — that would silently drop event 3 from the projection's view. This is exactly the ProcessedLineNumbers gap shape observed in production (a few line numbers missing while a later one is present); locking this invariant prevents the SQL side from ever introducing that shape.");
  }

  [Test]
  public async Task UpdateCursors_NoGap_CursorAdvancesToLatestProcessedAsync() {
    // Companion control test — when there is no gap, the cursor must advance
    // to the latest processed event. Confirms the invariant doesn't over-fire.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "BulkImportSagaProjection.NoGapTest";

    var eventId1 = (Guid)TrackedGuid.NewMedo();
    var eventId2 = (Guid)TrackedGuid.NewMedo();
    var eventId3 = (Guid)TrackedGuid.NewMedo();

    await _insertPerspectiveEventAsync(conn, streamId, perspectiveName, eventId1, processed: true);
    await _insertPerspectiveEventAsync(conn, streamId, perspectiveName, eventId2, processed: true);
    await _insertPerspectiveEventAsync(conn, streamId, perspectiveName, eventId3, processed: true);

    await _insertCursorAsync(conn, streamId, perspectiveName, lastEventId: eventId1);
    await _callUpdatePerspectiveCursorsAsync(conn, streamId, perspectiveName);

    var cursorLastEventId = await _readCursorLastEventIdAsync(conn, streamId, perspectiveName);
    await Assert.That(cursorLastEventId).IsEqualTo(eventId3)
      .Because("With no gap, the cursor advances to the latest processed event — confirms the gap-free SELECT isn't over-conservative on healthy streams.");
  }

  [Test]
  public async Task UpdateCursors_OnlyUnprocessedEvents_CursorUnchangedAsync() {
    // When every pe row for the pair has processed_at IS NULL, there's no
    // contiguous-processed-range to advance to — cursor must be preserved
    // at its previous position via the COALESCE(new_last_event_id,
    // pc.last_event_id) construct in migration 016.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "BulkImportSagaProjection.PreserveOnNoProgress";

    var existingCursorEvent = (Guid)TrackedGuid.NewMedo();
    var futureEvent1 = (Guid)TrackedGuid.NewMedo();
    var futureEvent2 = (Guid)TrackedGuid.NewMedo();

    // Seed wh_event_store with the existing-cursor event so the FK on
    // wh_perspective_cursors.last_event_id is satisfied when we seed the cursor below.
    await _insertEventStoreRowAsync(conn, streamId, existingCursorEvent);

    await _insertPerspectiveEventAsync(conn, streamId, perspectiveName, futureEvent1, processed: false);
    await _insertPerspectiveEventAsync(conn, streamId, perspectiveName, futureEvent2, processed: false);

    await _insertCursorAsync(conn, streamId, perspectiveName, lastEventId: existingCursorEvent);
    await _callUpdatePerspectiveCursorsAsync(conn, streamId, perspectiveName);

    var cursorLastEventId = await _readCursorLastEventIdAsync(conn, streamId, perspectiveName);
    await Assert.That(cursorLastEventId).IsEqualTo(existingCursorEvent)
      .Because("When no new gap-free progress can be reported, the COALESCE in mig 016's UPDATE preserves the cursor's previous last_event_id. Otherwise the cursor would null-out the existing position and lose the projection's prior progress.");
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private static async Task _insertEventStoreRowAsync(
      NpgsqlConnection conn, Guid streamId, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES (@eid, @stream, @stream, 'Test', 'TestEvent', nextval('wh_event_sequence'), NOW())";
    cmd.Parameters.AddWithValue("eid", eventId);
    cmd.Parameters.AddWithValue("stream", streamId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _insertPerspectiveEventAsync(
      NpgsqlConnection conn, Guid streamId, string perspectiveName, Guid eventId, bool processed) {
    // The FK constraint on wh_perspective_cursors.last_event_id requires
    // wh_event_store to contain the event row — insert it first so the
    // later cursor seed/advance can reference it.
    await _insertEventStoreRowAsync(conn, streamId, eventId);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = processed
      ? @"INSERT INTO wh_perspective_events
            (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at, processed_at)
          VALUES (@work, @stream, @pname, @eid, 0, 0, NOW(), NOW())"
      : @"INSERT INTO wh_perspective_events
            (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at)
          VALUES (@work, @stream, @pname, @eid, 0, 0, NOW())";
    cmd.Parameters.AddWithValue("work", (Guid)TrackedGuid.NewMedo());
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("pname", perspectiveName);
    cmd.Parameters.AddWithValue("eid", eventId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _insertCursorAsync(
      NpgsqlConnection conn, Guid streamId, string perspectiveName, Guid lastEventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status)
      VALUES (@stream, @pname, @last, 0)
      ON CONFLICT (stream_id, perspective_name) DO UPDATE SET last_event_id = EXCLUDED.last_event_id";
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("pname", perspectiveName);
    cmd.Parameters.AddWithValue("last", lastEventId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _callUpdatePerspectiveCursorsAsync(
      NpgsqlConnection conn, Guid streamId, string perspectiveName) {
    var payload = $$"""[{"StreamId":"{{streamId}}","PerspectiveName":"{{perspectiveName}}"}]""";
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT update_perspective_cursors(@p::jsonb)";
    cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) { Value = payload });
    _ = await cmd.ExecuteScalarAsync();
  }

  private static async Task<Guid?> _readCursorLastEventIdAsync(
      NpgsqlConnection conn, Guid streamId, string perspectiveName) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT last_event_id FROM wh_perspective_cursors
      WHERE stream_id = @stream AND perspective_name = @pname LIMIT 1";
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("pname", perspectiveName);
    var result = await cmd.ExecuteScalarAsync();
    return result is Guid g ? g : null;
  }
}
