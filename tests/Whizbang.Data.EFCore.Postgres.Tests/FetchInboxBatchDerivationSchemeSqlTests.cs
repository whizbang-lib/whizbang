// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// An inbox row records the derivation scheme its first run ran under, and every retry runs under it (#2633, migration
/// 740): the fetch stamps a row on its first run and returns each row's recorded scheme, so a row a build before the
/// column ran first keeps that build's ids on a retry.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#a-receptor-invoked-locally</docs>
[Category("Shard3")]
public class FetchInboxBatchDerivationSchemeSqlTests : EFCoreTestBase {
  private const string COMMAND_TYPE = "Producer.AdjustOrder, Producer";

  [Test]
  public async Task FirstRun_IsStampedWithThisBuildsScheme_AndReturnsItAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _registerInstanceAsync(conn);
    var stream = (Guid)TrackedGuid.New();
    var id = (Guid)TrackedGuid.New();
    await _storeLeasedAsync(conn, instance, id, stream, attempts: 1);

    var rows = await _coordinator(dbContext).FetchInboxBatchAsync([stream], instance, 100, null);

    await Assert.That(rows.Single().DerivationScheme).IsEqualTo(HandlingDerivationScheme.OwnHandling);
    await Assert.That(await _recordedAsync(conn, id)).IsEqualTo((short)1)
      .Because("the first run records its scheme, so a retry on any later build runs under it");
  }

  [Test]
  public async Task ARowAnEarlierBuildRanFirst_IsNotStamped_AndReturnsTheSharedSchemeAsync() {
    // Claimed twice (attempts 2) and nothing recorded: a build before the column ran it first
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _registerInstanceAsync(conn);
    var stream = (Guid)TrackedGuid.New();
    var id = (Guid)TrackedGuid.New();
    await _storeLeasedAsync(conn, instance, id, stream, attempts: 2);

    var rows = await _coordinator(dbContext).FetchInboxBatchAsync([stream], instance, 100, null);

    await Assert.That(rows.Single().DerivationScheme).IsEqualTo(HandlingDerivationScheme.SharedWithCaller)
      .Because("the retry derives the ids its first run derived on the earlier build");
    await Assert.That(await _recordedAsync(conn, id)).IsNull();
  }

  [Test]
  public async Task ARecordedScheme_IsReturnedOnARetry_AndNeverOverwrittenAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _registerInstanceAsync(conn);
    var stream = (Guid)TrackedGuid.New();
    var id = (Guid)TrackedGuid.New();
    await _storeLeasedAsync(conn, instance, id, stream, attempts: 1);
    await _execAsync(conn, "UPDATE wh_inbox_state SET derivation_scheme = 0 WHERE message_id = @id", id);

    var first = await _coordinator(dbContext).FetchInboxBatchAsync([stream], instance, 100, null);
    await _execAsync(conn, "UPDATE wh_inbox_state SET attempts = 3 WHERE message_id = @id", id);
    var retry = await _coordinator(dbContext).FetchInboxBatchAsync([stream], instance, 100, null);

    await Assert.That(first.Single().DerivationScheme).IsEqualTo(HandlingDerivationScheme.SharedWithCaller);
    await Assert.That(retry.Single().DerivationScheme).IsEqualTo(HandlingDerivationScheme.SharedWithCaller);
    await Assert.That(await _recordedAsync(conn, id)).IsEqualTo((short)0);
  }

  [Test]
  public async Task ARowTheBudgetCuts_IsNotStampedAsync() {
    // Two rows of one stream with room for one: the second is not run now, so it records nothing
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _registerInstanceAsync(conn);
    var stream = (Guid)TrackedGuid.New();
    var first = (Guid)TrackedGuid.New();
    var second = (Guid)TrackedGuid.New();
    await _storeLeasedAsync(conn, instance, first, stream, attempts: 1);
    await _storeLeasedAsync(conn, instance, second, stream, attempts: 1);

    var rows = await _coordinator(dbContext).FetchInboxBatchAsync([stream], instance, 1, null);

    await Assert.That(rows.Select(r => r.MessageId)).IsEquivalentTo([first]);
    await Assert.That(await _recordedAsync(conn, second)).IsNull();
  }

  [Test]
  public async Task TheEarlierFetch_StillRecordsNothingAsync() {
    // A build before 740 calls fetch_inbox_batch_in_stream_order, unchanged: its first runs record no scheme
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _registerInstanceAsync(conn);
    var stream = (Guid)TrackedGuid.New();
    var id = (Guid)TrackedGuid.New();
    await _storeLeasedAsync(conn, instance, id, stream, attempts: 1);

    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT count(*) FROM fetch_inbox_batch_in_stream_order(@s, @i, 100, NULL)";
      cmd.Parameters.AddWithValue("s", new[] { stream });
      cmd.Parameters.AddWithValue("i", instance);
      await Assert.That(Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(1L);
    }

    await Assert.That(await _recordedAsync(conn, id)).IsNull();
  }

  // ===== Helpers =====

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext dbContext) =>
    new(dbContext, JsonContextRegistry.CreateCombinedOptions());

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task<short?> _recordedAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT derivation_scheme FROM wh_inbox_state WHERE message_id = @id";
    cmd.Parameters.AddWithValue(nameof(id), id);
    var value = await cmd.ExecuteScalarAsync();
    return value is DBNull or null ? null : (short)value;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue(nameof(id), id);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>One command, stored by store_inbox_messages, then leased to the instance as a claim leases it.</summary>
  private static async Task _storeLeasedAsync(NpgsqlConnection conn, Guid instance, Guid id, Guid stream, int attempts) {
    var message = $$"""
      {
        "MessageId": "{{id}}",
        "HandlerName": "ProbeHandler",
        "MessageType": "{{COMMAND_TYPE}}",
        "EnvelopeType": "MessageEnvelope",
        "Envelope": {"p": {"Probe": 1}, "h": []},
        "Metadata": {},
        "Scope": null,
        "StreamId": "{{stream}}",
        "IsEvent": false,
        "Flags": 0,
        "SourceServiceId": "{{Guid.Empty}}",
        "SourceCommitSequence": 0
      }
      """;
    await using (var store = conn.CreateCommand()) {
      store.CommandText = "SELECT * FROM store_inbox_messages(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
      store.Parameters.AddWithValue("p", "[" + message + "]");
      store.Parameters.AddWithValue("inst", instance);
      await using var reader = await store.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        // drain
      }
    }
    await using var lease = conn.CreateCommand();
    lease.CommandText = """
      UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes', attempts = @attempts
      WHERE message_id = @id
      """;
    lease.Parameters.AddWithValue("inst", instance);
    lease.Parameters.AddWithValue(nameof(attempts), attempts);
    lease.Parameters.AddWithValue(nameof(id), id);
    await lease.ExecuteNonQueryAsync();
  }

  private static async Task<Guid> _registerInstanceAsync(NpgsqlConnection conn) {
    var instanceId = (Guid)TrackedGuid.New();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
      """;
    cmd.Parameters.AddWithValue("id", instanceId);
    await cmd.ExecuteNonQueryAsync();
    return instanceId;
  }
}
