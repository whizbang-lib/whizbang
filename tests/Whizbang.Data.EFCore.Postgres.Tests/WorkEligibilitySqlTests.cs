// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 229 (#1638): "alive" and "eligible for work" are two tests. Alive (a fresh heartbeat, or a held alive-lock
/// in direct mode) keeps an instance from being reaped; only an instance whose recorded lifecycle phase is
/// AcceptingCommands or Running is eligible for work: a partition-assignment candidate and a member of claim_work's
/// peer ranking. A starting or draining instance is alive but not eligible, so its share goes to its peers at once. An
/// instance that records no phase at all (an older build) is eligible whenever it is live, as before.
/// </summary>
/// <remarks>
/// Every instance here is registered with a fresh heartbeat, so liveness never decides an outcome: each test changes
/// only the recorded phase, which is the one thing that has to move the result. Every claim runs inside a transaction
/// that is rolled back, so one test can claim twice over the same rows.
/// </remarks>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/229_OnlyReadyInstancesAreEligibleForWork.sql</code-under-test>
[Category("Shard3")]
public class WorkEligibilitySqlTests : EFCoreTestBase {
  private const string REGISTRATION_STALE = "whizbang.instance_registration_stale=true";

  /// <summary>The partitions rank 1 of 2 takes out of ten.</summary>
  private static readonly int[] _oddPartitions = [1, 3, 5, 7, 9];
  private static readonly int[] _allPartitions = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

  /// <summary>Every phase an instance can record, with whether it makes the instance eligible for work.</summary>
  public static IEnumerable<Func<(string Phase, bool Eligible)>> EveryPhase() =>
    Enum.GetValues<LifecyclePhase>().Select<LifecyclePhase, Func<(string, bool)>>(phase =>
      () => (phase.ToString(), phase is LifecyclePhase.AcceptingCommands or LifecyclePhase.Running));

  // --- the one definition ----------------------------------------------------------------------

  [Test]
  [MethodDataSource(nameof(EveryPhase))]
  public async Task Eligible_EachRecordedPhase_IsEligibleOnlyWhenReadyAsync(string phase, bool eligible) {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, phase);

    await Assert.That(await _isEligibleAsync(conn, instance)).IsEqualTo(eligible)
      .Because("only an instance accepting commands or running can process work; every startup phase, a pause and a "
        + "drain are alive but not ready");
  }

  [Test]
  public async Task Eligible_ARowThatRecordsNoPhase_IsEligibleAsync() {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, phase: null);

    await Assert.That(await _isEligibleAsync(conn, instance)).IsTrue()
      .Because("an older build that never records a phase keeps today's behavior: live is enough");
  }

  // --- the partition assigner's candidates -----------------------------------------------------

  [Test]
  public async Task Candidates_ALiveInstanceInMigrating_IsLeftOutAsync() {
    var (self, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, self, nameof(LifecyclePhase.Running));
    await _registerAsync(conn, peer, nameof(LifecyclePhase.Migrating));

    var candidates = await _candidatesAsync(conn);

    await Assert.That(candidates).Contains(self);
    await Assert.That(candidates).DoesNotContain(peer)
      .Because("an instance still migrating cannot claim, so a share assigned to it would stall until it is ready");
  }

  [Test]
  public async Task Candidates_AnInstanceThatReachesAcceptingCommands_BecomesACandidateAsync() {
    var (_, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, nameof(LifecyclePhase.Migrating));
    var whileMigrating = await _candidatesAsync(conn);

    await _recordPhaseAsync(conn, peer, nameof(LifecyclePhase.AcceptingCommands));
    var onceAccepting = await _candidatesAsync(conn);

    await Assert.That(whileMigrating).DoesNotContain(peer);
    await Assert.That(onceAccepting).Contains(peer)
      .Because("the transition into AcceptingCommands is the moment the instance's claim loop can take its share");
  }

  [Test]
  public async Task Candidates_ADrainingInstanceWithAFreshHeartbeat_IsLeftOutAtOnceAsync() {
    var (_, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, nameof(LifecyclePhase.Running));
    var whileRunning = await _candidatesAsync(conn);

    await _recordPhaseAsync(conn, peer, nameof(LifecyclePhase.Stopping));
    var onceDraining = await _candidatesAsync(conn);

    await Assert.That(whileRunning).Contains(peer);
    await Assert.That(onceDraining).DoesNotContain(peer)
      .Because("a draining instance's share moves to its peers without waiting for its heartbeat to lapse");
  }

  [Test]
  public async Task Candidates_AnOlderInstanceThatRecordsNoPhase_IsStillACandidateAsync() {
    var (_, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, phase: null);

    await Assert.That(await _candidatesAsync(conn)).Contains(peer)
      .Because("during a rolling upgrade an older build never records a phase and must keep today's behavior");
  }

  // --- claim_work's peer ranking ---------------------------------------------------------------

  [Test]
  public async Task Rank_APeerInMigrating_IsNotCounted_SoTheCallerCoversEveryPartitionAsync() {
    var (self, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, nameof(LifecyclePhase.Migrating));
    await _registerAsync(conn, self, nameof(LifecyclePhase.Running));
    await _seedOutboxAsync(conn, partitions: 10);

    var (partitions, _) = await _claimAsync(conn, self, phase: null);

    await Assert.That(partitions).IsEquivalentTo(_allPartitions)
      .Because("counted, a migrating peer would be given the even partitions, which nobody claims until it is ready");
  }

  [Test]
  public async Task Rank_APeerThatReachesAcceptingCommands_IsCountedAsync() {
    var (self, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, nameof(LifecyclePhase.Migrating));
    await _registerAsync(conn, self, nameof(LifecyclePhase.Running));
    await _seedOutboxAsync(conn, partitions: 10);
    var (whileMigrating, _) = await _claimAsync(conn, self, phase: null);

    await _recordPhaseAsync(conn, peer, nameof(LifecyclePhase.AcceptingCommands));
    var (onceAccepting, _) = await _claimAsync(conn, self, phase: null);

    await Assert.That(whileMigrating).IsEquivalentTo(_allPartitions);
    await Assert.That(onceAccepting).IsEquivalentTo(_oddPartitions)
      .Because("once its peer is ready, the caller is rank 1 of 2 and leaves the even partitions to it");
  }

  [Test]
  public async Task Rank_ADrainingPeer_ItsShareMovesToTheCallerAtOnceAsync() {
    var (self, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, nameof(LifecyclePhase.Running));
    await _registerAsync(conn, self, nameof(LifecyclePhase.Running));
    await _seedOutboxAsync(conn, partitions: 10);
    var (whileRunning, _) = await _claimAsync(conn, self, phase: null);

    await _recordPhaseAsync(conn, peer, nameof(LifecyclePhase.Stopping));
    var (onceDraining, _) = await _claimAsync(conn, self, phase: null);

    await Assert.That(whileRunning).IsEquivalentTo(_oddPartitions);
    await Assert.That(onceDraining).IsEquivalentTo(_allPartitions)
      .Because("the peer's heartbeat is still fresh; only its recorded phase says it is draining");
  }

  [Test]
  public async Task Rank_ADirectPeerHoldingItsLockInMigrating_IsNotCountedAsync() {
    var (self, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, nameof(LifecyclePhase.Migrating), InstanceConnectionModes.DIRECT, TimeSpan.FromSeconds(45));
    await _registerAsync(conn, self, nameof(LifecyclePhase.Running));
    await _seedOutboxAsync(conn, partitions: 10);
    await using var holder = await _holdAliveLockAsync(peer);

    var (partitions, _) = await _claimAsync(conn, self, phase: null);

    await Assert.That(partitions).IsEquivalentTo(_allPartitions)
      .Because("the alive-lock says the peer is alive, not that it is ready: a direct instance looks alive for its whole "
        + "migration");
  }

  [Test]
  public async Task Rank_AnOlderPeerThatRecordsNoPhase_IsCountedAsBeforeAsync() {
    var (self, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, phase: null);
    await _registerAsync(conn, self, nameof(LifecyclePhase.Running));
    await _seedOutboxAsync(conn, partitions: 10);

    var (partitions, _) = await _claimAsync(conn, self, phase: null);

    await Assert.That(partitions).IsEquivalentTo(_oddPartitions);
  }

  [Test]
  public async Task Rank_ACallerThatIsNotYetEligible_StillCountsItselfAsync() {
    var (self, peer) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, peer, nameof(LifecyclePhase.Running));
    await _registerAsync(conn, self, nameof(LifecyclePhase.Migrating));
    await _seedOutboxAsync(conn, partitions: 10);

    var (partitions, _) = await _claimAsync(conn, self, phase: null);

    await Assert.That(partitions).IsEquivalentTo(_oddPartitions)
      .Because("the caller is running this claim: ranked as absent it would fall to a solo rank and take every "
        + "partition its peer covers");
  }

  // --- the caller's own recorded phase ---------------------------------------------------------

  [Test]
  public async Task Notice_ACallerWhoseRowRecordsAnotherPhase_IsAskedToRegisterAsync() {
    var (self, _) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, self, nameof(LifecyclePhase.Migrating));
    await _seedOutboxAsync(conn, partitions: 1);

    var (_, notices) = await _claimAsync(conn, self, phase: nameof(LifecyclePhase.AcceptingCommands));

    await Assert.That(notices).Contains(REGISTRATION_STALE)
      .Because("a lost or overwritten transition would leave a ready instance ineligible; the claim finds it and the "
        + "caller's registration writes the phase it is in");
  }

  [Test]
  public async Task Notice_ACallerWhoseRowRecordsItsPhase_IsNotAskedToRegisterAsync() {
    var (self, _) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, self, nameof(LifecyclePhase.AcceptingCommands));
    await _seedOutboxAsync(conn, partitions: 1);

    var (_, notices) = await _claimAsync(conn, self, phase: nameof(LifecyclePhase.AcceptingCommands));

    await Assert.That(notices).DoesNotContain(REGISTRATION_STALE);
  }

  [Test]
  public async Task Notice_ADrainingCallerWhoseRowSaysSo_IsNotAskedToRegisterAsync() {
    var (self, _) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, self, nameof(LifecyclePhase.Stopping));
    await _seedOutboxAsync(conn, partitions: 1);

    var (_, notices) = await _claimAsync(conn, self, phase: nameof(LifecyclePhase.Stopping));

    await Assert.That(notices).DoesNotContain(REGISTRATION_STALE)
      .Because("a row that records the caller's phase is current, eligible or not; asking again would write on every claim");
  }

  [Test]
  public async Task Notice_ACallerThatSaysNothingOfItsPhase_IsJudgedAsBeforeAsync() {
    var (self, _) = _pair();
    await using var conn = await _openAsync();
    await _registerAsync(conn, self, nameof(LifecyclePhase.Migrating));
    await _seedOutboxAsync(conn, partitions: 1);

    var (_, notices) = await _claimAsync(conn, self, phase: null);

    await Assert.That(notices).DoesNotContain(REGISTRATION_STALE)
      .Because("an older build passes no phase, and its fresh registration is all the claim asked of it before");
  }

  [Test]
  [Arguments("AcceptingCommands", true)]
  [Arguments("Migrating", false)]
  public async Task Coordinator_PassesTheCallersPhase_SoARowRecordingAnotherIsReportedStaleAsync(
      string callerPhase, bool expectedStale) {
    var (self, _) = _pair();
    await using (var conn = await _openAsync()) {
      await _registerAsync(conn, self, nameof(LifecyclePhase.Migrating));
      await _seedOutboxAsync(conn, partitions: 1);
    }
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var batch = await coordinator.ClaimWorkAsync(new Whizbang.Core.Messaging.ClaimWorkRequest(
      self, "test", "test-host", 1, MaxStreams: 10, LifecyclePhase: callerPhase));

    await Assert.That(batch.InstanceRegistrationStale).IsEqualTo(expectedStale);
  }

  [Test]
  public async Task ARegistration_NoLongerMovesARecordedPhase_TheStageRecordMakesTheRowCurrentAsync() {
    var (self, _) = _pair();
    await using (var conn = await _openAsync()) {
      await _registerAsync(conn, self, nameof(LifecyclePhase.Migrating));
      await _seedOutboxAsync(conn, partitions: 1);
    }
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    var request = new Whizbang.Core.Messaging.ClaimWorkRequest(
      self, "test", "test-host", 1, MaxStreams: 10, LifecyclePhase: nameof(LifecyclePhase.AcceptingCommands));
    var first = await coordinator.ClaimWorkAsync(request);

    _ = await coordinator.RecordHeartbeatAsync(new Whizbang.Core.Messaging.HeartbeatRequest(
      self, "test", "test-host", 1, LifecyclePhase: nameof(LifecyclePhase.AcceptingCommands)));
    var afterTheBeat = await coordinator.ClaimWorkAsync(request);
    _ = await coordinator.RecordInstanceTransitionAsync(new Whizbang.Core.Startup.InstanceTransition(
      self, Whizbang.Core.Startup.StartupStage.AcceptingCommands, "accepting", LifecyclePhase.AcceptingCommands, null));
    var afterTheTransition = await coordinator.ClaimWorkAsync(request);

    await Assert.That(first.InstanceRegistrationStale).IsTrue();
    await Assert.That(afterTheBeat.InstanceRegistrationStale).IsTrue()
      .Because("278 (#1636): a registration never overwrites a recorded phase, which is why the claim loop no longer "
        + "announces one");
    await Assert.That(afterTheTransition.InstanceRegistrationStale).IsFalse()
      .Because("the stage record, the one writer of the phase, is what makes a ready instance's row current");
  }

  // --- the bootstrap registration --------------------------------------------------------------

  [Test]
  public async Task BootstrapRegistration_RecordsTheInstanceAsConnecting_SoItIsNotACandidateAsync() {
    var instance = new ServiceInstanceProvider(Guid.CreateVersion7(), "test", "test-host", processId: 1);

    await SchemaBootstrapPhase.RegisterInstanceAsync(() => new NpgsqlConnection(ConnectionString), "public", instance);

    await using var conn = await _openAsync();
    await Assert.That(await _phaseAsync(conn, instance.InstanceId)).IsEqualTo(nameof(LifecyclePhase.Connecting))
      .Because("the bootstrap registers an instance at the end of Preflight (#1636), before its migration pass; a row "
        + "without a phase would read as an older build, eligible for work it cannot yet claim");
    await Assert.That(await _candidatesAsync(conn)).DoesNotContain(instance.InstanceId);
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

  /// <summary>A registration row recording <paramref name="phase"/> (null: none), in the given mode, with a heartbeat of the given age.</summary>
  private static async Task _registerAsync(
      NpgsqlConnection conn, Guid instanceId, string? phase, string mode = InstanceConnectionModes.POOLED, TimeSpan age = default) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, connection_mode, lifecycle_phase)
      VALUES (@id, 'test', 'test-host', 1, NOW() - @age, NOW() - INTERVAL '2 hours', @mode, @phase)";
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue(nameof(age), age);
    cmd.Parameters.AddWithValue(nameof(mode), mode);
    cmd.Parameters.AddWithValue(nameof(phase), (object?)phase ?? DBNull.Value);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>A transition as the instance-state run control records it.</summary>
  private static async Task _recordPhaseAsync(NpgsqlConnection conn, Guid instanceId, string phase) {
    var recorded = await _scalarAsync<bool>(conn, $"SELECT record_instance_state('{instanceId}'::uuid, '{phase}')");
    if (!recorded) {
      throw new InvalidOperationException($"the phase of {instanceId} was not recorded");
    }
  }

  private static async Task<string?> _phaseAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT lifecycle_phase FROM wh_service_instances WHERE instance_id = @id";
    cmd.Parameters.AddWithValue("id", instanceId);
    return await cmd.ExecuteScalarAsync() as string;
  }

  private static async Task<bool> _isEligibleAsync(NpgsqlConnection conn, Guid instanceId) =>
    await _scalarAsync<bool>(conn,
      $"SELECT wh_instance_is_eligible_for_work(si) FROM wh_service_instances si WHERE si.instance_id = '{instanceId}'");

  private static async Task<List<Guid>> _candidatesAsync(NpgsqlConnection conn) {
    var candidates = new List<Guid>();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT instance_id FROM wh_partition_assignment_candidates()";
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      candidates.Add(reader.GetGuid(0));
    }
    return candidates;
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
  /// after it, and the notices it raised. <paramref name="phase"/> is the phase the caller says it is in (null: none,
  /// as an older build passes).
  /// </summary>
  private static async Task<(List<int> Partitions, List<string> Notices)> _claimAsync(
      NpgsqlConnection conn, Guid instanceId, string? phase) {
    var notices = new List<string>();
    void OnNotice(object? sender, NpgsqlNoticeEventArgs e) => notices.Add(e.Notice.MessageText);
    var partitions = new List<int>();
    await using var tx = await conn.BeginTransactionAsync();
    conn.Notice += OnNotice;
    try {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = phase is null
        ? @"SELECT count(*) FROM claim_work(
              p_instance_id => @inst, p_service_name => 'test', p_host_name => 'test-host', p_process_id => 1,
              p_max_streams => 100, p_partition_count => 10000, p_lease_seconds => 300)"
        : @"SELECT count(*) FROM claim_work(
              p_instance_id => @inst, p_service_name => 'test', p_host_name => 'test-host', p_process_id => 1,
              p_max_streams => 100, p_partition_count => 10000, p_lease_seconds => 300, p_lifecycle_phase => @phase)";
      cmd.Parameters.AddWithValue("inst", instanceId);
      if (phase is not null) {
        cmd.Parameters.AddWithValue(nameof(phase), phase);
      }
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
