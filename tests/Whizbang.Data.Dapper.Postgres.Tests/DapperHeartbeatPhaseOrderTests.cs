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

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The instance's recorded phase through the Dapper driver. Migration 244 (#1781) kept a stale beat from rolling the
/// phase back; since 278 (#1636) a beat never overwrites a recorded phase at all, and this driver records its
/// transitions through the stage record, as the EF Core driver does: one write of stage, phase and version.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/244_HeartbeatNeverRollsThePhaseBack.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/278_OneWriterOfTheInstancePhase.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs</code-under-test>
public class DapperHeartbeatPhaseOrderTests : PostgresTestBase {
  private const string SERVICE = "dapper-phase-svc";
  private const string HOST = "dapper-phase-host";
  private const int PROCESS = 7;

  [Test]
  public async Task AStaleBeat_AfterALaterBeat_KeepsTheLaterPhase_AndTheInstanceEligibleAsync() {
    var coordinator = _build();
    var instance = Guid.CreateVersion7();
    await _beatAsync(coordinator, instance, HOST, LifecyclePhase.AcceptingCommands);

    await _beatAsync(coordinator, instance, HOST, LifecyclePhase.Migrating);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await Assert.That(await _phaseAsync(conn, instance)).IsEqualTo(nameof(LifecyclePhase.AcceptingCommands))
      .Because("the claim worker's registration and the heartbeat race the same way on this driver");
    await Assert.That(await conn.ExecuteScalarAsync<bool>(
        "SELECT wh_instance_is_eligible_for_work(si) FROM wh_service_instances si WHERE si.instance_id = @instance",
        new { instance })).IsTrue();
  }

  [Test]
  public async Task ABeat_NeverMovesTheRecordedPhase_EitherWayAsync() {
    var coordinator = _build();
    var instance = Guid.CreateVersion7();
    await _beatAsync(coordinator, instance, HOST, LifecyclePhase.AcceptingCommands);

    await _beatAsync(coordinator, instance, HOST, LifecyclePhase.Migrating);
    await _beatAsync(coordinator, instance, HOST, LifecyclePhase.Paused);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await Assert.That(await _phaseAsync(conn, instance)).IsEqualTo(nameof(LifecyclePhase.AcceptingCommands))
      .Because("the registration that inserted the row recorded its phase; after that only the stage record writes it");
    await Assert.That(await conn.ExecuteScalarAsync<bool>(
        "SELECT wh_instance_is_eligible_for_work(si) FROM wh_service_instances si WHERE si.instance_id = @instance",
        new { instance })).IsTrue();
  }

  [Test]
  public async Task ATransition_IsRecorded_StagePhaseAndVersionTogether_AndAPauseKeepsTheStageAsync() {
    var coordinator = _build();
    var instance = Guid.CreateVersion7();
    await _beatAsync(coordinator, instance, HOST, LifecyclePhase.Migrating);

    var running = await coordinator.RecordInstanceTransitionAsync(new Whizbang.Core.Startup.InstanceTransition(
      instance, Whizbang.Core.Startup.StartupStage.Running, "serving", LifecyclePhase.Running, "8.0.0"));
    var paused = await coordinator.RecordInstanceTransitionAsync(new Whizbang.Core.Startup.InstanceTransition(
      instance, null, null, LifecyclePhase.Paused, null));
    var missing = await coordinator.RecordInstanceTransitionAsync(new Whizbang.Core.Startup.InstanceTransition(
      Guid.CreateVersion7(), Whizbang.Core.Startup.StartupStage.Running, "serving", LifecyclePhase.Running, null));

    await Assert.That(running).IsTrue();
    await Assert.That(paused).IsTrue();
    await Assert.That(missing).IsFalse().Because("an instance with no row records nothing");
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await Assert.That(await conn.ExecuteScalarAsync<string>(
        """
        SELECT startup_stage || ' ' || startup_stage_message || ' ' || lifecycle_phase || ' ' || library_version
        FROM wh_service_instances WHERE instance_id = @instance
        """, new { instance }))
      .IsEqualTo("70 serving Paused 8.0.0");
  }

  [Test]
  public async Task ARestartUnderTheSameInstanceId_FromAnotherHost_RecordsItsEarlierPhaseAsync() {
    var coordinator = _build();
    var instance = Guid.CreateVersion7();
    await _beatAsync(coordinator, instance, HOST, LifecyclePhase.Running);

    await _beatAsync(coordinator, instance, "restarted-host", LifecyclePhase.Connecting);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await Assert.That(await conn.ExecuteScalarAsync<string>(
        "SELECT lifecycle_phase FROM wh_service_instances WHERE instance_id = @instance", new { instance }))
      .IsEqualTo(nameof(LifecyclePhase.Connecting))
      .Because("a new process legitimately starts behind the phase the old one left on the row");
  }

  private static Task<string?> _phaseAsync(NpgsqlConnection conn, Guid instance) =>
    conn.ExecuteScalarAsync<string?>(
      "SELECT lifecycle_phase FROM wh_service_instances WHERE instance_id = @instance", new { instance });

  /// <summary>The Dapper coordinator as the heartbeat worker and the stage reporter resolve it: through the interface.</summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance",
    Justification = "Calls go through the interface on purpose: that is how the heartbeat worker and the stage reporter reach it.")]
  private IWorkCoordinator _build() =>
    new DapperWorkCoordinator(ConnectionString, new JsonSerializerOptions { TypeInfoResolver = InfrastructureJsonContext.Default },
      NullLogger<DapperWorkCoordinator>.Instance);

  private static async Task _beatAsync(IWorkCoordinator coordinator, Guid instance, string host, LifecyclePhase phase) {
    var accepted = await coordinator.RecordHeartbeatAsync(
      new HeartbeatRequest(instance, SERVICE, host, PROCESS, LifecyclePhase: phase.ToString()));
    if (!accepted) {
      throw new InvalidOperationException($"the beat of {instance} was refused");
    }
  }
}
