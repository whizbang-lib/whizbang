// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Core.Tests.Schema;

/// <summary>
/// The bootstrap statement for the dropped <c>wh_receptor_processing</c>: found by the drop migration's name whatever its
/// number, and keyed on the drop's ledger row recording the hash it has now.
/// </summary>
/// <docs>operations/infrastructure/migrations#unused-tables-are-dropped</docs>
public class RetiredReceptorTableTests {
  [Test]
  public async Task FindDropMigration_FindsItByName_WhateverItsNumberAsync() {
    var found = RetiredReceptorTable.FindDropMigration(["031_ClaimingIndexes", "512_ReceptorProcessingTableIsDropped"]);

    await Assert.That(found).IsEqualTo("512_ReceptorProcessingTableIsDropped")
      .Because("the Dapper driver names a migration without its extension, the EF Core driver with it");
  }

  [Test]
  public async Task FindDropMigration_WhenItIsNotAmongThem_IsNullAsync() {
    await Assert.That(RetiredReceptorTable.FindDropMigration(["031_ClaimingIndexes.sql"])).IsNull();
  }

  [Test]
  public async Task BootstrapSql_CreatesTheTableOnlyWhileTheDropsRowDoesNotRecordItsHashAsync() {
    var sql = RetiredReceptorTable.BootstrapSql("\"svc\"", "464_ReceptorProcessingTableIsDropped.sql", "abc'd");

    await Assert.That(sql).Contains("to_regclass('\"svc\".wh_schema_migrations') IS NOT NULL");
    await Assert.That(sql).Contains("m.file_name = '464_ReceptorProcessingTableIsDropped.sql'");
    await Assert.That(sql).Contains("m.content_hash = 'abc''d'").Because("a value is written as a literal, its quotes doubled");
    await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS \"svc\".wh_receptor_processing");
  }

  [Test]
  public async Task Arguments_AreRequiredAsync() {
    await Assert.That(() => RetiredReceptorTable.FindDropMigration(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => RetiredReceptorTable.BootstrapSql(null!, "n", "h")).Throws<ArgumentNullException>();
    await Assert.That(() => RetiredReceptorTable.BootstrapSql("s", null!, "h")).Throws<ArgumentNullException>();
    await Assert.That(() => RetiredReceptorTable.BootstrapSql("s", "n", null!)).Throws<ArgumentNullException>();
  }
}
