using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Phase H step 10 slice 1 — RED-first locks for canonical event ordering in
/// <c>wh_event_store</c>. The version assigned at backfill time MUST match the
/// canonical UUIDv7 message_id ordering, not the wall-clock <c>created_at</c> /
/// <c>received_at</c> on the storage row.
/// </summary>
/// <remarks>
/// <para>
/// Production observation on JDX BFF (2026-05-03 during job creation): cursor
/// inversions firing repeatedly on <c>BulkJobImportOrchestration+Projection</c> and
/// <c>UberDraftJob+Projection</c>. Each inversion triggers a full replay or
/// snapshot-restore. Root cause: high-rate event emission produces inbox/outbox
/// rows whose <c>received_at</c>/<c>created_at</c> timestamps disagree with their
/// UUIDv7 <c>message_id</c> ordering. The version assigned at backfill is based on
/// row-arrival order, so a "later" version may correspond to an "earlier" event_id.
/// Perspectives apply by version, advance cursor, then later see the
/// chronologically-earlier event_id and treat it as an inversion.
/// </para>
/// <para>
/// Fix: order the version assignment by <c>message_id</c> (UUIDv7 = chronological
/// at the source) so version order matches event_id order. Cursor advances are
/// then monotonic by both axes simultaneously — no inversions in the steady state.
/// </para>
/// </remarks>
/// <docs>fundamentals/event-store/version-ordering</docs>
public class EventStoreVersionOrderingSqlTests : EFCoreTestBase {

  // ============================================================================
  // OUTBOX: _emit_event_store_chain
  // ============================================================================

  [Test]
  public async Task EmitEventStoreChain_AssignsVersions_ByMessageIdOrder_NotCreatedAtAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, instanceId);

    // Three message_ids in canonical UUIDv7 order: A < B < C.
    var idA = (Guid)TrackedGuid.NewMedo();
    await Task.Delay(2);
    var idB = (Guid)TrackedGuid.NewMedo();
    await Task.Delay(2);
    var idC = (Guid)TrackedGuid.NewMedo();

    // Deliberately INSERT in reverse-message-id order so the ROW_NUMBER OVER ORDER BY
    // created_at would produce versions C=1, B=2, A=3 — i.e. version order DISAGREES with
    // message_id order. With the fix, ORDER BY message_id pins versions to A=1, B=2, C=3.
    var nowOldest = DateTimeOffset.UtcNow.AddSeconds(-30);
    await _insertOutboxEventAsync(conn, idC, streamId, instanceId, createdAt: nowOldest);                          // earliest created_at, latest message_id
    await _insertOutboxEventAsync(conn, idB, streamId, instanceId, createdAt: nowOldest.AddSeconds(10));
    await _insertOutboxEventAsync(conn, idA, streamId, instanceId, createdAt: nowOldest.AddSeconds(20));            // latest created_at, earliest message_id

    await _callEmitEventStoreChainAsync(conn, instanceId, [idA, idB, idC]);

    var versions = await _readVersionsAsync(conn, streamId);
    await Assert.That(versions[idA]).IsLessThan(versions[idB])
      .Because("UUIDv7 A < B → version(A) must be < version(B), regardless of created_at order");
    await Assert.That(versions[idB]).IsLessThan(versions[idC]);
  }

  // ============================================================================
  // INBOX backfill in claim_work
  // ============================================================================

  [Test]
  public async Task ClaimWorkInboxBackfill_AssignsVersions_ByMessageIdOrder_NotReceivedAtAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, instanceId);

    var idA = (Guid)TrackedGuid.NewMedo();
    await Task.Delay(2);
    var idB = (Guid)TrackedGuid.NewMedo();
    await Task.Delay(2);
    var idC = (Guid)TrackedGuid.NewMedo();

    // INSERT in reverse message_id order → received_at ascending DOES NOT match message_id ascending.
    var nowOldest = DateTimeOffset.UtcNow.AddSeconds(-30);
    await _insertInboxEventAsync(conn, idC, streamId, instanceId, receivedAt: nowOldest);
    await _insertInboxEventAsync(conn, idB, streamId, instanceId, receivedAt: nowOldest.AddSeconds(10));
    await _insertInboxEventAsync(conn, idA, streamId, instanceId, receivedAt: nowOldest.AddSeconds(20));

    // claim_work runs the inbox-backfill INSERT into wh_event_store as a side effect.
    await _callClaimWorkAsync(conn, instanceId);

    var versions = await _readVersionsAsync(conn, streamId);
    await Assert.That(versions[idA]).IsLessThan(versions[idB])
      .Because("inbox backfill must assign versions by message_id order so cursor monotonicity matches event_id monotonicity");
    await Assert.That(versions[idB]).IsLessThan(versions[idC]);
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private static async Task _callEmitEventStoreChainAsync(NpgsqlConnection conn, Guid instanceId, Guid[] messageIds) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT _emit_event_store_chain(@p_ids, @p_inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
    cmd.Parameters.Add(new NpgsqlParameter("p_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = messageIds });
    cmd.Parameters.AddWithValue("p_inst", instanceId);
    await cmd.ExecuteScalarAsync();
  }

  private static async Task _callClaimWorkAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM claim_work(@p_inst, 'test-svc', 'test-host', 100, 100, 100, 100)";
    cmd.Parameters.AddWithValue("p_inst", instanceId);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) { }
  }

  private static async Task<Dictionary<Guid, int>> _readVersionsAsync(NpgsqlConnection conn, Guid streamId) {
    var dict = new Dictionary<Guid, int>();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_id, version FROM wh_event_store WHERE stream_id = @s ORDER BY version";
    cmd.Parameters.AddWithValue("s", streamId);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      dict[reader.GetGuid(0)] = reader.GetInt32(1);
    }
    return dict;
  }

  private static async Task _insertOutboxEventAsync(
      NpgsqlConnection conn, Guid messageId, Guid streamId, Guid instanceId, DateTimeOffset createdAt) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, scope, status, attempts,
         created_at, stream_id, partition_number, instance_id, lease_expiry, is_event)
      VALUES (@msg, 'topic', 'TestEvent', 'TestEnv', '{""p"":1}'::jsonb, '{}'::jsonb, '{}'::jsonb, 1, 0,
              @created, @stream, 0, @inst, NOW() + INTERVAL '5 minutes', true)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("inst", instanceId);
    ins.Parameters.Add(new NpgsqlParameter("created", NpgsqlDbType.TimestampTz) { Value = createdAt });
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertInboxEventAsync(
      NpgsqlConnection conn, Guid messageId, Guid streamId, Guid instanceId, DateTimeOffset receivedAt) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, scope, status, attempts,
         received_at, instance_id, lease_expiry, stream_id, partition_number, is_event)
      VALUES (@msg, 'TestHandler', 'TestEvent', '{""p"":1}'::jsonb, '{}'::jsonb, '{}'::jsonb, 1, 0,
              @received, @inst, NOW() + INTERVAL '5 minutes', @stream, 0, true)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("inst", instanceId);
    ins.Parameters.Add(new NpgsqlParameter("received", NpgsqlDbType.TimestampTz) { Value = receivedAt });
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
    cmd.Parameters.AddWithValue("id", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }
}
