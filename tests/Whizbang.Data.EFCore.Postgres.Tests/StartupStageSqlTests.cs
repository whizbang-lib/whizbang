// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 231 (#1636): each instance records its startup stage on its own row (<c>startup_stage</c>,
/// <c>startup_stage_message</c>, <c>startup_stage_entered_at</c>), with <c>lifecycle_phase</c> written from it. Only a
/// recorded stage of Accept commands (60) or Running (70) makes an instance eligible for work; a row that records no
/// stage is judged by its phase as under 229, and a phase that is not a stage (a pause) still keeps an instance out.
/// </summary>
/// <remarks>
/// Every instance here is registered with a fresh heartbeat, so liveness never decides an outcome: each test changes
/// only the recorded stage (or phase), which is the one thing that has to move the result.
/// </remarks>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/231_StartupStageRecord.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Shard3")]
public class StartupStageSqlTests : EFCoreTestBase {
  /// <summary>Every stage, with whether recording it makes the instance eligible for work.</summary>
  public static IEnumerable<Func<(StartupStage Stage, bool Eligible)>> EveryStage() =>
    Enum.GetValues<StartupStage>().Select<StartupStage, Func<(StartupStage, bool)>>(stage =>
      () => (stage, stage is StartupStage.AcceptingCommands or StartupStage.Running));

  // --- the columns -----------------------------------------------------------------------------

  [Test]
  public async Task Columns_AreOnTheInstanceRow_WithTheirTypesAsync() {
    await using var conn = await _openAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT column_name || ' ' || data_type FROM information_schema.columns
      WHERE table_schema = 'public' AND table_name = 'wh_service_instances' AND column_name LIKE 'startup_stage%'
      ORDER BY column_name ASC
      """;
    var columns = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      columns.Add(reader.GetString(0));
    }

    await Assert.That(columns).IsEquivalentTo([
      "startup_stage smallint",
      "startup_stage_entered_at timestamp with time zone",
      "startup_stage_message text",
   ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task ApplyingTheMigrationAgain_TakesNoExclusiveLockOnTheInstanceTableAsync() {
    var sql = new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, "public")
      .GetMigration("231_StartupStageRecord")!.Sql;
    await using var conn = await _openAsync();
    await using var tx = await conn.BeginTransactionAsync();
    await using (var apply = new NpgsqlCommand(sql, conn, tx)) {
      await apply.ExecuteNonQueryAsync();
    }

    await using var locks = new NpgsqlCommand("""
      SELECT count(*) FROM pg_locks
      WHERE pid = pg_backend_pid() AND relation = 'wh_service_instances'::regclass AND mode = 'AccessExclusiveLock'
      """, conn, tx);
    var held = (long)(await locks.ExecuteScalarAsync())!;
    await tx.RollbackAsync();

    await Assert.That(held).IsEqualTo(0L)
      .Because("the running instances read and write this table on every beat; a re-run whose columns exist must not "
        + "queue an ACCESS EXCLUSIVE lock behind them or in front of them");
  }

  // --- the write -------------------------------------------------------------------------------

  [Test]
  public async Task Record_ARegisteredInstance_WritesTheStageMessageTimeAndPhaseAsync() {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, phase: null);
    var before = await _rowAsync(conn, instance);
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);

    var recorded = await coordinator.RecordStartupStageAsync(instance, StartupStage.Coordinate, "recomputing partitions");

    await Assert.That(recorded).IsTrue();
    var row = await _rowAsync(conn, instance);
    await Assert.That(row.Stage).IsEqualTo((short)50);
    await Assert.That(row.Message).IsEqualTo("recomputing partitions");
    await Assert.That(row.EnteredAt).IsNotNull();
    await Assert.That(row.Phase).IsEqualTo(nameof(LifecyclePhase.Migrating))
      .Because("lifecycle_phase stays on the row, written from the stage");
    await Assert.That(row.Heartbeat).IsEqualTo(before.Heartbeat).Because("a stage is not a heartbeat");
  }

  [Test]
  public async Task Record_AnInstanceWithNoRow_ReportsNothingWrittenAsync() {
    await using var dbContext = CreateDbContext();

    var recorded = await _coordinator(dbContext).RecordStartupStageAsync(Guid.CreateVersion7(), StartupStage.Elect, "voting");

    await Assert.That(recorded).IsFalse().Because("before it registers an instance has no row, which is expected");
  }

  [Test]
  public async Task Record_ProgressWithinAStage_KeepsWhenItWasEntered_AndANewStageStartsItsOwnAsync() {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, phase: null);
    await _recordAsync(conn, instance, StartupStage.Migrate, "migration 1 of 3");
    await _execAsync(conn, $"UPDATE wh_service_instances SET startup_stage_entered_at = '2000-01-01Z' WHERE instance_id = '{instance}'");

    await _recordAsync(conn, instance, StartupStage.Migrate, "migration 2 of 3");
    var progressed = await _rowAsync(conn, instance);
    await _recordAsync(conn, instance, StartupStage.Converge, "analyzing");
    var next = await _rowAsync(conn, instance);

    await Assert.That(progressed.Message).IsEqualTo("migration 2 of 3");
    await Assert.That(progressed.EnteredAt).IsEqualTo(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))
      .Because("progress within a stage is not a new stage; the time in stage keeps counting");
    await Assert.That(next.EnteredAt!.Value.Year).IsGreaterThan(2000).Because("a new stage starts its own time");
  }

  // --- eligible for work -----------------------------------------------------------------------

  [Test]
  [MethodDataSource(nameof(EveryStage))]
  public async Task Eligible_EachRecordedStage_IsEligibleOnlyInAcceptCommandsOrRunningAsync(StartupStage stage, bool eligible) {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, phase: null);

    await _recordAsync(conn, instance, stage, "a stage");

    await Assert.That(await _isEligibleAsync(conn, instance)).IsEqualTo(eligible)
      .Because("a starting or draining instance is alive but cannot do the work; only 60 and 70 can");
  }

  [Test]
  [Arguments(null, true)]
  [Arguments(nameof(LifecyclePhase.Migrating), false)]
  [Arguments(nameof(LifecyclePhase.Running), true)]
  public async Task Eligible_ARowThatRecordsNoStage_IsJudgedByItsPhaseAsync(string? phase, bool eligible) {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, phase);

    await Assert.That(await _isEligibleAsync(conn, instance)).IsEqualTo(eligible)
      .Because("an older build records no stage and keeps the 229 rule during a rolling upgrade");
  }

  [Test]
  public async Task Eligible_ARunningInstanceThatIsPaused_IsNotEligibleAsync() {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, phase: null);
    await _recordAsync(conn, instance, StartupStage.Running, "serving");

    await _execAsync(conn, $"SELECT record_instance_state('{instance}'::uuid, '{nameof(LifecyclePhase.Paused)}')");

    await Assert.That(await _isEligibleAsync(conn, instance)).IsFalse()
      .Because("a pause is a phase, not a stage, and kept an instance out under 229; recording stages must not undo that");
  }

  [Test]
  public async Task Candidates_FollowTheRecordedStageAsync() {
    var instance = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerAsync(conn, instance, phase: null);

    await _recordAsync(conn, instance, StartupStage.Coordinate, "coordinating");
    var coordinating = await _candidatesAsync(conn);
    await _recordAsync(conn, instance, StartupStage.AcceptingCommands, "accepting");
    var accepting = await _candidatesAsync(conn);
    await _recordAsync(conn, instance, StartupStage.Draining, "stopping");
    var draining = await _candidatesAsync(conn);

    await Assert.That(coordinating).DoesNotContain(instance);
    await Assert.That(accepting).Contains(instance)
      .Because("an instance becomes a candidate exactly when it records Accept commands");
    await Assert.That(draining).DoesNotContain(instance)
      .Because("a draining instance's share goes to its peers the moment Draining is recorded");
  }

  // --- the operator's query --------------------------------------------------------------------

  [Test]
  public async Task Stages_ListEveryInstance_WithItsPositionMessageAndTimeInStageAsync() {
    var migrating = Guid.CreateVersion7();
    var draining = Guid.CreateVersion7();
    var older = Guid.CreateVersion7();
    await using var conn = await _openAsync();
    await _registerAsync(conn, migrating, phase: null);
    await _registerAsync(conn, draining, phase: null);
    await _registerAsync(conn, older, phase: nameof(LifecyclePhase.Running));
    await _recordAsync(conn, migrating, StartupStage.Migrate, "migration 42 of 231");
    await _recordAsync(conn, draining, StartupStage.Draining, "host stopping");

    var rows = await _stagesAsync(conn);

    await Assert.That(rows[migrating]).IsEqualTo("30|Migrate|3|7|migration 42 of 231|True|Migrating|False");
    await Assert.That(rows[draining]).IsEqualTo("80|Draining||7|host stopping|True|Stopping|False")
      .Because("a shutdown stage has no position among the seven startup stages");
    await Assert.That(rows[older]).IsEqualTo("||||7||False|Running|True")
      .Because("an older build records no stage, and is listed with the phase it records");
  }

  // ─────────────────────────────────────────────────────────────────────────

  /// <summary>The coordinator as the stage reporter resolves it: through the interface.</summary>
  private static Whizbang.Core.Messaging.IWorkCoordinator _coordinator(WorkCoordinationDbContext dbContext) =>
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

  /// <summary>A registration row recording <paramref name="phase"/> (null: none) and no stage, with a fresh heartbeat.</summary>
  private static async Task _registerAsync(NpgsqlConnection conn, Guid instanceId, string? phase) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, lifecycle_phase)
      VALUES (@id, 'test', 'test-host', 1, NOW(), NOW() - INTERVAL '2 hours', @phase)
      """;
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue(nameof(phase), (object?)phase ?? DBNull.Value);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>A stage as the reporter writes it: through the function, with the phase it maps to.</summary>
  private static async Task _recordAsync(NpgsqlConnection conn, Guid instanceId, StartupStage stage, string message) {
    await using var cmd = new NpgsqlCommand("SELECT wh_record_startup_stage(@id, @stage, @message, @phase)", conn);
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue(nameof(stage), (short)stage);
    cmd.Parameters.AddWithValue(nameof(message), message);
    cmd.Parameters.AddWithValue("phase", StartupStages.LifecyclePhaseOf(stage).ToString());
    if (await cmd.ExecuteScalarAsync() is not true) {
      throw new InvalidOperationException($"the stage of {instanceId} was not recorded");
    }
  }

  private static async Task<(short? Stage, string? Message, DateTime? EnteredAt, string? Phase, DateTime Heartbeat)> _rowAsync(
      NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = new NpgsqlCommand("""
      SELECT startup_stage, startup_stage_message, startup_stage_entered_at, lifecycle_phase, last_heartbeat_at
      FROM wh_service_instances WHERE instance_id = @id
      """, conn);
    cmd.Parameters.AddWithValue("id", instanceId);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (
      reader.IsDBNull(0) ? null : reader.GetInt16(0),
      reader.IsDBNull(1) ? null : reader.GetString(1),
      reader.IsDBNull(2) ? null : reader.GetDateTime(2),
      reader.IsDBNull(3) ? null : reader.GetString(3),
      reader.GetDateTime(4));
  }

  private static async Task<bool> _isEligibleAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = new NpgsqlCommand(
      "SELECT wh_instance_is_eligible_for_work(si) FROM wh_service_instances si WHERE si.instance_id = @id", conn);
    cmd.Parameters.AddWithValue("id", instanceId);
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<List<Guid>> _candidatesAsync(NpgsqlConnection conn) {
    var candidates = new List<Guid>();
    await using var cmd = new NpgsqlCommand("SELECT instance_id FROM wh_partition_assignment_candidates()", conn);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      candidates.Add(reader.GetGuid(0));
    }
    return candidates;
  }

  /// <summary>Each instance's row of the operator query, as "stage|name|X|Y|message|has time in stage|phase|eligible".</summary>
  private static async Task<Dictionary<Guid, string>> _stagesAsync(NpgsqlConnection conn) {
    var rows = new Dictionary<Guid, string>();
    await using var cmd = new NpgsqlCommand("""
      SELECT instance_id,
             concat_ws('|', coalesce(startup_stage::text, ''), coalesce(stage_name, ''), coalesce(stage_position::text, ''),
                       stage_count::text, coalesce(startup_stage_message, ''),
                       (time_in_stage IS NOT NULL AND time_in_stage >= INTERVAL '0')::text,
                       coalesce(lifecycle_phase, ''), eligible_for_work::text)
      FROM wh_instance_startup_stages()
      """, conn);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows[reader.GetGuid(0)] = reader.GetString(1).Replace("true", "True", StringComparison.Ordinal)
        .Replace("false", "False", StringComparison.Ordinal);
    }
    return rows;
  }
}
