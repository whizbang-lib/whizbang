// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.RunControl;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A heartbeat and an instance's recorded lifecycle phase. Migration 244 (#1781) stopped a stale beat moving the phase
/// back down the startup and shutdown ladder; migration 278 (#1636) makes the stage record the only writer of a
/// recorded phase, so a beat records the phase and version only as a registration: on the row it inserts, on a row
/// that records none, and for a new process under the same instance id. Every other beat leaves them alone.
/// </summary>
/// <remarks>
/// <para>
/// Every beat here is sent as the worker sends it, through the coordinator. Nothing waits for a race: the order the
/// race produces is written in sequence.
/// </para>
/// <para>
/// A beat ahead of the row, a phase off the ladder and a phase the database does not know were each taken from the
/// beat under 244; under 278 none of them is, because the stage record repairs its own lost writes.
/// </para>
/// </remarks>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/244_HeartbeatNeverRollsThePhaseBack.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/278_OneWriterOfTheInstancePhase.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Shard3")]
public class HeartbeatPhaseOrderSqlTests : EFCoreTestBase {
  private const string SERVICE = "phase-order-svc";
  private const string HOST = "phase-order-host";
  private const int PROCESS = 4242;

  /// <summary>The ladder phases in the order a process moves through them.</summary>
  private static readonly LifecyclePhase[] _ladder = [
    LifecyclePhase.Starting, LifecyclePhase.Connecting, LifecyclePhase.Migrating, LifecyclePhase.AcceptingCommands,
    LifecyclePhase.Running, LifecyclePhase.Stopping, LifecyclePhase.Stopped,
  ];

  /// <summary>Every lifecycle phase, with its position on the ladder (null: off the ladder).</summary>
  public static IEnumerable<Func<(LifecyclePhase Phase, int? Rank)>> EveryPhase() =>
    Enum.GetValues<LifecyclePhase>().Select<LifecyclePhase, Func<(LifecyclePhase, int?)>>(phase => () => {
      var index = Array.IndexOf(_ladder, phase);
      return (phase, index < 0 ? null : index);
    });

  // --- the failure #1781 reports -----------------------------------------------------------------

  [Test]
  public async Task AStaleBeat_AfterAcceptCommandsIsRecorded_KeepsThePhase_AndTheInstanceEligibleAsync() {
    var instance = Guid.CreateVersion7();
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await _beatAsync(coordinator, instance, LifecyclePhase.Migrating);
    await _stageAsync(coordinator, instance, StartupStage.Coordinate, "coordinating");

    await _stageAsync(coordinator, instance, StartupStage.AcceptingCommands, "accepting");
    await _beatAsync(coordinator, instance, LifecyclePhase.Migrating);

    await using var conn = await _openAsync();
    await Assert.That(await _phaseAsync(conn, instance)).IsEqualTo(nameof(LifecyclePhase.AcceptingCommands))
      .Because("a beat built before stage 60 was recorded carries Migrating and must not write it back over the stage");
    await Assert.That(await _isEligibleAsync(conn, instance)).IsTrue()
      .Because("an instance that has recorded Accept commands is ready, whatever a late beat carried");
  }

  [Test]
  [Arguments(StartupStage.Preflight, LifecyclePhase.Starting)]
  [Arguments(StartupStage.Running, LifecyclePhase.AcceptingCommands)]
  [Arguments(StartupStage.Running, LifecyclePhase.Connecting)]
  [Arguments(StartupStage.Draining, LifecyclePhase.Running)]
  [Arguments(StartupStage.Stopped, LifecyclePhase.Stopping)]
  public async Task AStaleBeat_BehindTheRecordedStage_KeepsThePhaseTheStageWroteAsync(
      StartupStage stage, LifecyclePhase stale) {
    var instance = Guid.CreateVersion7();
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await _beatAsync(coordinator, instance, stale);
    await _stageAsync(coordinator, instance, stage, "a stage");

    await _beatAsync(coordinator, instance, stale);

    await using var conn = await _openAsync();
    await Assert.That(await _phaseAsync(conn, instance)).IsEqualTo(StartupStages.LifecyclePhaseOf(stage).ToString())
      .Because("a process never moves backwards along the ladder, so a beat behind the row is a stale beat");
  }

  [Test]
  public async Task AStaleBeat_OnARowThatRecordsNoStage_DoesNotRollTheTransitionBackAsync() {
    var instance = Guid.CreateVersion7();
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await _beatAsync(coordinator, instance, LifecyclePhase.Migrating);
    await _phaseOnlyAsync(coordinator, instance, LifecyclePhase.AcceptingCommands);

    await _beatAsync(coordinator, instance, LifecyclePhase.Migrating);

    await using var conn = await _openAsync();
    await Assert.That(await _phaseAsync(conn, instance)).IsEqualTo(nameof(LifecyclePhase.AcceptingCommands))
      .Because("the transition recorder races the beat the same way where no stage is recorded");
    await Assert.That(await _isEligibleAsync(conn, instance)).IsTrue();
  }

  // --- what the guard leaves alone ---------------------------------------------------------------

  [Test]
  public async Task ABeatAheadOfTheRow_DoesNotMoveThePhase_TheStageRecordIsItsOnlyWriterAsync() {
    var instance = Guid.CreateVersion7();
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await _beatAsync(coordinator, instance, LifecyclePhase.Migrating);
    await _stageAsync(coordinator, instance, StartupStage.Migrate, "migrating");

    await _beatAsync(coordinator, instance, LifecyclePhase.AcceptingCommands);

    await using var conn = await _openAsync();
    await Assert.That(await _phaseAsync(conn, instance)).IsEqualTo(nameof(LifecyclePhase.Migrating))
      .Because("278 (#1636): the stage record is the only writer of a recorded phase, and repairs its own lost writes "
        + "on the next registration signal; a beat that moved it would race the transition again");
  }

  [Test]
  [Arguments(LifecyclePhase.Running, LifecyclePhase.Paused)]
  [Arguments(LifecyclePhase.Running, LifecyclePhase.StandingBy)]
  [Arguments(LifecyclePhase.Running, LifecyclePhase.Faulted)]
  [Arguments(LifecyclePhase.StandingBy, LifecyclePhase.Running)]
  [Arguments(LifecyclePhase.Paused, LifecyclePhase.Resuming)]
  public async Task APhaseOffTheLadder_OnABeat_IsNotWritten_EitherWayAsync(LifecyclePhase recorded, LifecyclePhase beat) {
    var instance = Guid.CreateVersion7();
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await _beatAsync(coordinator, instance, LifecyclePhase.Running);
    await _stageAsync(coordinator, instance, StartupStage.Running, "serving");
    await _phaseOnlyAsync(coordinator, instance, recorded);

    await _beatAsync(coordinator, instance, beat);

    await using var conn = await _openAsync();
    await Assert.That(await _phaseAsync(conn, instance)).IsEqualTo(recorded.ToString())
      .Because("a pause and a stand-by are written by the stage record's phase-only write, never by a beat");
  }

  [Test]
  public async Task ABeatThatNamesNoPhase_KeepsTheRecordedPhaseAsync() {
    var instance = Guid.CreateVersion7();
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await _beatAsync(coordinator, instance, LifecyclePhase.Running);

    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instance, SERVICE, HOST, PROCESS));

    await using var conn = await _openAsync();
    await Assert.That(await _phaseAsync(conn, instance)).IsEqualTo(nameof(LifecyclePhase.Running));
  }

  [Test]
  public async Task APhaseTheDatabaseDoesNotKnow_OnABeat_IsNotWrittenAsync() {
    var instance = Guid.CreateVersion7();
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await _beatAsync(coordinator, instance, LifecyclePhase.Running);

    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instance, SERVICE, HOST, PROCESS, LifecyclePhase: "Hibernating"));

    await using var conn = await _openAsync();
    await Assert.That(await _phaseAsync(conn, instance)).IsEqualTo(nameof(LifecyclePhase.Running));
  }

  // --- what a beat still records: its registration (278) ---------------------------------------

  [Test]
  public async Task ABeatOnARowThatRecordsNoPhaseOrVersion_FillsThemAsync() {
    var instance = Guid.CreateVersion7();
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instance, SERVICE, HOST, PROCESS));

    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(
      instance, SERVICE, HOST, PROCESS, LifecyclePhase: nameof(LifecyclePhase.Connecting), LibraryVersion: "2.0.0"));

    await using var conn = await _openAsync();
    await Assert.That(await _phaseAsync(conn, instance)).IsEqualTo(nameof(LifecyclePhase.Connecting))
      .Because("a row that records no phase reads as an older build, eligible whenever live; the registration that "
        + "created it could not say, so the beat does");
    await Assert.That(await _versionAsync(conn, instance)).IsEqualTo("2.0.0");
  }

  [Test]
  public async Task ABeat_NeverOverwritesARecordedVersionAsync() {
    var instance = Guid.CreateVersion7();
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instance, SERVICE, HOST, PROCESS, LibraryVersion: "1.0.0"));

    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instance, SERVICE, HOST, PROCESS, LibraryVersion: "9.9.9"));

    await using var conn = await _openAsync();
    await Assert.That(await _versionAsync(conn, instance)).IsEqualTo("1.0.0")
      .Because("the version is recorded at registration; one process runs one version");
  }

  [Test]
  public async Task ANewProcessUnderTheSameInstanceId_RecordsItsVersionAsync() {
    var instance = Guid.CreateVersion7();
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instance, SERVICE, HOST, PROCESS, LibraryVersion: "1.0.0"));

    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instance, SERVICE, HOST, PROCESS + 1, LibraryVersion: "2.0.0"));

    await using var conn = await _openAsync();
    await Assert.That(await _versionAsync(conn, instance)).IsEqualTo("2.0.0")
      .Because("a new process registering under the old row is a registration, and runs its own version");
  }

  // --- restarts ----------------------------------------------------------------------------------

  [Test]
  [Arguments("another-host", PROCESS)]
  [Arguments(HOST, PROCESS + 1)]
  public async Task ARestartUnderTheSameInstanceId_StartsBehindTheOldProcess_AndIsNotEligibleAsync(
      string hostName, int processId) {
    var instance = Guid.CreateVersion7();
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await _beatAsync(coordinator, instance, LifecyclePhase.Running);
    await _stageAsync(coordinator, instance, StartupStage.Running, "serving");

    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(
      instance, SERVICE, hostName, processId, LifecyclePhase: nameof(LifecyclePhase.Connecting)));

    await using var conn = await _openAsync();
    await Assert.That(await _phaseAsync(conn, instance)).IsEqualTo(nameof(LifecyclePhase.Connecting))
      .Because("a new process under an instance id the old one left behind starts at the bottom of the ladder");
    await Assert.That(await _isEligibleAsync(conn, instance)).IsFalse()
      .Because("the old process's Running stage must not make a starting process eligible for work");
  }

  [Test]
  public async Task ARestartUnderANewInstanceId_RecordsItsOwnPhase_WhateverTheOldRowSaysAsync() {
    var previous = Guid.CreateVersion7();
    var restarted = Guid.CreateVersion7();
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await _beatAsync(coordinator, previous, LifecyclePhase.Running);
    await _stageAsync(coordinator, previous, StartupStage.Running, "serving");

    await _beatAsync(coordinator, restarted, LifecyclePhase.Connecting);

    await using var conn = await _openAsync();
    await Assert.That(await _phaseAsync(conn, restarted)).IsEqualTo(nameof(LifecyclePhase.Connecting));
    await Assert.That(await _isEligibleAsync(conn, restarted)).IsFalse();
  }

  // --- the ladder --------------------------------------------------------------------------------

  [Test]
  [MethodDataSource(nameof(EveryPhase))]
  public async Task Rank_FollowsTheLadder_AndIsNullOffItAsync(LifecyclePhase phase, int? rank) {
    await using var conn = await _openAsync();
    await using var cmd = new NpgsqlCommand("SELECT wh_lifecycle_phase_rank(@phase)", conn);
    cmd.Parameters.AddWithValue(nameof(phase), phase.ToString());

    var result = await cmd.ExecuteScalarAsync();

    await Assert.That(result is DBNull ? null : (int?)Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture))
      .IsEqualTo(rank).Because("the SQL ladder must name the phases exactly as LifecyclePhase does, in order");
  }

  // ─────────────────────────────────────────────────────────────────────────

  /// <summary>The coordinator as the heartbeat worker and the stage reporter resolve it: through the interface.</summary>
  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext dbContext) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());

  /// <summary>A beat from this test's one process, carrying <paramref name="phase"/>, as the heartbeat worker sends it.</summary>
  private static async Task _beatAsync(IWorkCoordinator coordinator, Guid instance, LifecyclePhase phase) {
    var accepted = await coordinator.RecordHeartbeatAsync(
      new HeartbeatRequest(instance, SERVICE, HOST, PROCESS, LifecyclePhase: phase.ToString()));
    if (!accepted) {
      throw new InvalidOperationException($"the beat of {instance} was refused");
    }
  }

  /// <summary>A stage as the reporter writes it: one transition, with the phase the stage maps to.</summary>
  private static async Task _stageAsync(IWorkCoordinator coordinator, Guid instance, StartupStage stage, string message) =>
    _ = await coordinator.RecordInstanceTransitionAsync(
      new InstanceTransition(instance, stage, message, StartupStages.LifecyclePhaseOf(stage), null));

  /// <summary>A pause or a stand-by as the reporter writes it: the phase alone, keeping the stage.</summary>
  private static async Task _phaseOnlyAsync(IWorkCoordinator coordinator, Guid instance, LifecyclePhase phase) =>
    _ = await coordinator.RecordInstanceTransitionAsync(new InstanceTransition(instance, null, null, phase, null));

  private static async Task<string?> _versionAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = new NpgsqlCommand("SELECT library_version FROM wh_service_instances WHERE instance_id = @id", conn);
    cmd.Parameters.AddWithValue("id", instanceId);
    return await cmd.ExecuteScalarAsync() as string;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<string?> _phaseAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = new NpgsqlCommand("SELECT lifecycle_phase FROM wh_service_instances WHERE instance_id = @id", conn);
    cmd.Parameters.AddWithValue("id", instanceId);
    return await cmd.ExecuteScalarAsync() as string;
  }

  private static async Task<bool> _isEligibleAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = new NpgsqlCommand(
      "SELECT wh_instance_is_eligible_for_work(si) FROM wh_service_instances si WHERE si.instance_id = @id", conn);
    cmd.Parameters.AddWithValue("id", instanceId);
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }
}
