// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.SecondPass;

/// <summary>
/// Which embedded resources count as migrations, in one class: a resource outside the migrations
/// folder is not one, a file in the folder that is not SQL (the constants file) is not one, and every
/// SQL file in the folder is. No database.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresMigrationProvider.cs</code-under-test>
[Category("Unit")]
[Category("Shard2")]
public class PostgresMigrationResourceFilterTests {

  // The framework's own assembly embeds its migrations and a constants file beside them. Only the SQL
  // files are migrations: the constants file must never be applied as a script.
  [Test]
  public async Task GetMigrations_FromTheFrameworkAssembly_ListsOnlyTheSqlFilesAsync() {
    var migrations = new PostgresMigrationProvider().GetMigrations();

    await Assert.That(migrations.Count).IsGreaterThan(0);
    await Assert.That(migrations.Any(m => m.Name.StartsWith("constants", StringComparison.Ordinal))).IsFalse()
      .Because("the constants file sits in the migrations folder but is not a SQL script");
  }

  // An assembly whose resources all live outside a migrations folder has no migrations, rather than
  // having its other resources read as scripts.
  [Test]
  public async Task GetMigrations_FromAnAssemblyWithNoMigrationsFolder_ListsNothingAsync() {
    var assembly = typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly;
    var provider = new PostgresMigrationProvider(assembly, "public");

    var migrations = provider.GetMigrations();

    await Assert.That(assembly.GetManifestResourceNames()).IsNotEmpty()
      .Because("the assembly must carry resources, or the filter is never asked about one");
    await Assert.That(migrations).IsEmpty();
  }
}
