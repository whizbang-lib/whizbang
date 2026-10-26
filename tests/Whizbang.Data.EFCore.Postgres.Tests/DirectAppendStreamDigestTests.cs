// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A direct append (<c>IEventStore.AppendAsync</c>, through <c>wh_append_event</c>) folds its event into
/// <c>wh_stream_digests</c> exactly as the emit chain does (#1600). The full recompute the integrity sweep
/// trusts (<c>ComputeStreamDigestsAsync</c>) has always counted directly-appended rows, so a stream written by
/// both paths must have a table digest equal to a recomputation over all its events, and an event of a
/// digest-exempt type (an audit record, #1492) is never folded.
/// </summary>
/// <docs>resilience/stream-integrity#direct-appends-digested</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/225_DirectAppendFoldsTheStreamDigest.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class DirectAppendStreamDigestTests : EFCoreTestBase {
  private const string CHAIN_EVENT_TYPE = "Whizbang.Tests.DirectAppendDigestChainEvent";
  private static readonly DateTimeOffset _fixedTime = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task MixedPathStream_TableDigestEqualsARecomputationOverAllItsEventsAsync() {
    var streamId = Guid.CreateVersion7();
    await using var connection = await _openAsync();
    await _emitThroughTheChainAsync(connection, Guid.CreateVersion7(), streamId);

    await using (var context = CreateDbContext()) {
      var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
      await store.AppendAsync(streamId, _envelope());
      await store.AppendAsync(streamId, _envelope());
    }

    var (differences, storedEvents) = await _compareWithRecomputeAsync(connection, streamId);
    await Assert.That(differences).IsEqualTo(0L)
      .Because("the table must be row-identical to the recompute the integrity sweep trusts");
    await Assert.That(storedEvents).IsEqualTo(3L)
      .Because("one chain event and two direct appends are all folded");
  }

  [Test]
  public async Task DirectAppend_FoldsByTenantIntoTheLocalBucketAsync() {
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    await using var connection = await _openAsync();

    await _appendDirectAsync(connection, eventId, streamId, "Whizbang.Tests.DirectAppendTenantEvent, Whizbang.Tests", """{"t":"tenant-direct"}""");

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = """
      SELECT d.origin_service_id, d.scope_tenant, d.event_type, d.event_count,
             d.digest_lo = hashtextextended(@id::text, 0), d.digest_hi = hashtextextended(@id::text, 1)
      FROM wh_stream_digests d WHERE d.stream_id = @sid
      """;
    cmd.Parameters.AddWithValue("id", eventId);
    cmd.Parameters.AddWithValue("sid", streamId);
    await using var r = await cmd.ExecuteReaderAsync();
    await Assert.That(await r.ReadAsync()).IsTrue().Because("a direct append is folded into the stream's bucket");
    await Assert.That(r.GetGuid(0)).IsEqualTo(Guid.Empty).Because("a direct append is locally originated");
    await Assert.That(r.GetString(1)).IsEqualTo("tenant-direct");
    await Assert.That(r.GetString(2)).IsEqualTo("Whizbang.Tests.DirectAppendTenantEvent, Whizbang.Tests");
    await Assert.That(r.GetInt32(3)).IsEqualTo(1);
    await Assert.That(r.GetBoolean(4)).IsTrue();
    await Assert.That(r.GetBoolean(5)).IsTrue();
  }

  [Test]
  public async Task DirectAppend_OfADigestExemptType_IsNotFoldedAsync() {
    var streamId = Guid.CreateVersion7();
    await using var connection = await _openAsync();

    await _appendDirectAsync(connection, Guid.CreateVersion7(), streamId, DigestExemptTypes.TypeNames[0] + ", Whizbang.Core", "null");
    await _appendDirectAsync(connection, Guid.CreateVersion7(), streamId, DigestExemptTypes.TypeNames[1] + ", Whizbang.Core", "null");

    var (differences, storedEvents) = await _compareWithRecomputeAsync(connection, streamId);
    await Assert.That(storedEvents).IsEqualTo(0L)
      .Because("audit records are left out of every digest by type, on the direct path as on the chain (#1492)");
    await Assert.That(differences).IsEqualTo(0L);
  }

  [Test]
  public async Task CloseStream_AfterDirectAppends_TableStillMatchesTheRecomputeAsync() {
    var streamId = Guid.CreateVersion7();
    await using var connection = await _openAsync();
    await _emitThroughTheChainAsync(connection, Guid.CreateVersion7(), streamId);
    await using (var context = CreateDbContext()) {
      var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
      await store.AppendAsync(streamId, _envelope());
      await store.AppendAsync(streamId, _envelope());
    }

    // close_stream XORs the truncated rows back out. Before the fold, it subtracted direct appends that were never added.
    await using (var close = connection.CreateCommand()) {
      close.CommandText = "SELECT truncate_status FROM wh_truncate_stream_prefix(@sid, 2, FALSE)";
      close.Parameters.AddWithValue("sid", streamId);
      await Assert.That((string?)await close.ExecuteScalarAsync()).IsEqualTo("truncated");
    }

    var (differences, _) = await _compareWithRecomputeAsync(connection, streamId);
    await Assert.That(differences).IsEqualTo(0L)
      .Because("a close subtracts exactly what was folded, whichever path stored it");
  }

  [Test]
  public async Task TheFunction_ExistsOnce_AndAReRunOfTheMigrationStillFoldsAsync() {
    await using var connection = await _openAsync();
    var migration = new PostgresMigrationProvider().GetMigrations().Single(m => m.Name.StartsWith("225_", StringComparison.Ordinal));
    await using (var rerun = connection.CreateCommand()) {
      rerun.CommandText = migration.Sql;  // schema and constants already substituted
      await rerun.ExecuteNonQueryAsync();
    }

    await using (var count = connection.CreateCommand()) {
      count.CommandText = "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public' AND p.proname = 'wh_append_event'";
      await Assert.That(Convert.ToInt32(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(1);
    }

    var streamId = Guid.CreateVersion7();
    await _appendDirectAsync(connection, Guid.CreateVersion7(), streamId, "Whizbang.Tests.DirectAppendRerunEvent, Whizbang.Tests", "null");
    var (differences, storedEvents) = await _compareWithRecomputeAsync(connection, streamId);
    await Assert.That(storedEvents).IsEqualTo(1L);
    await Assert.That(differences).IsEqualTo(0L);
  }

  // ===== Helpers =====

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  /// <summary>Stores one event through the outbox emit chain (commit_handler_result, then _emit_event_store_chain).</summary>
  private static async Task _emitThroughTheChainAsync(NpgsqlConnection connection, Guid eventId, Guid streamId) {
    var request = $$"""
      {
        "instance_id": "{{Guid.CreateVersion7()}}",
        "service_name": "test",
        "host_name": "test-host",
        "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}",
          "Destination": "out-topic",
          "MessageType": "{{CHAIN_EVENT_TYPE}}",
          "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": {},
          "Scope": null,
          "StreamId": "{{streamId}}",
          "IsEvent": true,
          "Flags": 0
        }]
      }
      """;
    await using var call = connection.CreateCommand();
    call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
    call.Parameters.AddWithValue("req", request);
    _ = await call.ExecuteScalarAsync();
  }

  private static async Task _appendDirectAsync(NpgsqlConnection connection, Guid eventId, Guid streamId, string eventType, string scopeJson) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = """
      SELECT wh_append_event(@id, @sid, @sid, 'DirectAppend', @type, '{}'::jsonb, '{}'::jsonb, @scope::jsonb, @at)
      """;
    cmd.Parameters.AddWithValue("id", eventId);
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("type", eventType);
    cmd.Parameters.AddWithValue("scope", scopeJson);
    cmd.Parameters.AddWithValue("at", _fixedTime);
    _ = await cmd.ExecuteScalarAsync();
  }

  /// <summary>
  /// Compares the stream's rows in wh_stream_digests with the recompute ComputeStreamDigestsAsync runs (settle 0):
  /// every origin lane, ephemeral and at-most-once rows out, digest-exempt types out. Returns the number of rows in
  /// either set and not the other, and the number of events the table holds for the stream.
  /// </summary>
  private static async Task<(long Differences, long StoredEvents)> _compareWithRecomputeAsync(NpgsqlConnection connection, Guid streamId) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = """
      WITH recomputed AS (
        SELECT COALESCE(es.origin_service_id, '00000000-0000-0000-0000-000000000000'::uuid) AS origin_service_id,
               COALESCE(es.scope->>'t', '') AS scope_tenant, es.event_type, es.stream_id,
               bit_xor(hashtextextended(es.event_id::text, 0)) AS digest_lo,
               bit_xor(hashtextextended(es.event_id::text, 1)) AS digest_hi,
               COUNT(*)::int AS event_count
        FROM wh_event_store es
        WHERE es.stream_id = @sid
          AND COALESCE(es.flags, 0) & 8 = 0
          AND COALESCE(es.flags, 0) & 32 = 0
          AND split_part(es.event_type, ',', 1) <> ALL(@exempt::text[])
        GROUP BY 1, 2, 3, 4
      ),
      stored AS (
        SELECT origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count
        FROM wh_stream_digests WHERE stream_id = @sid
      )
      SELECT (SELECT COUNT(*) FROM ((TABLE recomputed EXCEPT TABLE stored)
                                    UNION ALL
                                    (TABLE stored EXCEPT TABLE recomputed)) diff),
             (SELECT COALESCE(SUM(event_count), 0) FROM stored)
      """;
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("exempt", DigestExemptTypes.TypeNames.ToArray());
    await using var r = await cmd.ExecuteReaderAsync();
    await r.ReadAsync();
    return (r.GetInt64(0), r.GetInt64(1));
  }

  private static MessageEnvelope<OrderCreatedEvent> _envelope() => new() {
    MessageId = MessageId.New(),
    Payload = new OrderCreatedEvent { OrderId = Guid.CreateVersion7(), CustomerName = "DirectAppendDigest" },
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        Timestamp = _fixedTime,
        ServiceInstance = new ServiceInstanceInfo {
          InstanceId = Guid.CreateVersion7(),
          ServiceName = "test-service",
          HostName = "test-host",
          ProcessId = 123
        }
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };
}
