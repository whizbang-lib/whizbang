// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #1753: a rewrite run by a program outside the process (the pg_repack client). The coordinator owns what it owns
/// for <c>VACUUM FULL</c>: the table-name check, the choice of connection (the schema-initialization one, which owns
/// the table, when configured), refusing a table the application role does not own, and the re-measure afterwards.
/// It hands the program the qualified table and the connection as libpq environment variables. Here the "program"
/// is a delegate that connects with exactly those variables and rewrites, so no client is installed.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public class ExternalTableRewriteTests : EFCoreTestBase {
  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<long> _heapBytesAsync() {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand("SELECT pg_relation_size('wh_settings')", db);
    return (long)(await command.ExecuteScalarAsync())!;
  }

  /// <summary>Rewrites the target the way the client would: connecting only with the variables it was handed.</summary>
  private static async Task<bool> _rewriteAsLibpqWouldAsync(TableRewriteTarget target, CancellationToken cancellationToken) {
    var env = target.ClientEnvironment;
    var connectionString = new NpgsqlConnectionStringBuilder {
      Host = env["PGHOST"],
      Port = int.Parse(env["PGPORT"], System.Globalization.CultureInfo.InvariantCulture),
      Database = env["PGDATABASE"],
      Username = env["PGUSER"],
      Password = env["PGPASSWORD"],
    }.ConnectionString;
    await using var db = new NpgsqlConnection(connectionString);
    await db.OpenAsync(cancellationToken);
    await using var command = new NpgsqlCommand($"VACUUM (FULL) {target.QualifiedTable}", db);
    await command.ExecuteNonQueryAsync(cancellationToken);
    return true;
  }

  private static WorkCoordinationDbContext _context(Action<DbContextOptionsBuilder<WorkCoordinationDbContext>> configure, IServiceProvider services) {
    var builder = new DbContextOptionsBuilder<WorkCoordinationDbContext>()
      .UseApplicationServiceProvider(services)
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    configure(builder);
    return new WorkCoordinationDbContext(builder.Options);
  }

  [Test]
  [Timeout(120000)]
  public async Task WithAnInitConnection_TheProgramGetsItInItsEnvironment_AndTheTableIsReMeasuredAsync(CancellationToken cancellationToken) {
    await TableBloat.ManufactureAsync(ConnectionString, cancellationToken);
    var role = $"wh_app_{Guid.NewGuid():N}"[..30];
    await _execAsync($"""
      CREATE ROLE {role} LOGIN PASSWORD 'data-only';
      GRANT USAGE ON SCHEMA public TO {role};
      GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {role};
      GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO {role};
      """);
    try {
      var services = new ServiceCollection()
        .AddKeyedSingleton<SchemaInitConnection>(typeof(WorkCoordinationDbContext), new SchemaInitConnection(ConnectionString));
      await using var provider = services.BuildServiceProvider();
      var applicationConnection = new NpgsqlConnectionStringBuilder(ConnectionString) { Username = role, Password = "data-only" }.ConnectionString;
      await using var context = _context(b => b.UseNpgsql(applicationConnection), provider);
      var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions());
      var candidate = (await coordinator.GetTablesNeedingRewriteAsync(cancellationToken)).Single(c => c.TableName == "wh_settings");
      var before = await _heapBytesAsync();
      TableRewriteTarget? seen = null;

      var ratio = await coordinator.RewriteTableWithAsync("wh_settings", (target, ct) => {
        seen = target;
        return _rewriteAsLibpqWouldAsync(target, ct);
      }, cancellationToken);

      var init = new NpgsqlConnectionStringBuilder(ConnectionString);
      await Assert.That(seen).IsNotNull();
      await Assert.That(seen!.QualifiedTable).EndsWith("wh_settings");
      await Assert.That(seen.ClientEnvironment["PGUSER"]).IsEqualTo(init.Username)
        .Because("the init connection owns the table; the data-only application role could not rewrite it");
      await Assert.That(seen.ClientEnvironment["PGPASSWORD"]).IsEqualTo(init.Password);
      await Assert.That(seen.ClientEnvironment["PGDATABASE"]).IsEqualTo(init.Database);
      await Assert.That(await _heapBytesAsync()).IsLessThan(before / 2);
      await Assert.That(ratio!.Value).IsLessThan(candidate.BloatRatio)
        .Because("the coordinator analyzes and re-measures after the program, so the step can tell a rewrite that worked");
    } finally {
      await _execAsync($"DROP OWNED BY {role}; DROP ROLE {role};");
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task WithoutAnInitConnection_AConfiguredStringIsHandedOver_PasswordIncludedAsync(CancellationToken cancellationToken) {
    await using var provider = new ServiceCollection().BuildServiceProvider();
    await using var context = _context(b => b.UseNpgsql(ConnectionString), provider);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions());
    TableRewriteTarget? seen = null;

    var ratio = await coordinator.RewriteTableWithAsync("wh_settings", (target, _) => {
      seen = target;
      return Task.FromResult(true);
    }, cancellationToken);

    await Assert.That(seen!.ClientEnvironment["PGPASSWORD"]).IsEqualTo(new NpgsqlConnectionStringBuilder(ConnectionString).Password)
      .Because("the configured string, unlike the live connection's, still carries the password");
    await Assert.That(ratio.HasValue).IsTrue().Because("the table was analyzed and measured after the program reported success");
  }

  [Test]
  [Timeout(120000)]
  public async Task WithOnlyADataSource_NoPasswordIsHandedOver_AndLibpqFindsItsOwnAsync(CancellationToken cancellationToken) {
    await using var dataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();
    await using var provider = new ServiceCollection().BuildServiceProvider();
    await using var context = _context(b => b.UseNpgsql(dataSource), provider);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions());
    TableRewriteTarget? seen = null;

    _ = await coordinator.RewriteTableWithAsync("wh_settings", (target, _) => {
      seen = target;
      return Task.FromResult(true);
    }, cancellationToken);

    await Assert.That(seen!.ClientEnvironment.ContainsKey("PGPASSWORD")).IsFalse()
      .Because("a data source never gives its password back; libpq then reads the host's password file or service file");
    await Assert.That(seen.ClientEnvironment["PGDATABASE"]).IsEqualTo(new NpgsqlConnectionStringBuilder(ConnectionString).Database);
  }

  [Test]
  [Timeout(120000)]
  public async Task AProgramThatDidNotRewrite_IsNotMeasuredAsync(CancellationToken cancellationToken) {
    await using var provider = new ServiceCollection().BuildServiceProvider();
    await using var context = _context(b => b.UseNpgsql(ConnectionString), provider);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions());

    var ratio = await coordinator.RewriteTableWithAsync("wh_settings", (_, _) => Task.FromResult(false), cancellationToken);

    await Assert.That(ratio).IsNull().Because("the step then leaves the request queued");
  }

  [Test]
  [Timeout(120000)]
  public async Task ATableTheApplicationRoleDoesNotOwn_IsNotHandedOverAsync(CancellationToken cancellationToken) {
    var role = $"wh_app_{Guid.NewGuid():N}"[..30];
    await _execAsync($"""
      CREATE ROLE {role} LOGIN PASSWORD 'data-only';
      GRANT USAGE ON SCHEMA public TO {role};
      GRANT SELECT ON ALL TABLES IN SCHEMA public TO {role};
      """);
    try {
      await using var provider = new ServiceCollection().BuildServiceProvider();
      var applicationConnection = new NpgsqlConnectionStringBuilder(ConnectionString) { Username = role, Password = "data-only" }.ConnectionString;
      await using var context = _context(b => b.UseNpgsql(applicationConnection), provider);
      var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions());
      var called = false;

      var ratio = await coordinator.RewriteTableWithAsync("wh_settings", (_, _) => {
        called = true;
        return Task.FromResult(true);
      }, cancellationToken);

      await Assert.That(ratio).IsNull();
      await Assert.That(called).IsFalse().Because("pg_repack would fail on a table the role does not own; it is reported, not attempted (#1351)");
    } finally {
      await _execAsync($"DROP OWNED BY {role}; DROP ROLE {role};");
    }
  }

  [Test]
  [Arguments("orders")]
  [Arguments("wh_x; DROP TABLE wh_outbox")]
  public async Task ANameThatIsNotAFrameworkTable_IsRefusedAsync(string tableName) {
    await using var provider = new ServiceCollection().BuildServiceProvider();
    await using var context = _context(b => b.UseNpgsql(ConnectionString), provider);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions());

    await Assert.That(() => coordinator.RewriteTableWithAsync(tableName, (_, _) => Task.FromResult(true)))
      .Throws<ArgumentException>();
    await Assert.That(() => coordinator.RewriteTableWithAsync("wh_settings", null!)).Throws<ArgumentNullException>();
  }
}
