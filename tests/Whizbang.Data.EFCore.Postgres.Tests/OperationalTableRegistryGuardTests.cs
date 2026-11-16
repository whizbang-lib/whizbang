// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2354, the gravity: every framework table in the migrated schema is classified as State or Rolling, so a migration
/// that creates a table with no retention rule fails the build. The table list is read from the real schema the
/// migrations built, never written by hand; the registry is checked against it in both directions.
/// </summary>
/// <docs>operations/infrastructure/operational-retention#every-table-is-classified</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/OperationalTableRegistry.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class OperationalTableRegistryGuardTests : EFCoreTestBase {

  /// <summary>Every ordinary or partitioned framework table in the schema, partitions excluded (they follow their parent).</summary>
  private async Task<List<string>> _schemaTablesAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT c.relname
      FROM pg_class c
      JOIN pg_namespace n ON n.oid = c.relnamespace
      WHERE n.nspname = current_schema()
        AND c.relkind IN ('r', 'p')
        AND NOT c.relispartition
        AND c.relpersistence <> 't'
        AND c.relname LIKE 'wh\_%'
      ORDER BY c.relname
      """;
    var tables = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      tables.Add(reader.GetString(0));
    }
    return tables;
  }

  [Test]
  public async Task TheGuard_ReadsTheRealSchemaAsync() {
    var tables = await _schemaTablesAsync();

    await Assert.That(tables.Count).IsGreaterThan(90).Because("a guard over no tables proves nothing");
    await Assert.That(tables).Contains("wh_event_store");
    await Assert.That(tables).Contains("wh_integrity_origin_rewrites");
  }

  [Test]
  public async Task EveryTableInTheSchema_IsClassifiedAsync() {
    var unclassified = (await _schemaTablesAsync()).Where(t => OperationalTableRegistry.Find(t) is null).ToList();

    await Assert.That(unclassified).IsEmpty()
      .Because("a table with no State or Rolling entry has no retention rule; add it to OperationalTableRegistry with "
             + "what bounds it (State) or its category and what removes its rows (Rolling)");
  }

  [Test]
  public async Task EveryRegistryEntry_IsATableInTheSchemaAsync() {
    var schema = (await _schemaTablesAsync()).ToHashSet(StringComparer.Ordinal);

    var stale = OperationalTableRegistry.Tables.Select(t => t.Name).Where(n => !schema.Contains(n)).ToList();

    await Assert.That(stale).IsEmpty().Because("an entry for a table no migration creates any more is stale");
  }

  [Test]
  public async Task TheRegistry_NamesEachTableOnce_InNameOrder_AndEveryRollingTableHasACategoryAsync() {
    var names = OperationalTableRegistry.Tables.Select(t => t.Name).ToList();

    await Assert.That(names).IsEquivalentTo(names.Distinct().Order(StringComparer.Ordinal).ToList(),
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(OperationalTableRegistry.Tables.Where(t =>
        (t.Class == OperationalTableClass.Rolling) != t.Category.HasValue)).IsEmpty()
      .Because("a Rolling table rests by its category, and a State table has none");
    await Assert.That(OperationalTableRegistry.Tables.Where(t => t.SweptHere && t.Class != OperationalTableClass.Rolling)).IsEmpty();
  }

  [Test]
  public async Task APerspectiveTable_IsStateByItsPrefix_AndAnUnknownTableIsNotClassifiedAsync() {
    await Assert.That(OperationalTableRegistry.Find("wh_per_order_summary")!.Class).IsEqualTo(OperationalTableClass.State);
    await Assert.That(OperationalTableRegistry.Find("wh_new_operational_table")).IsNull();
  }

  [Test]
  public async Task TheSweep_RemovesExactlyTheTablesTheRegistryMarksSweptHereAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT DISTINCT table_name FROM wh_sweep_operational_retention(NOW(), '{}'::jsonb, 1) ORDER BY 1";
    var swept = new List<string>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        swept.Add(reader.GetString(0));
      }
    }

    await Assert.That(swept).IsEquivalentTo(
      OperationalTableRegistry.Tables.Where(t => t.SweptHere).Select(t => t.Name).Order(StringComparer.Ordinal).ToList())
      .Because("the SQL sweep and the registry must agree on which tables the operational retention removes rows from");
  }
}
