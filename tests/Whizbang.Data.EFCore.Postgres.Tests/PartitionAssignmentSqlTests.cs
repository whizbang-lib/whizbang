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
/// Migration 203 (#1254): the published partition assignment, fenced by the role epoch where it is written and where
/// claim_work reads it, leased by the assigner's liveness, and the liveness signals per connection mode.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/203_PartitionAssigner.sql</code-under-test>
[Category("Shard3")]
public class PartitionAssignmentSqlTests : EFCoreTestBase {
  private const string ROLE = PartitionAssignerOptions.ROLE;
  private static readonly TimeSpan _signalDeadline = TimeSpan.FromSeconds(30);

  // --- the fence where it is written ----------------------------------------------------------

  [Test]
  public async Task Publish_ByTheRoleHolder_WritesTheAssignmentAndAnnouncesItAsync() {
    var assigner = Guid.CreateVersion7();
    var peer = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var epoch = await _electAsync(conn, assigner);
    await using var listener = await _openAsync();
    await _execAsync(listener, $"LISTEN \"{PartitionAssignmentCache.CHANNEL}\"");
    string? payload = null;
    listener.Notification += (_, e) => payload = e.Payload;

    var revision = await _publishAsync(conn, assigner, epoch, [assigner, peer]);
    // The signal: the server delivers the announcement to this session. Bounded, never a wait in itself.
    using (var deadline = new CancellationTokenSource(_signalDeadline)) {
      await listener.WaitAsync(deadline.Token);
    }

    await Assert.That(revision).IsEqualTo(1);
    await Assert.That(payload).IsEqualTo($"{epoch}:1");
    var read = await _readAsync();
    await Assert.That(read!.Assignment.Epoch).IsEqualTo(epoch);
    await Assert.That(read.Assignment.AssignerInstanceId).IsEqualTo(assigner);
    await Assert.That(read.Assignment.Members).IsEquivalentTo([assigner, peer], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Publish_ByADeposedAssigner_IsRefusedAsync() {
    var first = Guid.CreateVersion7();
    var second = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var oldEpoch = await _electAsync(conn, first);
    _ = await _publishAsync(conn, first, oldEpoch, [first]);
    await _execAsync(conn, $"SELECT wh_release_role('{ROLE}', '{first}'::uuid, {oldEpoch})");
    var newEpoch = await _electAsync(conn, second);

    var refused = await Assert.ThrowsAsync<PostgresException>(() => _publishAsync(conn, first, oldEpoch, [first, second]));

    await Assert.That(refused!.SqlState).IsEqualTo("WHF01");
    await Assert.That(newEpoch).IsGreaterThan(oldEpoch);
    var read = await _readAsync();
    await Assert.That(read!.Assignment.Members).IsEquivalentTo([first])
      .Because("a deposed assigner writes nothing");
  }

  [Test]
  public async Task Publish_AgainInTheSameTenure_AdvancesTheRevisionAsync() {
    var assigner = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var epoch = await _electAsync(conn, assigner);

    _ = await _publishAsync(conn, assigner, epoch, [assigner]);
    var second = await _publishAsync(conn, assigner, epoch, [assigner]);

    await Assert.That(second).IsEqualTo(2);
  }

  [Test]
  public async Task Publish_ByANewTenure_RestartsTheRevisionAtTheNewEpochAsync() {
    var first = Guid.CreateVersion7();
    var second = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var oldEpoch = await _electAsync(conn, first);
    _ = await _publishAsync(conn, first, oldEpoch, [first]);
    _ = await _publishAsync(conn, first, oldEpoch, [first]);
    await _execAsync(conn, $"SELECT wh_release_role('{ROLE}', '{first}'::uuid, {oldEpoch})");
    var newEpoch = await _electAsync(conn, second);

    var revision = await _publishAsync(conn, second, newEpoch, [second]);

    await Assert.That(revision).IsEqualTo(1);
    await Assert.That((await _readAsync())!.Assignment.Epoch).IsEqualTo(newEpoch);
  }

  // --- the lease is the assigner's liveness ---------------------------------------------------

  [Test]
  public async Task Renew_ByTheAssignerAtItsEpoch_ExtendsTheLeaseAsync() {
    var assigner = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var epoch = await _electAsync(conn, assigner);
    _ = await _publishAsync(conn, assigner, epoch, [assigner]);
    await _execAsync(conn, "UPDATE wh_published_partition_assignment SET lease_expires_at = clock_timestamp() + INTERVAL '1 second'");

    var renewed = await _scalarAsync<bool>(conn, $"SELECT wh_renew_partition_assignment('{assigner}'::uuid, {epoch})");

    await Assert.That(renewed).IsTrue();
    await Assert.That((await _readAsync())!.LeaseRemaining).IsGreaterThan(TimeSpan.FromSeconds(60));
  }

  [Test]
  public async Task Renew_ByAnInstanceThatNoLongerHoldsTheRole_ChangesNothingAsync() {
    var first = Guid.CreateVersion7();
    var second = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var epoch = await _electAsync(conn, first);
    _ = await _publishAsync(conn, first, epoch, [first]);
    await _execAsync(conn, $"SELECT wh_release_role('{ROLE}', '{first}'::uuid, {epoch})");
    _ = await _electAsync(conn, second);
    await _execAsync(conn, "UPDATE wh_published_partition_assignment SET lease_expires_at = clock_timestamp() + INTERVAL '1 second'");

    var renewed = await _scalarAsync<bool>(conn, $"SELECT wh_renew_partition_assignment('{first}'::uuid, {epoch})");

    await Assert.That(renewed).IsFalse();
    await Assert.That((await _readAsync())!.LeaseRemaining).IsLessThan(TimeSpan.FromSeconds(2));
  }

  [Test]
  public async Task Heartbeat_OfTheAssignerHoldingTheRole_RenewsTheAssignmentLeaseAsync() {
    var assigner = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var epoch = await _electAsync(conn, assigner);
    _ = await _publishAsync(conn, assigner, epoch, [assigner]);
    await _execAsync(conn, "UPDATE wh_published_partition_assignment SET lease_expires_at = clock_timestamp() + INTERVAL '1 second'");

    await _heartbeatAsync(conn, assigner, null);

    await Assert.That((await _readAsync())!.LeaseRemaining).IsGreaterThan(TimeSpan.FromSeconds(60))
      .Because("the heartbeat is the assigner's liveness where it has no alive-lock, so it renews the lease");
  }

  [Test]
  public async Task Heartbeat_OfADeposedAssigner_LeavesTheLeaseAloneAsync() {
    var first = Guid.CreateVersion7();
    var second = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var epoch = await _electAsync(conn, first);
    _ = await _publishAsync(conn, first, epoch, [first]);
    await _execAsync(conn, $"SELECT wh_release_role('{ROLE}', '{first}'::uuid, {epoch})");
    _ = await _electAsync(conn, second);
    await _execAsync(conn, "UPDATE wh_published_partition_assignment SET lease_expires_at = clock_timestamp() + INTERVAL '1 second'");

    await _heartbeatAsync(conn, first, null);

    await Assert.That((await _readAsync())!.LeaseRemaining).IsLessThan(TimeSpan.FromSeconds(2));
  }

  [Test]
  public async Task Read_ReturnsThePublishedAssignmentAndTheTimeLeftOnItsLeaseAsync() {
    var assigner = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await Assert.That(await _readAsync()).IsNull().Because("nothing is published yet");
    var epoch = await _electAsync(conn, assigner);
    _ = await _publishAsync(conn, assigner, epoch, [assigner]);

    var read = await _readAsync();

    await Assert.That(read!.LeaseRemaining).IsGreaterThan(TimeSpan.FromSeconds(80));
    await Assert.That(read.LeaseRemaining).IsLessThanOrEqualTo(TimeSpan.FromSeconds(90));
    await Assert.That(read.Assignment.LeaseExpiresAt).IsGreaterThan(read.Assignment.PublishedAt);
  }

  // --- liveness per connection mode -----------------------------------------------------------

  [Test]
  public async Task Heartbeat_RecordsTheConnectionMode_AndANullKeepsItAsync() {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();

    await _heartbeatAsync(conn, instance, InstanceConnectionModes.DIRECT);
    var recorded = await _scalarAsync<string>(conn, $"SELECT connection_mode FROM wh_service_instances WHERE instance_id = '{instance}'");
    await _heartbeatAsync(conn, instance, null);
    var kept = await _scalarAsync<string>(conn, $"SELECT connection_mode FROM wh_service_instances WHERE instance_id = '{instance}'");

    await Assert.That(recorded).IsEqualTo(InstanceConnectionModes.DIRECT);
    await Assert.That(kept).IsEqualTo(InstanceConnectionModes.DIRECT);
  }

  [Test]
  public async Task Candidates_ADirectInstanceHoldingItsAliveLock_ReportsTheLockAsync() {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, instance, InstanceConnectionModes.DIRECT);
    await using var lockHolder = await _openAsync();
    await _execAsync(lockHolder, $"SELECT claim_instance_alive_lock('{instance}'::uuid)");

    var candidate = await _candidateAsync(instance);

    await Assert.That(candidate.Mode).IsEqualTo(InstanceConnectionMode.Direct);
    await Assert.That(candidate.AliveLockHeld).IsTrue();
    await Assert.That(candidate.HeartbeatAge).IsLessThan(TimeSpan.FromSeconds(30));
  }

  [Test]
  public async Task Candidates_APooledInstance_IsNeverJudgedByALockAsync() {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, instance, InstanceConnectionModes.POOLED);
    await using var lockHolder = await _openAsync();
    await _execAsync(lockHolder, $"SELECT claim_instance_alive_lock('{instance}'::uuid)");

    var candidate = await _candidateAsync(instance);

    await Assert.That(candidate.Mode).IsEqualTo(InstanceConnectionMode.Pooled);
    await Assert.That(candidate.AliveLockHeld).IsFalse();
  }

  [Test]
  public async Task Candidates_AnEvictedInstance_IsLeftOutAsync() {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, instance, InstanceConnectionModes.POOLED);
    await _execAsync(conn, $"INSERT INTO wh_instance_evictions (instance_id, reason) VALUES ('{instance}', 'test')");

    var store = _store();
    var candidates = await store.ReadCandidatesAsync(CancellationToken.None);

    await Assert.That(candidates.Any(c => c.InstanceId == instance)).IsFalse();
  }

  // --- the fence where claim_work reads it ----------------------------------------------------

  [Test]
  public async Task ClaimWork_WithTheCurrentAssignment_RanksByItAsync() {
    var (conn, self, version) = await _assignedClaimerAsync();
    await using (conn) {
      var (claimed, stale) = await _claimAsync(conn, self, version);

      await Assert.That(stale).IsFalse();
      await Assert.That(claimed).IsEquivalentTo([1])
        .Because("this instance is rank 1 of 2 in the assignment, so it takes the odd partition only");
    }
  }

  [Test]
  public async Task ClaimWork_WithASupersededAssignment_RanksItselfAndAsksForARefreshAsync() {
    var (conn, self, version) = await _assignedClaimerAsync();
    await using (conn) {
      var (claimed, stale) = await _claimAsync(conn, self, version with { Revision = version.Revision + 1 });

      await Assert.That(stale).IsTrue();
      await Assert.That(claimed.Order()).IsEquivalentTo([0, 1])
        .Because("never stop claiming: alone in the heartbeat rows, it ranks itself solo and takes both");
    }
  }

  [Test]
  public async Task ClaimWork_WithAnExpiredAssignment_RanksItselfAndAsksForARefreshAsync() {
    var (conn, self, version) = await _assignedClaimerAsync();
    await using (conn) {
      await _execAsync(conn, "UPDATE wh_published_partition_assignment SET lease_expires_at = clock_timestamp() - INTERVAL '1 second'");

      var (claimed, stale) = await _claimAsync(conn, self, version);

      await Assert.That(stale).IsTrue();
      await Assert.That(claimed.Order()).IsEquivalentTo([0, 1]);
    }
  }

  [Test]
  public async Task ClaimWork_WhenNotAMemberOfTheCurrentAssignment_RanksItselfWithoutANoticeAsync() {
    var assigner = Guid.CreateVersion7();
    var self = Guid.CreateVersion7();
    var conn = await _openAsync();
    await using (conn) {
      var epoch = await _electAsync(conn, assigner);
      var revision = await _publishAsync(conn, assigner, epoch, [assigner]);
      await _execAsync(conn, $"UPDATE wh_service_instances SET last_heartbeat_at = NOW() - INTERVAL '1 hour' WHERE instance_id = '{assigner}'");
      await _heartbeatAsync(conn, self, InstanceConnectionModes.POOLED);
      await _seedOutboxAsync(conn);

      var (claimed, stale) = await _claimAsync(conn, self, new PartitionAssignmentVersion(epoch, revision));

      await Assert.That(stale).IsFalse().Because("the version is current; this instance is simply not in it yet");
      await Assert.That(claimed.Order()).IsEquivalentTo([0, 1]);
    }
  }

  [Test]
  public async Task Coordinator_PresentingASupersededVersion_ReportsTheAssignmentStaleOnTheBatchAsync() {
    var (conn, self, version) = await _assignedClaimerAsync();
    await using (conn) {
      await using var ctx = CreateDbContext();
      var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

      var stale = await coordinator.ClaimWorkAsync(new Whizbang.Core.Messaging.ClaimWorkRequest(
        self, "test", "test-host", 1, MaxStreams: 10, PartitionAssignment: version with { Revision = version.Revision + 1 }));
      var current = await coordinator.ClaimWorkAsync(new Whizbang.Core.Messaging.ClaimWorkRequest(
        self, "test", "test-host", 1, MaxStreams: 10, PartitionAssignment: version));

      await Assert.That(stale.PartitionAssignmentStale).IsTrue()
        .Because("the claim's in-band notice is how the claim worker learns to refresh its copy");
      await Assert.That(current.PartitionAssignmentStale).IsFalse();
      await Assert.That(stale.InstanceRegistrationStale).IsFalse().Because("one notice must not be read as the other");
    }
  }

  [Test]
  public async Task Coordinator_Heartbeat_RecordsTheConnectionModeItIsGivenAsync() {
    var instance = Guid.CreateVersion7();
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    _ = await coordinator.RecordHeartbeatAsync(new Whizbang.Core.Messaging.HeartbeatRequest(
      instance, "test", "test-host", 1, ConnectionMode: InstanceConnectionMode.Direct));

    await using var conn = await _openAsync();
    await Assert.That(await _scalarAsync<string>(conn, $"SELECT connection_mode FROM wh_service_instances WHERE instance_id = '{instance}'"))
      .IsEqualTo(InstanceConnectionModes.DIRECT);
  }

  // --- the store ------------------------------------------------------------------------------

  [Test]
  public async Task Store_PublishReadAndRenew_RoundTripAsync() {
    var assigner = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    var epoch = await _electAsync(conn, assigner);
    var store = _store();

    var published = await store.PublishAsync(ROLE, assigner, epoch, [assigner], TimeSpan.FromSeconds(90), CancellationToken.None);
    var renewed = await store.RenewAsync(assigner, epoch, CancellationToken.None);
    var refused = await store.PublishAsync(ROLE, assigner, epoch + 1, [assigner], TimeSpan.FromSeconds(90), CancellationToken.None);
    var read = await store.ReadAsync(CancellationToken.None);

    await Assert.That(published!.Revision).IsEqualTo(1);
    await Assert.That(renewed).IsTrue();
    await Assert.That(refused).IsNull().Because("the fence refuses an epoch the caller does not hold");
    await Assert.That(read!.Assignment.Version).IsEqualTo(published.Version);
    await Assert.That(read.Assignment.Members).IsEquivalentTo(published.Members);
    await Assert.That(read.Assignment.LeaseExpiresAt).IsGreaterThan(published.LeaseExpiresAt)
      .Because("the renewal moved the lease on from the publish");
  }

  // --- helpers --------------------------------------------------------------------------------

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

  private async Task<PartitionAssignmentCandidate> _candidateAsync(Guid instance) =>
    (await _store().ReadCandidatesAsync(CancellationToken.None)).Single(c => c.InstanceId == instance);

  private async Task<PartitionAssignmentRead?> _readAsync() => await _store().ReadAsync(CancellationToken.None);

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T), CultureInfo.InvariantCulture);
  }

  private static async Task _heartbeatAsync(NpgsqlConnection conn, Guid instance, string? mode) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT record_heartbeat(@inst, 'test', 'test-host', 1, '{}'::jsonb, NULL, NULL, NULL, @mode)";
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(mode), NpgsqlDbType.Text) { Value = (object?)mode ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>Registers the instance and wins the assigner role for it; returns the epoch.</summary>
  private static async Task<long> _electAsync(NpgsqlConnection conn, Guid instance) {
    await _heartbeatAsync(conn, instance, InstanceConnectionModes.POOLED);
    return await _scalarAsync<long>(conn,
      $"SELECT epoch FROM wh_vote_role('{ROLE}', '{instance}'::uuid, INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL) v WHERE v.outcome = 'granted'");
  }

  private static async Task<long> _publishAsync(NpgsqlConnection conn, Guid instance, long epoch, Guid[] members) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_publish_partition_assignment(@role, @inst, @epoch, @members, INTERVAL '90 seconds')";
    cmd.Parameters.AddWithValue("role", ROLE);
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.AddWithValue(nameof(epoch), epoch);
    cmd.Parameters.AddWithValue(nameof(members), members);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  /// <summary>Two outbox rows on two unowned streams, partitions 0 and 1.</summary>
  private static async Task _seedOutboxAsync(NpgsqlConnection conn) =>
    await _execAsync(conn, @"
      INSERT INTO wh_outbox (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
      SELECT gen_random_uuid(), 'test-topic', 'TestEvent', '{}', '{}', 0, 0, clock_timestamp(), gen_random_uuid(), p
      FROM generate_series(0, 1) p");

  /// <summary>
  /// A claimer that is rank 1 of 2 in the current assignment, while the heartbeat rows hold only itself (the peer's
  /// registration is gone), so ranking itself would put it at rank 0 of 1.
  /// </summary>
  private async Task<(NpgsqlConnection Conn, Guid Self, PartitionAssignmentVersion Version)> _assignedClaimerAsync() {
    var assigner = new Guid("00000000-0000-0000-0000-000000000001");
    var self = new Guid("ffffffff-0000-0000-0000-000000000002");
    var conn = await _openAsync();
    var epoch = await _electAsync(conn, assigner);
    var revision = await _publishAsync(conn, assigner, epoch, [assigner, self]);
    await _execAsync(conn, $"UPDATE wh_service_instances SET last_heartbeat_at = NOW() - INTERVAL '1 hour' WHERE instance_id = '{assigner}'");
    await _heartbeatAsync(conn, self, InstanceConnectionModes.POOLED);
    await _seedOutboxAsync(conn);
    return (conn, self, new PartitionAssignmentVersion(epoch, revision));
  }

  /// <summary>Runs claim_work with the version; returns the partitions of the outbox rows it leased and whether it asked for a refresh.</summary>
  private static async Task<(List<int> Partitions, bool Stale)> _claimAsync(
      NpgsqlConnection conn, Guid self, PartitionAssignmentVersion version) {
    var stale = false;
    void OnNotice(object? sender, NpgsqlNoticeEventArgs e) =>
      stale |= e.Notice.MessageText == "whizbang.partition_assignment_stale=true";
    conn.Notice += OnNotice;
    try {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = @"SELECT count(*) FROM claim_work(
        p_instance_id => @inst, p_service_name => 'test', p_host_name => 'test-host', p_process_id => 1,
        p_max_streams => 25, p_partition_count => 10000, p_lease_seconds => 300,
        p_assignment_epoch => @epoch, p_assignment_revision => @revision)";
      cmd.Parameters.AddWithValue("inst", self);
      cmd.Parameters.AddWithValue("epoch", version.Epoch);
      cmd.Parameters.AddWithValue("revision", version.Revision);
      _ = await cmd.ExecuteScalarAsync();
    } finally {
      conn.Notice -= OnNotice;
    }
    var partitions = new List<int>();
    await using var read = conn.CreateCommand();
    read.CommandText = "SELECT partition_number FROM wh_outbox WHERE instance_id = @inst";
    read.Parameters.AddWithValue("inst", self);
    await using var reader = await read.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      partitions.Add(reader.GetInt32(0));
    }
    return (partitions, stale);
  }
}
