// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Issue #1611: the Dapper driver's partition maintenance step converts <c>wh_log</c> and keeps its partitions while
/// partitioning is enabled, and does nothing when it is off, on an instance that does not hold the maintainer duty,
/// or with no table registered.
/// </summary>
/// <docs>operations/infrastructure/partitioned-ledgers</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperPartitionMaintenanceStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/PartitionMaintenance.cs</code-under-test>
[NotInParallel("PostgreSQL")]
[Category("Integration")]
public class DapperPartitionMaintenanceStepTests : PostgresTestBase {
  private static readonly DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

  private DapperPartitionMaintenanceStep _step(DateTimeOffset? now = null) =>
    new(ConnectionString, NullLogger<DapperPartitionMaintenanceStep>.Instance, new FixedClock(now ?? _now));

  private static ServiceProvider _services(bool enabled, int createAhead = 3, DutyHolderWorker? holder = null, params PartitionedTable[] tables) {
    var services = new ServiceCollection();
    services.AddSingleton(Options.Create(new MaintenanceWorkerOptions {
      Partitioning = new MaintenancePartitioningOptions { Enabled = enabled, CreateAhead = createAhead },
    }));
    foreach (var table in tables) {
      services.AddPartitionedTable(table);
    }
    if (holder is not null) {
      services.AddSingleton(holder);
    }
    return services.BuildServiceProvider();
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  // The cycles here run at fixed moments, and the database stamps log rows with its own clock, which is later. A row
  // stamped after the fixed moment's week would fall outside the legacy partition the conversion attaches, so the
  // log starts empty.
  [Before(Test)]
  public async Task EmptyTheLogAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync("DELETE FROM wh_log");
  }

  private async Task<string> _logKindAsync() {
    await using var connection = await _openAsync();
    return await connection.ExecuteScalarAsync<string>("SELECT relkind::text FROM pg_class WHERE oid = 'wh_log'::regclass") ?? "";
  }

  [Test]
  public async Task Enabled_ConvertsTheLog_AndCreatesPartitionsAheadAsync() {
    await using var services = _services(true, 2, null, PartitionedTables.Log, PartitionedTables.Log);

    await _step().RunAsync(services, CancellationToken.None);

    await using var connection = await _openAsync();
    await Assert.That(await _logKindAsync()).IsEqualTo("p");
    var partitions = (await connection.QueryAsync<string>(
      "SELECT c.relname::text FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid WHERE i.inhparent = 'wh_log'::regclass ORDER BY 1")).ToList();
    await Assert.That(partitions).IsEquivalentTo(["wh_log_default", "wh_log_legacy", "wh_log_p20261026", "wh_log_p20261102"])
      .Because("the legacy partition holds the current week, so two weeks ahead start the Monday after; a duplicate registration is read once");
  }

  [Test]
  public async Task TheLogsWindow_IsReadFromItsSettingAsync() {
    await using (var setup = await _openAsync()) {
      await setup.ExecuteAsync(
        "INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES ('log_retention_days', '60', 'integer') " +
        "ON CONFLICT (setting_key) DO UPDATE SET setting_value = '60'");
    }
    await using var services = _services(enabled: true, tables: PartitionedTables.Log);
    await _step().RunAsync(services, CancellationToken.None);

    // Thirty days on, the legacy partition (ending 2026-10-26) is inside a 60-day window, though past the 14-day default.
    await _step(_now.AddDays(30)).RunAsync(services, CancellationToken.None);

    await using var connection = await _openAsync();
    await Assert.That(await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM pg_class WHERE relname = 'wh_log_legacy'"))
      .IsEqualTo(1L);
  }

  [Test]
  public async Task AFixedWindow_DropsWhatEndedBeforeItAsync() {
    await using (var setup = await _openAsync()) {
      await setup.ExecuteAsync(
        "CREATE TABLE wh_partition_probe (id BIGSERIAL, at TIMESTAMPTZ NOT NULL, PRIMARY KEY (id, at)) PARTITION BY RANGE (at)");
    }
    var probe = PartitionedTable.RetainedFor("wh_partition_probe", "at", TimeSpan.FromDays(1), TimeSpan.FromDays(2));
    await using var services = _services(enabled: true, createAhead: 1, tables: probe);
    await _step().RunAsync(services, CancellationToken.None);

    await _step(_now.AddDays(4)).RunAsync(services, CancellationToken.None);

    await using var connection = await _openAsync();
    var partitions = (await connection.QueryAsync<string>(
      "SELECT c.relname::text FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid WHERE i.inhparent = 'wh_partition_probe'::regclass ORDER BY 1")).ToList();
    await Assert.That(partitions).IsEquivalentTo(
      ["wh_partition_probe_default", "wh_partition_probe_p20261025", "wh_partition_probe_p20261026"])
      .Because("10-21 and 10-22 ended before the window opened at 10-23 12:00; 10-25 and 10-26 are the current day and one ahead");
  }

  [Test]
  public async Task AnIndefiniteWindow_DropsNothingAsync() {
    await using (var setup = await _openAsync()) {
      await setup.ExecuteAsync(
        "CREATE TABLE wh_partition_probe (id BIGSERIAL, at TIMESTAMPTZ NOT NULL, PRIMARY KEY (id, at)) PARTITION BY RANGE (at)");
    }
    var probe = PartitionedTable.RetainedIndefinitely("wh_partition_probe", "at", TimeSpan.FromDays(1));
    await using var services = _services(enabled: true, createAhead: 1, tables: probe);
    await _step().RunAsync(services, CancellationToken.None);

    await _step(_now.AddDays(400)).RunAsync(services, CancellationToken.None);

    await using var connection = await _openAsync();
    var partitions = (await connection.QueryAsync<string>(
      "SELECT c.relname::text FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid WHERE i.inhparent = 'wh_partition_probe'::regclass ORDER BY 1")).ToList();
    await Assert.That(partitions).Contains("wh_partition_probe_p20261021")
      .Because("with no window and no setting key, the manager is passed no window and removes nothing");
  }

  [Test]
  public async Task Disabled_ChangesNothingAsync() {
    await using var services = _services(enabled: false, tables: PartitionedTables.Log);

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(await _logKindAsync()).IsEqualTo("r");
  }

  [Test]
  public async Task WithoutTheOptions_ChangesNothingAsync() {
    var services = new ServiceCollection();
    services.AddPartitionedTable(PartitionedTables.Log);
    await using var provider = services.BuildServiceProvider();

    await _step().RunAsync(provider, CancellationToken.None);

    await Assert.That(await _logKindAsync()).IsEqualTo("r");
  }

  [Test]
  public async Task NothingRegistered_ChangesNothingAsync() {
    await using var services = _services(enabled: true);

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(await _logKindAsync()).IsEqualTo("r");
  }

  [Test]
  public async Task AnInstanceNotHoldingTheMaintainerDuty_DoesNoDdlAsync() {
    await using var services = _services(enabled: true, holder: DutyHolders.NotHolding(), tables: PartitionedTables.Log);

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(await _logKindAsync()).IsEqualTo("r")
      .Because("with role assignment managing the maintainer duty, only its holder changes partitions");
  }

  [Test]
  public async Task TheMaintainerDutyHolder_RunsTheCycleAsync() {
    var holder = DutyHolders.Holding();
    await holder.RunOnceAsync(CancellationToken.None);
    await using var services = _services(enabled: true, holder: holder, tables: PartitionedTables.Log);

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(await _logKindAsync()).IsEqualTo("p");
  }

  [Test]
  public async Task AHolderLoopWithoutTheMaintainerRole_LeavesEveryInstanceToTheSqlLockAsync() {
    await using var services = _services(enabled: true, holder: DutyHolders.NotHolding(StartupDuties.MIGRATOR), tables: PartitionedTables.Log);

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(await _logKindAsync()).IsEqualTo("p");
  }

  [Test]
  public async Task TheStep_IsNamed_AndRejectsMissingArgumentsAsync() {
    await Assert.That(_step().Name).IsEqualTo("partition-maintenance");
    await Assert.That(() => _step().RunAsync(null!, CancellationToken.None)).ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => new DapperPartitionMaintenanceStep(" ", NullLogger<DapperPartitionMaintenanceStep>.Instance, new FixedClock(_now)))
      .ThrowsExactly<ArgumentException>();
    await Assert.That(() => new DapperPartitionMaintenanceStep(ConnectionString, null!, new FixedClock(_now)))
      .ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => new DapperPartitionMaintenanceStep(ConnectionString, NullLogger<DapperPartitionMaintenanceStep>.Instance, null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  [Arguments(true, true)]
  [Arguments(false, false)]
  public async Task TheDriver_RegistersTheStepAndTheFrameworkTablesAsync(bool entriesOverload, bool hostLogging) {
    var services = new ServiceCollection();
    if (hostLogging) {
      services.AddLogging();
      services.AddSingleton<TimeProvider>(new FixedClock(_now));
    }
    if (entriesOverload) {
      services.AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), initializeSchema: false, perspectiveEntries: []);
    } else {
      services.AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions());
    }

    await using var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();
    await Assert.That(scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<DapperPartitionMaintenanceStep>().Count()).IsEqualTo(1);
    await Assert.That(scope.ServiceProvider.GetServices<PartitionedTable>().Distinct()).IsEquivalentTo(
      [PartitionedTables.Log, PartitionedTables.Signals(TimeSpan.FromDays(7)), PartitionedTables.LifecycleCompletions(7)]);
  }

  private sealed class FixedClock(DateTimeOffset now) : TimeProvider {
    public override DateTimeOffset GetUtcNow() => now;
  }
}
