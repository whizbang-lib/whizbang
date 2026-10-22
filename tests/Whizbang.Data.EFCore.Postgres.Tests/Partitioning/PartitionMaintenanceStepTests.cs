// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Partitioning;

/// <summary>
/// Issue #1611: the EF Core driver's partition maintenance step converts <c>wh_log</c> and keeps its partitions while
/// partitioning is enabled, through the schema-initialization connection when one is configured, and does nothing
/// when partitioning is off or the instance does not hold the maintainer duty. The driver registers it.
/// </summary>
/// <docs>operations/infrastructure/partitioned-ledgers</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/PartitionMaintenanceStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/PostgresDriverExtensions.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class PartitionMaintenanceStepTests : EFCoreTestBase {
  private static readonly DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

  // The cycles here run at a fixed moment, and the database stamps log rows with its own clock, which is later. A
  // row stamped after the fixed moment's week would fall outside the legacy partition the conversion attaches, so
  // the log starts empty.
  [Before(Test)]
  public async Task EmptyTheLogAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync("DELETE FROM wh_log");
  }

  private static PartitionMaintenanceStep _step() =>
    new(typeof(WorkCoordinationDbContext), NullLogger<PartitionMaintenanceStep>.Instance, new FakeTimeProvider(_now));

  private ServiceProvider _services(bool enabled, WorkCoordinationDbContext? context = null, string? initConnection = null,
      bool registerLog = true, Whizbang.Core.Startup.DutyHolderWorker? holder = null) {
    var services = new ServiceCollection();
    services.AddSingleton(context ?? CreateDbContext());
    services.AddSingleton(Options.Create(new MaintenanceWorkerOptions {
      Partitioning = new MaintenancePartitioningOptions { Enabled = enabled },
    }));
    if (registerLog) {
      services.AddPartitionedTable(PartitionedTables.Log);
    }
    if (initConnection is not null) {
      services.AddKeyedSingleton<SchemaInitConnection>(typeof(WorkCoordinationDbContext), new SchemaInitConnection(initConnection));
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

  private async Task<string> _logKindAsync() {
    await using var connection = await _openAsync();
    return await connection.ExecuteScalarAsync<string>("SELECT relkind::text FROM pg_class WHERE oid = 'wh_log'::regclass");
  }

  [Test]
  public async Task Enabled_ConvertsTheLog_AndCreatesPartitionsAheadAsync() {
    await using var services = _services(enabled: true);

    await _step().RunAsync(services, CancellationToken.None);

    await using var connection = await _openAsync();
    await Assert.That(await _logKindAsync()).IsEqualTo("p");
    await Assert.That(await connection.QueryAsync<string>(
        "SELECT c.relname::text FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid WHERE i.inhparent = 'wh_log'::regclass ORDER BY 1"))
      .IsEquivalentTo(["wh_log_default", "wh_log_legacy", "wh_log_p20261026", "wh_log_p20261102", "wh_log_p20261109"]);
  }

  [Test]
  public async Task WithAnInitConnection_ChangesTheTablesThroughItAsync() {
    // The application connection names a database that does not exist, so a run that used it would fail.
    var unreachable = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "wh_no_such_database" }.ConnectionString;
    await using var applicationContext = new WorkCoordinationDbContext(
      new DbContextOptionsBuilder<WorkCoordinationDbContext>().UseNpgsql(unreachable).Options);
    await using var services = _services(enabled: true, context: applicationContext, initConnection: ConnectionString);

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(await _logKindAsync()).IsEqualTo("p")
      .Because("partition DDL needs the table owner's rights, which the role behind the -init connection has");
  }

  [Test]
  public async Task AContextWithoutTheFrameworkModel_WorksInThePublicSchemaAsync() {
    await using var context = new BareContext(new DbContextOptionsBuilder<BareContext>().UseNpgsql(ConnectionString).Options);
    var services = new ServiceCollection();
    services.AddSingleton(context);
    services.AddSingleton(Options.Create(new MaintenanceWorkerOptions {
      Partitioning = new MaintenancePartitioningOptions { Enabled = true },
    }));
    services.AddPartitionedTable(PartitionedTables.Log);
    await using var provider = services.BuildServiceProvider();

    await new PartitionMaintenanceStep(typeof(BareContext), NullLogger<PartitionMaintenanceStep>.Instance, new FakeTimeProvider(_now))
      .RunAsync(provider, CancellationToken.None);

    await Assert.That(await _logKindAsync()).IsEqualTo("p")
      .Because("a context that maps no framework table names no schema, so the step works in public, as the driver does");
  }

  [Test]
  public async Task Disabled_ChangesNothingAsync() {
    await using var services = _services(enabled: false);

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(await _logKindAsync()).IsEqualTo("r")
      .Because("with partitioning off nothing is converted, created or dropped");
  }

  [Test]
  public async Task NothingRegistered_ChangesNothingAsync() {
    await using var services = _services(enabled: true, registerLog: false);

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(await _logKindAsync()).IsEqualTo("r");
  }

  [Test]
  public async Task AnInstanceNotHoldingTheMaintainerDuty_DoesNoDdlAsync() {
    await using var services = _services(enabled: true, holder: DutyHolders.NotHolding());

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(await _logKindAsync()).IsEqualTo("r");
  }

  [Test]
  public async Task TheStep_IsNamed_AndRejectsMissingArgumentsAsync() {
    await Assert.That(_step().Name).IsEqualTo("partition-maintenance");
    await Assert.That(() => _step().RunAsync(null!, CancellationToken.None)).ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => new PartitionMaintenanceStep(null!, NullLogger<PartitionMaintenanceStep>.Instance, new FakeTimeProvider(_now)))
      .ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => new PartitionMaintenanceStep(typeof(WorkCoordinationDbContext), null!, new FakeTimeProvider(_now)))
      .ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => new PartitionMaintenanceStep(typeof(WorkCoordinationDbContext), NullLogger<PartitionMaintenanceStep>.Instance, null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task TheDriver_RegistersTheStepAndTheLog_WithOrWithoutHostLoggingAsync(bool hostLogging) {
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(o => o.UseInMemoryDatabase("PartitionRegistration"));
    if (hostLogging) {
      services.AddLogging();
      services.AddSingleton<TimeProvider>(new FakeTimeProvider(_now));
    }
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<PostgresTestDbContext>().WithDriver.Postgres;
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<PostgresTestDbContext>().WithDriver.Postgres;

    await using var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();
    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<PartitionMaintenanceStep>().ToList();

    await Assert.That(steps).Count().IsGreaterThanOrEqualTo(1);
    await Assert.That(scope.ServiceProvider.GetServices<PartitionedTable>().Distinct()).IsEquivalentTo([PartitionedTables.Log])
      .Because("wh_log is registered by the driver, and read once however often the driver is added");
  }

  // A context that maps nothing, so it names no schema for the framework's tables.
  private sealed class BareContext(DbContextOptions<BareContext> options) : DbContext(options);
}
