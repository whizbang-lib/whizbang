// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A table rewrite under a least-privilege application role. <c>VACUUM FULL</c> needs the table owner's rights,
/// and Postgres does not refuse a role without them: it warns, skips the table and reports success. So the
/// rewrite is judged by the space it reclaims, never by the absence of an error. Without a schema-initialization
/// connection, a table the application role does not own is not attempted at all (#1351).
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public class TableRewriteUnderDataOnlyRoleTests : EFCoreTestBase {

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

  /// <summary>
  /// With a schema-initialization connection configured, the rewrite runs through it and reclaims the heap. Without
  /// one, the data-only role's <c>VACUUM FULL</c> is skipped by Postgres and the heap keeps its size: that case is
  /// what shows the role really lacks the rights, so the other case is not passing for some unrelated reason.
  /// </summary>
  [Test]
  [Timeout(120000)]
  [Arguments(true)]
  [Arguments(false)]
  public async Task UnderADataOnlyApplicationRole_TheInitConnectionDoesTheRewriteAsync(
      bool withInitConnection, CancellationToken cancellationToken) {
    await TableBloat.ManufactureAsync(ConnectionString, cancellationToken);
    var role = $"wh_app_{Guid.NewGuid():N}"[..30];
    await _execAsync($"""
      CREATE ROLE {role} LOGIN PASSWORD 'data-only';
      GRANT USAGE ON SCHEMA public TO {role};
      GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {role};
      GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO {role};
      GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO {role};
      """);
    try {
      var applicationConnection = new NpgsqlConnectionStringBuilder(ConnectionString) {
        Username = role,
        Password = "data-only",
      }.ConnectionString;
      var services = new ServiceCollection();
      if (withInitConnection) {
        services.AddKeyedSingleton<SchemaInitConnection>(typeof(WorkCoordinationDbContext), new SchemaInitConnection(ConnectionString));
      }
      await using var provider = services.BuildServiceProvider();
      // As AddDbContext does: the context carries the application's services.
      await using var applicationContext = new WorkCoordinationDbContext(
        new DbContextOptionsBuilder<WorkCoordinationDbContext>()
          .UseNpgsql(applicationConnection)
          .UseApplicationServiceProvider(provider)
          .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
          .Options);
      var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        applicationContext, JsonContextRegistry.CreateCombinedOptions());
      var before = await _heapBytesAsync();
      var candidate = (await coordinator.GetTablesNeedingRewriteAsync(cancellationToken)).Single(c => c.TableName == "wh_settings");

      var ratioAfter = await coordinator.RewriteTableAsync("wh_settings", cancellationToken);

      var after = await _heapBytesAsync();
      if (withInitConnection) {
        await Assert.That(after).IsLessThan(before / 2)
          .Because("the init connection, which owns the table, rewrote it and the deleted rows' space came back");
        await Assert.That(ratioAfter!.Value).IsLessThan(candidate.BloatRatio);
      } else {
        await Assert.That(after).IsEqualTo(before)
          .Because("the application role does not own the table, so nothing is reclaimed");
        await Assert.That(ratioAfter).IsNull()
          .Because("a table the role does not own is reported and not attempted (#1351), so the step leaves the request queued");
      }
    } finally {
      await _execAsync($"DROP OWNED BY {role}; DROP ROLE {role};");
    }
  }
}
