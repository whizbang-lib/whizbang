using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Slice 26.7 — RED-first locks for <c>get_stream_events</c> returning
/// <c>commit_sequence</c> alongside the existing fields. Cursor-swap consumers
/// (slices 26.8-11) read this column for commit-stable ordering; NULL means the
/// stamper hasn't caught up yet, callers fall back to event_id ordering.
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
  public async Task GetStreamEvents_ReturnsNullCommitSequenceBeforeStampingAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();

    await _registerInstanceAsync(conn, instanceId);
    await _insertEventStoreRowAsync(conn, eventId, streamId, version: 1);
    await _insertPerspectiveEventLeasedAsync(conn, workId, streamId, "Projection.Test", eventId, instanceId);

    // Skip stamping — commit_sequence stays NULL.
    var commitSeq = await _readCommitSequenceFromGetStreamEventsAsync(conn, instanceId, streamId);
    await Assert.That(commitSeq).IsNull()
      .Because("unstamped rows return commit_sequence=NULL; consumers fall back to event_id ordering");
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
         event_data, metadata, scope, created_at)
      VALUES
        (@eid, @sid, @sid, 'TestAggregate', @ver, 'TestEvent',
         '{}'::jsonb, '{}'::jsonb, NULL, NOW())";
    ins.Parameters.AddWithValue("eid", eventId);
    ins.Parameters.AddWithValue("sid", streamId);
    ins.Parameters.AddWithValue("ver", version);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertPerspectiveEventLeasedAsync(
      NpgsqlConnection conn, Guid workId, Guid streamId, string perspectiveName, Guid eventId, Guid instanceId) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry,
         partition_number, status, attempts, created_at)
      VALUES (@work, @stream, @persp, @event, @inst, NOW() + INTERVAL '5 minutes',
              0, 0, 0, NOW())";
    ins.Parameters.AddWithValue("work", workId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("persp", perspectiveName);
    ins.Parameters.AddWithValue("event", eventId);
    ins.Parameters.AddWithValue("inst", instanceId);
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
