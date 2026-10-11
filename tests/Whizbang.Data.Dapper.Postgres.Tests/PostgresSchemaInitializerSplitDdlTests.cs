// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// How a perspective's DDL is split for the column-copy migration: what runs against the old table first,
/// the CREATE TABLE for the new one, and what runs after the swap.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs</code-under-test>
public class PostgresSchemaInitializerSplitDdlTests {
  [Test]
  public async Task DdlWithACreateTable_SplitsAroundItAsync() {
    var (pre, create, post) = PostgresSchemaInitializer._splitDdl("""
      ALTER TABLE wh_per_probe RENAME COLUMN old TO label;
      CREATE TABLE IF NOT EXISTS wh_per_probe (id uuid PRIMARY KEY, label text);
      CREATE INDEX IF NOT EXISTS ix_probe_label ON wh_per_probe (label);
      """);

    await Assert.That(pre).IsEqualTo("ALTER TABLE wh_per_probe RENAME COLUMN old TO label;");
    await Assert.That(create).IsEqualTo("CREATE TABLE IF NOT EXISTS wh_per_probe (id uuid PRIMARY KEY, label text);");
    await Assert.That(post).IsEqualTo("CREATE INDEX IF NOT EXISTS ix_probe_label ON wh_per_probe (label);");
  }

  [Test]
  public async Task DdlWithNoCreateTable_ComesBackWholeWithNothingAroundItAsync() {
    const string ddl = "CREATE INDEX IF NOT EXISTS ix_probe_label ON wh_per_probe (label);";

    var (pre, create, post) = PostgresSchemaInitializer._splitDdl(ddl);

    await Assert.That(pre).IsEmpty();
    await Assert.That(create).IsEqualTo(ddl)
      .Because("DDL with no table to split is run as it is, never dropped");
    await Assert.That(post).IsEmpty();
  }
}
