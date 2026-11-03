// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Versioning;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2027: the migration ledger records the application version beside the library version, as the instance rows record
/// it, so the version record of whoever migrated can be compared with the fleet's. Before this it recorded only the
/// DbContext assembly's name and assembly version, as a note nothing read.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaLedgerVersion.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard2")]
public class SchemaLedgerApplicationVersionTests : EFCoreTestBase {
  [Test]
  [Timeout(120000)]
  public async Task APass_RecordsTheApplicationVersionInTheLedgerAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    // A drifted hash makes the next start apply that migration again, which records the pass's version.
    await _execAsync(conn, @"
      UPDATE wh_schema_migrations SET content_hash = 'drifted'
      WHERE file_name = (SELECT file_name FROM wh_schema_migrations WHERE owner = 'whizbang' ORDER BY file_name LIMIT 1)",
      cancellationToken);
    var services = new ServiceCollection();
    services.AddSingleton<IApplicationVersionProvider>(new ApplicationVersionProvider(ApplicationVersion.FromRecorded("deploy:77")));
    await using var provider = services.BuildServiceProvider();

    await using var context = CreateDbContext();
    await context.EnsureWhizbangDatabaseInitializedAsync(serviceProvider: provider, cancellationToken: cancellationToken);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_schema_versions WHERE application_version = 'deploy:77'";
    await Assert.That((long)(await cmd.ExecuteScalarAsync(cancellationToken))!).IsEqualTo(1L);
  }

  [Test]
  public async Task ApplicationVersionOf_ReadsTheHostsProvider_OrNothingAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IApplicationVersionProvider>(new ApplicationVersionProvider(ApplicationVersion.FromRecorded("2.0.0")));
    await using var provider = services.BuildServiceProvider();
    await using var empty = new ServiceCollection().BuildServiceProvider();

    await Assert.That(SchemaLedgerVersion.ApplicationVersionOf(provider)).IsEqualTo("2.0.0");
    await Assert.That(SchemaLedgerVersion.ApplicationVersionOf(empty)).IsNull();
    await Assert.That(SchemaLedgerVersion.ApplicationVersionOf(null)).IsNull();
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync(ct);
  }
}
