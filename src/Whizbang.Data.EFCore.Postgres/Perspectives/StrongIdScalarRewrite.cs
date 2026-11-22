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
/// <param name="Sql">The <c>DO</c> block.</param>
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
/// serializer's metadata for a column stored as one value. One statement per table, journaled in
/// <c>wh_stored_form_migrations</c> under a name that carries a hash of the paths, so a model that gains an id is
/// scanned again. Settled by the first pass that converts nothing; a table that does not exist yet is settled at once.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdScalarRewriteTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdScalarRewriteIntegrationTests.cs</tests>
public static class StrongIdScalarRewrite {
  /// <summary>The journal name's prefix and the journal row's kind.</summary>
  public const string KIND = "strong-id-scalar";

  private const string JSONB = "jsonb";
  private const string END_IF = "  END IF;\n";

  /// <summary>The rewrites for every perspective table in a model that stores a strongly-typed id, in table order.</summary>
  /// <param name="model">The context's model.</param>
  /// <param name="documentOptions">The options every document is read with.</param>
  /// <param name="schema">The schema the tables live in.</param>
  /// <returns>One entry per table with an id.</returns>
  public static ImmutableArray<StrongIdScalarTable> ForModel(IModel model, JsonSerializerOptions documentOptions, string schema) {
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
        tables.Add(new StrongIdScalarTable(JournalName(table, paths), table, StatementFor(schema, table, paths)));
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
        _walkMapped(complex, column, [], found, topLevel: true);
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

  /// <summary>The statement that converts one table, as one transaction.</summary>
  /// <param name="schema">The schema the table lives in.</param>
  /// <param name="table">The table.</param>
  /// <param name="paths">Its id paths.</param>
  /// <returns>A <c>DO</c> block.</returns>
  public static string StatementFor(string schema, string table, ImmutableArray<StrongIdPath> paths) {
    ArgumentNullException.ThrowIfNull(schema);
    ArgumentNullException.ThrowIfNull(table);

    var quotedSchema = SqlText.Identifier(schema);
    var qualifiedTable = $"{quotedSchema}.{SqlText.Identifier(table)}";
    var journal = $"{quotedSchema}.{StoredFormMigrationSql.JOURNAL}";
    var tableLiteral = SqlText.Literal(table);
    var name = SqlText.Literal(JournalName(table, paths));

    var sb = new StringBuilder();
    sb.Append("DO $wb$\n");
    sb.Append("DECLARE\n");
    sb.Append("  v_settled timestamptz;\n");
    sb.Append("  v_count bigint;\n");
    sb.Append("  v_touched bigint := 0;\n");
    sb.Append("BEGIN\n");
    sb.Append(CultureInfo.InvariantCulture, $"  IF to_regclass({SqlText.Literal(journal)}) IS NULL THEN\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"    RAISE NOTICE USING MESSAGE = format('%s: strong ids: no journal yet, nothing to convert', {tableLiteral});\n");
    sb.Append("    RETURN;\n");
    sb.Append(END_IF);
    sb.Append(CultureInfo.InvariantCulture, $"  SELECT settled_at INTO v_settled FROM {journal} WHERE name = {name};\n");
    sb.Append("  IF v_settled IS NOT NULL THEN\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"    RAISE NOTICE USING MESSAGE = format('%s: strong ids: settled, skipped', {tableLiteral});\n");
    sb.Append("    RETURN;\n");
    sb.Append(END_IF);
    // A table this release has not created yet holds no row in the old form: it is settled at once below.
    sb.Append(CultureInfo.InvariantCulture, $"  IF to_regclass({SqlText.Literal(qualifiedTable)}) IS NOT NULL THEN\n");

    foreach (var path in paths) {
      var column = SqlText.Identifier(path.Column);
      var segments = "ARRAY[" + string.Join(",", path.Segments.Select(SqlText.Literal)) + "]::text[]";
      var rewritten = $"{quotedSchema}.wh_strong_id_scalar_at({column}, {segments})";
      sb.Append(CultureInfo.InvariantCulture, $"    UPDATE {qualifiedTable} SET {column} = {rewritten}\n");
      sb.Append(CultureInfo.InvariantCulture, $"      WHERE {column} IS DISTINCT FROM {rewritten};\n");
      sb.Append("    GET DIAGNOSTICS v_count = ROW_COUNT;\n");
      sb.Append("    v_touched := v_touched + v_count;\n");
    }

    sb.Append(END_IF);
    sb.Append(CultureInfo.InvariantCulture,
      $"  INSERT INTO {journal} (name, table_name, kind, first_applied_at, last_applied_at, rows_converted, settled_at)\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"  VALUES ({name}, {tableLiteral}, {SqlText.Literal(KIND)}, now(), now(), v_touched, CASE WHEN v_touched = 0 THEN now() END)\n");
    sb.Append("  ON CONFLICT (name) DO UPDATE SET last_applied_at = now(),\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"    rows_converted = {journal}.rows_converted + EXCLUDED.rows_converted, settled_at = EXCLUDED.settled_at;\n");
    sb.Append("  IF v_touched > 0 THEN\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"    {IndexStatistics.MarkRewrittenSql(SqlText.Literal(schema), tableLiteral)}\n");
    sb.Append(StoredFormApplier.RecordSql(journal, "name", name, "    "));
    sb.Append(END_IF);
    sb.Append(CultureInfo.InvariantCulture,
      $"  RAISE NOTICE USING MESSAGE = format('%s: strong ids: converted, %s row update(s)', {tableLiteral}, v_touched);\n");
    sb.Append("END\n");
    sb.Append("$wb$;");
    return sb.ToString();
  }

  /// <summary>Walks a JSON-mapped complex property the way Entity Framework reads it.</summary>
  private static void _walkMapped(
      IComplexProperty complex, string column, ImmutableArray<string> prefix, List<StrongIdPath> found, bool topLevel) {
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
      }
    }

    foreach (var nested in complex.ComplexType.GetComplexProperties()) {
      _walkMapped(nested, column, here, found, topLevel: false);
    }
  }

  /// <summary>Walks a type the way the serializer reads it, through its own metadata.</summary>
  private static void _walkSerialized(
      JsonSerializerOptions options, Type type, string column, ImmutableArray<string> prefix,
      List<StrongIdPath> found, HashSet<Type> visiting) {
    if (WhizbangIdForms.TryGet(Nullable.GetUnderlyingType(type) ?? type, out _)) {
      found.Add(new StrongIdPath(column, prefix));
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
