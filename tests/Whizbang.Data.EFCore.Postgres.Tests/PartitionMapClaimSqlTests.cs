// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 565 (#2558): the published assignment carries an explicit partition map, and every claim reads it through
/// one predicate. Each claim test publishes the map that inverts the modulo (the claimer, member 1 of 2, gets the even
/// partitions where the modulo gives it the odd ones) over work on partitions 0 and 1, so the map and the modulo give
/// opposite answers and the test tells them apart.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#incremental-map</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/565_ThePublishedAssignmentCarriesThePartitionMap.sql</code-under-test>
[Category("Shard3")]
public class PartitionMapClaimSqlTests : EFCoreTestBase {
  private const string ROLE = PartitionAssignerOptions.ROLE;
  private const int P = PartitionMap.PARTITION_COUNT;
  private static readonly Guid _assigner = new("00000000-0000-0000-0000-000000000001");
  private static readonly Guid _self = new("ffffffff-0000-0000-0000-000000000002");

  /// <summary>The map that inverts the modulo for two members: member 1 owns the even partitions, member 0 the odd.</summary>
  private static short[] _inverted() => [.. Enumerable.Range(0, P).Select(p => (short)(p % 2 == 0 ? 1 : 0))];

  // --- the predicate ----------------------------------------------------------------------------

  [Test]
  public async Task PartitionInShare_WithAMap_IsTheMapsAnswer_AndWithoutOne_TheResidueAsync() {
    await using var conn = await _openAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT wh_partition_in_share(0, 1, 2, @map), wh_partition_in_share(1, 1, 2, @map),
             wh_partition_in_share(0, 1, 2, NULL), wh_partition_in_share(1, 1, 2, NULL),
             wh_partition_in_share(10000, 1, 2, @map), wh_partition_in_share(1, 1, 2, '{}'::smallint[])
      """;
    cmd.Parameters.Add(new NpgsqlParameter("map", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = _inverted() });
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();

    await Assert.That(reader.GetBoolean(0)).IsTrue().Because("the map gives partition 0 to member 1");
    await Assert.That(reader.GetBoolean(1)).IsFalse();
    await Assert.That(reader.GetBoolean(2)).IsFalse().Because("with no map, partition 0 is member 0's residue");
    await Assert.That(reader.GetBoolean(3)).IsTrue();
    await Assert.That(reader.GetBoolean(4)).IsTrue().Because("a partition past the map wraps, so none is left unowned");
    await Assert.That(reader.GetBoolean(5)).IsTrue().Because("an empty map is no map");
  }

  // --- the publish and the read -----------------------------------------------------------------

  [Test]
  public async Task Publish_WithAMap_StoresIt_AndTheReadReturnsItAsync() {
    await using var conn = await _openAsync();
    var epoch = await _electAsync(conn, _assigner);
    var map = PartitionMap.Initial([_assigner, _self, Guid.CreateVersion7()]).Rebalance([_assigner, _self]);

    var published = await _store().PublishAsync(ROLE, _assigner, epoch, map, TimeSpan.FromSeconds(90), CancellationToken.None);
    var read = await _store().ReadAsync(CancellationToken.None);

    await Assert.That(published!.Owners).IsEquivalentTo(map.Owners, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(read!.Assignment.Owners).IsEquivalentTo(map.Owners, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(PartitionMap.FromPublished(read.Assignment).MovesTo(map)).IsEqualTo(0);
  }

  [Test]
  public async Task Publish_OfTheEmptyMap_StoresNoMembersAndNoMapAsync() {
    await using var conn = await _openAsync();
    var epoch = await _electAsync(conn, _assigner);

    var published = await _store().PublishAsync(ROLE, _assigner, epoch, PartitionMap.Empty, TimeSpan.FromSeconds(90), CancellationToken.None);

    await Assert.That(published!.Members).IsEmpty();
    await Assert.That(published.Owners).IsNull();
  }

  [Test]
  public async Task Publish_WithoutAMap_StoresNone_SoTheClaimsRankByModuloAsync() {
    // An assigner of an older release calls the five-argument publish. Its claimers rank by modulo, and must go on doing
    // so: the map is NULL, and the claim takes partition 1, its residue as member 1 of 2.
    await using var conn = await _openAsync();
    var epoch = await _electAsync(conn, _assigner);
    var revision = await _publishAsync(conn, epoch, [_assigner, _self], owners: null);
    await _claimerAsync(conn);
    await _seedOutboxAsync(conn);

    var read = await _store().ReadAsync(CancellationToken.None);
    await _claimAsync(conn, new PartitionAssignmentVersion(epoch, revision));

    await Assert.That(read!.Assignment.Owners).IsNull();
    await Assert.That(await _claimedAsync(conn, "wh_outbox")).IsEquivalentTo([1]);
  }

  [Test]
  public async Task Publish_AMapOfAnotherLength_IsRefusedAsync() {
    await using var conn = await _openAsync();
    var epoch = await _electAsync(conn, _assigner);

    var refusal = await Assert.That(async () => await _publishAsync(conn, epoch, [_assigner], owners: [0, 0, 0]))
      .Throws<PostgresException>();

    await Assert.That(refusal!.SqlState).IsEqualTo("22023").Because("the partition count is fixed at 10,000");
  }

  [Test]
  public async Task Publish_AMapNamingAMemberItDoesNotList_IsRefusedAsync() {
    await using var conn = await _openAsync();
    var epoch = await _electAsync(conn, _assigner);
    var map = _inverted();

    var refusal = await Assert.That(async () => await _publishAsync(conn, epoch, [_assigner], map))
      .Throws<PostgresException>();

    await Assert.That(refusal!.SqlState).IsEqualTo("22023")
      .Because("index 1 names no member of a one-member list, so its partitions would be claimed by nobody");
  }

  // --- the claims read the map --------------------------------------------------------------------

  [Test]
  public async Task ClaimWork_Outbox_WithAMap_TakesThePartitionsTheMapGivesIt_NotItsResidueAsync() {
    await using var conn = await _openAsync();
    var version = await _mappedClaimerAsync(conn);
    await _seedOutboxAsync(conn);

    await _claimAsync(conn, version);

    await Assert.That(await _claimedAsync(conn, "wh_outbox")).IsEquivalentTo([0])
      .Because("the map gives partition 0 to this instance; the modulo would have given it partition 1");
  }

  [Test]
  public async Task ClaimWork_Inbox_WithAMap_TakesThePartitionsTheMapGivesIt_NotItsResidueAsync() {
    await using var conn = await _openAsync();
    var version = await _mappedClaimerAsync(conn);
    await _seedInboxAsync(conn, leasedTo: null);

    await _claimAsync(conn, version);

    await Assert.That(await _claimedAsync(conn, "wh_inbox_state")).IsEquivalentTo([0]);
  }

  [Test]
  public async Task ClaimWork_Perspective_WithAMap_TakesThePartitionsTheMapGivesIt_NotItsResidueAsync() {
    await using var conn = await _openAsync();
    var version = await _mappedClaimerAsync(conn);
    await _execAsync(conn, """
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry, partition_number, status, attempts, created_at)
      SELECT gen_random_uuid(), gen_random_uuid(), 'TestPerspective', gen_random_uuid(), NULL, NULL, p, 0, 0,
             clock_timestamp() - INTERVAL '10 minutes'
      FROM generate_series(0, 1) p
      """);

    await _claimAsync(conn, version);

    await Assert.That(await _claimedAsync(conn, "wh_perspective_events")).IsEquivalentTo([0]);
  }

  [Test]
  public async Task ClaimWork_HeldInboxLeases_AreRenewedOnlyOnPartitionsTheMapGivesItAsync() {
    // This instance holds inbox rows on partitions 0 and 1 whose leases ran out. Whatever path takes them back (the
    // acquisition or the held-lease walk), only the partition the map gives it may be leased again.
    await using var conn = await _openAsync();
    var version = await _mappedClaimerAsync(conn);
    await _seedInboxAsync(conn, leasedTo: _self);

    await _claimAsync(conn, version);

    var live = await _partitionsAsync(conn,
      $"SELECT partition_number FROM wh_inbox_state WHERE instance_id = '{_self}' AND lease_expiry > clock_timestamp()");
    await Assert.That(live).IsEquivalentTo([0]);
  }

  [Test]
  public async Task ClaimWork_WithAMapButAStaleVersion_RanksItselfByTheResidueAsync() {
    await using var conn = await _openAsync();
    var version = await _mappedClaimerAsync(conn);
    await _seedOutboxAsync(conn);

    await _claimAsync(conn, version with { Revision = version.Revision + 1 });

    await Assert.That((await _claimedAsync(conn, "wh_outbox")).Order()).IsEquivalentTo([0, 1])
      .Because("a claim that ranks itself (alone among the live) ranks by the residue, never by a map it cannot use");
  }

  [Test]
  public async Task ClaimWork_WithAMapThatLeavesItOut_RanksItselfByTheResidueAsync() {
    // The map gives partitions 0 and 1 to member 1, another instance; this claimer is no member. It ranks itself alone,
    // so it takes both. Were the map carried into its own ranking (rank 0), it would take neither.
    await using var conn = await _openAsync();
    var epoch = await _electAsync(conn, _assigner);
    var other = Guid.CreateVersion7();
    var map = new short[P];
    Array.Fill(map, (short)1);
    var revision = await _publishAsync(conn, epoch, [_assigner, other], map);
    await _claimerAsync(conn);
    await _seedOutboxAsync(conn);

    await _claimAsync(conn, new PartitionAssignmentVersion(epoch, revision));

    await Assert.That((await _claimedAsync(conn, "wh_outbox")).Order()).IsEquivalentTo([0, 1]);
  }

  // --- helpers --------------------------------------------------------------------------------------

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private PgPartitionAssignmentStore _store() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    return new PgPartitionAssignmentStore(
      Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString, SignalingMode = WorkSignalingMode.ListenNotify }),
      configuration);
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _heartbeatAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT record_heartbeat(@inst, 'test', 'test-host', 1, '{}'::jsonb, NULL, NULL, NULL, 'pooled')";
    cmd.Parameters.AddWithValue("inst", instance);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>Registers the instance and wins the assigner role for it; returns the epoch.</summary>
  private static async Task<long> _electAsync(NpgsqlConnection conn, Guid instance) {
    await _heartbeatAsync(conn, instance);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText =
      $"SELECT epoch FROM wh_vote_role('{ROLE}', '{instance}'::uuid, INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL) v WHERE v.outcome = 'granted'";
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  /// <summary>Publishes through the SQL function: the older five-argument call when <paramref name="owners"/> is null.</summary>
  private static async Task<long> _publishAsync(NpgsqlConnection conn, long epoch, Guid[] members, short[]? owners) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = owners is null
      ? "SELECT wh_publish_partition_assignment(@role, @inst, @epoch, @members, INTERVAL '90 seconds')"
      : "SELECT wh_publish_partition_assignment(@role, @inst, @epoch, @members, INTERVAL '90 seconds', @owners)";
    cmd.Parameters.AddWithValue("role", ROLE);
    cmd.Parameters.AddWithValue("inst", _assigner);
    cmd.Parameters.AddWithValue(nameof(epoch), epoch);
    cmd.Parameters.AddWithValue(nameof(members), members);
    if (owners is not null) {
      cmd.Parameters.Add(new NpgsqlParameter(nameof(owners), NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = owners });
    }
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// The claimer is live alone (the assigner's heartbeat is an hour old), so ranking itself would take every partition;
  /// the assignment is what makes it take a share.
  /// </summary>
  private static async Task _claimerAsync(NpgsqlConnection conn) {
    await _execAsync(conn, $"UPDATE wh_service_instances SET last_heartbeat_at = NOW() - INTERVAL '1 hour' WHERE instance_id = '{_assigner}'");
    await _heartbeatAsync(conn, _self);
  }

  /// <summary>The claimer is member 1 of [assigner, self] under the map that inverts the modulo; returns the version.</summary>
  private static async Task<PartitionAssignmentVersion> _mappedClaimerAsync(NpgsqlConnection conn) {
    var epoch = await _electAsync(conn, _assigner);
    var revision = await _publishAsync(conn, epoch, [_assigner, _self], _inverted());
    await _claimerAsync(conn);
    return new PartitionAssignmentVersion(epoch, revision);
  }

  /// <summary>Two outbox rows on two unowned streams, partitions 0 and 1.</summary>
  private static Task _seedOutboxAsync(NpgsqlConnection conn) => _execAsync(conn, """
    INSERT INTO wh_outbox (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
    SELECT gen_random_uuid(), 'test-topic', 'TestEvent', '{}', '{}', 0, 0, clock_timestamp(), gen_random_uuid(), p
    FROM generate_series(0, 1) p
    """);

  /// <summary>Two inbox rows on two streams, partitions 0 and 1: unleased, or leased to an instance and run out.</summary>
  private static Task _seedInboxAsync(NpgsqlConnection conn, Guid? leasedTo) {
    var owner = leasedTo is { } id ? $"'{id}'::uuid" : "NULL::uuid";
    var expiry = leasedTo is null ? "NULL::timestamptz" : "clock_timestamp() - INTERVAL '1 minute'";
    return _execAsync(conn, $$"""
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event)
        SELECT gen_random_uuid(), 'TestHandler', 'TestEvent', '{"p": {} }', '{}',
               clock_timestamp() - INTERVAL '10 minutes' + (p * INTERVAL '1 second'), gen_random_uuid(), TRUE
        FROM generate_series(0, 1) p
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, instance_id, lease_expiry)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 0,
             (row_number() OVER (ORDER BY received_at) - 1)::int, {{owner}}, {{expiry}}
      FROM m
      """);
  }

  private static async Task _claimAsync(NpgsqlConnection conn, PartitionAssignmentVersion version) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT count(*) FROM claim_work(
        p_instance_id => @inst, p_service_name => 'test', p_host_name => 'test-host', p_process_id => 1,
        p_max_streams => 25, p_partition_count => 10000, p_lease_seconds => 300,
        p_assignment_epoch => @epoch, p_assignment_revision => @revision)
      """;
    cmd.Parameters.AddWithValue("inst", _self);
    cmd.Parameters.AddWithValue("epoch", version.Epoch);
    cmd.Parameters.AddWithValue("revision", version.Revision);
    _ = await cmd.ExecuteScalarAsync();
  }

  /// <summary>The partitions of the rows of <paramref name="table"/> the claimer holds.</summary>
  private static Task<List<int>> _claimedAsync(NpgsqlConnection conn, string table) =>
    _partitionsAsync(conn, $"SELECT partition_number FROM {table} WHERE instance_id = '{_self}'");

  private static async Task<List<int>> _partitionsAsync(NpgsqlConnection conn, string sql) {
    var partitions = new List<int>();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      partitions.Add(reader.GetInt32(0));
    }
    return partitions;
  }
}
