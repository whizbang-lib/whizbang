// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2106: every function that writes <c>wh_stream_digests</c> leaves rows equal to a fold of their events: the hash lanes
/// and counts exactly as before the shared fold helper (the lock on today's values), and the id lanes (the XOR of each
/// event id's two 64-bit halves) on every row it folds from scratch. A row without id lanes keeps NULL. The writers
/// whose setup needs its own fixture are checked in their own suites with <see cref="StoredDigestInvariant"/>: adoption
/// and batch reconcile, re-attribution, refiling, the superseded-event reaper, at-most-once flagging and archive restore.
/// </summary>
/// <docs>resilience/stream-integrity#one-digest-fold</docs>
[Category("Integration")]
[Category("Shard2")]
public class StreamDigestWritersTests : EFCoreTestBase {
  private const string TYPE = "Whizbang.Tests.DigestWriterEvent, Whizbang.Tests";
  private const string OTHER_TYPE = "Whizbang.Tests.DigestWriterOtherEvent, Whizbang.Tests";
  private static readonly long[] _adoptedSequence = [7L];

  private static async Task<NpgsqlConnection> _openAsync(Microsoft.EntityFrameworkCore.DbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task _executeAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      // drained: some calls return rows
    }
  }

  /// <summary>Commits outbox events through the handler-result path, which runs the outbox emit chain.</summary>
  private static Task _emitAsync(NpgsqlConnection connection, params (Guid Id, Guid Stream, string Type, string Tenant)[] events) {
    var messages = string.Join(",", events.Select(e => $$"""
      {
        "MessageId": "{{e.Id}}", "Destination": "out-topic", "MessageType": "{{e.Type}}", "EnvelopeType": null,
        "Envelope": {"Payload": {"n": 1}, "MessageId": "{{e.Id}}", "Hops": []},
        "Metadata": {}, "Scope": {"t": "{{e.Tenant}}"}, "StreamId": "{{e.Stream}}", "IsEvent": true, "Flags": 0
      }
      """));
    var request = $$"""
      {"instance_id": "{{TrackedGuid.New().Value}}", "service_name": "test", "host_name": "test-host", "process_id": 1,
       "new_outbox_messages": [{{messages}}]}
      """;
    return _executeAsync(connection, "SELECT commit_handler_result(@req::jsonb)", ("req", request));
  }

  private static (Guid, Guid, string, string) _event(Guid stream, string type = TYPE, string tenant = "tenant-a") =>
    (TrackedGuid.New().Value, stream, type, tenant);

  [Test]
  public async Task OutboxEmitChain_FoldsEveryLaneAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = TrackedGuid.New().Value;
    var other = TrackedGuid.New().Value;

    await _emitAsync(conn, _event(stream), _event(stream), _event(stream, OTHER_TYPE), _event(other, tenant: "tenant-b"));

    await StoredDigestInvariant.AssertAgreeAsync(conn, stream, other);
  }

  [Test]
  public async Task OutboxEmitChain_FoldingIntoARowWithoutIdLanes_KeepsThemNullAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = TrackedGuid.New().Value;
    await _emitAsync(conn, _event(stream));
    await _executeAsync(conn, "UPDATE wh_stream_digests SET id_lo = NULL, id_hi = NULL WHERE stream_id = @s", ("s", stream));

    await _emitAsync(conn, _event(stream));

    _ = await StoredDigestInvariant.AssertHashLanesAgreeAsync(conn, stream);
    await StoredDigestInvariant.AssertIdLanesAgreeAsync(conn, filled: 0, stream);
  }

  [Test]
  public async Task InboxEmitChain_FoldsEveryLaneIntoTheOriginsBucketAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = TrackedGuid.New().Value;
    var origin = TrackedGuid.New().Value;
    var instance = TrackedGuid.New().Value;
    Guid[] ids = [TrackedGuid.New().Value, TrackedGuid.New().Value];
    var inbox = "[" + string.Join(",", ids.Select((id, i) => $$"""
      {
        "MessageId": "{{id}}", "HandlerName": "TestHandler", "MessageType": "{{TYPE}}", "EnvelopeType": "MessageEnvelope",
        "Envelope": {"p": {"n": 1}, "h": []}, "Metadata": {}, "Scope": {"t": "tenant-a"}, "StreamId": "{{stream}}",
        "IsEvent": true, "Flags": 0, "SourceServiceId": "{{origin}}", "SourceCommitSequence": {{i + 7}}
      }
      """)) + "]";
    await _executeAsync(conn, "SELECT * FROM store_inbox_messages(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 4)",
      ("p", inbox), ("inst", instance));
    await _executeAsync(conn,
      "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = ANY(@ids)",
      ("inst", instance), ("ids", ids));

    await _executeAsync(conn, "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 4)", ("inst", instance));

    await StoredDigestInvariant.AssertAgreeAsync(conn, stream);
  }

  [Test]
  public async Task DirectAppend_FoldsEveryLaneAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = TrackedGuid.New().Value;

    foreach (var type in new[] { TYPE, TYPE, OTHER_TYPE }) {
      await _executeAsync(conn, """
        SELECT wh_append_event(@id, @sid, @sid, 'DirectAppend', @type, '{}'::jsonb, '{}'::jsonb, '{"t":"tenant-a"}'::jsonb, NOW())
        """, ("id", TrackedGuid.New().Value), ("sid", stream), ("type", type));
    }

    await StoredDigestInvariant.AssertAgreeAsync(conn, stream);
  }

  [Test]
  public async Task TruncateStreamPrefix_SubtractsEveryLaneOfTheTruncatedEventsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = TrackedGuid.New().Value;
    await _emitAsync(conn, _event(stream), _event(stream), _event(stream));

    await _executeAsync(conn, "SELECT * FROM wh_truncate_stream_prefix(@s, 1, FALSE)", ("s", stream));

    await StoredDigestInvariant.AssertAgreeAsync(conn, stream);
  }

  [Test]
  public async Task ReclassifyEphemeral_SubtractsEveryLaneOfTheReclassifiedEventsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var reclassified = TrackedGuid.New().Value;
    var kept = TrackedGuid.New().Value;
    const string RECLASSIFIED_TYPE = "Whizbang.Tests.DigestWriterReclassifiedEvent, Whizbang.Tests";
    await _emitAsync(conn, _event(reclassified, RECLASSIFIED_TYPE), _event(reclassified, RECLASSIFIED_TYPE), _event(kept));

    await _executeAsync(conn, "SELECT * FROM reclassify_events_ephemeral(ARRAY[@t])", ("t", RECLASSIFIED_TYPE));

    await StoredDigestInvariant.AssertAgreeAsync(conn, kept);
    await Assert.That(await _rowsAsync(conn, reclassified)).IsEqualTo(0L)
      .Because("every lane of the reclassified events left the bucket, and the emptied bucket is removed");
  }

  [Test]
  public async Task AdoptRedeliveredOrigin_MovesEveryLaneToTheOriginsBucketAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = TrackedGuid.New().Value;
    var origin = TrackedGuid.New().Value;
    var received = _event(stream);
    var own = _event(stream);
    await _emitAsync(conn, received, own);

    await _executeAsync(conn, "SELECT * FROM wh_adopt_redelivered_origin(@o, @ids, @seqs)",
      ("o", origin), ("ids", new[] { received.Item1 }), ("seqs", _adoptedSequence));

    await StoredDigestInvariant.AssertAgreeAsync(conn, stream);
    await Assert.That(await _rowsAsync(conn, stream)).IsEqualTo(2L)
      .Because("the adopted event's lanes left the local bucket for the origin's, and the authored one stayed");
  }

  [Test]
  public async Task SeedStreamDigestsBatch_FoldsEveryLaneOfTheSeededStreamsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = TrackedGuid.New().Value;
    await _emitAsync(conn, _event(stream), _event(stream), _event(stream, OTHER_TYPE));
    await _executeAsync(conn, "DELETE FROM wh_stream_digests WHERE stream_id = @s", ("s", stream));
    await _executeAsync(conn, """
      UPDATE wh_stream_digest_seed_cursor SET last_seq = (SELECT COALESCE(MAX(seq), 0) FROM wh_stream_digest_seed_todo);
      INSERT INTO wh_stream_digest_seed_todo (stream_id) VALUES (@s);
      """, ("s", stream));

    await _executeAsync(conn, "SELECT wh_seed_stream_digests_batch(100)");

    await StoredDigestInvariant.AssertAgreeAsync(conn, stream);
  }

  private static async Task<long> _rowsAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_stream_digests WHERE stream_id = @s";
    cmd.Parameters.AddWithValue("s", stream);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }
}
