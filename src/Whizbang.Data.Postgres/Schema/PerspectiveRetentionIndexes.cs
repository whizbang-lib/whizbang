// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Whizbang.Data.Postgres.Schema;

/// <summary>
/// The <c>updated_at</c> index of each perspective table that declares row retention: built when it is missing and
/// declared to the managed-object reconcile, so it is kept, while the same index on every other table is retired.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The safe default is no index (#2087).</strong> Every update rewrites <c>updated_at</c>, and a btree over it
/// keeps PostgreSQL from making the update HOT (measured on PostgreSQL 17 at fillfactor 80: no HOT updates with the
/// index, about 56 percent without it). So the schema generators no longer build it on every table. Two things still
/// ask for it: a model that queries <c>UpdatedAt</c> declares it with
/// <c>[PerspectiveIndex(PerspectiveRowColumns.UpdatedAt)]</c>, which the generators build like any declared index; and a
/// table enrolled in row retention gets it here, because the sliding-TTL and row-cap sweeps range-scan the column
/// (1.5 ms with the btree against 7.3 ms with BRIN at 300,000 rows).
/// </para>
/// <para>
/// Enrollment is read from <c>wh_perspective_registry.row_retention_enrolled</c>, the column the sweeps themselves read,
/// so the index follows exactly the tables they sweep. It is not known when the schema is generated: retention is
/// declared through the model's events and attributes, resolved by another generator, and carried into the registry at
/// startup.
/// </para>
/// <para>
/// It runs inside the reconcile's plan transaction, under the schema lock, before the plan reads the tables: the plan
/// then sees the index it built, and a declared index is never planned for retirement in the same run that builds it.
/// A start reconciles, and so does the maintenance step once per fleet per window, so a table enrolled after its
/// start's reconcile gets its index within one window. An index of the same definition under another name (the one an
/// earlier Dapper release built) stands for it, as <c>wh_ensure_index</c> records, and no twin is built.
/// </para>
/// <para>
/// The index is built with a plain <c>CREATE INDEX</c>, which blocks writes to the table while it builds. That happens
/// once, when a table is first enrolled; a table enrolled before this release already has it.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-indexes#the-updated_at-index</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/RetentionUpdatedAtIndexReconcileTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRetentionIndexNameTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupParity/StartupParityTests.cs:TheUpdatedAtIndex_IsBuiltAndKeptForATableThatDeclaresRetentionAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupParity/StartupParityTests.cs:TheUpdatedAtIndex_IsRetiredFromATableWithoutRetentionAsync</tests>
public static partial class PerspectiveRetentionIndexes {
  /// <summary>What the declaration names as having asked for the index, in the ledger and the report.</summary>
  public const string DECLARED_BY = "row retention (wh_perspective_registry)";

  private const string PERSPECTIVE_TABLE_PREFIX = "wh_per_";
  private const int IDENTIFIER_LIMIT = 63;
  private const string REGISTRY = "wh_perspective_registry";

  /// <summary>
  /// The index's name on <paramref name="table"/>: the one the schema generators derive for a declared
  /// <c>[PerspectiveIndex(PerspectiveRowColumns.UpdatedAt)]</c>, and gave every table before #2087.
  /// </summary>
  /// <param name="table">The bare table name.</param>
  /// <returns>The index name, within PostgreSQL's identifier limit.</returns>
  /// <remarks>
  /// The generators' scheme (<c>PostgresIdentifiers.WithinLimit</c>), repeated because a runtime assembly cannot use a
  /// generator's: the name as it is when it fits, otherwise its first 54 characters and a 32-bit FNV-1a digest of the
  /// whole. A test fails when the two differ.
  /// </remarks>
  public static string IndexName(string table) {
    ArgumentException.ThrowIfNullOrWhiteSpace(table);
    var name = $"idx_{table.Replace(PERSPECTIVE_TABLE_PREFIX, "", StringComparison.Ordinal)}_updated_at";
    if (name.Length <= IDENTIFIER_LIMIT) {
      return name;
    }
    var hash = 2166136261u;
    foreach (var c in name) {
      hash = (hash ^ c) * 16777619u;
    }
    var suffix = "_" + hash.ToString("x8", CultureInfo.InvariantCulture);
    return name[..(IDENTIFIER_LIMIT - suffix.Length)] + suffix;
  }

  /// <summary>
  /// Builds the index on each enrolled table that lacks it, when <paramref name="build"/> allows, and returns the
  /// declaration of every enrolled table's index.
  /// </summary>
  /// <param name="connection">An open connection, in the reconcile's transaction.</param>
  /// <param name="schema">The schema, bare.</param>
  /// <param name="build">Whether missing indexes may be built: false in a mode that changes nothing.</param>
  /// <param name="logger">Where a built index is reported.</param>
  /// <param name="cancellationToken">Cancels the work.</param>
  /// <returns>One declaration per enrolled table that exists and has the column.</returns>
  internal static async Task<IReadOnlyList<DeclaredSchemaObject>> EnsureAsync(
      NpgsqlConnection connection, string schema, bool build, ILogger logger, CancellationToken cancellationToken) {
    if (!await _registryCarriesEnrollmentAsync(connection, schema, cancellationToken).ConfigureAwait(false)) {
      return [];
    }

    var declared = (await _enrolledTablesAsync(connection, schema, cancellationToken).ConfigureAwait(false))
      .ConvertAll(table => new DeclaredSchemaObject(table, IndexName(table), ManagedObjectKind.Index, DECLARED_BY));
    if (!build || declared.Count == 0) {
      return declared;
    }

    var equivalents = await ManagedSchemaCatalog.ReadEquivalentsAsync(connection, schema, cancellationToken).ConfigureAwait(false);
    var present = await _presentIndexesAsync(
      connection, schema, [.. declared.Select(d => d.Name), .. equivalents.Select(e => e.Existing)], cancellationToken)
      .ConfigureAwait(false);
    // An index recorded as standing for the declared one counts only while it is still there.
    var standIns = equivalents.Where(e => present.Contains(e.Existing)).Select(e => (e.Table, e.Declared)).ToHashSet();
    var quotedSchema = PgIdentifier.Quote(schema);
    foreach (var index in declared.Where(d => !present.Contains(d.Name) && !standIns.Contains((d.Table, d.Name)))) {
      await using var command = new NpgsqlCommand(
        $"SELECT {quotedSchema}.wh_ensure_index($wbix$CREATE INDEX IF NOT EXISTS {PgIdentifier.Quote(index.Name)} "
        + $"ON {quotedSchema}.{PgIdentifier.Quote(index.Table)} (updated_at)$wbix$)", connection);
      // wh_ensure_index returns what it did: created, or equivalent:<name> when an index of the same definition
      // turned up since the read above.
      var outcome = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      LogEnsured(logger, schema, index.Table, index.Name, outcome);
    }
    return declared;
  }

  // Whether the registry exists and has the enrollment column: a schema the retention migrations have not reached
  // (or a registry some other code created) has no enrolled table.
  private static async Task<bool> _registryCarriesEnrollmentAsync(
      NpgsqlConnection connection, string schema, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand($"""
      SELECT EXISTS (
        SELECT 1 FROM pg_catalog.pg_attribute a
        JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND c.relname = '{REGISTRY}' AND a.attname = 'row_retention_enrolled' AND NOT a.attisdropped)
      """, connection);
    command.Parameters.AddWithValue(nameof(schema), schema);
    return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
  }

  // The enrolled tables that exist in the schema and have the column; a row whose table is gone, or that was never
  // a perspective table with the row columns, has nothing to index.
  private static async Task<List<string>> _enrolledTablesAsync(
      NpgsqlConnection connection, string schema, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand($"""
      SELECT DISTINCT r.table_name
      FROM {PgIdentifier.Quote(schema)}.{REGISTRY} r
      JOIN pg_catalog.pg_namespace n ON n.nspname = @schema
      JOIN pg_catalog.pg_class c ON c.relnamespace = n.oid AND c.relname = r.table_name AND c.relkind IN ('r', 'p')
      JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid AND a.attname = 'updated_at' AND NOT a.attisdropped
      WHERE r.row_retention_enrolled
      ORDER BY r.table_name
      """, connection);
    command.Parameters.AddWithValue(nameof(schema), schema);
    var tables = new List<string>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      tables.Add(reader.GetString(0));
    }
    return tables;
  }

  private static async Task<HashSet<string>> _presentIndexesAsync(
      NpgsqlConnection connection, string schema, string[] names, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand("""
      SELECT c.relname FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
      WHERE n.nspname = @schema AND c.relkind = 'i' AND c.relname = ANY (@names)
      """, connection);
    command.Parameters.AddWithValue(nameof(schema), schema);
    command.Parameters.AddWithValue(nameof(names), names);
    var present = new HashSet<string>(StringComparer.Ordinal);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      present.Add(reader.GetString(0));
    }
    return present;
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Schema reconcile ({Schema}.{Table}): the table declares row retention, so its updated_at index {Index} was ensured ({Outcome})")]
  private static partial void LogEnsured(ILogger logger, string schema, string table, string index, string? outcome);
}
