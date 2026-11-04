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
/// Migration 317 (#2027): every function that judges whether an instance is alive asks the one liveness rule (316).
/// Each test below sets up a case on which the reader's old definition and the rule disagree, so it fails if that
/// reader ever goes back to a definition of its own: a peer inside the window it declares but past the claim's old
/// fixed 30 seconds, or a direct peer live only by its held alive-lock, or a peer an old wider window still counted.
/// </summary>
/// <docs>fundamentals/workers/instance-liveness</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/317_EveryReaderJudgesLivenessByTheRule.sql</code-under-test>
[Category("Shard3")]
public class LivenessRuleReadersSqlTests : EFCoreTestBase {
  private const string REGISTRATION_STALE = "whizbang.instance_registration_stale=true";
  private static readonly int[] _oddPartitions = [1, 3, 5, 7, 9];

  // --- claim_work ----------------------------------------------------------------------------------

  [Test]
  public async Task Rank_ADirectPeerThatLostItsLock_IsNotCountedPastOneBeatAsync() {
    var (self, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, InstanceConnectionModes.DIRECT, TimeSpan.FromSeconds(45), windowSeconds: 150);
    await _registerAsync(conn, self, InstanceConnectionModes.DIRECT, TimeSpan.Zero, windowSeconds: 150);
    await _seedOutboxAsync(conn, partitions: 10);

    var (partitions, _) = await _claimAsync(conn, self, aliveLockHeld: false);

    await Assert.That(partitions).Count().IsEqualTo(10)
      .Because("the peer holds no lock and its heartbeat is past one beat, so the caller is the only live instance and "
        + "takes every partition after about 30 seconds, not after the peer's 150 second window");
  }

  [Test]
  public async Task Rank_ADirectPeerWithoutItsLock_IsCountedInsideOneBeatAsync() {
    var (self, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, InstanceConnectionModes.DIRECT, TimeSpan.FromSeconds(20), windowSeconds: 150);
    await _registerAsync(conn, self, InstanceConnectionModes.DIRECT, TimeSpan.Zero, windowSeconds: 150);
    await _seedOutboxAsync(conn, partitions: 10);

    var (partitions, _) = await _claimAsync(conn, self, aliveLockHeld: false);

    await Assert.That(partitions).IsEquivalentTo(_oddPartitions)
      .Because("a 20 second heartbeat is inside one beat, so the peer is still counted and the caller is rank 1 of 2");
  }

  [Test]
  public async Task Rank_APeerInsideItsOwnWindow_IsCountedPastTheOldThirtySecondCutoffAsync() {
    var (self, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, InstanceConnectionModes.POOLED, TimeSpan.FromSeconds(80), windowSeconds: 90);
    await _registerAsync(conn, self, InstanceConnectionModes.POOLED, TimeSpan.Zero, windowSeconds: 90);
    await _seedOutboxAsync(conn, partitions: 10);

    var (partitions, _) = await _claimAsync(conn, self, aliveLockHeld: false);

    await Assert.That(partitions).IsEquivalentTo(_oddPartitions);
  }

  [Test]
  public async Task Caller_LiveByTheRule_IsNotAskedToReRegisterAsync() {
    var self = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, self, InstanceConnectionModes.POOLED, TimeSpan.FromSeconds(80), windowSeconds: 90);

    var (_, notices) = await _claimAsync(conn, self, aliveLockHeld: false);

    await Assert.That(notices).DoesNotContain(REGISTRATION_STALE);
  }

  // --- the partition assigner's candidates ---------------------------------------------------------

  [Test]
  public async Task Candidates_ReportWhetherTheRuleCallsEachLiveAsync() {
    var inside = (Guid)TrackedGuid.New();
    var outside = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, inside, InstanceConnectionModes.POOLED, TimeSpan.FromSeconds(100), windowSeconds: 150);
    await _registerAsync(conn, outside, InstanceConnectionModes.POOLED, TimeSpan.FromSeconds(100), windowSeconds: 90);

    await Assert.That(await _candidateIsLiveAsync(conn, inside)).IsTrue();
    await Assert.That(await _candidateIsLiveAsync(conn, outside)).IsFalse()
      .Because("the assigner reads the rule's answer and applies no window of its own");
  }

  // --- calculate_instance_rank ---------------------------------------------------------------------

  [Test]
  public async Task CalculateInstanceRank_CountsOnlyInstancesTheRuleCallsLiveAsync() {
    var self = (Guid)TrackedGuid.New();
    var liveByItsLock = (Guid)TrackedGuid.New();
    var dead = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, self, InstanceConnectionModes.POOLED, TimeSpan.Zero, windowSeconds: 150);
    await _registerAsync(conn, liveByItsLock, InstanceConnectionModes.DIRECT, TimeSpan.FromSeconds(200), windowSeconds: 150);
    await _registerAsync(conn, dead, InstanceConnectionModes.POOLED, TimeSpan.FromSeconds(100), windowSeconds: 90);
    await using var holder = await _holdAliveLockAsync(liveByItsLock);

    var count = await _scalarAsync<int>(conn,
      $"SELECT active_instance_count FROM calculate_instance_rank('{self}'::uuid, NOW() - INTERVAL '30 seconds')");

    await Assert.That(count).IsEqualTo(2);
  }

  // --- doorbells -----------------------------------------------------------------------------------

  [Test]
  public async Task NotifyOwners_TargetsOnlyInstancesTheRuleCallsLiveAsync() {
    var liveByItsLock = (Guid)TrackedGuid.New();
    var dead = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, liveByItsLock, InstanceConnectionModes.DIRECT, TimeSpan.FromSeconds(200), windowSeconds: 150);
    await _registerAsync(conn, dead, InstanceConnectionModes.POOLED, TimeSpan.FromSeconds(100), windowSeconds: 90);
    await using var holder = await _holdAliveLockAsync(liveByItsLock);

    await using var tx = await conn.BeginTransactionAsync();
    await _executeAsync(conn, $"SELECT notify_instance_owners_with_payload('inbox', 'inbox', ARRAY['{TrackedGuid.New()}']::uuid[])", tx);
    var toLive = await _doorbellsAsync(conn, liveByItsLock, tx);
    var toDead = await _doorbellsAsync(conn, dead, tx);
    await tx.RollbackAsync();

    await Assert.That(toLive).IsEqualTo(1L)
      .Because("the only instance the rule calls live is the deterministic owner of an unclaimed stream");
    await Assert.That(toDead).IsEqualTo(0L);
  }

  [Test]
  public async Task DeadLetterReady_WakesOnlyInstancesTheRuleCallsLiveAsync() {
    var liveByItsLock = (Guid)TrackedGuid.New();
    var deadInsideTheOldFiveMinutes = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, liveByItsLock, InstanceConnectionModes.DIRECT, TimeSpan.FromSeconds(200), windowSeconds: 150);
    await _registerAsync(conn, deadInsideTheOldFiveMinutes, InstanceConnectionModes.POOLED, TimeSpan.FromSeconds(240), windowSeconds: 90);
    await using var holder = await _holdAliveLockAsync(liveByItsLock);

    await using var tx = await conn.BeginTransactionAsync();
    await _executeAsync(conn, $@"
      INSERT INTO wh_dead_letters
        (dead_letter_id, source_table, source_id, message_type, envelope, failure_reason, attempts_when_dlq,
         recovery_status, recovery_attempts, generation, error_fingerprint, error_fingerprint_version)
      VALUES ('{TrackedGuid.New()}', 'wh_inbox', '{TrackedGuid.New()}', 'T', '{{}}'::jsonb, 5, 3, 0, 0, 'seed/1', 'fp', 1)", tx);
    var toLive = await _doorbellsAsync(conn, liveByItsLock, tx);
    var toDead = await _doorbellsAsync(conn, deadInsideTheOldFiveMinutes, tx);
    await tx.RollbackAsync();

    await Assert.That(toLive).IsEqualTo(1L);
    await Assert.That(toDead).IsEqualTo(0L)
      .Because("a heartbeat four minutes old is past the window the instance declared; the old five minutes no longer applies");
  }

  // ------------------------------------------------------------------

  private static (Guid Self, Guid Peer) _pair() {
    var suffix = ((Guid)TrackedGuid.New()).ToString("N")[..12];
    return (new Guid($"ffffffff-0000-0000-0000-{suffix}"), new Guid($"00000000-0000-0000-0000-{suffix}"));
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, NpgsqlTransaction? tx = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Transaction = tx;
    return (T)Convert.ChangeType(await cmd.ExecuteScalarAsync(), typeof(T), CultureInfo.InvariantCulture)!;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, NpgsqlTransaction? tx = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Transaction = tx;
    await cmd.ExecuteNonQueryAsync();
  }

  private static Task<long> _doorbellsAsync(NpgsqlConnection conn, Guid instance, NpgsqlTransaction tx) =>
    _scalarAsync<long>(conn, $"SELECT count(*) FROM wh_doorbell_queue WHERE channel = 'wh_work_i_{instance}'", tx);

  private static Task<bool> _candidateIsLiveAsync(NpgsqlConnection conn, Guid instance) =>
    _scalarAsync<bool>(conn, $"SELECT is_live FROM wh_partition_assignment_candidates() WHERE instance_id = '{instance}'");

  private async Task<NpgsqlConnection> _holdAliveLockAsync(Guid instanceId) {
    var holder = await _openAsync();
    if (!await _scalarAsync<bool>(holder, $"SELECT claim_instance_alive_lock('{instanceId}'::uuid)")) {
      throw new InvalidOperationException($"the alive-lock for {instanceId} was already held");
    }
    return holder;
  }

  private static async Task _registerAsync(NpgsqlConnection conn, Guid instanceId, string mode, TimeSpan age, int? windowSeconds) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, connection_mode, liveness_window_seconds)
      VALUES (@id, 'test', 'test-host', 1, NOW() - @age, NOW() - INTERVAL '2 hours', @mode, @window)";
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue(nameof(age), age);
    cmd.Parameters.AddWithValue(nameof(mode), mode);
    cmd.Parameters.AddWithValue("window", (object?)windowSeconds ?? DBNull.Value);
    await cmd.ExecuteNonQueryAsync();
  }

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
