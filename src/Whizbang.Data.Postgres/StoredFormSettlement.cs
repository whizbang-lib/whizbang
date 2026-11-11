// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Whether every stored-form conversion a model declares has settled, read without a lock (#1992). Part of what makes a
/// schema <c>Current</c>: the stored-form rewrite takes the schema lock whenever a model declares a conversion, even
/// when every one of them would find nothing to do, so a restart may skip it only when each one's own record says it
/// is settled.
/// </summary>
/// <remarks>
/// <para>Three records, one per kind of conversion:</para>
/// <list type="bullet">
///   <item>the canonical temporal rewrite: <c>wh_perspective_forms.settled_at</c> for each table it names;</item>
///   <item>the stored-form migrations and the declared-key spelling pass: <c>wh_stored_form_migrations.settled_at</c>
///   for each journal name;</item>
///   <item>a physical enum column (<c>enum-column:&lt;table&gt;.&lt;column&gt;</c>): settled once the column is no
///   longer stored as text.</item>
/// </list>
/// <para>
/// A conversion with no record yet is not settled while its table holds rows, and a rewrite whose kind this cannot read
/// is never assumed settled: the full startup path runs and settles it. A conversion with no record on a table that
/// holds no row, and no record of it at all, has nothing to convert (#2194): the conversions run before the schema pass, so on a new database they
/// find none of the tables the pass then builds, and record nothing; without this a database one complete pass built
/// would read as current only from its third start. Nothing is recorded for such a table, so rows written into it later
/// (by an earlier release, during a rollout) make the schema not current again, and the next start converts them.
/// </para>
/// </remarks>
/// <docs>operations/startup/rolling-upgrades#current</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupAssessorTests.cs:StoredForms_AreSettledOnlyWhenEveryLedgerSaysSoAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/OnePassReadsCurrentTests.cs</tests>
public static class StoredFormSettlement {
  private const string ENUM_COLUMN = "enum-column:";

  /// <summary>Whether every named conversion has settled.</summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="schema">The schema the tables and records live in.</param>
  /// <param name="temporalTables">The tables the canonical temporal rewrite names.</param>
  /// <param name="journal">The stored-form migrations and declared-key tables: each journal name with its table.</param>
  /// <param name="physicalRewrites">The generated physical-column rewrites, by name.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns><see langword="true"/> when nothing is left to convert.</returns>
  /// <remarks>
  /// A conversion whose table does not exist has nothing to convert: each conversion waits for its table and records
  /// nothing until it exists, and a table the schema pass owes makes the schema not current on its own.
  /// </remarks>
  public static async Task<bool> IsSettledAsync(
      NpgsqlConnection connection,
      string schema,
      IEnumerable<string> temporalTables,
      IEnumerable<(string Name, string Table)> journal,
      IEnumerable<string> physicalRewrites,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(temporalTables);
    ArgumentNullException.ThrowIfNull(journal);
    ArgumentNullException.ThrowIfNull(physicalRewrites);
    var target = string.IsNullOrEmpty(schema) ? "public" : schema.Replace("\"", string.Empty);
    var prefix = PgIdentifier.Quote(target) + ".";

    var tables = temporalTables.Distinct(StringComparer.Ordinal).ToArray();
    if (tables.Length > 0 && await _openAsync(connection, target, prefix, $"""
        SELECT DISTINCT w.tbl, EXISTS (SELECT 1 FROM {prefix}wh_perspective_forms f WHERE f.table_name = w.name)
        FROM unnest($2::text[], $3::text[]) AS w(name, tbl)
        WHERE to_regclass(quote_ident($1) || '.' || quote_ident(w.tbl)) IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM {prefix}wh_perspective_forms f WHERE f.table_name = w.name AND f.settled_at IS NOT NULL)
        """, tables, tables, cancellationToken).ConfigureAwait(false)) {
      return false;
    }

    var entries = journal.Distinct().ToArray();
    if (entries.Length > 0 && await _openAsync(connection, target, prefix, $"""
        SELECT DISTINCT w.tbl, EXISTS (SELECT 1 FROM {prefix}wh_stored_form_migrations j WHERE j.name = w.name)
        FROM unnest($2::text[], $3::text[]) AS w(name, tbl)
        WHERE to_regclass(quote_ident($1) || '.' || quote_ident(w.tbl)) IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM {prefix}wh_stored_form_migrations j WHERE j.name = w.name AND j.settled_at IS NOT NULL)
        """, [.. entries.Select(e => e.Name)], [.. entries.Select(e => e.Table)], cancellationToken).ConfigureAwait(false)) {
      return false;
    }

    var columns = new List<(string Table, string Column)>();
    foreach (var name in physicalRewrites) {
      if (!name.StartsWith(ENUM_COLUMN, StringComparison.Ordinal) || name[ENUM_COLUMN.Length..].Split('.') is not [var table, var column]) {
        return false;
      }
      columns.Add((table, column));
    }
    return columns.Count == 0 || !await _anyAsync(connection, target, """
      SELECT count(*) FROM information_schema.columns c
      JOIN unnest($2::text[], $3::text[]) AS w(name, tbl) ON w.tbl = c.table_name AND w.name = c.column_name
      WHERE c.table_schema = $1 AND c.data_type IN ('text', 'character varying')
      """, [.. columns.Select(c => c.Column)], [.. columns.Select(c => c.Table)], cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Whether any conversion is still open: the query names each table with a conversion not settled, and whether that
  /// conversion has a record at all. One recorded and not settled is open (a pass that converted rows runs once more);
  /// one never recorded is open only while its table holds a row (#2194). Parameters: the schema, the names, the tables.
  /// </summary>
  private static async Task<bool> _openAsync(
      NpgsqlConnection connection, string schema, string prefix, string sql, string[] names, string[] tables,
      CancellationToken cancellationToken) {
    var unrecorded = new List<string>();
    await using (var command = _command(connection, sql, schema, names, tables)) {
      await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        if (reader.GetBoolean(1)) {
          return true;
        }
        unrecorded.Add(reader.GetString(0));
      }
    }
    foreach (var table in unrecorded) {
      await using var rows = connection.CreateCommand();
#pragma warning disable S2077 // the schema and the table are quoted by PgIdentifier
      rows.CommandText = $"SELECT EXISTS (SELECT 1 FROM {prefix}{PgIdentifier.Quote(table)})";
#pragma warning restore S2077
      if (await rows.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true) {
        return true;
      }
    }
    return false;
  }

  /// <summary>Whether the query counts anything. Parameters: the schema, the names, the tables.</summary>
  private static async Task<bool> _anyAsync(
      NpgsqlConnection connection, string schema, string sql, string[] names, string[] tables, CancellationToken cancellationToken) {
    await using var command = _command(connection, sql, schema, names, tables);
    return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! > 0;
  }

  private static NpgsqlCommand _command(NpgsqlConnection connection, string sql, string schema, string[] names, string[] tables) {
    var command = connection.CreateCommand();
#pragma warning disable S2077 // the schema is quoted by PgIdentifier; every value is bound
    command.CommandText = sql;
#pragma warning restore S2077
    command.Parameters.AddWithValue(schema);
    command.Parameters.AddWithValue(names);
    command.Parameters.AddWithValue(tables);
    return command;
  }
}
