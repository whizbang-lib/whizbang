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
/// Migration 316 (#2027): the instance row is the one durable record, judged by ONE liveness rule
/// (<c>wh_instance_liveness</c>). An instance is live while its heartbeat is newer than the window it declares for
/// itself, or while it is a direct instance holding its alive-lock inside the definitive-dead cutoff. The row also
/// records the application version beside the library version.
/// </summary>
/// <remarks>
/// Ages are relative to the database's clock (<c>NOW() - age</c>), never the host's. A held alive-lock is a session of
/// its own that took it, as an instance's shared notify connection does; releasing it is <c>pg_advisory_unlock</c> on
/// that session, which returns once the lock is gone.
/// </remarks>
/// <docs>fundamentals/workers/instance-liveness</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/316_OneDurableInstanceRecord.sql</code-under-test>
[Category("Shard3")]
public class InstanceLivenessRuleSqlTests : EFCoreTestBase {
  // --- the rule --------------------------------------------------------------------------------

  [Test]
  public async Task Liveness_AFreshHeartbeatWithinTheInstancesOwnWindow_IsLiveAsync() {
    var instance = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, InstanceConnectionModes.POOLED, TimeSpan.FromSeconds(100), windowSeconds: 150);

    await Assert.That(await _isLiveAsync(conn, instance)).IsTrue()
      .Because("100 seconds is inside the 150 second window the instance declared: one late beat is not a death");
  }

  [Test]
  public async Task Liveness_AHeartbeatPastTheInstancesOwnWindow_IsNotLiveAsync() {
    var instance = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, InstanceConnectionModes.POOLED, TimeSpan.FromSeconds(100), windowSeconds: 90);

    await Assert.That(await _isLiveAsync(conn, instance)).IsFalse();
  }

  [Test]
  public async Task Liveness_ARowThatRecordsNoWindow_IsJudgedByThirtySecondsAsync() {
    var fresh = (Guid)TrackedGuid.New();
    var stale = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, fresh, InstanceConnectionModes.POOLED, TimeSpan.FromSeconds(20), windowSeconds: null);
    await _registerAsync(conn, stale, InstanceConnectionModes.POOLED, TimeSpan.FromSeconds(40), windowSeconds: null);

    await Assert.That(await _isLiveAsync(conn, fresh)).IsTrue();
    await Assert.That(await _isLiveAsync(conn, stale)).IsFalse()
      .Because("an older build records no window and is judged by the 30 seconds the claim always used");
  }

  [Test]
  public async Task Liveness_ADirectInstanceHoldingItsAliveLock_IsLivePastItsWindowAsync() {
    var instance = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, InstanceConnectionModes.DIRECT, TimeSpan.FromSeconds(200), windowSeconds: 150);
    await using var holder = await _holdAliveLockAsync(instance);

    await Assert.That(await _isLiveAsync(conn, instance)).IsTrue()
      .Because("a direct instance holding its alive-lock is live inside the definitive-dead cutoff whatever its heartbeat");
    await Assert.That(await _liveIdsAsync(conn)).Contains(instance);
  }

  [Test]
  public async Task Liveness_ADirectInstanceWhoseLockIsReleased_IsJudgedByItsWindowAsync() {
    var instance = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, InstanceConnectionModes.DIRECT, TimeSpan.FromSeconds(200), windowSeconds: 150);
    await using var holder = await _holdAliveLockAsync(instance);
    await _releaseAliveLockAsync(holder, instance);

    await Assert.That(await _liveIdsAsync(conn)).DoesNotContain(instance);
  }

  [Test]
  public async Task Liveness_ADirectInstanceHoldingItsAliveLock_PastTheDefinitiveCutoff_IsNotLiveAsync() {
    var instance = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    // Twice a 150 second window is 300 seconds, which is also the five-minute floor.
    await _registerAsync(conn, instance, InstanceConnectionModes.DIRECT, TimeSpan.FromSeconds(310), windowSeconds: 150);
    await using var holder = await _holdAliveLockAsync(instance);

    await Assert.That(await _isLiveAsync(conn, instance)).IsFalse()
      .Because("a lock can outlive its process on a half-open connection; past the definitive cutoff it is not believed");
  }

  [Test]
  public async Task Liveness_APooledInstanceHoldingALock_IsJudgedByItsHeartbeatAloneAsync() {
    var instance = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, InstanceConnectionModes.POOLED, TimeSpan.FromSeconds(200), windowSeconds: 150);
    await using var holder = await _holdAliveLockAsync(instance);

    await Assert.That(await _isLiveAsync(conn, instance)).IsFalse();
  }

  [Test]
  public async Task IsLive_AnInstanceWithNoRow_IsNotLiveAsync() {
    await using var conn = await _openAsync();

    await Assert.That(await _isLiveAsync(conn, (Guid)TrackedGuid.New())).IsFalse();
  }

  [Test]
  public async Task IsInstanceAlive_DelegatesToTheRule_WhateverThresholdTheCallerPassesAsync() {
    var instance = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, InstanceConnectionModes.POOLED, TimeSpan.FromSeconds(100), windowSeconds: 150);

    // An older caller passing its own 30 second threshold no longer gets a definition of its own.
    await Assert.That(await _scalarAsync<bool>(conn, $"SELECT is_instance_alive('{instance}'::uuid, 30)")).IsTrue();
    await Assert.That(await _scalarAsync<bool>(conn, $"SELECT is_instance_alive('{instance}'::uuid, 9999)")).IsTrue();
  }

  // --- the record --------------------------------------------------------------------------------

  [Test]
  public async Task Heartbeat_RecordsTheWindowAndTheApplicationVersionAsync() {
    var instance = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();

    await _heartbeatAsync(conn, instance, window: "150", appVersion: "'2.4.1'");

    await Assert.That(await _columnAsync(conn, instance, "liveness_window_seconds::text")).IsEqualTo("150");
    await Assert.That(await _columnAsync(conn, instance, "application_version")).IsEqualTo("2.4.1");
  }

  [Test]
  public async Task Heartbeat_ANullWindowKeepsTheRecordedOneAsync() {
    var instance = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, instance, window: "150", appVersion: "NULL");

    await _heartbeatAsync(conn, instance, window: "NULL", appVersion: "NULL");

    await Assert.That(await _columnAsync(conn, instance, "liveness_window_seconds::text")).IsEqualTo("150");
  }

  [Test]
  public async Task Heartbeat_RecordsTheApplicationVersionOnlyAsARegistrationAsync() {
    var instance = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, instance, window: "150", appVersion: "'2.4.1'");

    await _heartbeatAsync(conn, instance, window: "150", appVersion: "'9.9.9'");

    await Assert.That(await _columnAsync(conn, instance, "application_version")).IsEqualTo("2.4.1")
      .Because("one process runs one version; only a new process under the same id records another");
  }

  [Test]
  public async Task Transition_RecordsTheApplicationVersion_AndANullKeepsItAsync() {
    var instance = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, InstanceConnectionModes.POOLED, TimeSpan.Zero, windowSeconds: 150);

    await _scalarAsync<bool>(conn, $"SELECT wh_record_instance_transition('{instance}'::uuid, 10::smallint, 'x', 'Starting', '1.0.0', 'deploy:7')");
    await _scalarAsync<bool>(conn, $"SELECT wh_record_instance_transition('{instance}'::uuid, 20::smallint, 'y', 'Starting', NULL, NULL)");

    await Assert.That(await _columnAsync(conn, instance, "application_version")).IsEqualTo("deploy:7");
  }

  // --- the reap ----------------------------------------------------------------------------------

  [Test]
  public async Task Reap_RemovesExactlyTheInstancesTheRuleSaysAreNotLive_WhateverCutoffTheCallerPassesAsync() {
    var live = (Guid)TrackedGuid.New();
    var dead = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, live, InstanceConnectionModes.POOLED, TimeSpan.FromSeconds(100), windowSeconds: 150);
    await _registerAsync(conn, dead, InstanceConnectionModes.POOLED, TimeSpan.FromSeconds(100), windowSeconds: 90);

    // A caller passing a cutoff that would reap both, as an older release's maintenance pass does.
    await _scalarAsync<long>(conn, "SELECT count(*) FROM cleanup_stale_instances(NOW() - INTERVAL '1 second', NOW() - INTERVAL '2 seconds')");

    await Assert.That(await _isRegisteredAsync(conn, live)).IsTrue();
    await Assert.That(await _isRegisteredAsync(conn, dead)).IsFalse();
  }

  [Test]
  public async Task Reap_ADirectInstanceHoldingItsLockPastTheDefinitiveCutoff_IsRemovedAsync() {
    var instance = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, InstanceConnectionModes.DIRECT, TimeSpan.FromSeconds(310), windowSeconds: 150);
    await using var holder = await _holdAliveLockAsync(instance);

    await _scalarAsync<long>(conn, "SELECT count(*) FROM cleanup_stale_instances(NULL, NULL)");

    await Assert.That(await _isRegisteredAsync(conn, instance)).IsFalse();
  }

  [Test]
  public async Task Reap_WakesOnlyLiveInstancesAsync() {
    var dead = (Guid)TrackedGuid.New();
    var liveByItsLock = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _registerAsync(conn, dead, InstanceConnectionModes.POOLED, TimeSpan.FromSeconds(400), windowSeconds: 150);
    // Past its heartbeat window, live by its held alive-lock: the rule's answer, which a heartbeat cutoff would miss.
    await _registerAsync(conn, liveByItsLock, InstanceConnectionModes.DIRECT, TimeSpan.FromSeconds(200), windowSeconds: 150);
    await using var holder = await _holdAliveLockAsync(liveByItsLock);
    await using var listener = await _openAsync();
    var heard = new List<string>();
    listener.Notification += (_, e) => heard.Add(e.Channel);
    await _executeAsync(listener, $"LISTEN \"wh_work_i_{liveByItsLock}\"; LISTEN \"wh_work_i_{dead}\"");

    await _scalarAsync<long>(conn, "SELECT count(*) FROM cleanup_stale_instances(NOW() - INTERVAL '30 seconds', NULL)");
    // A round trip on the listening session delivers whatever was notified and committed before it.
    await _executeAsync(listener, "SELECT 1");

    await Assert.That(await _isRegisteredAsync(conn, dead)).IsFalse();
    await Assert.That(heard).Contains($"wh_work_i_{liveByItsLock}");
    await Assert.That(heard).DoesNotContain($"wh_work_i_{dead}");
  }

  // ------------------------------------------------------------------

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

  private static async Task _executeAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  private static Task<bool> _isLiveAsync(NpgsqlConnection conn, Guid instance) =>
    _scalarAsync<bool>(conn, $"SELECT wh_instance_is_live('{instance}'::uuid)");

  private static async Task<List<Guid>> _liveIdsAsync(NpgsqlConnection conn) {
    var ids = new List<Guid>();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT instance_id FROM wh_live_instance_ids()";
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  private static async Task<string?> _columnAsync(NpgsqlConnection conn, Guid instance, string column) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {column} FROM wh_service_instances WHERE instance_id = @id";
    cmd.Parameters.AddWithValue("id", instance);
    return await cmd.ExecuteScalarAsync() as string;
  }

  private static Task<bool> _isRegisteredAsync(NpgsqlConnection conn, Guid instanceId) =>
    _scalarAsync<bool>(conn, $"SELECT EXISTS (SELECT 1 FROM wh_service_instances WHERE instance_id = '{instanceId}')");

  /// <summary>A session of its own holding the instance's alive-lock, as its shared notify connection does.</summary>
  private async Task<NpgsqlConnection> _holdAliveLockAsync(Guid instanceId) {
    var holder = await _openAsync();
    if (!await _scalarAsync<bool>(holder, $"SELECT claim_instance_alive_lock('{instanceId}'::uuid)")) {
      throw new InvalidOperationException($"the alive-lock for {instanceId} was already held");
    }
    return holder;
  }

  private static async Task _releaseAliveLockAsync(NpgsqlConnection holder, Guid instanceId) {
    if (!await _scalarAsync<bool>(holder, $"SELECT pg_advisory_unlock(hashtext('wh_instance_alive:' || '{instanceId}'))")) {
      throw new InvalidOperationException($"the alive-lock for {instanceId} was not held by this session");
    }
  }

  /// <summary>A registration row with a connection mode, a heartbeat age by the database clock, and a declared window.</summary>
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

  /// <summary>One heartbeat with the given window and application version, written as SQL literals.</summary>
  private static async Task _heartbeatAsync(NpgsqlConnection conn, Guid instanceId, string window, string appVersion) {
    var accepted = await _scalarAsync<bool>(conn, $@"
      SELECT record_heartbeat('{instanceId}'::uuid, 'test', 'test-host', 1, '{{}}'::jsonb, 'Running', '1.0.0', {window}, 'pooled', {appVersion})");
    if (!accepted) {
      throw new InvalidOperationException($"the heartbeat of {instanceId} was refused");
    }
  }
}
