// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The bootstrap's statement for <c>wh_receptor_processing</c>, a table nothing reads or writes, which migration 464
/// drops: it creates the table only until that migration has recorded its current hash.
/// </summary>
/// <remarks>
/// <para>
/// Migration 031 creates an index on the table at the top level, and a shipped migration is never edited. So the table
/// has to exist whenever 031 runs: on a database migrating from empty, and on any pass that runs 031 again (an upgrade
/// whose recorded hashes predate the current files, which the replay of every migration over its own earlier run
/// stands for). The bootstrap runs before the migrations on every pass, and a table it always declared would be
/// created again on every pass after the drop. Keyed on the drop's own ledger row: until it records the hash the drop
/// has now, the drop has yet to run on this pass (or runs again on it), so the table is created and the drop removes
/// it once 031 is done; once it records that hash, the drop has run and stays skipped, and so does the table.
/// </para>
/// <para>
/// Each driver passes the hash it records for the drop, computed over the text it hashes (the Dapper driver over the
/// schema-substituted text, the EF Core driver over its transformed text), so the comparison is the ledger's own.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#unused-tables-are-dropped</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/UnusedTablesAreDroppedTests.ReceptorTable.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperRetiredReceptorTableTests.cs</tests>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Schema/RetiredReceptorTableTests.cs</tests>
public static class RetiredReceptorTable {
  /// <summary>The drop migration's name after its number, so the lookup survives a renumbering. A driver names a
  /// migration with or without its <c>.sql</c> extension, so the extension is not part of it.</summary>
  public const string DROP_MIGRATION_NAME = "_ReceptorProcessingTableIsDropped";

  /// <summary>The drop migration's name among <paramref name="migrationNames"/>, or null when it is not among them.</summary>
  /// <param name="migrationNames">The migration file names a driver applies.</param>
  /// <returns>The name, or null.</returns>
  public static string? FindDropMigration(IEnumerable<string> migrationNames) {
    ArgumentNullException.ThrowIfNull(migrationNames);
    return migrationNames.FirstOrDefault(n => n.Contains(DROP_MIGRATION_NAME, StringComparison.Ordinal));
  }

  /// <summary>
  /// The statement that creates the table while the drop migration's ledger row does not record
  /// <paramref name="dropMigrationHash"/>.
  /// </summary>
  /// <param name="schema">The schema as SQL writes it (quoted, or a placeholder the driver substitutes).</param>
  /// <param name="dropMigrationName">The drop migration's file name, as the ledger records it.</param>
  /// <param name="dropMigrationHash">The hash the driver records for the drop migration as it is now.</param>
  /// <returns>One <c>DO</c> block.</returns>
  public static string BootstrapSql(string schema, string dropMigrationName, string dropMigrationHash) {
    ArgumentNullException.ThrowIfNull(schema);
    ArgumentNullException.ThrowIfNull(dropMigrationName);
    ArgumentNullException.ThrowIfNull(dropMigrationHash);
    return $$"""
      DO $whizbang_retired_receptor$
      DECLARE
        v_dropped BOOLEAN := FALSE;
      BEGIN
        -- The ledger is read only once it exists: a statement naming a missing table fails when it is prepared,
        -- whatever an OR beside it says, and a database migrating from empty has no ledger yet.
        IF to_regclass('{{schema}}.wh_schema_migrations') IS NOT NULL THEN
          SELECT EXISTS (SELECT 1 FROM {{schema}}.wh_schema_migrations m
                         WHERE m.file_name = '{{_literal(dropMigrationName)}}'
                           AND m.content_hash = '{{_literal(dropMigrationHash)}}')
          INTO v_dropped;
        END IF;
        IF NOT v_dropped THEN
          CREATE TABLE IF NOT EXISTS {{schema}}.wh_receptor_processing (
            id UUID NOT NULL PRIMARY KEY,
            event_id UUID NOT NULL,
            receptor_name TEXT NOT NULL,
            stream_id UUID NOT NULL,
            partition_number INTEGER,
            status SMALLINT NOT NULL DEFAULT 0,
            attempts INTEGER NOT NULL DEFAULT 0,
            error TEXT,
            instance_id UUID,
            lease_expiry TIMESTAMPTZ,
            started_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
            claimed_at TIMESTAMPTZ,
            completed_at TIMESTAMPTZ,
            processed_at TIMESTAMPTZ
          );
        END IF;
      END
      $whizbang_retired_receptor$;
      """;
  }

  private static string _literal(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
