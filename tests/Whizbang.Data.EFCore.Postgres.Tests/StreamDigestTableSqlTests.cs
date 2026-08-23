using System;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for migration 087's incrementally-maintained <c>wh_stream_digests</c> table
/// (Stream Integrity A1c). The emit chain XOR-folds every newly-stored, digest-eligible event into
/// its (origin, tenant, event_type, stream) bucket; the inbox flavor additionally stamps
/// <c>wh_event_store.origin_service_id / origin_commit_sequence</c> from the wh_inbox source columns
/// (the 046 contract, never populated before 087); <c>close_stream</c> and
/// <c>reclassify_events_ephemeral</c> subtract the rows they remove from the audited set. The final
/// invariant: the table always matches the full recompute (ComputeStreamDigestsAsync's query).
/// </summary>
/// <docs>proposals/stream-integrity</docs>
[Category("Shard1")]
public class StreamDigestTableSqlTests : EFCoreTestBase {
  private const string ZERO_UUID = "00000000-0000-0000-0000-000000000000";

  private static string _commitRequest(Guid instanceId, string messagesJson) => $$"""
    {
      "instance_id": "{{instanceId}}",
      "service_name": "test",
      "host_name": "test-host",
      "process_id": 1,
      "new_outbox_messages": [{{messagesJson}}]
    }
    """;

  private static string _outboxMessage(Guid eventId, Guid streamId, string eventType, int flags, string scopeJson = "null") => $$"""
    {
      "MessageId": "{{eventId}}",
      "Destination": "out-topic",
      "MessageType": "{{eventType}}",
      "EnvelopeType": null,
      "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
      "Metadata": {},
      "Scope": {{scopeJson}},
      "StreamId": "{{streamId}}",
      "IsEvent": true,
      "Flags": {{flags}}
    }
    """;

  private static async Task<NpgsqlConnection> _openAsync(Microsoft.EntityFrameworkCore.DbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task _commitAsync(NpgsqlConnection connection, Guid instanceId, string messagesJson) {
    await using var call = connection.CreateCommand();
    call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
    call.Parameters.AddWithValue("req", _commitRequest(instanceId, messagesJson));
    _ = await call.ExecuteScalarAsync();
  }

  private static async Task<(long Lo, long Hi)> _expectedDigestAsync(NpgsqlConnection connection, params Guid[] eventIds) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = """
      SELECT bit_xor(hashtextextended(x::text, 0)), bit_xor(hashtextextended(x::text, 1))
      FROM unnest(@ids::uuid[]) AS x
      """;
    cmd.Parameters.AddWithValue("ids", eventIds);
    await using var r = await cmd.ExecuteReaderAsync();
    await r.ReadAsync();
    return (r.GetInt64(0), r.GetInt64(1));
  }

  private static async Task<(long Lo, long Hi, int Count)?> _digestRowAsync(
      NpgsqlConnection connection, Guid streamId, string originUuid = ZERO_UUID) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = """
      SELECT digest_lo, digest_hi, event_count FROM wh_stream_digests
      WHERE stream_id = @sid AND origin_service_id = @origin::uuid
      """;
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("origin", originUuid);
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) {
      return null;
    }
    return (r.GetInt64(0), r.GetInt64(1), r.GetInt32(2));
  }

  [Test]
  public async Task EmitChain_OutboxEvent_FoldsIntoLocalBucketAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await _commitAsync(connection, Guid.NewGuid(),
      _outboxMessage(eventId, streamId, "Whizbang.Tests.DigestFoldEvent", flags: 0));

    var row = await _digestRowAsync(connection, streamId);
    await Assert.That(row).IsNotNull()
      .Because("A locally-published sourced event must fold into the zero-uuid (local origin) bucket.");
    var expected = await _expectedDigestAsync(connection, eventId);
    await Assert.That(row!.Value.Lo).IsEqualTo(expected.Lo).Because("digest_lo = hashtextextended(event_id, 0).");
    await Assert.That(row.Value.Hi).IsEqualTo(expected.Hi).Because("digest_hi = hashtextextended(event_id, 1).");
    await Assert.That(row.Value.Count).IsEqualTo(1);
  }

  [Test]
  public async Task EmitChain_TenantScopedEvent_BucketsByTenantAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await _commitAsync(connection, Guid.NewGuid(),
      _outboxMessage(eventId, streamId, "Whizbang.Tests.DigestTenantEvent", flags: 0, scopeJson: """{"t":"tenant-42"}"""));

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT scope_tenant, event_count FROM wh_stream_digests WHERE stream_id = @sid";
    cmd.Parameters.AddWithValue("sid", streamId);
    await using var r = await cmd.ExecuteReaderAsync();
    await Assert.That(await r.ReadAsync()).IsTrue();
    await Assert.That(r.GetString(0)).IsEqualTo("tenant-42")
      .Because("The bucket carries the tenant extracted from scope->>'t', matching the recompute.");
    await Assert.That(r.GetInt32(1)).IsEqualTo(1);
  }

  [Test]
  public async Task EmitChain_EphemeralEvent_IsNotFoldedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var streamId = Guid.NewGuid();
    await _commitAsync(connection, Guid.NewGuid(),
      _outboxMessage(Guid.NewGuid(), streamId, "Whizbang.Tests.DigestEphemeralEvent", flags: 8));

    var row = await _digestRowAsync(connection, streamId);
    await Assert.That(row).IsNull()
      .Because("Ephemeral events (flags & 8) are excluded from audited digests — their deletion paths (reaper, pointer-prune) never touch buckets.");
  }

  [Test]
  public async Task InboxEmitChain_ReceivedEvent_StampsOriginAndFoldsIntoOriginBucketAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var originId = Guid.NewGuid();
    var inbox = $$"""
      [{
        "MessageId": "{{eventId}}",
        "HandlerName": "TestHandler",
        "MessageType": "Whizbang.Tests.RemoteDigestEvent",
        "EnvelopeType": "MessageEnvelope",
        "Envelope": {"p": {"OrderId": 42}, "h": []},
        "Metadata": {},
        "Scope": null,
        "StreamId": "{{streamId}}",
        "IsEvent": true,
        "Flags": 0,
        "SourceServiceId": "{{originId}}",
        "SourceCommitSequence": 7
      }]
      """;
    await using (var store = connection.CreateCommand()) {
      store.CommandText = "SELECT * FROM store_inbox_messages(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      store.Parameters.AddWithValue("p", inbox);
      store.Parameters.AddWithValue("inst", instanceId);
      await using var r = await store.ExecuteReaderAsync();
      while (await r.ReadAsync()) { /* drain */ }
    }
    await using (var lease = connection.CreateCommand()) {
      lease.CommandText = "UPDATE wh_inbox SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = @id";
      lease.Parameters.AddWithValue("inst", instanceId);
      lease.Parameters.AddWithValue("id", eventId);
      await lease.ExecuteNonQueryAsync();
    }
    await using (var emit = connection.CreateCommand()) {
      emit.CommandText = "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      emit.Parameters.AddWithValue("inst", instanceId);
      _ = await emit.ExecuteScalarAsync();
    }

    // The 046 contract, finally live: the received event's pointer carries the origin identity.
    await using (var v = connection.CreateCommand()) {
      v.CommandText = "SELECT origin_service_id, origin_commit_sequence FROM wh_event_store WHERE event_id = @id";
      v.Parameters.AddWithValue("id", eventId);
      await using var r = await v.ExecuteReaderAsync();
      await Assert.That(await r.ReadAsync()).IsTrue().Because("The received event must be stored.");
      await Assert.That(r.GetGuid(0)).IsEqualTo(originId)
        .Because("The inbox emit chain stamps origin_service_id from wh_inbox.source_service_id — consumer-side origin-keyed verification depends on it.");
      await Assert.That(r.GetInt64(1)).IsEqualTo(7L)
        .Because("origin_commit_sequence carries the origin's commit sequence for checkpoint-window counting.");
    }

    var row = await _digestRowAsync(connection, streamId, originId.ToString());
    await Assert.That(row).IsNotNull().Because("The received event folds into ITS ORIGIN's bucket, not the local one.");
    await Assert.That(row!.Value.Count).IsEqualTo(1);
    var localRow = await _digestRowAsync(connection, streamId);
    await Assert.That(localRow).IsNull().Because("A received event must not pollute the local-origin lane.");
  }

  [Test]
  public async Task InboxEmitChain_ZeroSource_NormalizesToLocalBucketAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var inbox = $$"""
      [{
        "MessageId": "{{eventId}}",
        "HandlerName": "TestHandler",
        "MessageType": "Whizbang.Tests.LoopbackDigestEvent",
        "EnvelopeType": "MessageEnvelope",
        "Envelope": {"p": {"OrderId": 42}, "h": []},
        "Metadata": {},
        "Scope": null,
        "StreamId": "{{streamId}}",
        "IsEvent": true,
        "Flags": 0,
        "SourceServiceId": "{{ZERO_UUID}}",
        "SourceCommitSequence": 0
      }]
      """;
    await using (var store = connection.CreateCommand()) {
      store.CommandText = "SELECT * FROM store_inbox_messages(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      store.Parameters.AddWithValue("p", inbox);
      store.Parameters.AddWithValue("inst", instanceId);
      await using var r = await store.ExecuteReaderAsync();
      while (await r.ReadAsync()) { /* drain */ }
    }
    await using (var lease = connection.CreateCommand()) {
      lease.CommandText = "UPDATE wh_inbox SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = @id";
      lease.Parameters.AddWithValue("inst", instanceId);
      lease.Parameters.AddWithValue("id", eventId);
      await lease.ExecuteNonQueryAsync();
    }
    await using (var emit = connection.CreateCommand()) {
      emit.CommandText = "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      emit.Parameters.AddWithValue("inst", instanceId);
      _ = await emit.ExecuteScalarAsync();
    }

    await using (var v = connection.CreateCommand()) {
      v.CommandText = "SELECT origin_service_id IS NULL FROM wh_event_store WHERE event_id = @id";
      v.Parameters.AddWithValue("id", eventId);
      await Assert.That((bool)(await v.ExecuteScalarAsync())!).IsTrue()
        .Because("A zero/self source means locally-originated — origin_service_id stays NULL per the 046 contract.");
    }
    var row = await _digestRowAsync(connection, streamId);
    await Assert.That(row).IsNotNull().Because("The loopback event folds into the LOCAL (zero-uuid) bucket.");
    await Assert.That(row!.Value.Count).IsEqualTo(1);
  }

  [Test]
  public async Task CloseStream_SubtractsTruncatedRows_SurvivorDigestRemainsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var streamId = Guid.NewGuid();
    var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
    var messages = string.Join(",\n", Array.ConvertAll(ids,
      id => _outboxMessage(id, streamId, "Whizbang.Tests.DigestCloseEvent", flags: 0)));
    await _commitAsync(connection, Guid.NewGuid(), messages);

    var seeded = await _digestRowAsync(connection, streamId);
    await Assert.That(seeded).IsNotNull();
    await Assert.That(seeded!.Value.Count).IsEqualTo(3).Because("All three events folded on emit.");

    // The event surviving the close is the version-3 row (versions follow message_id order).
    Guid survivorId;
    await using (var v = connection.CreateCommand()) {
      v.CommandText = "SELECT event_id FROM wh_event_store WHERE stream_id = @sid ORDER BY version DESC LIMIT 1";
      v.Parameters.AddWithValue("sid", streamId);
      survivorId = (Guid)(await v.ExecuteScalarAsync())!;
    }

    await using (var close = connection.CreateCommand()) {
      close.CommandText = "SELECT close_status FROM close_stream(@sid, 2, FALSE)";
      close.Parameters.AddWithValue("sid", streamId);
      var status = (string?)await close.ExecuteScalarAsync();
      await Assert.That(status).IsEqualTo("closed").Because("No unprocessed work + a carry-forward above the close point.");
    }

    var after = await _digestRowAsync(connection, streamId);
    await Assert.That(after).IsNotNull().Because("The surviving carry-forward keeps the bucket alive.");
    await Assert.That(after!.Value.Count).IsEqualTo(1).Because("close_stream subtracted the two truncated events.");
    var expected = await _expectedDigestAsync(connection, survivorId);
    await Assert.That(after.Value.Lo).IsEqualTo(expected.Lo)
      .Because("XOR-ing the truncated hashes back out leaves exactly the survivor's digest (XOR is self-inverse).");
    await Assert.That(after.Value.Hi).IsEqualTo(expected.Hi);
  }

  [Test]
  public async Task Reclassify_SubtractsReclassifiedRows_EmptiedBucketDroppedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var streamId = Guid.NewGuid();
    var messages = string.Join(",\n", new[] {
      _outboxMessage(Guid.NewGuid(), streamId, "Whizbang.Tests.DigestReclassifyEvent", flags: 0),
      _outboxMessage(Guid.NewGuid(), streamId, "Whizbang.Tests.DigestReclassifyEvent", flags: 0)
    });
    await _commitAsync(connection, Guid.NewGuid(), messages);

    var seeded = await _digestRowAsync(connection, streamId);
    await Assert.That(seeded).IsNotNull();
    await Assert.That(seeded!.Value.Count).IsEqualTo(2);

    await using (var reclassify = connection.CreateCommand()) {
      reclassify.CommandText = "SELECT events_reclassified FROM reclassify_events_ephemeral(ARRAY['Whizbang.Tests.DigestReclassifyEvent'])";
      var count = (long)(await reclassify.ExecuteScalarAsync())!;
      await Assert.That(count).IsEqualTo(2L).Because("Both rows flip to ephemeral (homogeneous stream, nothing blocked).");
    }

    var after = await _digestRowAsync(connection, streamId);
    await Assert.That(after).IsNull()
      .Because("Reclassified rows leave the audited set; the emptied bucket is dropped, matching a recompute that now sees zero eligible rows.");
  }

  [Test]
  public async Task DigestTable_MatchesFullRecompute_AfterMixedActivityAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    // Mixed activity: two streams of one type (one later closed), one tenant-scoped stream,
    // one ephemeral stream (never folded).
    var closedStream = Guid.NewGuid();
    var keptStream = Guid.NewGuid();
    var tenantStream = Guid.NewGuid();
    var ephemeralStream = Guid.NewGuid();
    var messages = string.Join(",\n", new[] {
      _outboxMessage(Guid.NewGuid(), closedStream, "Whizbang.Tests.DigestSweepEvent", flags: 0),
      _outboxMessage(Guid.NewGuid(), closedStream, "Whizbang.Tests.DigestSweepEvent", flags: 0),
      _outboxMessage(Guid.NewGuid(), keptStream, "Whizbang.Tests.DigestSweepEvent", flags: 0),
      _outboxMessage(Guid.NewGuid(), tenantStream, "Whizbang.Tests.DigestSweepTenantEvent", flags: 0, scopeJson: """{"t":"tenant-9"}"""),
      _outboxMessage(Guid.NewGuid(), ephemeralStream, "Whizbang.Tests.DigestSweepEphemeralEvent", flags: 8)
    });
    await _commitAsync(connection, Guid.NewGuid(), messages);

    await using (var close = connection.CreateCommand()) {
      close.CommandText = "SELECT close_status FROM close_stream(@sid, 1, FALSE)";
      close.Parameters.AddWithValue("sid", closedStream);
      var status = (string?)await close.ExecuteScalarAsync();
      await Assert.That(status).IsEqualTo("closed");
    }

    // The incremental table must EXACTLY match the recompute (ComputeStreamDigestsAsync's query,
    // settle 0) over these streams — the invariant the full-sweep verification trusts.
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = """
      WITH recomputed AS (
        SELECT COALESCE(es.origin_service_id, '00000000-0000-0000-0000-000000000000'::uuid) AS origin_service_id,
               COALESCE(es.scope->>'t', '') AS scope_tenant, es.event_type, es.stream_id,
               bit_xor(hashtextextended(es.event_id::text, 0)) AS digest_lo,
               bit_xor(hashtextextended(es.event_id::text, 1)) AS digest_hi,
               COUNT(*)::int AS event_count
        FROM wh_event_store es
        LEFT JOIN wh_event_body eb ON eb.event_id = es.event_id
        WHERE es.stream_id = ANY(@sids::uuid[])
          AND COALESCE(es.flags, 0) & 8 = 0
          AND COALESCE((eb.metadata->>'deliveryGuarantee')::integer, 0) <> 1
        GROUP BY 1, 2, 3, 4
      ),
      stored AS (
        SELECT origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count
        FROM wh_stream_digests WHERE stream_id = ANY(@sids::uuid[])
      )
      SELECT (SELECT COUNT(*) FROM ((TABLE recomputed EXCEPT TABLE stored)
                                    UNION ALL
                                    (TABLE stored EXCEPT TABLE recomputed)) diff),
             (SELECT COUNT(*) FROM stored)
      """;
    cmd.Parameters.AddWithValue("sids", new[] { closedStream, keptStream, tenantStream, ephemeralStream });
    await using var r = await cmd.ExecuteReaderAsync();
    await r.ReadAsync();
    await Assert.That(r.GetInt64(0)).IsEqualTo(0L)
      .Because("After folds + a close, the incremental table must be row-identical to the full recompute — the invariant the full sweep verifies.");
    await Assert.That(r.GetInt64(1)).IsEqualTo(3L)
      .Because("Three buckets survive: closed-stream remainder, kept stream, tenant stream — the ephemeral stream never folds.");
  }

  // ── A1c coordinator surface: table reads + trust-but-verify heal ─────────

  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static async Task _seedDigestAsync(
      NpgsqlConnection connection, string originUuid, string tenant, string eventType, Guid streamId,
      long lo, long hi, int count, bool settled = true) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = $"""
      INSERT INTO wh_stream_digests (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at)
      VALUES (@origin::uuid, @tenant, @type, @sid, @lo, @hi, @count, NOW() - INTERVAL '{(settled ? "2 hours" : "0 seconds")}')
      """;
    cmd.Parameters.AddWithValue("origin", originUuid);
    cmd.Parameters.AddWithValue("tenant", tenant);
    cmd.Parameters.AddWithValue("type", eventType);
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("lo", lo);
    cmd.Parameters.AddWithValue("hi", hi);
    cmd.Parameters.AddWithValue("count", count);
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task GetStreamDigests_ReadsOwnLane_MapsEmptyTenantToNullAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    var streamA = Guid.NewGuid();
    var streamB = Guid.NewGuid();
    var origin = Guid.NewGuid();
    await _seedDigestAsync(connection, ZERO_UUID, "", "Contracts.ReadProbe", streamA, 11, 12, 2);
    await _seedDigestAsync(connection, ZERO_UUID, "tenant-r", "Contracts.ReadProbe", streamB, 21, 22, 3);
    // A received-lane row must NOT appear in the own-lane read.
    await _seedDigestAsync(connection, origin.ToString(), "", "Contracts.ReadProbe", Guid.NewGuid(), 31, 32, 1);

    var own = await coordinator.GetStreamDigestsAsync(null, ["Contracts.ReadProbe"]);
    await Assert.That(own.Count).IsEqualTo(2).Because("Null origin = the zero-uuid own lane only.");
    var unscoped = own.Single(d => d.StreamId == streamA);
    await Assert.That(unscoped.TenantScope).IsNull()
      .Because("The table's '' sentinel maps back to null, matching recomputed rows.");
    await Assert.That(unscoped.DigestLo).IsEqualTo(11L);
    await Assert.That(unscoped.UpdatedAt).IsNotNull().Because("Table reads carry the bucket update time for settle-skipping.");
    var scoped = own.Single(d => d.StreamId == streamB);
    await Assert.That(scoped.TenantScope).IsEqualTo("tenant-r");

    var received = await coordinator.GetStreamDigestsAsync(origin, ["Contracts.ReadProbe"]);
    await Assert.That(received.Count).IsEqualTo(1).Because("An origin value reads that origin's received lane only.");
  }

  [Test]
  public async Task DigestReads_NormalizeLongFormTypeNames_ToTheStoredWireFormAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    // The table stores the short wire form; a caller holding a long AQN (Version/Culture/PKT)
    // must still match — normalize on read, or the lookup silently returns nothing.
    await _seedDigestAsync(connection, ZERO_UUID, "", "Contracts.NormProbe, Contracts.Assembly", Guid.NewGuid(), 5, 6, 1);
    const string longForm = "Contracts.NormProbe, Contracts.Assembly, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    var streams = await coordinator.GetStreamDigestsAsync(null, [longForm]);
    await Assert.That(streams.Count).IsEqualTo(1)
      .Because("stream-level digest reads normalize requested names to the stored wire form.");
    var types = await coordinator.GetTypeDigestsAsync(null, [longForm]);
    await Assert.That(types.Count).IsEqualTo(1)
      .Because("type-level digest reads normalize requested names to the stored wire form.");
  }

  [Test]
  public async Task GetTypeDigests_RollsUpStreamBuckets_XorAndSumAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    await _seedDigestAsync(connection, ZERO_UUID, "", "Contracts.RollupProbe", Guid.NewGuid(), 0b1100, 0b0101, 2);
    await _seedDigestAsync(connection, ZERO_UUID, "", "Contracts.RollupProbe", Guid.NewGuid(), 0b1010, 0b0011, 3);
    await _seedDigestAsync(connection, ZERO_UUID, "tenant-x", "Contracts.RollupProbe", Guid.NewGuid(), 7, 8, 1);

    var types = await coordinator.GetTypeDigestsAsync(null, ["Contracts.RollupProbe"]);
    await Assert.That(types.Count).IsEqualTo(2).Because("One roll-up per (tenant, type).");
    var unscoped = types.Single(d => d.TenantScope is null);
    await Assert.That(unscoped.StreamId).IsEqualTo(Guid.Empty).Because("Type-level rows carry the empty stream id.");
    await Assert.That(unscoped.DigestLo).IsEqualTo((long)(0b1100 ^ 0b1010))
      .Because("The type digest is the XOR of its stream buckets — they partition the type's events.");
    await Assert.That(unscoped.DigestHi).IsEqualTo((long)(0b0101 ^ 0b0011));
    await Assert.That(unscoped.EventCount).IsEqualTo(5);
    var scoped = types.Single(d => d.TenantScope == "tenant-x");
    await Assert.That(scoped.EventCount).IsEqualTo(1);
  }

  [Test]
  public async Task VerifyDigestTable_HealsAllThreeDriftShapesAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    // Three settled event-backed streams, seeded DIRECTLY into the store (bypassing the emit
    // chain, so no folds happened — exactly the drift a rogue write path would leave).
    var corruptedStream = Guid.NewGuid();
    var missingStream = Guid.NewGuid();
    var phantomStream = Guid.NewGuid();
    var e1 = Guid.NewGuid();
    var e2 = Guid.NewGuid();
    await _seedEventAsync(connection, corruptedStream, 1, "Contracts.VerifyProbe", e1);
    await _seedEventAsync(connection, missingStream, 1, "Contracts.VerifyProbe", e2);
    // Bucket 1: exists but WRONG values. Bucket for missingStream: absent. Bucket 3: phantom.
    await _seedDigestAsync(connection, ZERO_UUID, "", "Contracts.VerifyProbe", corruptedStream, 999, 999, 9);
    await _seedDigestAsync(connection, ZERO_UUID, "", "Contracts.VerifyProbe", phantomStream, 1, 2, 1);

    var result = await coordinator.VerifyDigestTableAsync(TimeSpan.FromMinutes(60));
    await Assert.That(result.DriftUpdated).IsEqualTo(1).Because("The corrupted bucket disagrees with the recompute.");
    await Assert.That(result.DriftRemoved).IsEqualTo(1).Because("The phantom bucket has no backing events.");
    await Assert.That(result.DriftAdded).IsEqualTo(1).Because("The missing bucket exists in the recompute only.");
    await Assert.That(result.TotalDrift).IsEqualTo(3);

    // Healed: corrupted now matches the recompute; phantom gone; missing added.
    var expected1 = await _expectedDigestAsync(connection, e1);
    var healed = await _digestRowAsync(connection, corruptedStream);
    await Assert.That(healed!.Value.Lo).IsEqualTo(expected1.Lo);
    await Assert.That(healed.Value.Count).IsEqualTo(1);
    await Assert.That(await _digestRowAsync(connection, phantomStream)).IsNull();
    var added = await _digestRowAsync(connection, missingStream);
    var expected2 = await _expectedDigestAsync(connection, e2);
    await Assert.That(added!.Value.Lo).IsEqualTo(expected2.Lo);

    // A second pass is clean — the heal converged.
    var second = await coordinator.VerifyDigestTableAsync(TimeSpan.FromMinutes(60));
    await Assert.That(second.TotalDrift).IsEqualTo(0).Because("Verification heals to a fixed point.");
  }

  [Test]
  public async Task VerifyDigestTable_IgnoresUnsettledBucketsAndEventsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    // A FRESH event (inside settle) with no bucket must not read as "missing"; a fresh bucket
    // (recent updated_at) with corrupt values must not be touched this pass.
    var freshEventStream = Guid.NewGuid();
    var freshBucketStream = Guid.NewGuid();
    var e1 = Guid.NewGuid();
    var e2 = Guid.NewGuid();
    await _seedEventAsync(connection, freshEventStream, 1, "Contracts.SettleProbe", e1, aged: false);
    await _seedEventAsync(connection, freshBucketStream, 1, "Contracts.SettleProbe", e2);
    await _seedDigestAsync(connection, ZERO_UUID, "", "Contracts.SettleProbe", freshBucketStream, 777, 777, 7, settled: false);

    var result = await coordinator.VerifyDigestTableAsync(TimeSpan.FromMinutes(60));
    await Assert.That(result.TotalDrift).IsEqualTo(0)
      .Because("In-flight folds and fresh events are outside the settle window — never drift.");
    var untouched = await _digestRowAsync(connection, freshBucketStream);
    await Assert.That(untouched!.Value.Lo).IsEqualTo(777L).Because("Unsettled buckets are left alone this pass.");
  }

  private static async Task _seedEventAsync(
      NpgsqlConnection connection, Guid streamId, int version, string eventType, Guid eventId, bool aged = true) {
    await using (var store = connection.CreateCommand()) {
      store.CommandText = $"""
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, flags, created_at)
        VALUES (@event, @stream, @stream, 'TestAggregate', @type, 'null'::jsonb, @version, nextval('wh_commit_seq'), 0,
                NOW() - INTERVAL '{(aged ? "2 hours" : "0 seconds")}')
        """;
      store.Parameters.AddWithValue("event", eventId);
      store.Parameters.AddWithValue("stream", streamId);
      store.Parameters.AddWithValue("type", eventType);
      store.Parameters.AddWithValue("version", version);
      await store.ExecuteNonQueryAsync();
    }
    await using (var body = connection.CreateCommand()) {
      body.CommandText = """
        INSERT INTO wh_event_body (event_id, event_data, metadata)
        VALUES (@event, '{"seeded":true}'::jsonb, '{}'::jsonb)
        """;
      body.Parameters.AddWithValue("event", eventId);
      await body.ExecuteNonQueryAsync();
    }
  }
}
