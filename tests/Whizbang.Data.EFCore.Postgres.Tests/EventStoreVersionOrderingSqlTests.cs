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
/// Production observation on a consumer's app-service (during order creation): cursor
/// inversions firing repeatedly on <c>BulkImportOrchestration+Projection</c> and
/// <c>Order+Projection</c>. Each inversion triggers a full replay or
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
[Category("Shard3")]
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
  // SLICE 11 (Half B): batched _emit_event_store_chain — locks the producer-side
  // stream-affinity batching contract used by SlidingWindowOutboxBatchStrategy.
  // ============================================================================

  [Test]
  public async Task EmitChain_BatchOf50SameStream_AssignsContiguousVersionsAsync() {
    // Slice 11: SlidingWindowOutboxBatchStrategy will hand the chain a batch of up to 100
    // same-stream messages in a single call. Lock the contract: contiguous versions 1..N in
    // message_id order, regardless of insertion order in wh_outbox.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, instanceId);

    var ids = new Guid[50];
    for (var i = 0; i < 50; i++) {
      ids[i] = (Guid)TrackedGuid.NewMedo();
      await Task.Delay(1);
    }

    var nowOldest = DateTimeOffset.UtcNow.AddSeconds(-30);
    // Insert in REVERSE message_id order — created_at sequence disagrees with message_id
    // sequence, so any version assignment that doesn't ORDER BY message_id would violate
    // the contract.
    for (var i = 49; i >= 0; i--) {
      await _insertOutboxEventAsync(conn, ids[i], streamId, instanceId, createdAt: nowOldest.AddMilliseconds(i));
    }

    await _callEmitEventStoreChainAsync(conn, instanceId, ids);

    var versions = await _readVersionsAsync(conn, streamId);
    await Assert.That(versions.Count).IsEqualTo(50);
    for (var i = 0; i < 50; i++) {
      await Assert.That(versions[ids[i]]).IsEqualTo(i + 1)
        .Because($"contiguous versions: ids[{i}] (UUIDv7 ordered) must get version {i + 1}");
    }
  }

  [Test]
  public async Task EmitChain_BatchOfMixedStreams_PerStreamVersionsCorrectAsync() {
    // Slice 11: when a single emit batch contains messages spanning two different streams,
    // each stream's version sequence must be independently monotonic (1..N per stream),
    // not a single 1..(N+M) sequence across the union.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamA = (Guid)TrackedGuid.NewMedo();
    var streamB = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, instanceId);

    // 5 messages per stream, interleaved A/B in message_id order.
    var ids = new (Guid Id, Guid Stream)[10];
    for (var i = 0; i < 10; i++) {
      ids[i] = ((Guid)TrackedGuid.NewMedo(), i % 2 == 0 ? streamA : streamB);
      await Task.Delay(1);
    }

    var nowOldest = DateTimeOffset.UtcNow.AddSeconds(-30);
    foreach (var (id, stream) in ids) {
      await _insertOutboxEventAsync(conn, id, stream, instanceId, createdAt: nowOldest);
    }

    await _callEmitEventStoreChainAsync(conn, instanceId, [.. ids.Select(x => x.Id)]);

    var versionsA = await _readVersionsAsync(conn, streamA);
    var versionsB = await _readVersionsAsync(conn, streamB);
    await Assert.That(versionsA.Count).IsEqualTo(5);
    await Assert.That(versionsB.Count).IsEqualTo(5);

    var idsA = ids.Where(x => x.Stream == streamA).Select(x => x.Id).ToArray();
    var idsB = ids.Where(x => x.Stream == streamB).Select(x => x.Id).ToArray();
    for (var i = 0; i < 5; i++) {
      await Assert.That(versionsA[idsA[i]]).IsEqualTo(i + 1)
        .Because("stream A version sequence is independent of stream B");
      await Assert.That(versionsB[idsB[i]]).IsEqualTo(i + 1)
        .Because("stream B version sequence is independent of stream A");
    }
  }

  [Test]
  public async Task EmitChain_BatchedShuffledMessageIds_InsertsByMessageIdOrderAsync() {
    // Slice 11: regression lock for the cursor-inversion case at batch scale. Producer-side
    // sliding window may write same-stream messages to its bounded channel in non-deterministic
    // order under contention; the strategy sorts before flush, but the chain must also
    // independently ORDER BY message_id (defense in depth). 20 shuffled messages, single batch.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _registerInstanceAsync(conn, instanceId);

    var ordered = new Guid[20];
    for (var i = 0; i < 20; i++) {
      ordered[i] = (Guid)TrackedGuid.NewMedo();
      await Task.Delay(1);
    }

    // Deterministic shuffle (seed 42) so test failures reproduce.
    var rng = new Random(42);
    var shuffled = ordered.OrderBy(_ => rng.Next()).ToArray();
    var nowOldest = DateTimeOffset.UtcNow.AddSeconds(-30);
    foreach (var id in shuffled) {
      await _insertOutboxEventAsync(conn, id, streamId, instanceId, createdAt: nowOldest);
    }

    // Pass the SHUFFLED array to the chain — exercises the SQL ORDER BY explicitly.
    await _callEmitEventStoreChainAsync(conn, instanceId, shuffled);

    var versions = await _readVersionsAsync(conn, streamId);
    await Assert.That(versions.Count).IsEqualTo(20);
    for (var i = 0; i < 20; i++) {
      await Assert.That(versions[ordered[i]]).IsEqualTo(i + 1)
        .Because("regardless of array order in the call, version assignment is ORDER BY message_id");
    }
  }

  // ============================================================================
  // SLICE 4: ON CONFLICT DO NOTHING tolerates (stream_id, version) collision
  // ============================================================================

  [Test]
  public async Task EmitEventStoreChain_SourceUses_OnConflictDoNothing_NoConstraintAsync() {
    // Phase H step 10 slice 4 regression lock: pre-fix, the INSERT was
    // `ON CONFLICT (event_id) DO NOTHING` which only handled event_id duplicates. A
    // (stream_id, version) conflict (concurrent insert race) bubbled up as PG 23505 and
    // failed the whole claim_work tick — observed on a consumer's app-service in production.
    // The fix is
    // `ON CONFLICT DO NOTHING` with NO constraint specifier so PG handles both unique
    // constraints gracefully. With slices 2+3 in place we shouldn't hit the version conflict
    // in practice, but this is the third defensive layer.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var outboxSrc = await _readFunctionSourceAsync(conn, "_emit_event_store_chain");
    var inboxSrc = await _readFunctionSourceAsync(conn, "_emit_event_store_chain_for_inbox");

    // Both functions must use the constraint-less form. A specific-constraint form
    // (`ON CONFLICT (event_id)` or `ON CONFLICT ON CONSTRAINT ...`) only catches that one
    // constraint and lets others bubble up — which is exactly the bug we fixed.
    await Assert.That(outboxSrc).Contains("ON CONFLICT DO NOTHING");
    await Assert.That(outboxSrc).DoesNotContain("ON CONFLICT (event_id) DO NOTHING")
      .Because("the constraint-specific form fails on idx_event_store_stream conflicts");
    await Assert.That(inboxSrc).Contains("ON CONFLICT DO NOTHING");
    await Assert.That(inboxSrc).DoesNotContain("ON CONFLICT (event_id) DO NOTHING");
  }

  // ============================================================================
  // INVARIANT: advisory locks present in both backfill paths (slice 2)
  // ============================================================================

  [Test]
  public async Task EmitEventStoreChain_SourceContains_AdvisoryLockPerStreamAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var src = await _readFunctionSourceAsync(conn, "_emit_event_store_chain");

    await Assert.That(src).Contains("pg_advisory_xact_lock(hashtext('wh_event_store:'")
      .Because("the outbox backfill must take a per-stream advisory lock before reading MAX(version) — without it concurrent transactions race on version assignment");
  }

  [Test]
  public async Task EmitEventStoreChainForInbox_SourceContains_AdvisoryLockPerStreamAsync() {
    // Phase H step 10 slice 2 regression lock: the inbox backfill (in
    // _emit_event_store_chain_for_inbox, called by claim_work after claim_orphaned_inbox) used
    // to skip the lock — that's the bug observed on a consumer's app-service as PG 23505
    // on idx_event_store_stream during order creation.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var src = await _readFunctionSourceAsync(conn, "_emit_event_store_chain_for_inbox");

    await Assert.That(src).Contains("pg_advisory_xact_lock(hashtext('wh_event_store:'")
      .Because("_emit_event_store_chain_for_inbox must take a per-stream advisory lock before the backfill INSERT");
    await Assert.That(src).Contains("WITH inbox_events AS")
      .Because("anchor — the lock must precede the backfill CTE");
    var lockIdx = src.IndexOf("pg_advisory_xact_lock(hashtext('wh_event_store:'", StringComparison.Ordinal);
    var inboxBackfillIdx = src.IndexOf("WITH inbox_events AS", StringComparison.Ordinal);
    await Assert.That(lockIdx).IsLessThan(inboxBackfillIdx)
      .Because("the lock must be acquired BEFORE the backfill INSERT runs, otherwise it doesn't serialize the version computation");
  }

  private static async Task<string> _readFunctionSourceAsync(NpgsqlConnection conn, string functionName) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT pg_get_functiondef(p.oid) FROM pg_proc p WHERE p.proname = @n LIMIT 1";
    cmd.Parameters.AddWithValue("n", functionName);
    var result = await cmd.ExecuteScalarAsync();
    return result?.ToString() ?? string.Empty;
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
