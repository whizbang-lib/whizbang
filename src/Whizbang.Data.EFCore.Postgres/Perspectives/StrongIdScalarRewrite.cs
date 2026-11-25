// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>One strongly-typed id inside a stored document: the column and the path to it.</summary>
/// <param name="Column">The jsonb column: <c>data</c>, or a promoted field's own column.</param>
/// <param name="Segments">The keys from the column's root, with <see cref="TemporalPath.COLLECTION"/> for every element of a collection.</param>
/// <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
public readonly record struct StrongIdPath(string Column, ImmutableArray<string> Segments) {
  /// <summary>The path as text, for the journal name and a log line.</summary>
  public override string ToString() => Column + ":" + string.Join(".", Segments);
}

/// <summary>One table's strong-id rewrite: its journal name, its table and the statement that converts it.</summary>
/// <param name="Name">The journal name, <c>strong-id-scalar:&lt;table&gt;:&lt;shape&gt;</c>.</param>
/// <param name="Table">The table.</param>
/// <param name="Sql">The <c>DO</c> block that converts one batch.</param>
/// <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
public sealed record StrongIdScalarTable(string Name, string Table, string Sql);

/// <summary>
/// The stored-form rewrite that brings every strongly-typed id a perspective table stores as the object
/// <c>{"Value": "&lt;uuid&gt;"}</c> to the UUID string the readers and writers now use (#2518).
/// </summary>
/// <remarks>
/// <para>
/// A document written before #2518 holds each id as an object. Reads take either form; a query does not, because an
/// id predicate casts the stored text to uuid (<c>CAST(data -&gt;&gt; 'OrderId' AS uuid)</c>), which the object form
/// fails. So the object form is rewritten once, per table, in the stored-form phase, before any query runs.
/// </para>
/// <para>
/// The paths come from the readers, as the dictionary rewrite's do (<see cref="DictionaryPairsRewrite"/>): Entity
/// Framework's model for a document it maps (a property mapped by <see cref="WhizbangIdTypeMapping"/>) and the
/// serializer's metadata for a column stored as one value, through nested objects, collections and both the key and the
/// value of a dictionary. One statement per table, journaled in <c>wh_stored_form_migrations</c> under a name that
/// carries a hash of the paths, so a model that gains an id is scanned again.
/// </para>
/// <para>
/// Each statement converts one batch, in primary-key order, every path of a row in one update, and records in the journal
/// the key it reached (<c>resume_key</c>). <see cref="BatchedRewriteRunner"/> runs each batch in its own transaction under
/// the schema lock, startup one slice within a budget and the maintenance cycle (<see cref="StrongIdScalarRewriteStep"/>)
/// the rest, so a table of any size finishes and a restart redoes nothing. Settled by the first complete pass that
/// converts nothing; a table that does not exist yet is settled at once. The rewrite waits while an instance of an earlier
/// release is live, since that release reads only the object form. A row it has not reached yet is still queried
/// correctly, because an id read in SQL takes either form (<c>StrongIdToleranceRewriter</c>).
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdScalarRewriteIntegrationTests.cs</tests>
public static class StrongIdScalarRewrite {
  /// <summary>The journal name's prefix and the journal row's kind.</summary>
  public const string KIND = "strong-id-scalar";

  /// <summary>The rows one batch reads.</summary>
  public const int BATCH_SIZE = 1000;

  private const string JSONB = "jsonb";
  private const string END_IF = "  END IF;\n";

  /// <summary>The rewrites for every perspective table in a model that stores a strongly-typed id, in table order.</summary>
  /// <param name="model">The context's model.</param>
  /// <param name="documentOptions">The options every document is read with.</param>
  /// <param name="schema">The schema the tables live in.</param>
  /// <param name="batchSize">The rows one batch reads.</param>
  /// <returns>One entry per table with an id.</returns>
  public static ImmutableArray<StrongIdScalarTable> ForModel(
      IModel model, JsonSerializerOptions documentOptions, string schema, int batchSize = BATCH_SIZE) {
    ArgumentNullException.ThrowIfNull(model);
    ArgumentNullException.ThrowIfNull(documentOptions);
    ArgumentNullException.ThrowIfNull(schema);

    var tables = ImmutableArray.CreateBuilder<StrongIdScalarTable>();
    foreach (var entityType in model.GetEntityTypes()) {
      if (!entityType.ClrType.IsGenericType || entityType.ClrType.GetGenericTypeDefinition() != typeof(PerspectiveRow<>)
          || entityType.GetTableName() is not { } table) {
        continue;
      }
      var paths = PathsOf(entityType, documentOptions);
      if (!paths.IsEmpty) {
        var key = entityType.FindPrimaryKey()!.Properties[0];
        tables.Add(new StrongIdScalarTable(
          JournalName(table, paths), table, StatementFor(schema, table, paths, key.GetColumnName(), key.GetColumnType(), batchSize)));
      }
    }

    tables.Sort((a, b) => string.CompareOrdinal(a.Table, b.Table));
    return tables.ToImmutable();
  }

  /// <summary>Every strongly-typed id a reader reads out of one perspective row type.</summary>
  /// <param name="rowType">The row's entity type.</param>
  /// <param name="documentOptions">The options every document is read with.</param>
  /// <returns>The paths, distinct, in the order found.</returns>
  public static ImmutableArray<StrongIdPath> PathsOf(IEntityType rowType, JsonSerializerOptions documentOptions) {
    ArgumentNullException.ThrowIfNull(rowType);
    ArgumentNullException.ThrowIfNull(documentOptions);

    var found = new List<StrongIdPath>();
    foreach (var complex in rowType.GetComplexProperties()) {
      if (complex.ComplexType.GetContainerColumnName() is { } column) {
        _walkMapped(complex, column, [], found, documentOptions, topLevel: true);
      }
    }

    // A promoted jsonb field, or a document stored as one value: the serializer reads it, so its metadata is walked.
    foreach (var property in rowType.GetProperties().Where(p => p.GetColumnType() == JSONB)) {
      _walkSerialized(documentOptions, property.ClrType, property.GetColumnName(), [], found, []);
    }

    return [.. found.Distinct()];
  }

  /// <summary>The journal name of a table's rewrite: the table and a hash of its paths, so a new path rescans.</summary>
  /// <param name="table">The table.</param>
  /// <param name="paths">Its id paths.</param>
  /// <returns>The name.</returns>
  public static string JournalName(string table, ImmutableArray<StrongIdPath> paths) {
    var shape = string.Join("|", paths.Select(p => p.ToString()));
    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shape)))[..12];
    return $"{KIND}:{table}:{hash}";
  }

  /// <summary>
  /// The statement that converts one batch of one table, from the primary key the journal says the last batch reached, and
  /// records where it stopped. Run in a transaction of its own by <see cref="BatchedRewriteRunner"/>.
  /// </summary>
  /// <param name="schema">The schema the table lives in.</param>
  /// <param name="table">The table.</param>
  /// <param name="paths">Its id paths.</param>
  /// <param name="keyColumn">The table's primary key column, the order the batches walk.</param>
  /// <param name="keyType">That column's store type.</param>
  /// <param name="batchSize">The rows one batch reads.</param>
  /// <returns>A <c>DO</c> block.</returns>
  /// <remarks>
  /// The journal row is locked first, so two sessions never convert the same batch: the second waits, then reads the key
  /// the first recorded. A batch that finds no row past the key has reached the end of a pass. A pass that converted
  /// nothing settles the table; one that converted anything starts another from the beginning, which converts a row an
  /// earlier release wrote behind the key meanwhile. A table that does not exist yet is settled at once.
  /// </remarks>
  public static string StatementFor(
      string schema, string table, ImmutableArray<StrongIdPath> paths, string keyColumn, string keyType,
      int batchSize = BATCH_SIZE) {
    ArgumentNullException.ThrowIfNull(schema);
    ArgumentNullException.ThrowIfNull(table);
    ArgumentNullException.ThrowIfNull(keyColumn);
    ArgumentNullException.ThrowIfNull(keyType);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

    var quotedSchema = SqlText.Identifier(schema);
    var qualifiedTable = $"{quotedSchema}.{SqlText.Identifier(table)}";
    var journal = $"{quotedSchema}.{StoredFormMigrationSql.JOURNAL}";
    var tableLiteral = SqlText.Literal(table);
    var name = SqlText.Literal(JournalName(table, paths));
    var key = SqlText.Identifier(keyColumn);
    var row = $"WHERE name = {name}";

    // Each column once, with every path of it applied in turn: one update per row however many ids it holds.
    var rewrites = paths.GroupBy(p => p.Column).Select(column => {
      var quoted = SqlText.Identifier(column.Key);
      var rewritten = column.Aggregate(quoted, (inner, path) =>
        $"{quotedSchema}.wh_strong_id_scalar_at({inner}, ARRAY[{string.Join(",", path.Segments.Select(SqlText.Literal))}]::text[])");
      return (Column: quoted, Rewritten: rewritten);
    }).ToList();

    var sb = new StringBuilder();
    sb.Append("DO $wb$\n");
    sb.Append("DECLARE\n");
    sb.Append("  v_settled timestamptz;\n");
    sb.Append("  v_resume text;\n");
    sb.Append("  v_pass bigint;\n");
    sb.Append("  v_count bigint;\n");
    sb.Append(CultureInfo.InvariantCulture, $"  v_last {keyType};\n");
    sb.Append(CultureInfo.InvariantCulture, $"  v_next {keyType};\n");
    sb.Append("BEGIN\n");
    sb.Append(CultureInfo.InvariantCulture, $"  IF to_regclass({SqlText.Literal(journal)}) IS NULL THEN\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"    RAISE NOTICE USING MESSAGE = format('%s: strong ids: no journal yet, nothing to convert', {tableLiteral});\n");
    sb.Append("    RETURN;\n");
    sb.Append(END_IF);
    sb.Append(CultureInfo.InvariantCulture,
      $"  INSERT INTO {journal} (name, table_name, kind) VALUES ({name}, {tableLiteral}, {SqlText.Literal(KIND)}) ON CONFLICT (name) DO NOTHING;\n");
    // Locked, so a second session converting this table waits here and then reads the key the first recorded.
    sb.Append(CultureInfo.InvariantCulture,
      $"  SELECT settled_at, resume_key, pass_rows_converted INTO v_settled, v_resume, v_pass FROM {journal} {row} FOR UPDATE;\n");
    sb.Append("  IF v_settled IS NOT NULL THEN\n");
    sb.Append("    RETURN;\n");
    sb.Append(END_IF);
    sb.Append(CultureInfo.InvariantCulture, $"  IF to_regclass({SqlText.Literal(qualifiedTable)}) IS NULL THEN\n");
    // A table this release has not created yet holds no row in the old form.
    sb.Append(CultureInfo.InvariantCulture,
      $"    UPDATE {journal} SET first_applied_at = coalesce(first_applied_at, now()), last_applied_at = now(), settled_at = now(), resume_key = NULL {row};\n");
    sb.Append("    RETURN;\n");
    sb.Append(END_IF);
    sb.Append(CultureInfo.InvariantCulture, $"  v_last := v_resume::{keyType};\n");
    sb.Append(CultureInfo.InvariantCulture,
      // The batch's last key: ORDER BY rather than max(), which has no uuid overload before PostgreSQL 18.
      $"  SELECT b.{key} INTO v_next FROM (SELECT {key} FROM {qualifiedTable} WHERE v_last IS NULL OR {key} > v_last ORDER BY {key} LIMIT {batchSize}) b ORDER BY b.{key} DESC LIMIT 1;\n");
    sb.Append("  IF v_next IS NULL THEN\n");
    sb.Append("    IF v_pass = 0 THEN\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"      UPDATE {journal} SET first_applied_at = coalesce(first_applied_at, now()), last_applied_at = now(), settled_at = now(), resume_key = NULL {row};\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"      RAISE NOTICE USING MESSAGE = format('%s: strong ids: a complete pass converted nothing; settled', {tableLiteral});\n");
    sb.Append("    ELSE\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"      UPDATE {journal} SET last_applied_at = now(), resume_key = NULL, pass_rows_converted = 0 {row};\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"      RAISE NOTICE USING MESSAGE = format('%s: strong ids: a pass converted %s row(s); another checks for rows written meanwhile', {tableLiteral}, v_pass);\n");
    sb.Append("    END IF;\n");
    sb.Append("    RETURN;\n");
    sb.Append(END_IF);
    sb.Append(CultureInfo.InvariantCulture,
      $"  UPDATE {qualifiedTable} SET {string.Join(", ", rewrites.Select(r => $"{r.Column} = {r.Rewritten}"))}\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"    WHERE (v_last IS NULL OR {key} > v_last) AND {key} <= v_next\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"      AND ({string.Join(" OR ", rewrites.Select(r => $"{r.Column} IS DISTINCT FROM {r.Rewritten}"))});\n");
    sb.Append("  GET DIAGNOSTICS v_count = ROW_COUNT;\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"  UPDATE {journal} SET first_applied_at = coalesce(first_applied_at, now()), last_applied_at = now(), resume_key = v_next::text,\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"    pass_rows_converted = pass_rows_converted + v_count, rows_converted = rows_converted + v_count {row};\n");
    sb.Append("  IF v_count > 0 THEN\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"    {IndexStatistics.MarkRewrittenSql(SqlText.Literal(schema), tableLiteral)}\n");
    sb.Append(StoredFormApplier.RecordSql(journal, "name", name, "    "));
    sb.Append(END_IF);
    sb.Append("END\n");
    sb.Append("$wb$;");
    return sb.ToString();
  }

  /// <summary>Walks a JSON-mapped complex property the way Entity Framework reads it.</summary>
  private static void _walkMapped(
      IComplexProperty complex, string column, ImmutableArray<string> prefix, List<StrongIdPath> found,
      JsonSerializerOptions options, bool topLevel) {
    var here = topLevel ? prefix : prefix.Add(complex.GetJsonPropertyName()!);
    if (complex.IsCollection) {
      here = here.Add(TemporalPath.COLLECTION);
    }

    foreach (var property in complex.ComplexType.GetProperties()) {
      if (property.GetTypeMapping() is WhizbangIdTypeMapping) {
        found.Add(new StrongIdPath(column, here.Add(property.GetJsonPropertyName()!)));
      } else if (property.GetTypeMapping().ElementTypeMapping is WhizbangIdTypeMapping) {
        // A primitive collection of ids: every element.
        found.Add(new StrongIdPath(column, here.Add(property.GetJsonPropertyName()!).Add(TemporalPath.COLLECTION)));
      } else if (property.GetTypeMapping() is DictionaryMemberTypeMapping dictionary) {
        _walkDictionary(options, dictionary.Form, column, here.Add(property.GetJsonPropertyName()!), found, []);
      }
    }

    foreach (var nested in complex.ComplexType.GetComplexProperties()) {
      _walkMapped(nested, column, here, found, options, topLevel: false);
    }
  }

  /// <summary>
  /// Walks a dictionary's keys and values, stored as the pairs <c>[{"Key": .., "Value": ..}]</c> (#1911): an id is the
  /// key or the value of every pair, or sits below the value.
  /// </summary>
  private static void _walkDictionary(
      JsonSerializerOptions options, DictionaryMemberForm form, string column, ImmutableArray<string> path,
      List<StrongIdPath> found, HashSet<Type> visiting) {
    var pairs = path.Add(TemporalPath.COLLECTION);
    _walkSerialized(options, form.KeyType, column, pairs.Add(DictionaryMemberForm.KEY), found, visiting);
    _walkSerialized(options, form.ValueType, column, pairs.Add(DictionaryMemberForm.VALUE), found, visiting);
  }

  /// <summary>Walks a type the way the serializer reads it, through its own metadata.</summary>
  private static void _walkSerialized(
      JsonSerializerOptions options, Type type, string column, ImmutableArray<string> prefix,
      List<StrongIdPath> found, HashSet<Type> visiting) {
    if (WhizbangIdForms.TryGet(Nullable.GetUnderlyingType(type) ?? type, out _)) {
      found.Add(new StrongIdPath(column, prefix));
      return;
    }

    if (DictionaryMemberForms.TryGet(type, out var dictionary)) {
      _walkDictionary(options, dictionary, column, prefix, found, visiting);
      return;
    }

    // The resolver rather than the options: the options throw for a type nothing describes. The document options always
    // carry one (the registry's union).
    var info = options.TypeInfoResolver!.GetTypeInfo(type, options);
    if (info is null || !visiting.Add(type)) {
      return;
    }

    try {
      if (info.Kind == JsonTypeInfoKind.Object) {
        foreach (var property in info.Properties) {
          _walkSerialized(options, property.PropertyType, column, prefix.Add(property.Name), found, visiting);
        }
      } else if (info.Kind == JsonTypeInfoKind.Enumerable && info.ElementType is { } element) {
        _walkSerialized(options, element, column, prefix.Add(TemporalPath.COLLECTION), found, visiting);
      }
    } finally {
      visiting.Remove(type);
    }
  }
}
