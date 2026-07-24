using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Slice 26.7 — locks for <c>get_stream_events</c> surfacing <c>commit_sequence</c>
/// alongside the existing fields. Cursor-swap consumers read this column for
/// commit-stable ordering.
///
/// <para>Part B (mig 058) — the grace-windowed unstamped-row gate. The live drain excludes
/// rows whose <c>wh_event_store.commit_sequence</c> is still NULL (stamper hasn't caught up)
/// <em>while they are fresh</em>, so the drain applies events in stable commit order and the
/// separate-transaction insert-lag inversion can't reach it. Fresh unstamped rows are neither
/// claimed nor returned; they surface once stamped — OR after a grace window (5s) if the stamper
/// is lagging/absent, so a stuck stamper degrades to pre-058 behavior instead of STALLING the
/// worker. These tests lock that contract.</para>
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
public class GetStreamEventsCommitSequencePropagationSqlTests : EFCoreTestBase {

  [Test]
  public async Task GetStreamEvents_ReturnsCommitSequenceColumnWhenStampedAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();

    // Setup: event_store row + perspective_events row leased to this instance.
    await _registerInstanceAsync(conn, instanceId);
    await _insertEventStoreRowAsync(conn, eventId, streamId, version: 1);
    await _insertPerspectiveEventLeasedAsync(conn, workId, streamId, "Projection.Test", eventId, instanceId);

    // Stamp the event_store row.
    var stampedCount = await _stampPendingAsync(conn);
    await Assert.That(stampedCount).IsGreaterThan(0);

    // Now fetch via get_stream_events and assert commit_sequence is populated.
    var commitSeq = await _readCommitSequenceFromGetStreamEventsAsync(conn, instanceId, streamId);
    await Assert.That(commitSeq).IsNotNull()
      .Because("after stamper runs, get_stream_events must surface commit_sequence to the C# consumer");
    await Assert.That(commitSeq!.Value).IsGreaterThan(0L);
  }

  [Test]
  public async Task GetStreamEvents_ExcludesFreshUnstampedRowsAsync() {
    // Part B (mig 058): a FRESH unstamped perspective-events row is NOT returned by the drain
    // fetch — the stamper-lag race is closed at the SQL source. (Before mig 058 the row was
    // returned with commit_sequence=NULL and the C# fell back to event_id ordering, which is the
    // path the rewind storm rode in on.)
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();

    await _registerInstanceAsync(conn, instanceId);
    await _insertEventStoreRowAsync(conn, eventId, streamId, version: 1);
    // created_at = NOW() → within the 5s grace window.
    await _insertPerspectiveEventLeasedAsync(conn, workId, streamId, "Projection.Test", eventId, instanceId);

    // Skip stamping — commit_sequence stays NULL → the fresh row must be invisible to the drain.
    var rowCount = await _countGetStreamEventsRowsAsync(conn, instanceId, streamId);
    await Assert.That(rowCount).IsEqualTo(0)
      .Because("mig 058 gates FRESH unstamped rows out of get_stream_events (neither claimed nor returned)");
  }

  [Test]
  public async Task GetStreamEvents_AgedUnstampedRowIsIncludedAfterGraceAsync() {
    // Resilience guarantee: if the stamper is lagging or absent (e.g. the ECommerce in-memory
    // sample's per-schema fixture), an unstamped row that has been pending past the 5s grace is
    // drained anyway — degrading to pre-058 behavior (NULLS LAST) rather than STALLING the worker.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();

    await _registerInstanceAsync(conn, instanceId);
    await _insertEventStoreRowAsync(conn, eventId, streamId, version: 1);
    // Backdate created_at well past the grace window; leave commit_sequence NULL (stamper absent).
    await _insertPerspectiveEventLeasedAsync(
      conn, workId, streamId, "Projection.Test", eventId, instanceId,
      createdAt: DateTimeOffset.UtcNow.AddMinutes(-1));

    var rowCount = await _countGetStreamEventsRowsAsync(conn, instanceId, streamId);
    await Assert.That(rowCount).IsEqualTo(1)
      .Because("an aged unstamped row past the grace is drained anyway — no stall on a lagging/absent stamper");
  }

  [Test]
  public async Task GetStreamEvents_StampedReturnedUnstampedExcludedOnSameStreamAsync() {
    // Two events on one stream: the stamped one is returned, the unstamped one is held back.
    // This is the contiguity guarantee the gate buys — the drain only ever sees a prefix
    // of commit-ordered events, never a stamped event sitting "ahead" of an unstamped gap.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var stampedEventId = (Guid)TrackedGuid.NewMedo();
    var unstampedEventId = (Guid)TrackedGuid.NewMedo();

    await _registerInstanceAsync(conn, instanceId);

    // Insert the first event and stamp it.
    await _insertEventStoreRowAsync(conn, stampedEventId, streamId, version: 1);
    await _insertPerspectiveEventLeasedAsync(
      conn, (Guid)TrackedGuid.NewMedo(), streamId, "Projection.Test", stampedEventId, instanceId);
    var stampedCount = await _stampPendingAsync(conn);
    await Assert.That(stampedCount).IsGreaterThan(0);

    // Insert the second event but DO NOT stamp it.
    await _insertEventStoreRowAsync(conn, unstampedEventId, streamId, version: 2);
    await _insertPerspectiveEventLeasedAsync(
      conn, (Guid)TrackedGuid.NewMedo(), streamId, "Projection.Test", unstampedEventId, instanceId);

    var returnedEventIds = await _readEventIdsFromGetStreamEventsAsync(conn, instanceId, streamId);
    await Assert.That(returnedEventIds).Contains(stampedEventId)
      .Because("the stamped event is part of the contiguous commit-ordered prefix");
    await Assert.That(returnedEventIds).DoesNotContain(unstampedEventId)
      .Because("the unstamped event is held back until the stamper catches up");
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task<long?> _readCommitSequenceFromGetStreamEventsAsync(
      NpgsqlConnection conn, Guid instanceId, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT out_commit_sequence FROM get_stream_events(@inst, ARRAY[@sid]::uuid[]) LIMIT 1";
    cmd.Parameters.AddWithValue("inst", instanceId);
    cmd.Parameters.AddWithValue("sid", streamId);
    var result = await cmd.ExecuteScalarAsync();
    return result switch {
      null or DBNull => null,
      _ => Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture)
    };
  }

  private static async Task<int> _countGetStreamEventsRowsAsync(
      NpgsqlConnection conn, Guid instanceId, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM get_stream_events(@inst, ARRAY[@sid]::uuid[])";
    cmd.Parameters.AddWithValue("inst", instanceId);
    cmd.Parameters.AddWithValue("sid", streamId);
    var result = await cmd.ExecuteScalarAsync();
    return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task<List<Guid>> _readEventIdsFromGetStreamEventsAsync(
      NpgsqlConnection conn, Guid instanceId, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT out_event_id FROM get_stream_events(@inst, ARRAY[@sid]::uuid[])";
    cmd.Parameters.AddWithValue("inst", instanceId);
    cmd.Parameters.AddWithValue("sid", streamId);
    var ids = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  private static async Task<int> _stampPendingAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT stamp_pending_commit_sequences(1000)";
    var result = await cmd.ExecuteScalarAsync();
    return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task _insertEventStoreRowAsync(NpgsqlConnection conn, Guid eventId, Guid streamId, int version) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, version, event_type,
         scope, created_at)
      VALUES
        (@eid, @sid, @sid, 'TestAggregate', @ver, 'TestEvent',
         NULL, NOW())";
    ins.Parameters.AddWithValue("eid", eventId);
    ins.Parameters.AddWithValue("sid", streamId);
    ins.Parameters.AddWithValue("ver", version);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertPerspectiveEventLeasedAsync(
      NpgsqlConnection conn, Guid workId, Guid streamId, string perspectiveName, Guid eventId, Guid instanceId,
      DateTimeOffset? createdAt = null) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry,
         partition_number, status, attempts, created_at)
      VALUES (@work, @stream, @persp, @event, @inst, NOW() + INTERVAL '5 minutes',
              0, 0, 0, @created)";
    ins.Parameters.AddWithValue("work", workId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("persp", perspectiveName);
    ins.Parameters.AddWithValue("event", eventId);
    ins.Parameters.AddWithValue("inst", instanceId);
    ins.Parameters.AddWithValue("created", createdAt?.UtcDateTime ?? (object)DateTime.UtcNow);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = EXCLUDED.last_heartbeat_at";
    cmd.Parameters.AddWithValue("id", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }
}
