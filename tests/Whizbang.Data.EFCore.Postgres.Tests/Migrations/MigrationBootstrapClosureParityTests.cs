// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Parity guard (#2561): the Dapper driver reads the bootstrap closure from the embedded migrations at run time, and the
/// EF Core driver's generator extracts it at build time. Both drivers elect a migrator over that closure, so the two
/// readers must agree on every shipped migration; a reader that drifts fails here, not on an empty database.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/MigrationBootstrapClosure.cs</code-under-test>
[Category("Shard3")]
public class MigrationBootstrapClosureParityTests {
  [Test]
  public async Task EveryShippedMigration_YieldsTheSameRegions_AtRunTimeAsTheGeneratorExtractsAsync() {
    var migrations = new PostgresMigrationProvider().GetMigrations();
    var withRegions = 0;

    foreach (var migration in migrations) {
      var atRunTime = MigrationBootstrapClosure.Extract(migration.Sql);
      await Assert.That(atRunTime).IsEqualTo(MigrationBootstrapRegions.Extract(migration.Sql))
        .Because($"{migration.Name}: both drivers elect over the same closure");
      withRegions += atRunTime is null ? 0 : 1;
    }

    await Assert.That(withRegions).IsGreaterThan(3)
      .Because("a positive control: several migrations carry bootstrap regions");
    await Assert.That([.. MigrationBootstrapClosure.RegionsOf(new PostgresMigrationProvider()).Select(r => r.Name)])
      .IsEquivalentTo(migrations.Where(m => MigrationBootstrapRegions.Extract(m.Sql) is not null).Select(m => m.Name).ToList(),
        TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the closure lists every migration with a region, in file order");
  }

  [Test]
  public async Task AMarkerThatOpensNothing_OrIsOnlyMentioned_YieldsNoRegionAsync() {
    const string EMPTY = "SELECT 1;\n-- @whizbang:bootstrap-begin\n   \n-- @whizbang:bootstrap-end\nSELECT 2;\n";
    const string MENTIONED = "-- see -- @whizbang:bootstrap-begin for the marker\nSELECT 1;\n";

    await Assert.That(MigrationBootstrapClosure.Extract(EMPTY)).IsNull();
    await Assert.That(MigrationBootstrapRegions.Extract(EMPTY)).IsNull();
    await Assert.That(MigrationBootstrapClosure.Extract(MENTIONED)).IsNull();
    await Assert.That(MigrationBootstrapRegions.Extract(MENTIONED)).IsNull();
    await Assert.That(MigrationBootstrapClosure.Extract("SELECT 1;\n")).IsNull();
  }

  [Test]
  public async Task AnUnclosedRegion_RunsToTheEndOfTheFile_AsTheGeneratorReadsItAsync() {
    const string UNCLOSED = "SELECT 0;\n-- @whizbang:bootstrap-begin\nCREATE TABLE a (id int);\nCREATE TABLE b (id int);\n";

    await Assert.That(MigrationBootstrapClosure.Extract(UNCLOSED)).IsEqualTo("CREATE TABLE a (id int);\nCREATE TABLE b (id int);\n\n");
    await Assert.That(MigrationBootstrapClosure.Extract(UNCLOSED)).IsEqualTo(MigrationBootstrapRegions.Extract(UNCLOSED));
  }
}
