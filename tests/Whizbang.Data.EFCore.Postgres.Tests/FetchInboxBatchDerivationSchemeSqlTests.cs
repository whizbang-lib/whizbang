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
/// The inbox fetch returns the derivation scheme each row's first claim recorded (#2633, migration 740), the shared
/// scheme for a row a build before the column claimed first, and writes nothing: the claim records it.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#a-receptor-invoked-locally</docs>
[Category("Shard3")]
public class FetchInboxBatchDerivationSchemeSqlTests : EFCoreTestBase {
  private const string COMMAND_TYPE = "Producer.AdjustOrder, Producer";

  [Test]
  [Arguments((short)1, HandlingDerivationScheme.OwnHandling)]
  [Arguments((short)0, HandlingDerivationScheme.SharedWithCaller)]
  public async Task ARecordedScheme_IsReturnedAsync(short recorded, HandlingDerivationScheme expected) {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _registerInstanceAsync(conn);
    var stream = (Guid)TrackedGuid.New();
    var id = (Guid)TrackedGuid.New();
    await _storeLeasedAsync(conn, instance, id, stream, attempts: 3);
    await using (var record = conn.CreateCommand()) {
      record.CommandText = "UPDATE wh_inbox_state SET derivation_scheme = @recorded WHERE message_id = @id";
      record.Parameters.AddWithValue(nameof(recorded), recorded);
      record.Parameters.AddWithValue(nameof(id), id);
      await record.ExecuteNonQueryAsync();
    }

    var rows = await _coordinator(dbContext).FetchInboxBatchAsync([stream], instance, 100, null);

    await Assert.That(rows.Single().DerivationScheme).IsEqualTo(expected);
  }

  [Test]
  public async Task ARowThatRecordsNothing_ReturnsTheSharedScheme_AndTheFetchWritesNothingAsync() {
    // Recorded by no claim: a build before the column claimed it first
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _registerInstanceAsync(conn);
    var stream = (Guid)TrackedGuid.New();
    var id = (Guid)TrackedGuid.New();
    await _storeLeasedAsync(conn, instance, id, stream, attempts: 1);

    var rows = await _coordinator(dbContext).FetchInboxBatchAsync([stream], instance, 100, null);

    await Assert.That(rows.Single().DerivationScheme).IsEqualTo(HandlingDerivationScheme.SharedWithCaller)
      .Because("the retry derives the ids its first run derived on the earlier build");
    await Assert.That(await _recordedAsync(conn, id)).IsNull()
      .Because("the claim is where a scheme is recorded, in the update it already makes; the fetch adds no write");
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
