using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Slice 26.14 — locks the rule that <c>_emit_event_store_chain</c> leases newly-inserted
/// <c>wh_perspective_events</c> rows to the stream's pinned owner (from
/// <c>wh_active_streams.assigned_instance_id</c>), NOT the commit instance.
///
/// <para>
/// The cross-instance saga race (root cause of a production run's asymmetry):
/// </para>
/// <list type="bullet">
/// <item>Instance A's receptor emits "SagaItemCompleted" → inserts perspective_events leased to A.</item>
/// <item>Instance B's receptor emits "SagaItemStarted" for the same stream → inserts perspective_events leased to B.</item>
/// <item>B's drainer (fetch_pending_perspective_events filters by instance_id=B) sees only its rows, applies them, cursor advances.</item>
/// <item>A's row sits leased to A. When A's lease expires or active_streams ownership flips to B,
///       claim_orphaned re-leases A's row to B. B drains it, sees cursor past it, logs an inversion + rewinds.</item>
/// </list>
///
/// <para>
/// Fix: route the lease through <c>wh_active_streams</c>. When a live owner is pinned for the stream,
/// lease the row to that owner regardless of which instance ran the commit. When no live owner exists
/// (new stream OR stale owner), leave the row unleased (<c>instance_id IS NULL</c>) so
/// <c>claim_orphaned_perspective_events</c> can pin + lease atomically on its next cycle.
/// </para>
/// </summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
[Category("Shard3")]
public class EmitEventStoreChainOwnerLeaseSqlTests : EFCoreTestBase {

  private const string EVENT_TYPE = "Whizbang.Tests.OwnerLeaseEvent, Whizbang.Tests";
  private const string PERSPECTIVE_NAME = "Test.OwnerLeasePerspective+Projection";

  // ---------- helpers ----------

  private static async Task _registerAssociationAsync(NpgsqlConnection conn, string eventType, string perspectiveName) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name,
         normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @messageType, 'perspective', @target, 'test-svc',
              @messageType, NOW(), NOW())
      ON CONFLICT DO NOTHING
      """;
    cmd.Parameters.AddWithValue("messageType", eventType);
    cmd.Parameters.AddWithValue("target", perspectiveName);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _registerLiveInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at)
      VALUES (@iid, 'test-svc', 'test-host', 1, NOW(), NOW())
      ON CONFLICT DO NOTHING
      """;
    cmd.Parameters.AddWithValue("iid", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _pinStreamAsync(NpgsqlConnection conn, Guid streamId, Guid ownerInstanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_active_streams
        (stream_id, partition_number, assigned_instance_id, last_activity_at)
      VALUES (@sid, 0, @owner, NOW())
      ON CONFLICT (stream_id) DO UPDATE SET assigned_instance_id = EXCLUDED.assigned_instance_id
      """;
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("owner", ownerInstanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _insertOutboxEventAsync(NpgsqlConnection conn, Guid messageId, Guid streamId, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata,
         status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event,
         created_at)
      VALUES
        (@id, 'test-dest', @type, 'env', '{"p":{}}'::jsonb, '{}'::jsonb,
         1, 0, @inst, NOW() + INTERVAL '5 minutes', 0, @sid, true, NOW())
      """;
    cmd.Parameters.AddWithValue("id", messageId);
    cmd.Parameters.AddWithValue("type", EVENT_TYPE);
    cmd.Parameters.AddWithValue("inst", instanceId);
    cmd.Parameters.AddWithValue("sid", streamId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _callEmitEventStoreChainAsync(NpgsqlConnection conn, Guid[] outboxIds, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
    cmd.Parameters.AddWithValue("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, outboxIds);
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _callEmitEventStoreChainNullLeaseAsync(NpgsqlConnection conn, Guid[] outboxIds) {
    // Strategy-flush path: caller passes NULL p_instance_id + NULL p_lease_expiry, signaling
    // "leave unleased — claim_orphaned will pick it up." This contract must NOT be broken by
    // the wh_active_streams owner lookup.
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT _emit_event_store_chain(@ids, NULL::uuid, NULL::timestamptz, NOW(), 10000)";
    cmd.Parameters.AddWithValue("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, outboxIds);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<(Guid? instanceId, DateTimeOffset? leaseExpiry)> _readLeaseAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT instance_id, lease_expiry FROM wh_perspective_events WHERE event_id = @eid LIMIT 1";
    cmd.Parameters.AddWithValue("eid", eventId);
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) {
      return (null, null);
    }
    var inst = await r.IsDBNullAsync(0) ? (Guid?)null : r.GetGuid(0);
    var exp = await r.IsDBNullAsync(1) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(1);
    return (inst, exp);
  }

  // ---------- TESTS ----------

  [Test]
  public async Task EmitEventStoreChain_StreamPinnedToOtherLiveInstance_LeasesToOwnerAsync() {
    // The smoking-gun fix: commit happens on instance A, but the stream is owned by B.
    // Pre-fix: perspective_events leased to A (the commit instance). Cross-instance race.
    // Post-fix: perspective_events leased to B (the stream owner).
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceA = (Guid)TrackedGuid.NewMedo();  // commit instance
    var instanceB = (Guid)TrackedGuid.NewMedo();  // stream owner
    var streamId = (Guid)TrackedGuid.NewMedo();
    var messageId = (Guid)TrackedGuid.NewMedo();

    await _registerAssociationAsync(conn, EVENT_TYPE, PERSPECTIVE_NAME);
    await _registerLiveInstanceAsync(conn, instanceA);
    await _registerLiveInstanceAsync(conn, instanceB);
    await _pinStreamAsync(conn, streamId, ownerInstanceId: instanceB);

    await _insertOutboxEventAsync(conn, messageId, streamId, instanceA);
    await _callEmitEventStoreChainAsync(conn, [messageId], instanceA);

    var (leasedTo, leaseExpiry) = await _readLeaseAsync(conn, messageId);

    await Assert.That(leasedTo).IsEqualTo(instanceB)
      .Because("perspective_events must lease to the stream's pinned owner (B), not the commit instance (A)");
    await Assert.That(leaseExpiry).IsNotNull()
      .Because("a leased row has a real lease_expiry");
  }

  [Test]
  public async Task EmitEventStoreChain_NoOwnerPinned_LeasesToCommitInstanceAsync() {
    // First-event-ever-for-this-stream case: no wh_active_streams row yet. Fall back to
    // the commit instance so sync paths (UI waiting on a perspective checkpoint) don't pay
    // claim_orphaned-polling-interval latency on first event. The pinning happens via
    // claim_orphaned on its next cycle; subsequent commits will see the pin and route correctly.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var commitInstance = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var messageId = (Guid)TrackedGuid.NewMedo();

    await _registerAssociationAsync(conn, EVENT_TYPE, PERSPECTIVE_NAME);
    await _registerLiveInstanceAsync(conn, commitInstance);
    await _insertOutboxEventAsync(conn, messageId, streamId, commitInstance);
    await _callEmitEventStoreChainAsync(conn, [messageId], commitInstance);

    var (leasedTo, leaseExpiry) = await _readLeaseAsync(conn, messageId);

    await Assert.That(leasedTo).IsEqualTo(commitInstance)
      .Because("no pinned owner → fall back to commit instance for zero-latency sync paths");
    await Assert.That(leaseExpiry).IsNotNull()
      .Because("fallback leases carry a real lease_expiry");
  }

  [Test]
  public async Task EmitEventStoreChain_PinnedOwnerIsDeadInstance_LeasesToCommitInstanceAsync() {
    // Edge case: wh_active_streams points to an instance that's no longer in
    // wh_service_instances (cleanup_stale_instances removed it). Treat as no owner, fall
    // back to commit instance. claim_orphaned will eventually re-pin to a live instance.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var commitInstance = (Guid)TrackedGuid.NewMedo();
    var deadOwner = (Guid)TrackedGuid.NewMedo();  // never inserted into wh_service_instances
    var streamId = (Guid)TrackedGuid.NewMedo();
    var messageId = (Guid)TrackedGuid.NewMedo();

    await _registerAssociationAsync(conn, EVENT_TYPE, PERSPECTIVE_NAME);
    await _registerLiveInstanceAsync(conn, commitInstance);
    await _pinStreamAsync(conn, streamId, ownerInstanceId: deadOwner);

    await _insertOutboxEventAsync(conn, messageId, streamId, commitInstance);
    await _callEmitEventStoreChainAsync(conn, [messageId], commitInstance);

    var (leasedTo, _) = await _readLeaseAsync(conn, messageId);

    await Assert.That(leasedTo).IsEqualTo(commitInstance)
      .Because("pinned owner not in wh_service_instances → treat as no owner; fall back to commit instance");
  }

  [Test]
  public async Task EmitEventStoreChain_NullLeaseExpiry_LeavesUnleased_EvenWithPinnedOwnerAsync() {
    // Regression lock for a production upload-stall bug (slice 26.14 follow-up).
    // The strategy-flush path calls store_outbox_messages with NULL p_instance_id +
    // NULL p_lease_expiry, signaling "leave unleased — claim_orphaned will pick it up."
    // store_outbox_messages UPSERTs wh_active_streams with the commit instance as owner.
    // If _emit_event_store_chain THEN routes through the owner lookup unconditionally, the
    // perspective_events row lands with instance_id SET (from owner) but lease_expiry NULL
    // (from caller), and claim_orphaned's `(instance_id IS NULL OR lease_expiry < now)`
    // filter excludes it → row stranded forever → sync sync_inquiry waiter times out →
    // "Upload not found" UI symptom. Guard the owner lookup behind p_lease_expiry IS NOT NULL.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var streamOwner = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var messageId = (Guid)TrackedGuid.NewMedo();

    await _registerAssociationAsync(conn, EVENT_TYPE, PERSPECTIVE_NAME);
    await _registerLiveInstanceAsync(conn, streamOwner);
    // Stream IS pinned (typical post-store_outbox_messages state).
    await _pinStreamAsync(conn, streamId, ownerInstanceId: streamOwner);
    // Outbox row inserted unleased (strategy-flush path).
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event,
           created_at)
        VALUES
          (@id, 'test-dest', @type, 'env', '{"p":{}}'::jsonb, '{}'::jsonb,
           1, 0, NULL, NULL, 0, @sid, true, NOW())
        """;
      ins.Parameters.AddWithValue("id", messageId);
      ins.Parameters.AddWithValue("type", EVENT_TYPE);
      ins.Parameters.AddWithValue("sid", streamId);
      await ins.ExecuteNonQueryAsync();
    }

    await _callEmitEventStoreChainNullLeaseAsync(conn, [messageId]);

    var (leasedTo, leaseExpiry) = await _readLeaseAsync(conn, messageId);

    await Assert.That(leasedTo).IsNull()
      .Because("caller passed NULL p_lease_expiry — owner-route would strand the row; preserve unleased contract for claim_orphaned");
    await Assert.That(leaseExpiry).IsNull();
  }

  [Test]
  public async Task EmitEventStoreChain_StreamPinnedToCommitInstance_LeasesToCommitInstanceAsync() {
    // Single-instance / co-located case: commit instance IS the stream owner. Lease goes
    // to commit instance — behaviorally identical to pre-fix; just confirming we don't
    // break the simple-deployment case.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instance = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var messageId = (Guid)TrackedGuid.NewMedo();

    await _registerAssociationAsync(conn, EVENT_TYPE, PERSPECTIVE_NAME);
    await _registerLiveInstanceAsync(conn, instance);
    await _pinStreamAsync(conn, streamId, ownerInstanceId: instance);

    await _insertOutboxEventAsync(conn, messageId, streamId, instance);
    await _callEmitEventStoreChainAsync(conn, [messageId], instance);

    var (leasedTo, leaseExpiry) = await _readLeaseAsync(conn, messageId);

    await Assert.That(leasedTo).IsEqualTo(instance);
    await Assert.That(leaseExpiry).IsNotNull();
  }
}
