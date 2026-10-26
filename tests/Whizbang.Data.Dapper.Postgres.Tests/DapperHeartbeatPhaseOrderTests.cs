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
/// Migration 244 (#1781) through the Dapper driver, whose rows record no startup stage: the phase there is written by
/// beats alone (the heartbeat worker's and the claim worker's registration), and a beat built before a transition
/// that lands after a later one must not roll the recorded phase back.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/244_HeartbeatNeverRollsThePhaseBack.sql</code-under-test>
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
    await Assert.That(await conn.ExecuteScalarAsync<string>(
        "SELECT lifecycle_phase FROM wh_service_instances WHERE instance_id = @instance", new { instance }))
      .IsEqualTo(nameof(LifecyclePhase.AcceptingCommands))
      .Because("the claim worker's registration and the heartbeat race the same way on this driver");
    await Assert.That(await conn.ExecuteScalarAsync<bool>(
        "SELECT wh_instance_is_eligible_for_work(si) FROM wh_service_instances si WHERE si.instance_id = @instance",
        new { instance })).IsTrue();
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

  private DapperWorkCoordinator _build() =>
    new(ConnectionString, new JsonSerializerOptions { TypeInfoResolver = InfrastructureJsonContext.Default },
      NullLogger<DapperWorkCoordinator>.Instance);

  private static async Task _beatAsync(DapperWorkCoordinator coordinator, Guid instance, string host, LifecyclePhase phase) {
    var accepted = await coordinator.RecordHeartbeatAsync(
      new HeartbeatRequest(instance, SERVICE, host, PROCESS, LifecyclePhase: phase.ToString()));
    if (!accepted) {
      throw new InvalidOperationException($"the beat of {instance} was refused");
    }
  }
}
