// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Versioning;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// #2122, owner decision 2: how each migration is reversed. A library migration declares one of three kinds in its
/// header (<c>-- Reversal: reversible</c>, <c>lossy</c> or <c>irreversible</c>), and a reversible or lossy one ships its
/// reverse beside it as <c>NNN_Name.down.sql</c>; a migration that declares nothing is one from before #2122 and counts
/// as irreversible. Generated application schema has its reverse generated: a perspective table the pass created is
/// dropped (lossy: its rows go with it), and the associations are registered again by the older version's own pass.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/MigrationReversal.cs</code-under-test>
/// <docs>operations/infrastructure/migrations#how-a-migration-is-reversed</docs>
[Category("Unit")]
[Category("Shard1")]
public class MigrationReversalTests {
  private static readonly System.Reflection.Assembly _probes = typeof(MigrationReversalTests).Assembly;

  private static string _transform(string sql) => sql.Replace("__SCHEMA__", "\"svc\"", StringComparison.Ordinal);

  [Test]
  [Arguments("-- Reversal: reversible", SchemaChangeReversal.Reversible)]
  [Arguments("-- Reversal: lossy (dropping the column discards what the newer version wrote)", SchemaChangeReversal.Lossy)]
  [Arguments("--   Reversal:   irreversible", SchemaChangeReversal.Irreversible)]
  public async Task TheHeader_DeclaresTheKindAsync(string header, SchemaChangeReversal kind) {
    await Assert.That(MigrationReversal.DeclaredBy($"-- Migration: 999_X.sql\n{header}\nSELECT 1;")).IsEqualTo(kind);
  }

  [Test]
  public async Task AMigrationThatDeclaresNothing_DeclaresNoKindAsync() {
    await Assert.That(MigrationReversal.DeclaredBy("-- Migration: 100_X.sql\nSELECT 1;")).IsNull();
  }

  [Test]
  public async Task AKindThatIsNotOneOfTheThree_IsRefusedAsync() {
    await Assert.That(() => MigrationReversal.DeclaredBy("-- Reversal: maybe\nSELECT 1;"))
      .Throws<InvalidOperationException>().WithMessageContaining("maybe");
  }

  [Test]
  public async Task ALegacyMigration_RecordsNoReversalAsync() {
    await Assert.That(MigrationReversal.ForLibraryMigration("100_X.sql", "SELECT 1;", _transform, _probes)).IsNull()
      .Because("every migration before #2122 has no reverse and counts as irreversible");
  }

  [Test]
  public async Task AnIrreversibleMigration_RecordsThatItCannotBeReversedAsync() {
    var reversal = MigrationReversal.ForLibraryMigration("998_X.sql", "-- Reversal: irreversible\nSELECT 1;", _transform, _probes);

    await Assert.That(reversal).IsEqualTo(new MigrationReversal(SchemaChangeReversal.Irreversible, null));
  }

  [Test]
  public async Task AReversibleMigration_CarriesItsReverseScript_TransformedLikeTheForwardOneAsync() {
    var reversal = MigrationReversal.ForLibraryMigration("999_ReversalProbe.sql", "-- Reversal: reversible\nSELECT 1;", _transform, _probes);

    await Assert.That(reversal!.Kind).IsEqualTo(SchemaChangeReversal.Reversible);
    await Assert.That(reversal.DownSql).Contains("DROP TABLE IF EXISTS \"svc\".wh_reversal_probe;");
  }

  [Test]
  public async Task AReversibleMigrationWithoutItsReverseScript_IsRefusedNamingTheFileAsync() {
    await Assert.That(() => MigrationReversal.ForLibraryMigration("997_Missing.sql", "-- Reversal: lossy\nSELECT 1;", _transform, _probes))
      .Throws<InvalidOperationException>().WithMessageContaining("997_Missing.down.sql");
  }

  [Test]
  public async Task ANewPerspectiveTable_IsReversedByDroppingIt_WithItsRowsAsync() {
    var reversal = MigrationReversal.DropTable("\"svc\".\"wh_per_orders\"");

    await Assert.That(reversal.Kind).IsEqualTo(SchemaChangeReversal.Lossy);
    await Assert.That(reversal.DownSql).IsEqualTo("DROP TABLE IF EXISTS \"svc\".\"wh_per_orders\";");
  }

  [Test]
  public async Task TheAssociations_AreRegisteredAgainByTheOlderVersionsPassAsync() {
    await Assert.That(MigrationReversal.RegisteredAgain).IsEqualTo(new MigrationReversal(SchemaChangeReversal.Reversible, string.Empty));
  }

  [Test]
  public async Task TheLedgerText_OfEachKind_IsItsNameAsync() {
    await Assert.That(MigrationReversal.LedgerText(SchemaChangeReversal.Reversible)).IsEqualTo("reversible");
    await Assert.That(MigrationReversal.LedgerText(SchemaChangeReversal.Lossy)).IsEqualTo("lossy");
    await Assert.That(MigrationReversal.LedgerText(SchemaChangeReversal.Irreversible)).IsEqualTo("irreversible");
    await Assert.That(MigrationReversal.FromLedgerText("lossy")).IsEqualTo(SchemaChangeReversal.Lossy);
    await Assert.That(MigrationReversal.FromLedgerText("reversible")).IsEqualTo(SchemaChangeReversal.Reversible);
    await Assert.That(MigrationReversal.FromLedgerText(null)).IsEqualTo(SchemaChangeReversal.Irreversible);
    await Assert.That(MigrationReversal.FromLedgerText("irreversible")).IsEqualTo(SchemaChangeReversal.Irreversible);
  }

  [Test]
  public async Task TheLibraryProvider_ReadsAMigrationsReverseScript_ByItsNameWithoutTheExtension_RenderedForItsSchemaAsync() {
    // The Dapper driver names a migration without ".sql" and renders it with the raw schema name, as its forward text.
    var provider = new PostgresMigrationProvider(_probes, "svc");

    var reversal = provider.ReversalOf(new Whizbang.Core.Data.MigrationScript("999_ReversalProbe", "-- Reversal: lossy\nSELECT 1;"));

    await Assert.That(reversal!.Kind).IsEqualTo(SchemaChangeReversal.Lossy);
    await Assert.That(reversal.DownSql).Contains("DROP TABLE IF EXISTS svc.wh_reversal_probe;");
    await Assert.That(reversal.DownSql).DoesNotContain("__SCHEMA__");
  }
}
