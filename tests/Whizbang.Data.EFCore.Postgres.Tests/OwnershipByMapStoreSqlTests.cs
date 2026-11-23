// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// With the service's switch on, storing work never makes the storing instance a stream's owner, and new perspective
/// work is leased by partition, never to a stream's stored owner (#2555). The first writer of a stream used to be pinned
/// as its owner in the stream ledger, and the event chain leased each new perspective row to that pinned owner, so a
/// stream stayed wherever it was first written whatever the map said.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/562_StoresNameNoOwnerInMapMode.sql</code-under-test>
[Category("Shard5")]
public class OwnershipByMapStoreSqlTests : EFCoreTestBase {
  private const string SUPPORTS_MAP = """{"partition_ownership_level":1}""";
  private const string EVENT_TYPE = "Whizbang.Tests.OwnershipStoreEvent, Whizbang.Tests";
  private const string PERSPECTIVE = "Test.OwnershipStorePerspective+Projection";
  private static readonly Guid _a = new("00000000-0000-0000-0000-00000000000a");
  private static readonly Guid _b = new("00000000-0000-0000-0000-00000000000b");

  [Test]
  public async Task StoreOutbox_InMapMode_RecordsTheStreamWithoutAnOwnerAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);

    await _execAsync(conn, "SELECT count(*) FROM store_outbox_messages(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 4)",
      ("p", _outboxMessage(stream)), ("inst", _a));

    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_active_streams WHERE stream_id = @s", ("s", stream))).IsEqualTo(1L);
    await Assert.That(await _ledgerOwnerAsync(conn, stream)).IsNull()
      .Because("the first writer is not the stream's owner; the partition's owner is named when it claims");
  }

  [Test]
  public async Task StoreInbox_InMapMode_RecordsTheStreamWithoutAnOwnerAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);

    await _execAsync(conn, "SELECT count(*) FROM store_inbox_messages(@p::jsonb, @inst, NULL, NOW(), 4)",
      ("p", _inboxMessage(stream)), ("inst", _a));

    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_active_streams WHERE stream_id = @s", ("s", stream))).IsEqualTo(1L);
    await Assert.That(await _ledgerOwnerAsync(conn, stream)).IsNull();
  }

  [Test]
  public async Task StoreOutbox_SwitchOff_PinsTheFirstWriter_AsBeforeAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a);

    await _execAsync(conn, "SELECT count(*) FROM store_outbox_messages(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 4)",
      ("p", _outboxMessage(stream)), ("inst", _a));

    await Assert.That(await _ledgerOwnerAsync(conn, stream)).IsEqualTo(_a);
  }

  [Test]
  public async Task StorePerspectiveEvents_InMapMode_LeasesToTheCallerOnlyOnAPartitionItOwnsAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    var (owner, other) = await _ownersAsync(conn, stream, partitionCount: 4);
    await _pinAsync(conn, stream, other);
    var byOther = (Guid)TrackedGuid.New();
    var byOwner = (Guid)TrackedGuid.New();

    await _storePerspectiveEventAsync(conn, stream, byOther, other);
    await _storePerspectiveEventAsync(conn, stream, byOwner, owner);

    await Assert.That(await _leaseAsync(conn, byOther)).IsEqualTo((null, false))
      .Because("the caller does not own the partition, so the row is left for the owner to claim, never leased to a stored owner");
    await Assert.That(await _leaseAsync(conn, byOwner)).IsEqualTo((owner, true))
      .Because("the caller owns the partition, so it keeps the row it stored");
  }

  [Test]
  public async Task EmitChain_InMapMode_LeasesByPartition_NeverToTheStoredOwnerAsync() {
    var stream = (Guid)TrackedGuid.New();
    var message = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    await _associationAsync(conn);
    var (owner, other) = await _ownersAsync(conn, stream, partitionCount: 10_000);
    await _pinAsync(conn, stream, owner);
    await _execAsync(conn, """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata,
         status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, created_at)
      VALUES (@id, 'test-dest', @type, 'env', '{"p":{}}'::jsonb, '{}'::jsonb,
              1, 0, @inst, NOW() + INTERVAL '5 minutes', 0, @s, true, NOW())
      """, ("id", message), ("type", EVENT_TYPE), ("inst", other), ("s", stream));

    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
      cmd.Parameters.AddWithValue("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, new[] { message });
      cmd.Parameters.AddWithValue("inst", other);
      await cmd.ExecuteNonQueryAsync();
    }

    await Assert.That(await _leaseAsync(conn, message)).IsEqualTo((null, false))
      .Because("the committing instance does not own the partition, and the stored owner is not leased to: the owner claims it");
  }

  // --- helpers --------------------------------------------------------------------------------

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  /// <summary>A and B live and supporting the mode, the map [A, B] published by A, and the switch on.</summary>
  private static async Task _mapModeAsync(NpgsqlConnection conn) {
    await _heartbeatAsync(conn, _a);
    await _heartbeatAsync(conn, _b);
    var epoch = Convert.ToInt64(await _scalarAsync<object>(conn,
      "SELECT epoch FROM wh_vote_role(@role, @a, INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL) v WHERE v.outcome = 'granted'",
      ("role", PartitionAssignerOptions.ROLE), ("a", _a)), CultureInfo.InvariantCulture);
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT wh_publish_partition_assignment(@role, @a, @epoch, @members, INTERVAL '90 seconds')";
      cmd.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
      cmd.Parameters.AddWithValue("a", _a);
      cmd.Parameters.AddWithValue(nameof(epoch), epoch);
      cmd.Parameters.Add(new NpgsqlParameter("members", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = new[] { _a, _b } });
      await cmd.ExecuteNonQueryAsync();
    }
    var answer = await _scalarAsync<string>(conn, "SELECT wh_open_partition_ownership_by_map(@role, @a, @epoch)",
      ("role", PartitionAssignerOptions.ROLE), ("a", _a), ("epoch", epoch));
    if (answer != "opened") {
      throw new InvalidOperationException($"precondition: the switch did not turn on ({answer})");
    }
  }

  private static Task _heartbeatAsync(NpgsqlConnection conn, Guid instance) =>
    _execAsync(conn, "SELECT record_heartbeat(@inst, 'test', 'test-host', 1, @metadata::jsonb, NULL, NULL, NULL, 'pooled')",
      ("inst", instance), ("metadata", SUPPORTS_MAP));

  /// <summary>Which of A and B the map [A, B] makes the owner of the stream's partition at this partition count.</summary>
  private static async Task<(Guid Owner, Guid Other)> _ownersAsync(NpgsqlConnection conn, Guid stream, int partitionCount) {
    var partition = await _scalarAsync<int>(conn, "SELECT compute_partition(@s, @n)", ("s", stream), ("n", partitionCount));
    return partition % 2 == 0 ? (_a, _b) : (_b, _a);
  }

  private static Task _pinAsync(NpgsqlConnection conn, Guid stream, Guid owner) =>
    _execAsync(conn, """
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at, lease_expiry)
      VALUES (@s, 0, @owner, NOW(), NOW() + INTERVAL '5 minutes')
      """, ("s", stream), ("owner", owner));

  private static Task _associationAsync(NpgsqlConnection conn) =>
    _execAsync(conn, """
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @type, 'perspective', @target, 'test-svc', @type, NOW(), NOW())
      ON CONFLICT DO NOTHING
      """, ("type", EVENT_TYPE), ("target", PERSPECTIVE));

  private static Task _storePerspectiveEventAsync(NpgsqlConnection conn, Guid stream, Guid eventId, Guid instance) =>
    _execAsync(conn, "SELECT count(*) FROM store_perspective_events(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 4)",
      ("p", $$"""[{"StreamId":"{{stream}}","PerspectiveName":"{{PERSPECTIVE}}","EventId":"{{eventId}}"}]"""), ("inst", instance));

  private static string _outboxMessage(Guid stream) => $$"""
    [{"MessageId":"{{(Guid)TrackedGuid.New()}}","Destination":"test-topic","MessageType":"TestMessage",
      "EnvelopeType":"MessageEnvelope","Envelope":{},"Metadata":{},"Scope":null,"StreamId":"{{stream}}","IsEvent":false}]
    """;

  private static string _inboxMessage(Guid stream) => $$"""
    [{"MessageId":"{{(Guid)TrackedGuid.New()}}","HandlerName":"TestHandler","MessageType":"TestMessage",
      "Envelope":{},"Metadata":{},"Scope":null,"StreamId":"{{stream}}","IsEvent":false}]
    """;

  private static async Task<(Guid? Holder, bool Leased)> _leaseAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT instance_id, lease_expiry IS NOT NULL FROM wh_perspective_events WHERE event_id = @e";
    cmd.Parameters.AddWithValue("e", eventId);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException($"precondition: no perspective row was stored for event {eventId}");
    }
    return (await reader.IsDBNullAsync(0) ? null : reader.GetGuid(0), reader.GetBoolean(1));
  }

  private static async Task<Guid?> _ledgerOwnerAsync(NpgsqlConnection conn, Guid stream) =>
    await _scalarAsync<object>(conn, "SELECT assigned_instance_id FROM wh_active_streams WHERE stream_id = @s", ("s", stream)) is Guid owner
      ? owner : null;

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync();
  }
}
