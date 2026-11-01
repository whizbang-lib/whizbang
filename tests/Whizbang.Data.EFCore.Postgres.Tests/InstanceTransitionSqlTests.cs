// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 278 (#1636): one write per transition. <c>wh_record_instance_transition</c> records the stage, what it is
/// doing, the lifecycle phase and the library version in one update, and is the only write of a recorded phase: a
/// stage writes the phase it maps to, and a pause or a stand-by writes its phase and keeps the stage.
/// </summary>
/// <remarks>
/// Every instance is registered with a fresh heartbeat by a plain insert, so nothing but the write under test moves the
/// row.
/// </remarks>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/278_OneWriterOfTheInstancePhase.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaBootstrapPhase.cs</code-under-test>
[Category("Shard3")]
public class InstanceTransitionSqlTests : EFCoreTestBase {
  [Test]
  public async Task AStage_IsWrittenWithItsPhaseAndTheVersion_AndIsNotAHeartbeatAsync() {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance);
    var before = await _rowAsync(conn, instance);
    await using var dbContext = CreateDbContext();

    var recorded = await _coordinator(dbContext).RecordInstanceTransitionAsync(new InstanceTransition(
      instance, StartupStage.AcceptingCommands, "accepting", LifecyclePhase.AcceptingCommands, "3.2.1"));

    await Assert.That(recorded).IsTrue();
    var row = await _rowAsync(conn, instance);
    await Assert.That($"{row.Stage} {row.Message} {row.Phase} {row.Version}")
      .IsEqualTo("60 accepting AcceptingCommands 3.2.1");
    await Assert.That(row.EnteredAt.HasValue).IsTrue();
    await Assert.That(row.Heartbeat).IsEqualTo(before.Heartbeat).Because("a transition is not a heartbeat");
  }

  [Test]
  public async Task ATransition_IsOneUpdateOfTheRowAsync() {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance);
    await using var tx = await conn.BeginTransactionAsync();

    var before = await _updatesInThisTransactionAsync(conn, tx);
    await using (var write = new NpgsqlCommand(
        "SELECT wh_record_instance_transition(@id, 70::smallint, 'serving', 'Running', '1.0.0')", conn, tx)) {
      write.Parameters.AddWithValue("id", instance);
      await write.ExecuteNonQueryAsync();
    }
    var after = await _updatesInThisTransactionAsync(conn, tx);
    await tx.RollbackAsync();

    await Assert.That(after - before).IsEqualTo(1L)
      .Because("stage, phase and version are written together: one update per transition");
  }

  [Test]
  public async Task APhaseOnlyWrite_KeepsTheStage_ItsMessage_AndWhenItWasEnteredAsync() {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance);
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await coordinator.RecordInstanceTransitionAsync(new InstanceTransition(
      instance, StartupStage.Running, "serving", LifecyclePhase.Running, "1.0.0"));
    var running = await _rowAsync(conn, instance);

    await coordinator.RecordInstanceTransitionAsync(new InstanceTransition(
      instance, null, null, LifecyclePhase.Paused, null));

    var paused = await _rowAsync(conn, instance);
    await Assert.That($"{paused.Stage} {paused.Message} {paused.Phase} {paused.Version}")
      .IsEqualTo("70 serving Paused 1.0.0");
    await Assert.That(paused.EnteredAt).IsEqualTo(running.EnteredAt);
    await Assert.That(await _isEligibleAsync(conn, instance)).IsFalse()
      .Because("a paused instance records Running as its stage and is still left out of work by its phase");
  }

  [Test]
  public async Task TheSameStageAgain_KeepsWhenItWasEntered_AndANewStageStartsItsOwnAsync() {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance);
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await coordinator.RecordInstanceTransitionAsync(new InstanceTransition(
      instance, StartupStage.Running, "serving", LifecyclePhase.Running, null));
    await _execAsync(conn, $"UPDATE wh_service_instances SET startup_stage_entered_at = '2000-01-01Z' WHERE instance_id = '{instance}'");

    await coordinator.RecordInstanceTransitionAsync(new InstanceTransition(
      instance, StartupStage.Running, "serving", LifecyclePhase.Running, null));
    var again = await _rowAsync(conn, instance);
    await coordinator.RecordInstanceTransitionAsync(new InstanceTransition(
      instance, StartupStage.Draining, "stopping", LifecyclePhase.Stopping, null));
    var next = await _rowAsync(conn, instance);

    await Assert.That(again.EnteredAt).IsEqualTo(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    await Assert.That(next.EnteredAt!.Value.Year).IsGreaterThan(2000);
  }

  [Test]
  public async Task ANullVersion_KeepsTheRecordedOneAsync() {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance);
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    await coordinator.RecordInstanceTransitionAsync(new InstanceTransition(
      instance, StartupStage.Running, "serving", LifecyclePhase.Running, "5.0.0"));

    await coordinator.RecordInstanceTransitionAsync(new InstanceTransition(
      instance, StartupStage.Draining, "stopping", LifecyclePhase.Stopping, null));

    await Assert.That((await _rowAsync(conn, instance)).Version).IsEqualTo("5.0.0");
  }

  [Test]
  public async Task AnInstanceWithNoRow_ReportsNothingWrittenAsync() {
    await using var dbContext = CreateDbContext();

    var recorded = await _coordinator(dbContext).RecordInstanceTransitionAsync(new InstanceTransition(
      Guid.CreateVersion7(), StartupStage.Elect, "voting", LifecyclePhase.Migrating, null));

    await Assert.That(recorded).IsFalse().Because("before it registers an instance has no row, which is expected");
  }

  [Test]
  public async Task Registration_RecordsNoPhase_TheStageRecordWritesItAsync() {
    var instance = new ServiceInstanceProvider(Guid.CreateVersion7(), "transition-svc", "transition-host", processId: 1);

    await SchemaBootstrapPhase.RegisterInstanceAsync(() => new NpgsqlConnection(ConnectionString), "public", instance);

    await using var conn = await _openAsync();
    var row = await _rowAsync(conn, instance.InstanceId);
    await Assert.That(row.Phase).IsNull()
      .Because("one writer (#1636): the registration's own stage write, held until the row existed, records the phase "
        + "with the stage and the version in one update right after it");
  }

  // ─────────────────────────────────────────────────────────────────────────

  private sealed record Row(short? Stage, string? Message, DateTime? EnteredAt, string? Phase, string? Version, DateTime Heartbeat);

  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext dbContext) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = new NpgsqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>A registration row recording no stage, phase or version, with a fresh heartbeat.</summary>
  private static async Task _registerAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = new NpgsqlCommand("""
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
      VALUES (@id, 'test', 'test-host', 1, NOW(), NOW() - INTERVAL '2 hours')
      """, conn);
    cmd.Parameters.AddWithValue("id", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<Row> _rowAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = new NpgsqlCommand("""
      SELECT startup_stage, startup_stage_message, startup_stage_entered_at, lifecycle_phase, library_version,
             last_heartbeat_at
      FROM wh_service_instances WHERE instance_id = @id
      """, conn);
    cmd.Parameters.AddWithValue("id", instanceId);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException($"instance {instanceId} has no row");
    }
    return new Row(
      reader.IsDBNull(0) ? null : reader.GetInt16(0),
      reader.IsDBNull(1) ? null : reader.GetString(1),
      reader.IsDBNull(2) ? null : reader.GetDateTime(2),
      reader.IsDBNull(3) ? null : reader.GetString(3),
      reader.IsDBNull(4) ? null : reader.GetString(4),
      reader.GetDateTime(5));
  }

  private static async Task<bool> _isEligibleAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = new NpgsqlCommand(
      "SELECT wh_instance_is_eligible_for_work(si) FROM wh_service_instances si WHERE si.instance_id = @id", conn);
    cmd.Parameters.AddWithValue("id", instanceId);
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }

  /// <summary>Rows this transaction has updated in the instance table, by the server's own count.</summary>
  private static async Task<long> _updatesInThisTransactionAsync(NpgsqlConnection conn, NpgsqlTransaction tx) {
    await using var cmd = new NpgsqlCommand(
      "SELECT COALESCE((SELECT n_tup_upd FROM pg_stat_xact_user_tables WHERE relid = 'wh_service_instances'::regclass), 0)",
      conn, tx);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }
}
