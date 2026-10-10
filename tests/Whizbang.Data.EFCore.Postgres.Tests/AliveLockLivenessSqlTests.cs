// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 204 (#1286): a direct instance is live while its alive-lock is held, so its heartbeat can run on the slow
/// cadence without any reader of heartbeat freshness taking it for dead; once the lock is gone it is judged by its
/// heartbeat at once, like a pooled instance; and a pooled instance is judged by its heartbeat alone, exactly as before.
/// </summary>
/// <remarks>
/// <para>
/// A direct instance on the slow cadence is modeled as a registration row whose heartbeat is 45 seconds old (past the
/// claim's 30 second rank cutoff, inside one slow beat of 60 seconds) and a separate session holding its alive-lock,
/// as the instance's shared notify connection does. The reap and the vote use a row 200 seconds old: past the
/// heartbeat's stale threshold (150 seconds with the default cadence) and short of the five-minute definitive-dead
/// cutoff, the window in which only the lock can keep an instance registered.
/// </para>
/// <para>
/// Losing the lock is <c>pg_advisory_unlock</c> on the holding session: it returns once the lock is released, so the
/// next statement sees it gone. Closing the session would release it only when the backend exits, which the test
/// could only wait for. Every claim runs inside a transaction that is rolled back, so one test can claim twice over the
/// same rows.
/// </para>
/// </remarks>
/// <docs>fundamentals/workers/instance-liveness</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/204_DirectInstancesAreLiveByTheirAliveLock.sql</code-under-test>
[Category("Shard3")]
public class AliveLockLivenessSqlTests : EFCoreTestBase {
  private const string REGISTRATION_STALE = "whizbang.instance_registration_stale=true";
  private const string ROLE = "alive-lock-liveness-test";

  /// <summary>A heartbeat on the slow cadence: past the claim's 30 second cutoff, inside one 60 second beat.</summary>
  private static readonly TimeSpan _slowBeatAge = TimeSpan.FromSeconds(45);

  /// <summary>Past the default stale threshold (150 seconds), short of the five-minute definitive-dead cutoff.</summary>
  private static readonly TimeSpan _pastTheReapThreshold = TimeSpan.FromSeconds(200);

  /// <summary>The partitions rank 1 of 2 takes out of ten.</summary>
  private static readonly int[] _oddPartitions = [1, 3, 5, 7, 9];
  private static readonly int[] _allPartitions = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

  // --- the one definition of a held alive-lock -------------------------------------------------

  [Test]
  public async Task Holders_ADirectInstanceHoldingItsLock_IsListedAsync() {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, InstanceConnectionModes.DIRECT, TimeSpan.Zero);
    await using var holder = await _holdAliveLockAsync(instance);

    await Assert.That(await _holdersAsync(conn)).Contains(instance);
  }

  [Test]
  public async Task Holders_APooledInstanceHoldingALock_IsNotListedAsync() {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, InstanceConnectionModes.POOLED, TimeSpan.Zero);
    await using var holder = await _holdAliveLockAsync(instance);

    await Assert.That(await _holdersAsync(conn)).DoesNotContain(instance)
      .Because("a pooled instance is judged by its heartbeat alone; a lock seen through a pooler is not its session's");
  }

  [Test]
  public async Task Holders_ADirectInstanceWhoseLockIsReleased_IsNotListedAsync() {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, InstanceConnectionModes.DIRECT, TimeSpan.Zero);
    await using var holder = await _holdAliveLockAsync(instance);
    await _releaseAliveLockAsync(holder, instance);

    await Assert.That(await _holdersAsync(conn)).DoesNotContain(instance);
  }

  [Test]
  public async Task Candidates_ADirectInstanceWhoseLockIsReleased_ReportsNoLockAsync() {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, InstanceConnectionModes.DIRECT, _slowBeatAge);
    await using var holder = await _holdAliveLockAsync(instance);
    await Assert.That(await _candidateLockHeldAsync(conn, instance)).IsTrue();

    await _releaseAliveLockAsync(holder, instance);

    await Assert.That(await _candidateLockHeldAsync(conn, instance)).IsFalse()
      .Because("the assigner judges a direct instance without its lock by its heartbeat, so the lock it reports must be the live one");
  }

  // --- claim_work's rank -----------------------------------------------------------------------

  [Test]
  public async Task Rank_ADirectPeerOnTheSlowCadenceHoldingItsLock_IsCountedAsync() {
    var (self, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, InstanceConnectionModes.DIRECT, _slowBeatAge);
    await _registerAsync(conn, self, InstanceConnectionModes.DIRECT, TimeSpan.Zero);
    await _seedOutboxAsync(conn, partitions: 10);
    await using var holder = await _holdAliveLockAsync(peer);

    var (partitions, _) = await _claimAsync(conn, self, aliveLockHeld: false);

    await Assert.That(partitions).IsEquivalentTo(_oddPartitions)
      .Because("a direct peer holding its alive-lock is live whatever its heartbeat's age, so the caller is rank 1 of 2; "
        + "counted out, it would take its peer's partitions as well");
  }

  [Test]
  public async Task Rank_ADirectPeerWhoseLockIsReleased_IsCountedOutAtOnceAsync() {
    var (self, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, InstanceConnectionModes.DIRECT, _slowBeatAge);
    await _registerAsync(conn, self, InstanceConnectionModes.DIRECT, TimeSpan.Zero);
    await _seedOutboxAsync(conn, partitions: 10);
    await using var holder = await _holdAliveLockAsync(peer);
    var (whileHeld, _) = await _claimAsync(conn, self, aliveLockHeld: false);

    await _releaseAliveLockAsync(holder, peer);
    var (afterRelease, _) = await _claimAsync(conn, self, aliveLockHeld: false);

    await Assert.That(whileHeld).IsEquivalentTo(_oddPartitions);
    await Assert.That(afterRelease).IsEquivalentTo(_allPartitions)
      .Because("without its lock a direct instance is judged by its heartbeat, which is past the cutoff: the slow "
        + "cadence must not keep it ranked once the lock is gone");
  }

  [Test]
  public async Task Rank_APooledPeerPastTheCutoff_IsCountedOutWhateverLockItsSessionHoldsAsync() {
    var (self, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, InstanceConnectionModes.POOLED, _slowBeatAge);
    await _registerAsync(conn, self, InstanceConnectionModes.POOLED, TimeSpan.Zero);
    await _seedOutboxAsync(conn, partitions: 10);
    await using var holder = await _holdAliveLockAsync(peer);

    var (partitions, _) = await _claimAsync(conn, self, aliveLockHeld: false);

    await Assert.That(partitions).IsEquivalentTo(_allPartitions)
      .Because("a pooled peer is ranked by its heartbeat alone, as before: past the cutoff it drops out");
  }

  [Test]
  public async Task Rank_APooledPeerWithAFreshHeartbeat_IsCountedAsync() {
    var (self, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, InstanceConnectionModes.POOLED, TimeSpan.Zero);
    await _registerAsync(conn, self, InstanceConnectionModes.POOLED, TimeSpan.Zero);
    await _seedOutboxAsync(conn, partitions: 10);

    var (partitions, _) = await _claimAsync(conn, self, aliveLockHeld: false);

    await Assert.That(partitions).IsEquivalentTo(_oddPartitions);
  }

  [Test]
  public async Task Rank_ARowWithNoRecordedMode_IsJudgedAsPooledAsync() {
    var (self, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, mode: null, _slowBeatAge);
    await _registerAsync(conn, self, mode: null, TimeSpan.Zero);
    await _seedOutboxAsync(conn, partitions: 10);
    await using var holder = await _holdAliveLockAsync(peer);

    var (partitions, _) = await _claimAsync(conn, self, aliveLockHeld: false);

    await Assert.That(partitions).IsEquivalentTo(_allPartitions)
      .Because("an instance that has not said how it is connected (an older build) beats on the fast cadence and is judged as pooled");
  }

  // --- claim_work's own-registration notice ----------------------------------------------------

  [Test]
  public async Task Notice_ADirectCallerHoldingItsLock_IsNotAskedToRegisterBetweenSlowBeatsAsync() {
    var (self, _) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, self, InstanceConnectionModes.DIRECT, _slowBeatAge);
    await _seedOutboxAsync(conn, partitions: 1);

    var (_, notices) = await _claimAsync(conn, self, aliveLockHeld: true);

    await Assert.That(notices).DoesNotContain(REGISTRATION_STALE)
      .Because("its peers rank it by its lock, so a re-registration would only undo the slow cadence");
  }

  [Test]
  public async Task Notice_ADirectCallerWithoutItsLock_IsAskedToRegisterAsync() {
    var (self, _) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, self, InstanceConnectionModes.DIRECT, _slowBeatAge);
    await _seedOutboxAsync(conn, partitions: 1);

    var (_, notices) = await _claimAsync(conn, self, aliveLockHeld: false);

    await Assert.That(notices).Contains(REGISTRATION_STALE);
  }

  [Test]
  public async Task Notice_APooledCallerPastTheCutoff_IsStillAskedToRegisterAsync() {
    var (self, _) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, self, InstanceConnectionModes.POOLED, _slowBeatAge);
    await _seedOutboxAsync(conn, partitions: 1);

    var (_, notices) = await _claimAsync(conn, self, aliveLockHeld: true);

    await Assert.That(notices).Contains(REGISTRATION_STALE)
      .Because("a pooled instance is judged by its heartbeat alone, whatever it says about a lock");
  }

  [Test]
  public async Task Notice_AMissingRegistration_IsReportedWhateverTheLockAsync() {
    var (self, _) = _pair();
    await using var conn = await _openAsync();
    await _seedOutboxAsync(conn, partitions: 1);

    var (_, notices) = await _claimAsync(conn, self, aliveLockHeld: true);

    await Assert.That(notices).Contains(REGISTRATION_STALE)
      .Because("a row that is not there must be written; the lock cannot stand in for a registration");
  }

  [Test]
  [Arguments(true, false)]
  [Arguments(false, true)]
  public async Task Coordinator_PassesTheCallersAliveLock_SoADirectCallerHoldingItIsNotReportedStaleAsync(
      bool aliveLockHeld, bool expectedStale) {
    var (self, _) = _pair();
    await using (var conn = await _openAsync()) {
      await _registerAsync(conn, self, InstanceConnectionModes.DIRECT, _slowBeatAge);
      await _seedOutboxAsync(conn, partitions: 1);
    }
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var batch = await coordinator.ClaimWorkAsync(new Whizbang.Core.Messaging.ClaimWorkRequest(
      self, "test", "test-host", 1, MaxStreams: 10, AliveLockHeld: aliveLockHeld));

    await Assert.That(batch.InstanceRegistrationStale).IsEqualTo(expectedStale);
  }

  // --- the stale-peer reap ---------------------------------------------------------------------

  [Test]
  public async Task Reap_ADirectPeerHoldingItsLock_IsNeverReapedAsync() {
    var (reaper, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, InstanceConnectionModes.DIRECT, _pastTheReapThreshold);
    await using var holder = await _holdAliveLockAsync(peer);

    await _heartbeatAsync(conn, reaper);

    await Assert.That(await _isRegisteredAsync(conn, peer)).IsTrue()
      .Because("a direct instance's alive-lock is authoritative: held, it is live whatever its heartbeat's age");
    await Assert.That(await _isEvictedAsync(conn, peer)).IsFalse();
  }

  [Test]
  public async Task Reap_ADirectPeerWhoseLockIsReleased_IsReapedOnTheNextPeerBeatAsync() {
    var (reaper, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, InstanceConnectionModes.DIRECT, _pastTheReapThreshold);
    await using var holder = await _holdAliveLockAsync(peer);
    await _heartbeatAsync(conn, reaper);
    await Assert.That(await _isRegisteredAsync(conn, peer)).IsTrue();

    await _releaseAliveLockAsync(holder, peer);
    await _heartbeatAsync(conn, reaper);

    await Assert.That(await _isRegisteredAsync(conn, peer)).IsFalse()
      .Because("once its lock is gone a direct instance is judged by its heartbeat, which is past the threshold");
    await Assert.That(await _isEvictedAsync(conn, peer)).IsTrue();
  }

  [Test]
  public async Task Reap_APooledPeerPastTheThreshold_IsReapedAsync() {
    var (reaper, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, InstanceConnectionModes.POOLED, _pastTheReapThreshold);

    await _heartbeatAsync(conn, reaper);

    await Assert.That(await _isRegisteredAsync(conn, peer)).IsFalse();
    await Assert.That(await _isEvictedAsync(conn, peer)).IsTrue();
  }

  // --- role votes ------------------------------------------------------------------------------

  [Test]
  public async Task Vote_ARoleHeldByADirectInstanceHoldingItsLock_IsNotVotedAwayAsync() {
    var (voter, holderId) = _pair();
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, holderId, InstanceConnectionModes.DIRECT);
    await Assert.That(await _voteAsync(conn, holderId)).IsEqualTo("granted");
    await _ageHeartbeatAsync(conn, holderId, _pastTheReapThreshold);
    await using var holder = await _holdAliveLockAsync(holderId);

    await _heartbeatAsync(conn, voter);
    var outcome = await _voteAsync(conn, voter);

    await Assert.That(outcome).IsEqualTo("contended")
      .Because("the holder is still registered: its lock kept the reap from taking it for dead");
  }

  [Test]
  public async Task Vote_ARoleHeldByADirectInstanceWhoseLockIsReleased_IsVotedAwayOnTheNextPeerBeatAsync() {
    var (voter, holderId) = _pair();
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, holderId, InstanceConnectionModes.DIRECT);
    await Assert.That(await _voteAsync(conn, holderId)).IsEqualTo("granted");
    await _ageHeartbeatAsync(conn, holderId, _pastTheReapThreshold);
    await using var holder = await _holdAliveLockAsync(holderId);
    await _heartbeatAsync(conn, voter);
    await Assert.That(await _voteAsync(conn, voter)).IsEqualTo("contended");

    await _releaseAliveLockAsync(holder, holderId);
    await _heartbeatAsync(conn, voter);
    var outcome = await _voteAsync(conn, voter);

    await Assert.That(outcome).IsEqualTo("granted")
      .Because("the reap took the holder for dead once its lock was gone, so its assignment is void and the role is free");
  }

  // --- the standby handshake's view of liveness ------------------------------------------------

  [Test]
  public async Task FleetSource_ReportsWhetherAPeersAliveLockIsHeldAsync() {
    var (_, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, InstanceConnectionModes.DIRECT, _slowBeatAge);
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    await using var provider = services.BuildServiceProvider();
    var source = new EFCorePostgresStartupFleetStatusSource(
      provider.GetRequiredService<IServiceScopeFactory>(), typeof(WorkCoordinationDbContext));
    await using var holder = await _holdAliveLockAsync(peer);

    var whileHeld = (await source.GetFleetAsync(CancellationToken.None)).Single(r => r.InstanceId == peer);
    await _releaseAliveLockAsync(holder, peer);
    var afterRelease = (await source.GetFleetAsync(CancellationToken.None)).Single(r => r.InstanceId == peer);

    await Assert.That(whileHeld.AliveLockHeld).IsTrue()
      .Because("the handshake must keep waiting for an older peer whose heartbeat is between slow beats");
    await Assert.That(afterRelease.AliveLockHeld).IsFalse();
  }

  [Test]
  public async Task StandbyRequest_ReportsWhetherTheRequestersAliveLockIsHeldAsync() {
    var (requester, _) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, requester, InstanceConnectionModes.DIRECT, _slowBeatAge);
    await _scalarAsync<bool>(conn, $"SELECT request_standby('{requester}'::uuid, '9.9.9')");
    try {
      await using var ctx = CreateDbContext();
      var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
      await using var holder = await _holdAliveLockAsync(requester);

      var whileHeld = await coordinator.GetStandbyRequestAsync(CancellationToken.None);
      await _releaseAliveLockAsync(holder, requester);
      var afterRelease = await coordinator.GetStandbyRequestAsync(CancellationToken.None);

      await Assert.That(whileHeld!.RequesterAliveLockHeld).IsTrue()
        .Because("a migrator between slow beats is alive; its peers must keep standing by");
      await Assert.That(afterRelease!.RequesterAliveLockHeld).IsFalse();
    } finally {
      await _scalarAsync<bool>(conn, $"SELECT clear_standby('{requester}'::uuid)");
    }
  }

  // ------------------------------------------------------------------

  /// <summary>Two fresh instance ids, the first sorting after the second, so among two live instances it is rank 1 of 2.</summary>
  private static (Guid Self, Guid Peer) _pair() {
    var suffix = Guid.NewGuid().ToString("N")[..12];
    return (new Guid($"ffffffff-0000-0000-0000-{suffix}"), new Guid($"00000000-0000-0000-0000-{suffix}"));
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return (T)Convert.ChangeType(await cmd.ExecuteScalarAsync(), typeof(T), CultureInfo.InvariantCulture)!;
  }

  /// <summary>A session of its own holding the instance's alive-lock, as its shared notify connection does.</summary>
  private async Task<NpgsqlConnection> _holdAliveLockAsync(Guid instanceId) {
    var holder = await _openAsync();
    var claimed = await _scalarAsync<bool>(holder, $"SELECT claim_instance_alive_lock('{instanceId}'::uuid)");
    if (!claimed) {
      throw new InvalidOperationException($"the alive-lock for {instanceId} was already held");
    }
    return holder;
  }

  /// <summary>Releases the lock on the session that holds it; returns once it is released.</summary>
  private static async Task _releaseAliveLockAsync(NpgsqlConnection holder, Guid instanceId) {
    var released = await _scalarAsync<bool>(holder,
      $"SELECT pg_advisory_unlock(hashtext('wh_instance_alive:' || '{instanceId}'))");
    if (!released) {
      throw new InvalidOperationException($"the alive-lock for {instanceId} was not held by this session");
    }
  }

  /// <summary>A registration row with the given connection mode (null: not recorded) and heartbeat age.</summary>
  private static async Task _registerAsync(NpgsqlConnection conn, Guid instanceId, string? mode, TimeSpan age) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, connection_mode)
      VALUES (@id, 'test', 'test-host', 1, NOW() - @age, NOW() - INTERVAL '2 hours', @mode)";
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue("age", age);
    cmd.Parameters.AddWithValue("mode", (object?)mode ?? DBNull.Value);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _ageHeartbeatAsync(NpgsqlConnection conn, Guid instanceId, TimeSpan age) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_service_instances SET last_heartbeat_at = NOW() - @age WHERE instance_id = @id";
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue("age", age);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>One heartbeat as the heartbeat worker sends it, with the default cadence's 150 second stale threshold.</summary>
  private static async Task _heartbeatAsync(NpgsqlConnection conn, Guid instanceId, string mode = InstanceConnectionModes.POOLED) {
    var accepted = await _scalarAsync<bool>(conn, $@"
      SELECT record_heartbeat('{instanceId}'::uuid, 'test', 'test-host', 1, '{{}}'::jsonb, 'Running', '1.0.0', 150, '{mode}')");
    if (!accepted) {
      throw new InvalidOperationException($"the heartbeat of {instanceId} was refused");
    }
  }

  private static async Task<string> _voteAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT outcome FROM wh_vote_role(@role, @id, INTERVAL '5 minutes', INTERVAL '0 seconds', NULL, ARRAY[1, 0], NULL)";
    cmd.Parameters.AddWithValue("role", ROLE);
    cmd.Parameters.AddWithValue("id", instanceId);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<bool> _isRegisteredAsync(NpgsqlConnection conn, Guid instanceId) =>
    await _scalarAsync<bool>(conn, $"SELECT EXISTS (SELECT 1 FROM wh_service_instances WHERE instance_id = '{instanceId}')");

  private static async Task<bool> _isEvictedAsync(NpgsqlConnection conn, Guid instanceId) =>
    await _scalarAsync<bool>(conn, $"SELECT EXISTS (SELECT 1 FROM wh_instance_evictions WHERE instance_id = '{instanceId}')");

  private static async Task<List<Guid>> _holdersAsync(NpgsqlConnection conn) {
    var holders = new List<Guid>();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT instance_id FROM wh_direct_alive_lock_holders()";
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      holders.Add(reader.GetGuid(0));
    }
    return holders;
  }

  private static async Task<bool> _candidateLockHeldAsync(NpgsqlConnection conn, Guid instanceId) =>
    await _scalarAsync<bool>(conn,
      $"SELECT alive_lock_held FROM wh_partition_assignment_candidates() WHERE instance_id = '{instanceId}'");

  /// <summary>One unowned outbox row on each of the given partitions, each on a stream of its own.</summary>
  private static async Task _seedOutboxAsync(NpgsqlConnection conn, int partitions) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id,
         partition_number)
      SELECT gen_random_uuid(), 'test-topic', 'TestEvent', '{}', '{}', 0, 0,
             NOW() - INTERVAL '1 minute' + (p * INTERVAL '1 millisecond'), gen_random_uuid(), p
      FROM generate_series(0, @n - 1) p";
    cmd.Parameters.AddWithValue("n", partitions);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// One self-ranked claim inside a transaction that is rolled back: the partitions of the outbox rows the caller held
  /// after it, and the notices it raised.
  /// </summary>
  private static async Task<(List<int> Partitions, List<string> Notices)> _claimAsync(
      NpgsqlConnection conn, Guid instanceId, bool aliveLockHeld) {
    var notices = new List<string>();
    void OnNotice(object? sender, NpgsqlNoticeEventArgs e) => notices.Add(e.Notice.MessageText);
    var partitions = new List<int>();
    await using var tx = await conn.BeginTransactionAsync();
    conn.Notice += OnNotice;
    try {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = @"
        SELECT count(*) FROM claim_work(
          p_instance_id => @inst, p_service_name => 'test', p_host_name => 'test-host', p_process_id => 1,
          p_max_streams => 100, p_partition_count => 10000, p_lease_seconds => 300, p_alive_lock_held => @held)";
      cmd.Parameters.AddWithValue("inst", instanceId);
      cmd.Parameters.AddWithValue("held", aliveLockHeld);
      await cmd.ExecuteNonQueryAsync();
    } finally {
      conn.Notice -= OnNotice;
    }
    await using (var leased = conn.CreateCommand()) {
      leased.CommandText = "SELECT partition_number FROM wh_outbox WHERE instance_id = @inst ORDER BY partition_number";
      leased.Parameters.AddWithValue("inst", instanceId);
      await using var reader = await leased.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        partitions.Add(reader.GetInt32(0));
      }
    }
    await tx.RollbackAsync();
    return (partitions, notices);
  }
}
