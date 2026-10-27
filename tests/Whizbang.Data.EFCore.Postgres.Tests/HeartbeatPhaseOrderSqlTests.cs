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
/// Migration 244 (#1781): a heartbeat never moves an instance's recorded lifecycle phase backwards along the startup
/// and shutdown ladder (Starting, Connecting, Migrating, AcceptingCommands, Running, Stopping, Stopped). A beat reads
/// the phase when it is built and writes it when it lands; when a startup stage or a transition was recorded in
/// between, the beat carries the phase before it, and writing that back made a ready instance read as ineligible for
/// work until its next beat.
/// </summary>
/// <remarks>
/// <para>
/// Every stale beat here is sent as the worker sends it, through the coordinator, carrying exactly the phase a beat
/// built before the transition carries. Nothing waits for a race: the order the race produces is written in sequence.
/// </para>
/// <para>
/// What the guard must leave alone is covered too: a beat ahead of the row (a lost transition, repaired), a phase off
/// the ladder (a pause, a stand-by and the revival from it), a phase the database does not know, and a restart, in
/// which a new process legitimately starts behind the phase the row recorded for the old one.
/// </para>
/// </remarks>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/244_HeartbeatNeverRollsThePhaseBack.sql</code-under-test>
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
    await coordinator.RecordStartupStageAsync(instance, StartupStage.Coordinate, "coordinating");

    await coordinator.RecordStartupStageAsync(instance, StartupStage.AcceptingCommands, "accepting");
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
    await coordinator.RecordStartupStageAsync(instance, stage, "a stage");

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
    await coordinator.RecordInstanceStateAsync(instance, nameof(LifecyclePhase.AcceptingCommands));

    await _beatAsync(coordinator, instance, LifecyclePhase.Migrating);

    await using var conn = await _openAsync();
    await Assert.That(await _phaseAsync(conn, instance)).IsEqualTo(nameof(LifecyclePhase.AcceptingCommands))
      .Because("the transition recorder races the beat the same way where no stage is recorded");
    await Assert.That(await _isEligibleAsync(conn, instance)).IsTrue();
  }

  // --- what the guard leaves alone ---------------------------------------------------------------

  [Test]
  public async Task ABeatAheadOfTheRow_MovesThePhaseForward_RepairingALostTransitionAsync() {
    var instance = Guid.CreateVersion7();
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await _beatAsync(coordinator, instance, LifecyclePhase.Migrating);
    await coordinator.RecordStartupStageAsync(instance, StartupStage.Migrate, "migrating");

    await _beatAsync(coordinator, instance, LifecyclePhase.AcceptingCommands);

    await using var conn = await _openAsync();
    await Assert.That(await _phaseAsync(conn, instance)).IsEqualTo(nameof(LifecyclePhase.AcceptingCommands))
      .Because("a transition whose own write was lost is repaired by the next beat, as it was before");
  }

  [Test]
  [Arguments(LifecyclePhase.Running, LifecyclePhase.Paused)]
  [Arguments(LifecyclePhase.Running, LifecyclePhase.StandingBy)]
  [Arguments(LifecyclePhase.Running, LifecyclePhase.Faulted)]
  [Arguments(LifecyclePhase.StandingBy, LifecyclePhase.Running)]
  [Arguments(LifecyclePhase.Paused, LifecyclePhase.Resuming)]
  public async Task APhaseOffTheLadder_IsTakenFromTheBeat_EitherWayAsync(LifecyclePhase recorded, LifecyclePhase beat) {
    var instance = Guid.CreateVersion7();
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await _beatAsync(coordinator, instance, LifecyclePhase.Running);
    await coordinator.RecordStartupStageAsync(instance, StartupStage.Running, "serving");
    await coordinator.RecordInstanceStateAsync(instance, recorded.ToString());

    await _beatAsync(coordinator, instance, beat);

    await using var conn = await _openAsync();
    await Assert.That(await _phaseAsync(conn, instance)).IsEqualTo(beat.ToString())
      .Because("a pause, a stand-by and a revival are not rungs of the ladder; the beat still repairs them");
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
  public async Task APhaseTheDatabaseDoesNotKnow_IsTakenFromTheBeatAsync() {
    var instance = Guid.CreateVersion7();
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await _beatAsync(coordinator, instance, LifecyclePhase.Running);

    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instance, SERVICE, HOST, PROCESS, LifecyclePhase: "Hibernating"));

    await using var conn = await _openAsync();
    await Assert.That(await _phaseAsync(conn, instance)).IsEqualTo("Hibernating")
      .Because("a phase with no place on the ladder cannot be behind it, so the beat's value stands as before");
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
    await coordinator.RecordStartupStageAsync(instance, StartupStage.Running, "serving");

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
    await coordinator.RecordStartupStageAsync(previous, StartupStage.Running, "serving");

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
