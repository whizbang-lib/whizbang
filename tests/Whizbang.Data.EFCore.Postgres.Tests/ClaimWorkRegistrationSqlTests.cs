// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A claim never writes the calling instance's registration row, so a heartbeat or a registration call can never
/// wait behind a claim's transaction (#1226, migration 196).
/// </summary>
/// <remarks>
/// <para>
/// <c>claim_work</c> used to repair its own <c>wh_service_instances</c> row inside the claim's transaction whenever the
/// row was older than its stale cutoff, and <c>record_heartbeat</c> for the same instance then waited for the whole
/// claim. Measured: 5.6 s behind a claim held open for 6 s. A slow claim made the heartbeat late and a late heartbeat
/// made the next claim hold the row again.
/// </para>
/// <para>
/// The claim now only reads the row. When the row is missing or stale it ranks the caller as live for its own claim
/// (the caller is demonstrably alive: it is executing the claim) and says so with a notice, and the caller registers
/// afterwards, in a statement of its own.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/196_ClaimLeavesRegistrationToTheCaller.sql</code-under-test>
[Category("Shard2")]
public class ClaimWorkRegistrationSqlTests : EFCoreTestBase {
  private const string REGISTRATION_STALE = "whizbang.instance_registration_stale=true";

  /// <summary>Sorts after <see cref="_peer"/>, so among two live instances it is rank 1 of 2.</summary>
  private static readonly Guid _self = new("ffffffff-0000-0000-0000-0000000000f1");
  private static readonly Guid _peer = new("00000000-0000-0000-0000-0000000000b2");

  /// <summary>The partitions rank 1 of 2 takes out of ten.</summary>
  private static readonly int[] _oddPartitions = [1, 3, 5, 7, 9];
  private static readonly int[] _allPartitions = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];

  [Test]
  public async Task ClaimWork_HeldOpen_AHeartbeatForTheSameInstanceDoesNotWaitAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _registerAsync(conn, _self, stale: true);
    await _seedOutboxAsync(conn, partitions: 1);

    await using var claimer = await _newConnectionAsync();
    await _execAsync(claimer, "BEGIN");
    _ = await _claimAsync(claimer, _self);

    // The claim's transaction is still open. A statement that has to wait for it fails at the lock timeout
    // instead of completing, so completing is the proof that nothing waited.
    await using var beater = await _newConnectionAsync();
    await _execAsync(beater, "SET lock_timeout = '250ms'");
    var accepted = await _scalarAsync<bool>(beater, $@"
      SELECT record_heartbeat('{_self}'::uuid, 'test', 'test-host', 1, '{{}}'::jsonb, 'Running', '1.0.0', 150)");

    await Assert.That(accepted).IsTrue()
      .Because("a heartbeat for the instance whose claim is still open must complete without waiting for that claim: "
        + "a liveness write that shares a row lock with the bulk path is late whenever the bulk path is slow");

    await _execAsync(claimer, "ROLLBACK");
  }

  [Test]
  public async Task ClaimWork_HeldOpen_TheRegistrationCallDoesNotWaitAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _registerAsync(conn, _self, stale: true);
    await _seedOutboxAsync(conn, partitions: 1);

    await using var claimer = await _newConnectionAsync();
    await _execAsync(claimer, "BEGIN");
    _ = await _claimAsync(claimer, _self);

    // The registration the claim worker sends when the claim reports a stale row: identity only.
    await using var registrar = await _newConnectionAsync();
    await _execAsync(registrar, "SET lock_timeout = '250ms'");
    var accepted = await _scalarAsync<bool>(registrar,
      $"SELECT record_heartbeat('{_self}'::uuid, 'test', 'test-host', 1)");

    await Assert.That(accepted).IsTrue()
      .Because("the registration the claim asks for runs on a connection of its own and must not wait for the claim");

    await _execAsync(claimer, "ROLLBACK");
  }

  /// <summary>
  /// A heartbeat that finds a stale peer reaps it inline, and the reap releases the peer's leases with plain updates
  /// of the work tables. When a claim has just taken one of those rows, the reap waits for the claim, and the
  /// heartbeat with it (#1217, migration 199).
  /// </summary>
  [Test]
  public async Task ClaimWork_HeldOpen_AHeartbeatThatFindsAStalePeerDoesNotWaitAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _registerAsync(conn, _self, stale: false);
    await _registerAsync(conn, _peer, stale: true);
    // The dead peer's lease has lapsed, so the claim takes its row.
    await _execAsync(conn, $@"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id,
         partition_number, instance_id, lease_expiry)
      VALUES (gen_random_uuid(), 'test-topic', 'TestEvent', '{{}}', '{{}}', 0, 1, NOW() - INTERVAL '1 hour',
              gen_random_uuid(), 1, '{_peer}', NOW() - INTERVAL '1 minute')");

    await using var claimer = await _newConnectionAsync();
    await _execAsync(claimer, "BEGIN");
    _ = await _claimAsync(claimer, _self);
    await Assert.That(await _scalarAsync<long>(claimer,
        $"SELECT count(*) FROM wh_outbox WHERE instance_id = '{_self}'")).IsEqualTo(1L)
      .Because("the claim has to hold the dead peer's row for the reap to have anything to wait on");

    await using var beater = await _newConnectionAsync();
    await _execAsync(beater, "SET lock_timeout = '250ms'");
    var accepted = await _scalarAsync<bool>(beater,
      $"SELECT record_heartbeat('{_self}'::uuid, 'test', 'test-host', 1, '{{}}'::jsonb, 'Running', '1.0.0', 150)");

    await Assert.That(accepted).IsTrue()
      .Because("the reap inside a heartbeat is opportunistic (maintenance reaps too), so it must step aside rather "
        + "than make the heartbeat wait for a claim");

    await _execAsync(claimer, "ROLLBACK");
    _ = await _scalarAsync<bool>(beater,
      $"SELECT record_heartbeat('{_self}'::uuid, 'test', 'test-host', 1, '{{}}'::jsonb, 'Running', '1.0.0', 150)");
    await Assert.That(await _scalarAsync<long>(beater,
        $"SELECT count(*) FROM wh_service_instances WHERE instance_id = '{_peer}'")).IsEqualTo(0L)
      .Because("once nothing holds the peer's rows, the next heartbeat reaps it as before");
  }

  [Test]
  public async Task ClaimWork_StaleRegistration_LeavesTheRowAlone_AndAsksForRegistrationAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _registerAsync(conn, _self, stale: true);
    await _seedOutboxAsync(conn, partitions: 1);
    var before = await _heartbeatAtAsync(conn, _self);

    var (_, notices) = await _claimAsync(conn, _self);

    await Assert.That(await _heartbeatAtAsync(conn, _self)).IsEqualTo(before)
      .Because("the claim no longer writes the caller's registration row; writing it inside the claim's transaction is "
        + "what made a heartbeat wait for the whole claim");
    await Assert.That(notices).Contains(REGISTRATION_STALE)
      .Because("the caller registers only when needed, and the claim is what can tell it the row has gone stale");
  }

  [Test]
  public async Task ClaimWork_MissingRegistration_DoesNotInsertIt_AndAsksForRegistrationAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _seedOutboxAsync(conn, partitions: 1);

    var (_, notices) = await _claimAsync(conn, _self);

    await Assert.That(await _scalarAsync<long>(conn,
        $"SELECT count(*) FROM wh_service_instances WHERE instance_id = '{_self}'")).IsEqualTo(0L)
      .Because("registration belongs to the caller now, in a statement of its own");
    await Assert.That(notices).Contains(REGISTRATION_STALE)
      .Because("a reaped row is the case the old self-heal existed for, so the caller must be told to put it back");
  }

  [Test]
  public async Task ClaimWork_FreshRegistration_DoesNotAskForRegistrationAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _registerAsync(conn, _self, stale: false);
    await _seedOutboxAsync(conn, partitions: 1);

    var (_, notices) = await _claimAsync(conn, _self);

    await Assert.That(notices).DoesNotContain(REGISTRATION_STALE)
      .Because("registration runs only when needed; a claim on a fresh row must not cause a write per poll");
  }

  [Test]
  public async Task ClaimWork_NothingToClaim_DoesNotAskForRegistrationAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);

    var (_, notices) = await _claimAsync(conn, _self);

    await Assert.That(notices).DoesNotContain(REGISTRATION_STALE)
      .Because("an empty store returns before ranking, as it always has; the heartbeat keeps an idle instance registered");
  }

  [Test]
  public async Task ClaimWork_StaleOwnRegistration_StillRanksItselfAmongTheLiveAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _registerAsync(conn, _peer, stale: false);
    await _registerAsync(conn, _self, stale: true);
    await _seedOutboxAsync(conn, partitions: 10);

    var (partitions, _) = await _claimAsync(conn, _self);

    await Assert.That(partitions).IsEquivalentTo(_oddPartitions)
      .Because("a caller with a stale row is still alive and must rank itself as one of two live instances (rank 1 of "
        + "2, the odd partitions); ranked as absent it would fall back to a solo rank and claim its peer's partitions "
        + "as well");
  }

  [Test]
  public async Task ClaimWork_MissingOwnRegistration_StillRanksItselfAmongTheLiveAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _registerAsync(conn, _peer, stale: false);
    await _seedOutboxAsync(conn, partitions: 10);

    var (partitions, _) = await _claimAsync(conn, _self);

    await Assert.That(partitions).IsEquivalentTo(_oddPartitions)
      .Because("a caller whose row was reaped is ranked with the live peer, not on its own");
  }

  [Test]
  public async Task ClaimWork_StalePeer_IsStillLeftOutOfTheRankAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _registerAsync(conn, _peer, stale: true);
    await _registerAsync(conn, _self, stale: false);
    await _seedOutboxAsync(conn, partitions: 10);

    var (partitions, _) = await _claimAsync(conn, _self);

    await Assert.That(partitions).IsEquivalentTo(_allPartitions)
      .Because("only the caller is counted live without a fresh row; a stale peer still drops out, so the caller takes "
        + "every partition");
  }

  [Test]
  public async Task Coordinator_StaleRegistration_IsReportedOnTheBatchAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _registerAsync(conn, _self, stale: true);
    await _seedOutboxAsync(conn, partitions: 1);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var batch = await coordinator.ClaimWorkAsync(new Whizbang.Core.Messaging.ClaimWorkRequest(
      _self, "test", "test-host", 1, MaxStreams: 10));

    await Assert.That(batch.InstanceRegistrationStale).IsTrue()
      .Because("the claim's in-band notice is how the claim worker learns to register after the claim");
    await Assert.That(batch.OutboxAcquisitionFull).IsFalse()
      .Because("one notice must not be read as the other");
  }

  [Test]
  public async Task Coordinator_FreshRegistration_IsNotReportedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _registerAsync(conn, _self, stale: false);
    await _seedOutboxAsync(conn, partitions: 1);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

    var batch = await coordinator.ClaimWorkAsync(new Whizbang.Core.Messaging.ClaimWorkRequest(
      _self, "test", "test-host", 1, MaxStreams: 10));

    await Assert.That(batch.InstanceRegistrationStale).IsFalse();
  }

  // ------------------------------------------------------------------

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  private async Task<NpgsqlConnection> _newConnectionAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return (T)Convert.ChangeType(await cmd.ExecuteScalarAsync(), typeof(T), CultureInfo.InvariantCulture)!;
  }

  private static async Task<DateTimeOffset> _heartbeatAtAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT last_heartbeat_at FROM wh_service_instances WHERE instance_id = @id";
    cmd.Parameters.AddWithValue("id", instanceId);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return await reader.GetFieldValueAsync<DateTimeOffset>(0);
  }

  /// <summary>A registration row, fresh or an hour past the claim's 30-second stale cutoff.</summary>
  private static async Task _registerAsync(NpgsqlConnection conn, Guid instanceId, bool stale) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
      VALUES (@id, 'test', 'test-host', 1, NOW() - @age, NOW() - INTERVAL '2 hours')";
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue("age", stale ? TimeSpan.FromHours(1) : TimeSpan.Zero);
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

  /// <summary>One claim, returning the partitions of the outbox rows the caller holds after it and the notices it raised.</summary>
  private static async Task<(List<int> Partitions, List<string> Notices)> _claimAsync(
      NpgsqlConnection conn, Guid instanceId) {
    var notices = new List<string>();
    void OnNotice(object? sender, NpgsqlNoticeEventArgs e) => notices.Add(e.Notice.MessageText);
    conn.Notice += OnNotice;
    var partitions = new List<int>();
    try {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = @"
        SELECT count(*) FROM claim_work(
          p_instance_id => @inst, p_service_name => 'test', p_host_name => 'test-host', p_process_id => 1,
          p_max_streams => 100, p_partition_count => 10000, p_lease_seconds => 300)";
      cmd.Parameters.AddWithValue("inst", instanceId);
      await cmd.ExecuteNonQueryAsync();
    } finally {
      conn.Notice -= OnNotice;
    }
    await using var leased = conn.CreateCommand();
    leased.CommandText = "SELECT partition_number FROM wh_outbox WHERE instance_id = @inst ORDER BY partition_number";
    leased.Parameters.AddWithValue("inst", instanceId);
    await using var reader = await leased.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      partitions.Add(reader.GetInt32(0));
    }
    return (partitions, notices);
  }
}
