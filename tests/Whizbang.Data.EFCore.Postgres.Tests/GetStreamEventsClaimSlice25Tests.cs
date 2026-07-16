using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the slice 25 invariants: <c>get_stream_events</c> performs an atomic
/// claim+fetch — before reading, it claims every eligible <c>wh_perspective_events</c>
/// row for the requested streams to the caller. Closes the cursor-advances-past-
/// orphaned-rows race that produced residual inversions after slices 23 + 24c.
/// </summary>
/// <remarks>
/// Background: slices 18a-e/23 ordered events at every flush boundary, slice 24c
/// made rewinds cheap, slice 25 stops the cursor from advancing past rows the
/// worker didn't see in its fetch result. The race source was the
/// <c>instance_id = p_instance_id</c> filter on <c>get_stream_events</c> — orphan
/// rows (instance_id NULL or expired lease) were invisible to the fetch and got
/// claimed later by <c>claim_orphaned_perspective_events</c>, by which point the
/// cursor had already advanced.
/// </remarks>
public class GetStreamEventsClaimSlice25Tests : EFCoreTestBase {

  [Test]
  public async Task GetStreamEvents_OrphanedRow_IsClaimedAndReturnedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "MyApp.Test+Projection";
    await _registerInstanceAsync(connection, instanceId);
    await _insertEventAsync(connection, streamId, eventId, perspectiveName);

    // Insert perspective_event with instance_id=NULL and no lease (orphaned).
    await _insertOrphanedPerspectiveEventAsync(connection, workId, streamId, perspectiveName, eventId);

    var rows = await _getStreamEventsAsync(connection, instanceId, [streamId]);

    await Assert.That(rows.Count).IsEqualTo(1);
    await Assert.That(rows[0].EventId).IsEqualTo(eventId);

    // Verify the row is now leased to the caller.
    var leased = await _getInstanceForRowAsync(connection, workId);
    await Assert.That(leased).IsEqualTo(instanceId);
  }

  [Test]
  public async Task GetStreamEvents_ExpiredLeaseRow_IsReclaimedAndReturnedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var ourInstance = (Guid)TrackedGuid.NewMedo();
    var otherInstance = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "MyApp.Test+Projection";
    await _registerInstanceAsync(connection, ourInstance);
    await _registerInstanceAsync(connection, otherInstance);
    await _insertEventAsync(connection, streamId, eventId, perspectiveName);

    // Row leased to another instance but the lease has expired.
    await _insertPerspectiveEventWithLeaseAsync(connection, workId, streamId, perspectiveName, eventId,
      otherInstance, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-1));

    var rows = await _getStreamEventsAsync(connection, ourInstance, [streamId]);

    await Assert.That(rows.Count).IsEqualTo(1);
    var leased = await _getInstanceForRowAsync(connection, workId);
    await Assert.That(leased).IsEqualTo(ourInstance);
  }

  [Test]
  public async Task GetStreamEvents_RowLeasedToOtherInstance_ValidLease_NotClaimedAsync() {
    // Row owned by another instance with a still-valid lease MUST NOT be poached by us.
    // The original instance keeps processing; our worker waits for orphan reclaim later.
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var ourInstance = (Guid)TrackedGuid.NewMedo();
    var otherInstance = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "MyApp.Test+Projection";
    await _registerInstanceAsync(connection, ourInstance);
    await _registerInstanceAsync(connection, otherInstance);
    await _insertEventAsync(connection, streamId, eventId, perspectiveName);

    await _insertPerspectiveEventWithLeaseAsync(connection, workId, streamId, perspectiveName, eventId,
      otherInstance, leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(5));  // valid lease

    var rows = await _getStreamEventsAsync(connection, ourInstance, [streamId]);

    // We don't get the row back — it's still validly leased to the other instance.
    await Assert.That(rows.Count).IsEqualTo(0);
    var leased = await _getInstanceForRowAsync(connection, workId);
    await Assert.That(leased).IsEqualTo(otherInstance);  // not poached
  }

  [Test]
  public async Task GetStreamEvents_AttemptsBumpsOnlyOnLeaseTakeoverAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var ourInstance = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "MyApp.Test+Projection";
    await _registerInstanceAsync(connection, ourInstance);
    await _insertEventAsync(connection, streamId, eventId, perspectiveName);

    // First call: orphan → claimed → attempts goes 0 → 1
    await _insertOrphanedPerspectiveEventAsync(connection, workId, streamId, perspectiveName, eventId);
    await _getStreamEventsAsync(connection, ourInstance, [streamId]);
    var attempts1 = await _getAttemptsForRowAsync(connection, workId);
    await Assert.That(attempts1).IsEqualTo(1);

    // Second call: same instance re-leasing → attempts MUST stay at 1
    await _getStreamEventsAsync(connection, ourInstance, [streamId]);
    var attempts2 = await _getAttemptsForRowAsync(connection, workId);
    await Assert.That(attempts2).IsEqualTo(1);
  }

  // --- helpers ---

  private sealed record StreamEventRow {
    public Guid EventId { get; init; }
    public Guid EventWorkId { get; init; }
  }

  private static async Task<List<StreamEventRow>> _getStreamEventsAsync(
      NpgsqlConnection connection, Guid instanceId, Guid[] streamIds) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT out_event_id, out_event_work_id FROM get_stream_events(@inst, @streams, NOW(), 300)";
    cmd.Parameters.AddWithValue("inst", instanceId);
    cmd.Parameters.Add(new NpgsqlParameter("streams", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = streamIds });

    var rows = new List<StreamEventRow>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(new StreamEventRow {
        EventId = reader.GetGuid(0),
        EventWorkId = reader.GetGuid(1),
      });
    }
    return rows;
  }

  private static async Task<Guid?> _getInstanceForRowAsync(NpgsqlConnection connection, Guid workId) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT instance_id FROM wh_perspective_events WHERE event_work_id = @id";
    cmd.Parameters.AddWithValue("id", workId);
    var result = await cmd.ExecuteScalarAsync();
    return result is null or DBNull ? null : (Guid)result;
  }

  private static async Task<int> _getAttemptsForRowAsync(NpgsqlConnection connection, Guid workId) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT attempts FROM wh_perspective_events WHERE event_work_id = @id";
    cmd.Parameters.AddWithValue("id", workId);
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _insertEventAsync(NpgsqlConnection connection, Guid streamId, Guid eventId, string aggregateType) {
    // Stamp commit_sequence at insert (via the real wh_commit_seq sequence). Mig 058 gates
    // unstamped rows out of get_stream_events entirely, so these claim/lease invariant tests
    // must seed stamped rows — which also matches production, where the stamper runs continuously
    // and a row is almost always stamped before the drain claims it.
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence)
      VALUES (@event, @stream, @stream, @agg, 'TestEvent', NULL,
              (SELECT COALESCE(MAX(version), 0) + 1 FROM wh_event_store WHERE stream_id = @stream),
              nextval('wh_commit_seq'))";
    cmd.Parameters.AddWithValue("event", eventId);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("agg", aggregateType);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _insertOrphanedPerspectiveEventAsync(
      NpgsqlConnection connection,
      Guid eventWorkId,
      Guid streamId,
      string perspectiveName,
      Guid eventId) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry,
         partition_number, status, attempts, created_at)
      VALUES (@work, @stream, @persp, @event, NULL, NULL, 0, 0, 0, NOW())";
    ins.Parameters.AddWithValue("work", eventWorkId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("persp", perspectiveName);
    ins.Parameters.AddWithValue("event", eventId);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertPerspectiveEventWithLeaseAsync(
      NpgsqlConnection connection,
      Guid eventWorkId,
      Guid streamId,
      string perspectiveName,
      Guid eventId,
      Guid instanceId,
      DateTimeOffset leaseExpiry) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry,
         partition_number, status, attempts, created_at)
      VALUES (@work, @stream, @persp, @event, @inst, @lease, 0, 0, 0, NOW())";
    ins.Parameters.AddWithValue("work", eventWorkId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("persp", perspectiveName);
    ins.Parameters.AddWithValue("event", eventId);
    ins.Parameters.AddWithValue("inst", instanceId);
    ins.Parameters.AddWithValue("lease", leaseExpiry);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection connection, Guid instanceId) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
    cmd.Parameters.AddWithValue("id", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }
}
