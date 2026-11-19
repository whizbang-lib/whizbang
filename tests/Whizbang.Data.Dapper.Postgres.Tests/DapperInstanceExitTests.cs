// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.RunControl;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Migration 363 (#2126) through the Dapper driver, as through the EF Core driver: every exit is one row of
/// <c>wh_instance_exits</c>, written with the instance's eviction tombstone read in the same call, and a peer's reap
/// records which instance evicted it and what the one liveness rule judged it by.
/// </summary>
/// <remarks>Ages are on the database's clock (<c>NOW() - age</c>), never the host's.</remarks>
/// <docs>operations/startup/instance-exits#the-exit-record</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/363_EveryExitIsRecorded.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs</code-under-test>
public class DapperInstanceExitTests : PostgresTestBase {
  [Test]
  public async Task AnEvictedInstancesExit_RecordsAndReturnsTheTombstoneAsync() {
    var coordinator = _build();
    var instance = (Guid)TrackedGuid.New();
    var peer = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await conn.ExecuteAsync(
      """
      INSERT INTO wh_instance_evictions (instance_id, evicted_at, reason, evicted_by, last_heartbeat_at, liveness_window_seconds)
      VALUES (@instance, NOW(), 'not live (wh_instance_liveness)', @peer, NOW() - INTERVAL '205 seconds', 150)
      """, new { instance, peer });

    var result = await coordinator.RecordInstanceExitAsync(_exit(instance, ExitCause.ClusterEviction));

    await Assert.That(result.Recorded).IsTrue();
    await Assert.That(result.Eviction!.EvictedBy).IsEqualTo(peer);
    await Assert.That(result.Eviction.LivenessWindowSeconds).IsEqualTo(150);
    await Assert.That(result.Eviction.LastHeartbeatAt.HasValue).IsTrue();
    await Assert.That(await conn.ExecuteScalarAsync<string>(
        "SELECT exit_cause || ' ' || exit_code || ' ' || evicted_by FROM wh_instance_exits WHERE instance_id = @instance",
        new { instance }))
      .IsEqualTo($"ClusterEviction 3 {peer}");
  }

  [Test]
  public async Task AnExitOfAnInstanceNeverEvicted_IsRecordedWithoutATombstoneAsync() {
    var coordinator = _build();
    var instance = (Guid)TrackedGuid.New();

    var result = await coordinator.RecordInstanceExitAsync(_exit(instance, ExitCause.HostShutdown));

    await using var conn = await _openAsync();
    await Assert.That(result.Recorded).IsTrue();
    await Assert.That(result.Eviction).IsNull();
    await Assert.That(await conn.ExecuteScalarAsync<long>(
        "SELECT count(*) FROM wh_instance_exits WHERE instance_id = @instance AND intended AND evicted_at IS NULL", new { instance }))
      .IsEqualTo(1L);
  }

  /// <summary>
  /// This driver applies no bootstrap closure: on a database its pass has not reached yet, the exit record's function
  /// does not exist. The exit is then not recorded, and the answer says why, for the exit's entry to state.
  /// </summary>
  [Test]
  public async Task AnExit_BeforeThePassCreatedItsFunction_IsNotRecorded_AndSaysWhyAsync() {
    var coordinator = _build();
    await using var conn = await _openAsync();
    await conn.ExecuteAsync(
      "DROP FUNCTION IF EXISTS wh_record_instance_exit(uuid, text, text, integer, text, integer, boolean, text, text, text, "
      + "text, text, double precision, double precision, text[], integer, integer, text)");

    var result = await coordinator.RecordInstanceExitAsync(_exit((Guid)TrackedGuid.New(), ExitCause.ClusterEviction));

    await Assert.That(result.Recorded).IsFalse();
    await Assert.That(result.NotRecordedBecause).IsEqualTo(
      "the exit record does not exist on this database yet: the schema pass has not created wh_record_instance_exit");
  }

  /// <summary>Any other failure surfaces, for the exit path to state in its entry.</summary>
  [Test]
  public async Task AnExit_ThatFailsForAnyOtherReason_ThrowsAsync() {
    var coordinator = _build();
    await using var conn = await _openAsync();
    await conn.ExecuteAsync("ALTER TABLE wh_instance_exits RENAME TO wh_instance_exits_moved");

    await Assert.That(async () => await coordinator.RecordInstanceExitAsync(_exit((Guid)TrackedGuid.New(), ExitCause.Crash)))
      .Throws<PostgresException>();
  }

  [Test]
  public async Task APeersReap_RecordsTheReaper_AndTheHeartbeatAndWindowItJudgedAsync() {
    var coordinator = _build();
    var victim = (Guid)TrackedGuid.New();
    var peer = (Guid)TrackedGuid.New();
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(victim, "victim-svc", "victim-host", 1, StaleThresholdSeconds: 150));
    await using var conn = await _openAsync();
    await conn.ExecuteAsync(
      "UPDATE wh_service_instances SET last_heartbeat_at = NOW() - INTERVAL '200 seconds' WHERE instance_id = @victim", new { victim });
    var lastHeartbeat = await conn.ExecuteScalarAsync<DateTime>(
      "SELECT last_heartbeat_at FROM wh_service_instances WHERE instance_id = @victim", new { victim });

    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(peer, "peer-svc", "peer-host", 2, StaleThresholdSeconds: 150));

    await Assert.That(await conn.ExecuteScalarAsync<Guid?>(
        "SELECT evicted_by FROM wh_instance_evictions WHERE instance_id = @victim", new { victim }))
      .IsEqualTo(peer);
    await Assert.That(await conn.ExecuteScalarAsync<DateTime>(
        "SELECT last_heartbeat_at FROM wh_instance_evictions WHERE instance_id = @victim", new { victim }))
      .IsEqualTo(lastHeartbeat);
    await Assert.That(await conn.ExecuteScalarAsync<int>(
        "SELECT liveness_window_seconds FROM wh_instance_evictions WHERE instance_id = @victim", new { victim }))
      .IsEqualTo(150);
  }

  private static InstanceExitRecord _exit(Guid instance, ExitCause cause) =>
    new(instance, "orders-svc", "orders-pod-7", 4242, cause, ExitCauses.ExitCodeOf(cause), ExitCauses.IsIntended(cause),
      "a reason", null, null, "Running", "Running", 10, 1, ["maintainer"], 1, 0, "9.9.9");

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  /// <summary>The Dapper coordinator as the exit path resolves it: through the interface.</summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance",
    Justification = "Calls go through the interface on purpose: that is how the exit path reaches it.")]
  private IWorkCoordinator _build() =>
    new DapperWorkCoordinator(ConnectionString, new JsonSerializerOptions { TypeInfoResolver = InfrastructureJsonContext.Default },
      NullLogger<DapperWorkCoordinator>.Instance);
}
