// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.RunControl;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 354 (#2126) through the EF Core driver: every exit is one row of <c>wh_instance_exits</c>, written with the
/// instance's eviction tombstone read in the same call, and a peer's reap records on the tombstone which instance
/// evicted it and what the one liveness rule judged it by (its last heartbeat and its window).
/// </summary>
/// <remarks>Ages are on the database's clock (<c>NOW() - age</c>), never the host's.</remarks>
/// <docs>operations/startup/instance-exits#the-exit-record</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/354_EveryExitIsRecorded.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/InstanceExitSql.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class InstanceExitRecordSqlTests : EFCoreTestBase {
  [Test]
  public async Task AnExit_IsRecordedWithEveryFact_AndNoTombstoneAsync(CancellationToken cancellationToken) {
    var instance = (Guid)TrackedGuid.New();
    var exit = _exit(instance, ExitCause.StartupFailure) with {
      ExceptionType = "StartupAssessmentException",
      ExceptionMessage = "the version record could not be read",
      HeldRoles = ["maintainer", "migrator"],
      HeldLeases = 4,
      InHand = 2,
    };

    var result = await _recordAsync(exit, cancellationToken);

    await Assert.That(result.Recorded).IsTrue();
    await Assert.That(result.Eviction).IsNull().Because("the instance was never evicted");
    await Assert.That(result.NotRecordedBecause).IsNull();
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand(
      """
      SELECT service_name, host_name, process_id, exit_cause, exit_code, intended, reason, exception_type, exception_message,
             startup_stage, lifecycle_phase, uptime_seconds, seconds_since_last_heartbeat, held_roles, held_leases, in_hand,
             library_version, evicted_at IS NULL, exited_at > NOW() - INTERVAL '1 minute'
      FROM wh_instance_exits WHERE instance_id = $1
      """, conn);
    cmd.Parameters.AddWithValue(instance);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    await Assert.That(await reader.ReadAsync(cancellationToken)).IsTrue();
    await Assert.That(reader.GetString(0)).IsEqualTo("orders-svc");
    await Assert.That(reader.GetString(1)).IsEqualTo("orders-pod-7");
    await Assert.That(reader.GetInt32(2)).IsEqualTo(4242);
    await Assert.That(reader.GetString(3)).IsEqualTo("StartupFailure").Because("the column holds the stable ExitCause name");
    await Assert.That(reader.GetInt32(4)).IsEqualTo(4);
    await Assert.That(reader.GetBoolean(5)).IsFalse();
    await Assert.That(reader.GetString(6)).IsEqualTo("a reason");
    await Assert.That(reader.GetString(7)).IsEqualTo("StartupAssessmentException");
    await Assert.That(reader.GetString(8)).IsEqualTo("the version record could not be read");
    await Assert.That(reader.GetString(9)).IsEqualTo("Migrate");
    await Assert.That(reader.GetString(10)).IsEqualTo("Migrating");
    await Assert.That(reader.GetDouble(11)).IsEqualTo(63.5);
    await Assert.That(reader.GetDouble(12)).IsEqualTo(12.25);
    await Assert.That(reader.GetFieldValue<string[]>(13)).IsEquivalentTo(["maintainer", "migrator"]);
    await Assert.That(reader.GetInt32(14)).IsEqualTo(4);
    await Assert.That(reader.GetInt32(15)).IsEqualTo(2);
    await Assert.That(reader.GetString(16)).IsEqualTo("9.9.9");
    await Assert.That(reader.GetBoolean(17)).IsTrue();
    await Assert.That(reader.GetBoolean(18)).IsTrue().Because("the row is stamped on the database's clock");
    await Assert.That(await reader.ReadAsync(cancellationToken)).IsFalse().Because("one exit is one row");
  }

  [Test]
  public async Task AnExitWithNothingOptional_IsRecorded_WithTheGapsLeftEmptyAsync(CancellationToken cancellationToken) {
    var instance = (Guid)TrackedGuid.New();
    var exit = new InstanceExitRecord(instance, "orders-svc", "orders-pod-7", 1, ExitCause.HostShutdown, 0, true, "stop",
      null, null, null, null, null, null, [], 0, 0, null);

    var result = await _recordAsync(exit, cancellationToken);

    await Assert.That(result.Recorded).IsTrue();
    await Assert.That(await _scalarAsync<long>(
        $"SELECT count(*) FROM wh_instance_exits WHERE instance_id = '{instance}' AND intended AND exit_code = 0 "
        + "AND startup_stage IS NULL AND uptime_seconds IS NULL AND library_version IS NULL AND cardinality(held_roles) = 0",
        cancellationToken))
      .IsEqualTo(1L);
  }

  [Test]
  public async Task AnEvictedInstancesExit_RecordsAndReturnsTheTombstoneAsync(CancellationToken cancellationToken) {
    var instance = (Guid)TrackedGuid.New();
    var peer = (Guid)TrackedGuid.New();
    await _executeAsync(
      "INSERT INTO wh_instance_evictions (instance_id, evicted_at, reason, evicted_by, last_heartbeat_at, liveness_window_seconds) "
      + $"VALUES ('{instance}', NOW() - INTERVAL '5 seconds', 'not live (wh_instance_liveness)', '{peer}', "
      + "NOW() - INTERVAL '205 seconds', 150)", cancellationToken);
    var (evictedAt, lastHeartbeatAt) = await _tombstoneTimesAsync(instance, cancellationToken);

    var result = await _recordAsync(_exit(instance, ExitCause.ClusterEviction), cancellationToken);

    await Assert.That(result.Recorded).IsTrue();
    var eviction = result.Eviction!;
    await Assert.That(eviction.EvictedAt).IsEqualTo(evictedAt);
    await Assert.That(eviction.EvictedBy).IsEqualTo(peer);
    await Assert.That(eviction.Reason).IsEqualTo("not live (wh_instance_liveness)");
    await Assert.That(eviction.LastHeartbeatAt).IsEqualTo(lastHeartbeatAt);
    await Assert.That(eviction.LivenessWindowSeconds).IsEqualTo(150);
    await Assert.That(await _scalarAsync<long>(
        $"SELECT count(*) FROM wh_instance_exits WHERE instance_id = '{instance}' AND exit_cause = 'ClusterEviction' "
        + $"AND exit_code = 3 AND evicted_by = '{peer}' AND evicted_liveness_window_seconds = 150 "
        + "AND eviction_reason = 'not live (wh_instance_liveness)' AND evicted_at IS NOT NULL AND evicted_last_heartbeat_at IS NOT NULL",
        cancellationToken))
      .IsEqualTo(1L).Because("the row carries the tombstone's account, so the record outlives the tombstone's retention");
  }

  [Test]
  public async Task ATombstoneWrittenBeforeThisRelease_IsReturnedWithItsGapsAsync(CancellationToken cancellationToken) {
    var instance = (Guid)TrackedGuid.New();
    await _executeAsync(
      $"INSERT INTO wh_instance_evictions (instance_id, evicted_at, reason) VALUES ('{instance}', NOW(), NULL)", cancellationToken);

    var result = await _recordAsync(_exit(instance, ExitCause.ClusterEviction), cancellationToken);

    var eviction = result.Eviction!;
    await Assert.That(eviction.EvictedBy).IsNull();
    await Assert.That(eviction.Reason).IsNull();
    await Assert.That(eviction.LastHeartbeatAt).IsNull();
    await Assert.That(eviction.LivenessWindowSeconds).IsNull();
  }

  [Test]
  public async Task AWrite_RemovesRowsOlderThanThirtyDaysAsync(CancellationToken cancellationToken) {
    var old = (Guid)TrackedGuid.New();
    var recent = (Guid)TrackedGuid.New();
    foreach (var (id, age) in new[] { (old, "31 days"), (recent, "29 days") }) {
      await _executeAsync(
        "INSERT INTO wh_instance_exits (instance_id, service_name, host_name, process_id, exit_cause, exit_code, intended, reason, exited_at) "
        + $"VALUES ('{id}', 'svc', 'host', 1, 'HostShutdown', 0, TRUE, 'stop', NOW() - INTERVAL '{age}')", cancellationToken);
    }

    _ = await _recordAsync(_exit((Guid)TrackedGuid.New(), ExitCause.HostShutdown), cancellationToken);

    await Assert.That(await _scalarAsync<long>($"SELECT count(*) FROM wh_instance_exits WHERE instance_id = '{old}'", cancellationToken))
      .IsEqualTo(0L);
    await Assert.That(await _scalarAsync<long>($"SELECT count(*) FROM wh_instance_exits WHERE instance_id = '{recent}'", cancellationToken))
      .IsEqualTo(1L);
  }

  [Test]
  public async Task APeersReap_RecordsTheReaper_AndTheHeartbeatAndWindowItJudgedAsync(CancellationToken cancellationToken) {
    var victim = (Guid)TrackedGuid.New();
    var peer = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    IWorkCoordinator coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      context, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(victim, "victim-svc", "victim-host", 1, StaleThresholdSeconds: 150),
      cancellationToken);
    await _executeAsync(
      $"UPDATE wh_service_instances SET last_heartbeat_at = NOW() - INTERVAL '200 seconds' WHERE instance_id = '{victim}'",
      cancellationToken);
    var lastHeartbeat = await _scalarAsync<DateTime>(
      $"SELECT last_heartbeat_at FROM wh_service_instances WHERE instance_id = '{victim}'", cancellationToken);

    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(peer, "peer-svc", "peer-host", 2, StaleThresholdSeconds: 150),
      cancellationToken);

    await Assert.That(await _scalarAsync<string>(
        $"SELECT evicted_by::text FROM wh_instance_evictions WHERE instance_id = '{victim}'", cancellationToken))
      .IsEqualTo(peer.ToString()).Because("the peer whose heartbeat ran the reap is recorded as the evicting peer");
    await Assert.That(await _scalarAsync<DateTime>(
        $"SELECT last_heartbeat_at FROM wh_instance_evictions WHERE instance_id = '{victim}'", cancellationToken))
      .IsEqualTo(lastHeartbeat);
    await Assert.That(await _scalarAsync<int>(
        $"SELECT liveness_window_seconds FROM wh_instance_evictions WHERE instance_id = '{victim}'", cancellationToken))
      .IsEqualTo(150);
    await Assert.That(await _scalarAsync<long>(
        "SELECT count(*) FROM wh_instance_evictions WHERE evicted_by IS NOT NULL AND evicted_by <> '" + peer + "'",
        cancellationToken))
      .IsEqualTo(0L).Because("the reaper's name is local to its own reap");
  }

  [Test]
  public async Task AReapByAnyOtherCaller_RecordsNoReaperAsync(CancellationToken cancellationToken) {
    var victim = (Guid)TrackedGuid.New();
    await _executeAsync(
      "INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, "
      + $"liveness_window_seconds) VALUES ('{victim}', 'svc', 'host', 1, NOW() - INTERVAL '100 seconds', NOW(), 60)",
      cancellationToken);

    await _executeAsync("SELECT * FROM cleanup_stale_instances(NULL, NULL)", cancellationToken);

    await Assert.That(await _scalarAsync<long>(
        $"SELECT count(*) FROM wh_instance_evictions WHERE instance_id = '{victim}' AND evicted_by IS NULL "
        + "AND liveness_window_seconds = 60 AND last_heartbeat_at IS NOT NULL", cancellationToken))
      .IsEqualTo(1L);
  }

  // ------------------------------------------------------------------

  private static InstanceExitRecord _exit(Guid instance, ExitCause cause) =>
    new(instance, "orders-svc", "orders-pod-7", 4242, cause, ExitCauses.ExitCodeOf(cause), ExitCauses.IsIntended(cause),
      "a reason", null, null, "Migrate", "Migrating", 63.5, 12.25, [], 0, 0, "9.9.9");

  private async Task<InstanceExitRecordResult> _recordAsync(InstanceExitRecord exit, CancellationToken cancellationToken) {
    await using var context = CreateDbContext();
    IWorkCoordinator coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      context, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    return await coordinator.RecordInstanceExitAsync(exit, cancellationToken);
  }

  private async Task<(DateTimeOffset EvictedAt, DateTimeOffset LastHeartbeatAt)> _tombstoneTimesAsync(
      Guid instance, CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand(
      "SELECT evicted_at, last_heartbeat_at FROM wh_instance_evictions WHERE instance_id = $1", conn);
    cmd.Parameters.AddWithValue(instance);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    await reader.ReadAsync(cancellationToken);
    return (reader.GetFieldValue<DateTimeOffset>(0), reader.GetFieldValue<DateTimeOffset>(1));
  }

  private async Task<NpgsqlConnection> _openAsync(CancellationToken cancellationToken) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    return conn;
  }

  private async Task _executeAsync(string sql, CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  private async Task<T> _scalarAsync<T>(string sql, CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand(sql, conn);
    return (T)(await cmd.ExecuteScalarAsync(cancellationToken))!;
  }
}
