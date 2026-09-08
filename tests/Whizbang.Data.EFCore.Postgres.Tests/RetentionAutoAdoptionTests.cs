#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The automatic half of the retention adoption gate (issue #712): migration 104 withholds the
/// enrolled reap until a perspective is acknowledged so a deploy cannot silently drain a historical
/// backlog, and nothing in the framework acknowledged. <c>adopt_enrolled_perspective_retention</c>
/// reads the backlog the window would remove, opens the gate, and reports one row per perspective;
/// the maintenance worker runs it immediately before the enrolled reap, so a declared window is
/// draining within one maintenance interval of the deploy and the surprise the gate exists for
/// becomes a report instead of a manual step.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/144_RetentionAutoAdoption.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
/// <docs>fundamentals/perspectives/row-retention</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class RetentionAutoAdoptionTests : EFCoreTestBase {
  private const string TABLE = "wh_per_auto_adoption";
  private const string CLR_TYPE = "TestApp.AutoAdoptionModel";
  private const string ACKNOWLEDGED_CLR_TYPE = "TestApp.AutoAdoptionAcknowledgedModel";
  private const string UNENROLLED_CLR_TYPE = "TestApp.AutoAdoptionUnenrolledModel";
  private static readonly string[] _onlyTheGatedPerspective = [CLR_TYPE];

  [Test]
  public async Task Adopt_NewlyEnrolled_ReportsTheBacklogAndOpensTheGateAsync() {
    await using var conn = await _openAsync();
    await _resetAsync(conn, rowCount: 10, idleDays: 200);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    var adopted = await coordinator.AdoptEnrolledPerspectiveRetentionAsync();

    await Assert.That(adopted.Count).IsEqualTo(1);
    await Assert.That(adopted[0].ClrTypeName).IsEqualTo(CLR_TYPE);
    await Assert.That(adopted[0].Backlog).IsEqualTo(10L)
      .Because("the backlog is the number the gate existed to surface; it is reported at the moment of adoption");
    await Assert.That(await _acknowledgedAsync(conn)).IsTrue();

    await using (var reap = new NpgsqlCommand("SELECT reap_enrolled_perspective_rows()", conn)) {
      await reap.ExecuteNonQueryAsync();
    }
    await Assert.That(await _countAsync(conn)).IsEqualTo(0L)
      .Because("once adopted the enrolled reap drains the backlog in the same cycle");
  }

  [Test]
  public async Task Adopt_SecondCycle_ReportsNothingAsync() {
    await using var conn = await _openAsync();
    await _resetAsync(conn, rowCount: 3, idleDays: 200);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    _ = await coordinator.AdoptEnrolledPerspectiveRetentionAsync();

    var again = await coordinator.AdoptEnrolledPerspectiveRetentionAsync();

    await Assert.That(again.Count).IsEqualTo(0)
      .Because("adoption is idempotent: an acknowledged perspective produces no row, so the steady state is silent");
  }

  [Test]
  public async Task Adopt_ListsOnlyEnrolledUnacknowledgedPerspectivesAsync() {
    await using var conn = await _openAsync();
    await _resetAsync(conn, rowCount: 1, idleDays: 200);
    await using (var controls = new NpgsqlCommand($@"
      DELETE FROM wh_perspective_registry WHERE clr_type_name IN ('{ACKNOWLEDGED_CLR_TYPE}', '{UNENROLLED_CLR_TYPE}');
      INSERT INTO wh_perspective_registry
        (clr_type_name, table_name, schema_json, schema_hash, service_name, row_retention_enrolled, row_ttl_seconds, retention_enforcement_acknowledged)
      VALUES ('{ACKNOWLEDGED_CLR_TYPE}', '{TABLE}', '{{}}'::jsonb, 'h', 'svc', TRUE, 60, TRUE),
             ('{UNENROLLED_CLR_TYPE}', '{TABLE}', '{{}}'::jsonb, 'h', 'svc', FALSE, NULL, FALSE);", conn)) {
      await controls.ExecuteNonQueryAsync();
    }
    await using var ctx = CreateDbContext();

    var adopted = await _coordinator(ctx).AdoptEnrolledPerspectiveRetentionAsync();

    await Assert.That(adopted.Select(a => a.ClrTypeName)).IsEquivalentTo(_onlyTheGatedPerspective)
      .Because("an acknowledged perspective is already adopted and an un-enrolled one has nothing to adopt");
  }

  [Test]
  [Timeout(120000)]
  public async Task MaintenanceCycle_DrainsADeclaredWindowWithoutAnAcknowledgeCallAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync();
    await _resetAsync(conn, rowCount: 10, idleDays: 200);
    var worker = _buildWorker(autoAcknowledge: null);

    await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker, cancellationToken);

    await Assert.That(await _acknowledgedAsync(conn)).IsTrue()
      .Because("turnkey: the declaration is in force on the next deploy with no manual step");
    await Assert.That(await _countAsync(conn)).IsEqualTo(0L)
      .Because("adoption and the enrolled reap run in the same cycle, so the backlog starts draining at once");
  }

  [Test]
  [Timeout(120000)]
  public async Task MaintenanceCycle_WithAutoAcknowledgeOff_KeepsTheGateAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync();
    await _resetAsync(conn, rowCount: 10, idleDays: 200);
    var worker = _buildWorker(autoAcknowledge: false);

    await Whizbang.Testing.MaintenanceTestDriver.RunOnceAsync(worker, cancellationToken);

    await Assert.That(await _acknowledgedAsync(conn)).IsFalse()
      .Because("AutoAcknowledge=false is today's behavior exactly: the gate waits for the acknowledge call");
    await Assert.That(await _countAsync(conn)).IsEqualTo(10L);
  }

  // -------------------------------------------------------------------------------------------

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private MaintenanceWorker _buildWorker(bool? autoAcknowledge) {
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    services.AddScoped<IWorkCoordinator>(sp => _coordinator(sp.GetRequiredService<WorkCoordinationDbContext>()));
    if (autoAcknowledge is { } value) {
      services.AddSingleton(Options.Create(new PerspectiveRowRetentionOptions { AutoAcknowledge = value }));
    }
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      NullLogger<MaintenanceWorker>.Instance);
  }

  /// <summary>An enrolled, unacknowledged perspective with a 60-day window and rows well past it.</summary>
  private static async Task _resetAsync(NpgsqlConnection conn, int rowCount, int idleDays) {
    await using var ddl = new NpgsqlCommand($@"
      DROP TABLE IF EXISTS {TABLE};
      CREATE TABLE {TABLE} (
        id UUID NOT NULL PRIMARY KEY,
        data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
        sys_created_at TIMESTAMPTZ, sys_updated_at TIMESTAMPTZ,
        expires_at TIMESTAMPTZ, version INTEGER NOT NULL);
      DELETE FROM wh_perspective_registry WHERE clr_type_name IN ('{CLR_TYPE}', '{ACKNOWLEDGED_CLR_TYPE}', '{UNENROLLED_CLR_TYPE}');
      INSERT INTO wh_perspective_registry (clr_type_name, table_name, schema_json, schema_hash, service_name)
      VALUES ('{CLR_TYPE}', '{TABLE}', '{{}}'::jsonb, 'h', 'svc');
      UPDATE wh_perspective_registry
         SET row_retention_enrolled = TRUE, row_ttl_seconds = {60 * 60 * 24 * 60},
             retention_enforcement_acknowledged = FALSE
       WHERE clr_type_name = '{CLR_TYPE}';
      INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, version)
      SELECT gen_random_uuid(), '{{}}'::jsonb, '{{}}'::jsonb, '{{}}'::jsonb,
             NOW() - make_interval(days => {idleDays}), NOW() - make_interval(days => {idleDays}), 1
      FROM generate_series(1, {rowCount});", conn);
    await ddl.ExecuteNonQueryAsync();
  }

  private static async Task<long> _countAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {TABLE}", conn);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task<bool> _acknowledgedAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand(
      $"SELECT retention_enforcement_acknowledged FROM wh_perspective_registry WHERE clr_type_name = '{CLR_TYPE}'", conn);
    return (bool)(await cmd.ExecuteScalarAsync() ?? false);
  }
}
