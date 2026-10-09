// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;

namespace Whizbang.Data.Postgres.Schema;

/// <summary>
/// Reads the objects on a schema's perspective tables from the catalog, reads and writes the ledger, and builds
/// the statement that drops one object.
/// </summary>
/// <remarks>
/// Only objects a reconcile may own are read: indexes that do not back a constraint, check / unique /
/// exclusion / foreign-key constraints (never the primary key), user triggers and extended statistics. The
/// table, its columns and its primary key are the table's structure and are left to the schema pass.
/// </remarks>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedSchemaReconcilerTests.cs</tests>
internal static class ManagedSchemaCatalog {
  private const string LIVE_OBJECTS_SQL = """
    WITH tables AS (
      SELECT c.oid, c.relname
      FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
      WHERE n.nspname = @schema AND c.relkind IN ('r', 'p') AND c.relname LIKE 'wh\_per\_%'
    )
    SELECT t.relname, i.relname, 'index', pg_get_indexdef(ix.indexrelid), obj_description(ix.indexrelid, 'pg_class')
    FROM pg_index ix JOIN tables t ON t.oid = ix.indrelid JOIN pg_class i ON i.oid = ix.indexrelid
    WHERE NOT EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conindid = ix.indexrelid)
    UNION ALL
    SELECT t.relname, k.conname, 'constraint', pg_get_constraintdef(k.oid), obj_description(k.oid, 'pg_constraint')
    FROM pg_constraint k JOIN tables t ON t.oid = k.conrelid
    WHERE k.contype IN ('c', 'u', 'x', 'f')
    UNION ALL
    SELECT t.relname, g.tgname, 'trigger', pg_get_triggerdef(g.oid), obj_description(g.oid, 'pg_trigger')
    FROM pg_trigger g JOIN tables t ON t.oid = g.tgrelid
    WHERE NOT g.tgisinternal
    UNION ALL
    SELECT t.relname, s.stxname, 'statistics', pg_get_statisticsobjdef(s.oid), obj_description(s.oid, 'pg_statistic_ext')
    FROM pg_statistic_ext s JOIN tables t ON t.oid = s.stxrelid
    """;

  /// <summary>Every object a reconcile may own on the schema's perspective tables.</summary>
  public static async Task<List<LiveSchemaObject>> ReadLiveAsync(NpgsqlConnection connection, string schema, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(LIVE_OBJECTS_SQL, connection);
    command.Parameters.AddWithValue(nameof(schema), schema);
    var objects = new List<LiveSchemaObject>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      objects.Add(new LiveSchemaObject(reader.GetString(0), reader.GetString(1), ParseKind(reader.GetString(2)),
        reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
    }
    return objects;
  }

  /// <summary>Every ledger row.</summary>
  public static async Task<List<LedgerRow>> ReadLedgerAsync(NpgsqlConnection connection, string ledgerTable, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(
      $"SELECT table_name, object_name, object_kind, owner, status, code_pinned, code_pin_source, code_pin_reason, db_pinned, db_pin_source, db_pin_reason, undeclared_since FROM {ledgerTable}",
      connection);
    var rows = new List<LedgerRow>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      rows.Add(new LedgerRow(
        reader.GetString(0), reader.GetString(1), ParseKind(reader.GetString(2)),
        _text(reader, 3), reader.GetString(4),
        reader.GetBoolean(5), _text(reader, 6), _text(reader, 7),
        reader.GetBoolean(8), _text(reader, 9), _text(reader, 10),
        reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11)));
    }
    return rows;
  }

  /// <summary>
  /// The pairs <c>wh_ensure_index</c> recorded: a declared index it did not build because another index on the table
  /// has the same definition, and that index's name. Empty when the schema has no record of any.
  /// </summary>
  public static async Task<List<(string Table, string Declared, string Existing)>> ReadEquivalentsAsync(
      NpgsqlConnection connection, string schema, CancellationToken cancellationToken) {
    var table = PgIdentifier.Quote(schema) + ".wh_index_equivalents";
    var pairs = new List<(string, string, string)>();
    await using (var exists = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", connection)) {
      exists.Parameters.AddWithValue("t", table);
      if (await exists.ExecuteScalarAsync(cancellationToken) is not true) {
        return pairs;
      }
    }
    await using var command = new NpgsqlCommand($"SELECT table_name, declared_name, existing_name FROM {table}", connection);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      pairs.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
    }
    return pairs;
  }

  private static string? _text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

  /// <summary>
  /// Writes the plan's ledger rows in one transaction. The code pin is written as planned; the database pin is only
  /// ever added (a new <c>whizbang:pin</c> comment) and never cleared or rewritten here.
  /// </summary>
  public static async Task WriteLedgerAsync(
      NpgsqlConnection connection, string ledgerTable, IReadOnlyList<LedgerWrite> records, CancellationToken cancellationToken) {
    if (records.Count == 0) {
      return;
    }

    await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
    foreach (var record in records) {
      await using var command = new NpgsqlCommand($"""
        INSERT INTO {ledgerTable} AS m (table_name, object_name, object_kind, definition, owner, status, declared_by,
          undeclared_since, last_seen_at, code_pinned, code_pin_source, code_pin_reason,
          db_pinned, db_pin_source, db_pin_reason, db_pinned_by, db_pinned_at, retired_at)
        VALUES (@table, @name, @kind, NULLIF(@definition, ''), @owner, @status, @declared_by, @undeclared_since,
          CASE WHEN @status = '{ManagedStatuses.RETIRED}' THEN NULL ELSE NOW() END,
          @code_pinned, @code_pin_source, @code_pin_reason,
          @adds_db_pin, CASE WHEN @adds_db_pin THEN '{PinSources.DB_COMMENT}' END, @db_pin_reason,
          CASE WHEN @adds_db_pin THEN current_user END, CASE WHEN @adds_db_pin THEN NOW() END,
          CASE WHEN @status = '{ManagedStatuses.RETIRED}' THEN NOW() END)
        ON CONFLICT (table_name, object_name) DO UPDATE SET
          object_kind = EXCLUDED.object_kind,
          definition = COALESCE(EXCLUDED.definition, m.definition),
          owner = EXCLUDED.owner,
          status = EXCLUDED.status,
          declared_by = EXCLUDED.declared_by,
          undeclared_since = EXCLUDED.undeclared_since,
          last_seen_at = COALESCE(EXCLUDED.last_seen_at, m.last_seen_at),
          code_pinned = EXCLUDED.code_pinned,
          code_pin_source = EXCLUDED.code_pin_source,
          code_pin_reason = EXCLUDED.code_pin_reason,
          db_pinned = m.db_pinned OR EXCLUDED.db_pinned,
          db_pin_source = CASE WHEN m.db_pinned THEN m.db_pin_source ELSE EXCLUDED.db_pin_source END,
          db_pin_reason = CASE WHEN m.db_pinned THEN m.db_pin_reason ELSE EXCLUDED.db_pin_reason END,
          db_pinned_by = CASE WHEN m.db_pinned THEN m.db_pinned_by ELSE EXCLUDED.db_pinned_by END,
          db_pinned_at = CASE WHEN m.db_pinned THEN m.db_pinned_at ELSE EXCLUDED.db_pinned_at END,
          retired_at = CASE WHEN EXCLUDED.status = '{ManagedStatuses.RETIRED}' THEN COALESCE(m.retired_at, NOW()) ELSE NULL END
        """, connection, transaction);
      command.Parameters.AddWithValue("table", record.Table);
      command.Parameters.AddWithValue("name", record.Name);
      command.Parameters.AddWithValue("kind", FormatKind(record.Kind));
      command.Parameters.AddWithValue("definition", record.Definition);
      command.Parameters.AddWithValue("owner", record.Owner);
      command.Parameters.AddWithValue("status", record.Status);
      command.Parameters.AddWithValue("declared_by", (object?)record.DeclaredBy ?? DBNull.Value);
      command.Parameters.AddWithValue("undeclared_since", (object?)record.UndeclaredSince ?? DBNull.Value);
      command.Parameters.AddWithValue("code_pinned", record.CodePinned);
      command.Parameters.AddWithValue("code_pin_source", (object?)record.CodePinSource ?? DBNull.Value);
      command.Parameters.AddWithValue("code_pin_reason", (object?)record.CodePinReason ?? DBNull.Value);
      command.Parameters.AddWithValue("adds_db_pin", record.AddsDbPin);
      command.Parameters.AddWithValue("db_pin_reason", (object?)record.NewDbPinReason ?? DBNull.Value);
      await command.ExecuteNonQueryAsync(cancellationToken);
    }
    await transaction.CommitAsync(cancellationToken);
  }

  /// <summary>The statement that drops one object.</summary>
  public static string DropStatement(string schema, PlannedDrop drop) {
    var qualifiedTable = $"{PgIdentifier.Quote(schema)}.{PgIdentifier.Quote(drop.Table)}";
    var name = PgIdentifier.Quote(drop.Name);
    return drop.Kind switch {
      ManagedObjectKind.Index => $"DROP INDEX CONCURRENTLY IF EXISTS {PgIdentifier.Quote(schema)}.{name}",
      ManagedObjectKind.Constraint => $"ALTER TABLE {qualifiedTable} DROP CONSTRAINT IF EXISTS {name}",
      ManagedObjectKind.Trigger => $"DROP TRIGGER IF EXISTS {name} ON {qualifiedTable}",
      ManagedObjectKind.Statistics => $"DROP STATISTICS IF EXISTS {PgIdentifier.Quote(schema)}.{name}",
      ManagedObjectKind.View => $"DROP VIEW IF EXISTS {PgIdentifier.Quote(schema)}.{name}",
      _ => throw new InvalidOperationException($"A {drop.Kind} is never dropped by a reconcile."),
    };
  }

  /// <summary>The ledger's spelling of a kind: its name in lower case.</summary>
  public static string FormatKind(ManagedObjectKind kind) => kind.ToString().ToLowerInvariant();

  /// <summary>A kind read from the ledger; an unknown spelling reads as an index.</summary>
  public static ManagedObjectKind ParseKind(string kind) =>
    Enum.TryParse<ManagedObjectKind>(kind, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
      ? parsed
      : ManagedObjectKind.Index;
}
