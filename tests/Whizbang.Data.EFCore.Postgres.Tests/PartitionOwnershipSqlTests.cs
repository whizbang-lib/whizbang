// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The partition ownership table, its one predicate and the per-service switch that turns ownership by map on (#2555).
/// The assigner writes one row per partition (owner, epoch, state) in the statement that publishes the map, fenced by
/// its role epoch; <c>wh_i_own</c> answers "may this instance claim this partition" with the epoch or NULL; and the
/// switch is a one-way <c>wh_settings</c> latch the assigner writes only once every live instance of the service says
/// it supports the mode.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/560_PartitionsAreOwnedByThePublishedMap.sql</code-under-test>
[Category("Shard4")]
public class PartitionOwnershipSqlTests : EFCoreTestBase {
  private const int PARTITIONS = 10_000;
  private const string SUPPORTS_MAP = """{"partition_ownership_level":1}""";
  private static readonly Guid _a = new("00000000-0000-0000-0000-00000000000a");
  private static readonly Guid _b = new("00000000-0000-0000-0000-00000000000b");
  private static readonly Guid _c = new("00000000-0000-0000-0000-00000000000c");

  [Test]
  public async Task Publish_WritesOneActiveRowPerPartition_OwnedByTheMembersInTurn_AtEpochOneAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a);
    await _heartbeatAsync(conn, _b);
    var epoch = await _voteAsync(conn, _a);

    await _publishAsync(conn, _a, epoch, [_a, _b]);

    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_partition_ownership")).IsEqualTo(PARTITIONS);
    await Assert.That(await _ownerAsync(conn, 0)).IsEqualTo(_a);
    await Assert.That(await _ownerAsync(conn, 1)).IsEqualTo(_b);
    await Assert.That(await _ownerAsync(conn, 9_999)).IsEqualTo(_b);
    await Assert.That(await _scalarAsync<long>(conn,
      "SELECT count(*) FROM wh_partition_ownership WHERE epoch = 1 AND state = 'active' AND next_owner_instance_id IS NULL AND handover_deadline IS NULL"))
      .IsEqualTo(PARTITIONS);
  }

  [Test]
  public async Task Republish_AdvancesTheEpochOfExactlyThePartitionsWhoseOwnerChangedAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a);
    await _heartbeatAsync(conn, _b);
    await _heartbeatAsync(conn, _c);
    var epoch = await _voteAsync(conn, _a);
    await _publishAsync(conn, _a, epoch, [_a, _b]);

    await _publishAsync(conn, _a, epoch, [_a, _b, _c]);

    // Partition p was members[p % 2] and is now members[p % 3]: unchanged exactly where both name the same member.
    var changed = Enumerable.Range(0, PARTITIONS).Count(p => (p % 2) != (p % 3));
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_partition_ownership WHERE epoch = 2")).IsEqualTo(changed);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_partition_ownership WHERE epoch = 1")).IsEqualTo(PARTITIONS - changed);
    await Assert.That(await _ownerAsync(conn, 2)).IsEqualTo(_c);
    await Assert.That(await _epochAsync(conn, 2)).IsEqualTo(2L);
    await Assert.That(await _ownerAsync(conn, 6)).IsEqualTo(_a);
    await Assert.That(await _epochAsync(conn, 6)).IsEqualTo(1L);
  }

  [Test]
  public async Task Republish_OfTheSameMembers_LeavesEveryEpochAsItWasAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a);
    await _heartbeatAsync(conn, _b);
    var epoch = await _voteAsync(conn, _a);
    await _publishAsync(conn, _a, epoch, [_a, _b]);

    await _publishAsync(conn, _a, epoch, [_a, _b]);

    await Assert.That(await _scalarAsync<long>(conn, "SELECT max(epoch) FROM wh_partition_ownership")).IsEqualTo(1L);
  }

  [Test]
  public async Task Publish_WithNoMembers_LeavesTheOwnershipAsItWasAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a);
    var epoch = await _voteAsync(conn, _a);
    await _publishAsync(conn, _a, epoch, [_a]);

    await _publishAsync(conn, _a, epoch, []);

    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_partition_ownership WHERE owner_instance_id = @a AND epoch = 1",
      ("a", _a))).IsEqualTo(PARTITIONS);
  }

  [Test]
  public async Task Publish_ByADeposedAssigner_WritesNoOwnershipAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a);
    var epoch = await _voteAsync(conn, _a);

    await Assert.That(async () => await _publishAsync(conn, _a, epoch + 1, [_a])).Throws<PostgresException>();

    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_partition_ownership")).IsEqualTo(0L);
  }

  [Test]
  public async Task IOwn_AnswersTheEpochForTheActiveOwner_AndNullForAnyoneElseAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a);
    await _heartbeatAsync(conn, _b);
    var epoch = await _voteAsync(conn, _a);
    await _publishAsync(conn, _a, epoch, [_a, _b]);

    await Assert.That(await _iOwnAsync(conn, 4, _a)).IsEqualTo(1L);
    await Assert.That(await _iOwnAsync(conn, 4, _b)).IsNull();
    await Assert.That(await _iOwnAsync(conn, 4, _c)).IsNull();
  }

  [Test]
  public async Task IOwn_APartitionHandingOver_IsOwnedByNobodyNewAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a);
    await _heartbeatAsync(conn, _b);
    var epoch = await _voteAsync(conn, _a);
    await _publishAsync(conn, _a, epoch, [_a, _b]);
    await _execAsync(conn,
      "UPDATE wh_partition_ownership SET state = 'handing_over', next_owner_instance_id = @b, handover_deadline = NOW() + INTERVAL '30 seconds' WHERE partition_number = 4",
      ("b", _b));

    await Assert.That(await _iOwnAsync(conn, 4, _a)).IsNull().Because("a claim needs the partition active");
    await Assert.That(await _iOwnAsync(conn, 4, _b)).IsNull().Because("the next owner owns nothing until the flip");
  }

  [Test]
  public async Task IOwn_WorkWithoutAPartition_IsAnyonesAtEpochZeroAsync() {
    await using var conn = await _openAsync();

    await Assert.That(await _iOwnAsync(conn, null, _a)).IsEqualTo(0L);
  }

  [Test]
  public async Task IOwn_APartitionNumberPastTheTable_IsOwnedThroughItsRemainderAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a);
    await _heartbeatAsync(conn, _b);
    var epoch = await _voteAsync(conn, _a);
    await _publishAsync(conn, _a, epoch, [_a, _b]);

    await Assert.That(await _iOwnAsync(conn, PARTITIONS + 1, _b)).IsEqualTo(1L);
    await Assert.That(await _iOwnAsync(conn, PARTITIONS + 1, _a)).IsNull();
  }

  [Test]
  public async Task Mode_IsOffUntilTheSwitchIsWrittenAsync() {
    await using var conn = await _openAsync();

    await Assert.That(await _scalarAsync<bool>(conn, "SELECT wh_partition_ownership_by_map()")).IsFalse();
  }

  [Test]
  public async Task Switch_StaysOffWhileALiveInstanceDoesNotSupportTheMode_AndTurnsOnOnceEveryLiveOneDoesAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a, SUPPORTS_MAP);
    await _heartbeatAsync(conn, _b);
    var epoch = await _voteAsync(conn, _a);
    await _publishAsync(conn, _a, epoch, [_a, _b]);

    var closed = await _openSwitchAsync(conn, _a, epoch);
    await _heartbeatAsync(conn, _b, SUPPORTS_MAP);
    var opened = await _openSwitchAsync(conn, _a, epoch);
    var again = await _openSwitchAsync(conn, _a, epoch);

    await Assert.That(closed).IsEqualTo("closed (1 of 2 live instances support ownership by map)");
    await Assert.That(opened).IsEqualTo("opened");
    await Assert.That(again).IsEqualTo("open");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT wh_partition_ownership_by_map()")).IsTrue();
  }

  [Test]
  public async Task Switch_AStaleInstanceThatDoesNotSupportTheMode_DoesNotHoldItOffAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a, SUPPORTS_MAP);
    await _heartbeatAsync(conn, _b);
    await _execAsync(conn, "UPDATE wh_service_instances SET last_heartbeat_at = NOW() - INTERVAL '1 hour' WHERE instance_id = @b", ("b", _b));
    var epoch = await _voteAsync(conn, _a);
    await _publishAsync(conn, _a, epoch, [_a]);

    await Assert.That(await _openSwitchAsync(conn, _a, epoch)).IsEqualTo("opened");
  }

  [Test]
  public async Task Switch_WithNoMapPublished_StaysOffAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a, SUPPORTS_MAP);
    var epoch = await _voteAsync(conn, _a);

    await Assert.That(await _openSwitchAsync(conn, _a, epoch)).IsEqualTo("closed (no map is published at this epoch)");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT wh_partition_ownership_by_map()")).IsFalse();
  }

  [Test]
  public async Task Switch_ByADeposedAssigner_IsRefusedAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a, SUPPORTS_MAP);
    var epoch = await _voteAsync(conn, _a);
    await _publishAsync(conn, _a, epoch, [_a]);

    var refused = await Assert.That(async () => await _openSwitchAsync(conn, _a, epoch + 1)).Throws<PostgresException>();

    await Assert.That(refused!.SqlState).IsEqualTo("WHF01");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT wh_partition_ownership_by_map()")).IsFalse();
  }

  [Test]
  public async Task Switch_OnceOn_StaysOn_WhenAnInstanceThatDoesNotSupportItJoinsAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a, SUPPORTS_MAP);
    var epoch = await _voteAsync(conn, _a);
    await _publishAsync(conn, _a, epoch, [_a]);
    _ = await _openSwitchAsync(conn, _a, epoch);

    await _heartbeatAsync(conn, _b);

    await Assert.That(await _openSwitchAsync(conn, _a, epoch)).IsEqualTo("open");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT wh_partition_ownership_by_map()")).IsTrue();
  }

  [Test]
  public async Task Publish_InMapMode_LeavesOutAMemberThatDoesNotSupportTheModeAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a, SUPPORTS_MAP);
    var epoch = await _voteAsync(conn, _a);
    await _publishAsync(conn, _a, epoch, [_a]);
    _ = await _openSwitchAsync(conn, _a, epoch);
    await _heartbeatAsync(conn, _b);
    await _heartbeatAsync(conn, _c, SUPPORTS_MAP);

    await _publishAsync(conn, _a, epoch, [_a, _b, _c]);

    await Assert.That(await _scalarAsync<Guid[]>(conn, "SELECT members FROM wh_published_partition_assignment")).IsEquivalentTo([_a, _c]);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_partition_ownership WHERE owner_instance_id = @b", ("b", _b)))
      .IsEqualTo(0L).Because("an instance that does not support ownership by map is given no partition once the mode is on");
  }

  [Test]
  public async Task MapIsValid_WhileItsLeaseRunsAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a);
    var epoch = await _voteAsync(conn, _a);
    await _publishAsync(conn, _a, epoch, [_a, _b]);

    await Assert.That(await _scalarAsync<bool>(conn, "SELECT wh_partition_map_is_valid()")).IsTrue()
      .Because("a member that is not live does not invalidate a map whose assigner still renews it");
  }

  [Test]
  public async Task MapIsValid_PastItsLease_WhileEveryMemberIsLiveAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a);
    await _heartbeatAsync(conn, _b);
    var epoch = await _voteAsync(conn, _a);
    await _publishAsync(conn, _a, epoch, [_a, _b]);
    await _lapseTheMapAsync(conn);

    await Assert.That(await _scalarAsync<bool>(conn, "SELECT wh_partition_map_is_valid()")).IsTrue()
      .Because("an assigner lapse never stalls claims while the map's members are live");
  }

  [Test]
  public async Task MapIsNotValid_PastItsLease_OnceAMemberIsNotLiveAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a);
    await _heartbeatAsync(conn, _b);
    var epoch = await _voteAsync(conn, _a);
    await _publishAsync(conn, _a, epoch, [_a, _b]);
    await _lapseTheMapAsync(conn);
    await _execAsync(conn, "UPDATE wh_service_instances SET last_heartbeat_at = NOW() - INTERVAL '1 hour' WHERE instance_id = @b", ("b", _b));

    await Assert.That(await _scalarAsync<bool>(conn, "SELECT wh_partition_map_is_valid()")).IsFalse();
  }

  [Test]
  public async Task MapIsNotValid_WhenNoneIsPublishedAsync() {
    await using var conn = await _openAsync();

    await Assert.That(await _scalarAsync<bool>(conn, "SELECT wh_partition_map_is_valid()")).IsFalse();
  }

  // --- helpers --------------------------------------------------------------------------------

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task _heartbeatAsync(NpgsqlConnection conn, Guid instance, string metadata = "{}") {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT record_heartbeat(@inst, 'test', 'test-host', 1, @metadata::jsonb, NULL, NULL, NULL, 'pooled')";
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.AddWithValue(nameof(metadata), metadata);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _voteAsync(NpgsqlConnection conn, Guid instance) {
    await using var vote = conn.CreateCommand();
    vote.CommandText = "SELECT epoch FROM wh_vote_role(@role, @inst, INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL) v WHERE v.outcome = 'granted'";
    vote.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
    vote.Parameters.AddWithValue("inst", instance);
    return Convert.ToInt64(await vote.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task _publishAsync(NpgsqlConnection conn, Guid assigner, long epoch, Guid[] members) {
    await using var publish = conn.CreateCommand();
    publish.CommandText = "SELECT wh_publish_partition_assignment(@role, @inst, @epoch, @members, INTERVAL '90 seconds')";
    publish.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
    publish.Parameters.AddWithValue("inst", assigner);
    publish.Parameters.AddWithValue(nameof(epoch), epoch);
    publish.Parameters.Add(new NpgsqlParameter(nameof(members), NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = members });
    await publish.ExecuteNonQueryAsync();
  }

  private static async Task<string?> _openSwitchAsync(NpgsqlConnection conn, Guid assigner, long epoch) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_open_partition_ownership_by_map(@role, @inst, @epoch)";
    cmd.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
    cmd.Parameters.AddWithValue("inst", assigner);
    cmd.Parameters.AddWithValue(nameof(epoch), epoch);
    return (string?)await cmd.ExecuteScalarAsync();
  }

  /// <summary>Runs the published map's lease out on the database's clock.</summary>
  private static Task _lapseTheMapAsync(NpgsqlConnection conn) =>
    _execAsync(conn, "UPDATE wh_published_partition_assignment SET lease_expires_at = NOW() - INTERVAL '1 second'");

  private static async Task<long?> _iOwnAsync(NpgsqlConnection conn, int? partition, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_i_own(@partition, @inst)";
    cmd.Parameters.Add(new NpgsqlParameter(nameof(partition), NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)partition ?? DBNull.Value });
    cmd.Parameters.AddWithValue("inst", instance);
    return await cmd.ExecuteScalarAsync() is long epoch ? epoch : null;
  }

  private static async Task<Guid> _ownerAsync(NpgsqlConnection conn, int partition) =>
    await _scalarAsync<Guid>(conn, "SELECT owner_instance_id FROM wh_partition_ownership WHERE partition_number = @p", ("p", partition));

  private static async Task<long> _epochAsync(NpgsqlConnection conn, int partition) =>
    await _scalarAsync<long>(conn, "SELECT epoch FROM wh_partition_ownership WHERE partition_number = @p", ("p", partition));

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
