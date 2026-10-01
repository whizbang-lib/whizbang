using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres;

/// <summary>Where a stored-form migration came from, which decides when it settles.</summary>
/// <docs>fundamentals/perspectives/stored-form-migrations#journal</docs>
public enum StoredFormMigrationKind {
  /// <summary>
  /// Generated from <see cref="StoredFormAttribute"/> or <see cref="StoredFormRemovedAttribute"/>: idempotent, and
  /// settled by the first pass that finds nothing left to convert.
  /// </summary>
  Generated,

  /// <summary>An <see cref="IStoredFormMigration"/>: raw SQL, settled by its first successful run.</summary>
  Custom,
}

/// <summary>One stored-form migration of one perspective table, ready for the stored-format rewrite phase.</summary>
/// <param name="Name">The name the journal records it under.</param>
/// <param name="Table">The perspective table, unquoted.</param>
/// <param name="Kind">Generated or custom.</param>
/// <param name="Sql">The statement: the journal gate, the conversion and the journal write, as one <c>DO</c> block.</param>
/// <docs>fundamentals/perspectives/stored-form-migrations</docs>
public sealed record StoredFormMigration(string Name, string Table, StoredFormMigrationKind Kind, string Sql);

/// <summary>
/// Assembles app-declared stored-form migrations into the statements the stored-format rewrite phase
/// (<see cref="CanonicalTemporalRewritePhase"/>) runs: one <c>DO</c> block per migration, each gated and journaled
/// in <c>wh_stored_form_migrations</c> (migration 176).
/// </summary>
/// <remarks>
/// <para>
/// A migration's block skips itself, with a notice, while its table or the journal does not exist yet, and once the
/// journal says it is settled, with one lookup and no scan. Otherwise it runs, and writes its journal row in the
/// same savepoint as its conversion: a conversion that fails or blocks leaves no trace.
/// </para>
/// <para>
/// A generated migration is idempotent and settles on the first pass that changes nothing. A custom migration's SQL
/// is not assumed idempotent: it runs once and settles then. Blocking uses the phase's own SQLSTATE
/// (<see cref="EnumColumnRewriteSql.BLOCKED_SQL_STATE"/>), which the phase turns into a
/// <see cref="StoredFormConversionBlockedException"/> once every other statement has committed.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/stored-form-migrations</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/StoredFormMigrationTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/StoredFormMigrationSqlTests.cs</tests>
public static class StoredFormMigrationSql {
  /// <summary>The journal table, unqualified.</summary>
  public const string JOURNAL = "wh_stored_form_migrations";

  private const string GENERATED = "generated";
  private const string CUSTOM = "custom";

  /// <summary>Builds a generated migration of one table from its steps, which run in order.</summary>
  /// <param name="schema">The schema, unquoted.</param>
  /// <param name="table">The perspective table, unquoted.</param>
  /// <param name="name">The name the journal records it under.</param>
  /// <param name="steps">The steps, in order.</param>
  public static StoredFormMigration Generated(string schema, string table, string name, params StoredFormStep[] steps) {
    _validate(schema, table, name);
    ArgumentNullException.ThrowIfNull(steps);
    if (steps.Length == 0) {
      throw new ArgumentException("A stored-form migration needs at least one step.", nameof(steps));
    }

    var context = new StepContext(name, new StoredFormMigrationTarget(schema, table).QualifiedTable, $"{schema}.{table}");
    var body = new StringBuilder();
    foreach (var step in steps) {
      if (step is null) {
        throw new ArgumentException("A stored-form migration's steps cannot be null.", nameof(steps));
      }
      body.Append(step.Render(context));
    }
    body.Append(_journalWrite(schema, table, name, GENERATED, settledWhen: "NOT v_changed"));
    body.Append(CultureInfo.InvariantCulture, $"""
        IF v_changed THEN
          RAISE NOTICE USING MESSAGE = format('%s: stored-form migration %s: converted, %s row update(s)', {SqlText.Literal(table)}, {SqlText.Literal(name)}, v_touched);
        ELSE
          RAISE NOTICE USING MESSAGE = format('%s: stored-form migration %s: nothing left to convert, settled', {SqlText.Literal(table)}, {SqlText.Literal(name)});
        END IF;

      """);
    return new StoredFormMigration(name, table, StoredFormMigrationKind.Generated, _block(schema, table, name, body.ToString(), "wh_sfm"));
  }

  /// <summary>Builds a custom migration of one table from an app's <see cref="IStoredFormMigration"/>.</summary>
  /// <param name="schema">The schema, unquoted.</param>
  /// <param name="table">The perspective table, unquoted.</param>
  /// <param name="migration">The migration.</param>
  public static StoredFormMigration Custom(string schema, string table, IStoredFormMigration migration) {
    ArgumentNullException.ThrowIfNull(migration);
    var name = migration.Name;
    _validate(schema, table, name);
    var sql = migration.BuildSql(new StoredFormMigrationTarget(schema, table));
    if (string.IsNullOrWhiteSpace(sql)) {
      throw new ArgumentException($"Stored-form migration '{name}' built no SQL.", nameof(migration));
    }

    // Dollar-quoted, under a tag the SQL does not contain, so nothing in it can end the quoting.
    var tag = _freeTag("wh_sfm_sql", sql);
    var body = new StringBuilder();
    body.Append(CultureInfo.InvariantCulture, $"  EXECUTE ${tag}$\n{sql}\n${tag}$;\n");
    body.Append("  GET DIAGNOSTICS v_count = ROW_COUNT;\n");
    body.Append("  v_touched := v_count;\n");
    body.Append(_journalWrite(schema, table, name, CUSTOM, settledWhen: "true"));
    body.Append(CultureInfo.InvariantCulture,
      $"  RAISE NOTICE USING MESSAGE = format('%s: stored-form migration %s: applied, %s row update(s)', {SqlText.Literal(table)}, {SqlText.Literal(name)}, v_touched);\n");
    return new StoredFormMigration(name, table, StoredFormMigrationKind.Custom, _block(schema, table, name, body.ToString(), _freeTag("wh_sfm", sql)));
  }

  /// <summary>
  /// The statements for the rewrite phase, one per migration, in the order given. Empty when there are none.
  /// </summary>
  /// <param name="schema">The schema, unquoted.</param>
  /// <param name="migrations">The migrations, in the order they run.</param>
  /// <exception cref="InvalidOperationException">Two migrations share a name.</exception>
  public static IReadOnlyList<(string Name, string Sql)> ForPhase(string schema, IReadOnlyList<StoredFormMigration> migrations) {
    ArgumentException.ThrowIfNullOrWhiteSpace(schema);
    ArgumentNullException.ThrowIfNull(migrations);
    _ensureUnique(migrations);
    return [.. migrations.Select(m => (m.Name, m.Sql))];
  }

  /// <summary>
  /// Records every migration in the journal as declared, before the rewrite phase runs them, so a migration that
  /// waits for its table or blocks on a value still shows as pending to anything reading the journal. Its own
  /// statement on its own connection, outside the phase's transaction: a declaration converts nothing, so it must not
  /// make the phase wait out older snapshots (the superseded-row-version fence). Idempotent, and a no-op once every
  /// name has a row; nothing is opened when there is nothing to declare.
  /// </summary>
  /// <param name="connectionFactory">Produces the connection the declaration runs on.</param>
  /// <param name="schema">The schema, unquoted.</param>
  /// <param name="migrations">The migrations the build declares.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <exception cref="InvalidOperationException">Two migrations share a name.</exception>
  public static async Task DeclareAsync(
      Func<NpgsqlConnection> connectionFactory,
      string schema,
      IReadOnlyList<StoredFormMigration> migrations,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentException.ThrowIfNullOrWhiteSpace(schema);
    ArgumentNullException.ThrowIfNull(migrations);
    _ensureUnique(migrations);
    if (migrations.Count == 0) {
      return;
    }

    var journal = $"{SqlText.Identifier(schema)}.{JOURNAL}";
    var values = string.Join(",\n    ", migrations.Select(m =>
      $"({SqlText.Literal(m.Name)}, {SqlText.Literal(m.Table)}, {SqlText.Literal(KindName(m.Kind))})"));
    var declare = $"""
      DO $wh_sfm_declare$
      BEGIN
        IF to_regclass({SqlText.Literal(journal)}) IS NULL THEN
          RETURN;
        END IF;
        INSERT INTO {journal} (name, table_name, kind) VALUES
          {values}
        ON CONFLICT (name) DO NOTHING;
      END $wh_sfm_declare$;
      """;

    await using var connection = connectionFactory();
    await connection.OpenAsync(cancellationToken);
    await using var command = new NpgsqlCommand(declare, connection);
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  private static void _ensureUnique(IReadOnlyList<StoredFormMigration> migrations) {
    var duplicates = migrations.GroupBy(m => m.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
    if (duplicates.Count > 0) {
      throw new InvalidOperationException(
        "Stored-form migration names must be unique, because the journal records each migration under its name: "
        + string.Join(", ", duplicates));
    }
  }

  /// <summary>The journal's name for a kind: <c>generated</c> or <c>custom</c>.</summary>
  internal static string KindName(StoredFormMigrationKind kind) => kind == StoredFormMigrationKind.Custom ? CUSTOM : GENERATED;

  // The gate every migration shares: wait for the table and the journal, skip once settled.
  private static string _block(string schema, string table, string name, string body, string tag) {
    var qualified = new StoredFormMigrationTarget(schema, table).QualifiedTable;
    var journal = $"{SqlText.Identifier(schema)}.{JOURNAL}";
    var t = SqlText.Literal(table);
    var n = SqlText.Literal(name);
    return $"""
      DO ${tag}$
      DECLARE
        v_count bigint := 0;
        v_touched bigint := 0;
        v_changed boolean := false;
        v_bad text;
      BEGIN
        IF to_regclass({SqlText.Literal(qualified)}) IS NULL OR to_regclass({SqlText.Literal(journal)}) IS NULL THEN
          RAISE NOTICE USING MESSAGE = format('%s: stored-form migration %s: table absent, waits for the table', {t}, {n});
          RETURN;
        END IF;
        IF EXISTS (SELECT 1 FROM {journal} WHERE name = {n} AND settled_at IS NOT NULL) THEN
          RAISE NOTICE USING MESSAGE = format('%s: stored-form migration %s: settled, skipped', {t}, {n});
          RETURN;
        END IF;
      {body}END ${tag}$;
      """;
  }

  private static string _journalWrite(string schema, string table, string name, string kind, string settledWhen) {
    var journal = $"{SqlText.Identifier(schema)}.{JOURNAL}";
    return $"""
        INSERT INTO {journal} AS wh_j (name, table_name, kind, first_applied_at, last_applied_at, rows_converted, settled_at)
        VALUES ({SqlText.Literal(name)}, {SqlText.Literal(table)}, '{kind}', NOW(), NOW(), v_touched, CASE WHEN {settledWhen} THEN NOW() END)
        ON CONFLICT (name) DO UPDATE SET
          table_name = EXCLUDED.table_name,
          kind = EXCLUDED.kind,
          first_applied_at = coalesce(wh_j.first_applied_at, EXCLUDED.first_applied_at),
          last_applied_at = EXCLUDED.last_applied_at,
          rows_converted = wh_j.rows_converted + EXCLUDED.rows_converted,
          settled_at = EXCLUDED.settled_at;

      """;
  }

  private static string _freeTag(string tag, string sql) {
    var free = new StringBuilder(tag);
    while (sql.Contains($"${free}$", StringComparison.Ordinal)) {
      free.Append("_x");
    }
    return free.ToString();
  }

  private static void _validate(string schema, string table, string name) {
    ArgumentException.ThrowIfNullOrWhiteSpace(schema);
    ArgumentException.ThrowIfNullOrWhiteSpace(table);
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
  }
}
