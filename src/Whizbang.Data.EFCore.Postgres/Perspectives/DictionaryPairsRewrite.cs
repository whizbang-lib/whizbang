// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>One dictionary inside a stored document: the column, the path to it, and its stored form.</summary>
/// <param name="Column">The jsonb column: <c>data</c>, or a promoted field's own column.</param>
/// <param name="Segments">The keys from the column's root, with <see cref="TemporalPath.COLLECTION"/> for every element of a collection.</param>
/// <param name="Form">The dictionary's stored form, which says how its keys are stored.</param>
/// <docs>fundamentals/perspectives/dictionary-members#stored-form-rewrite</docs>
public readonly record struct DictionaryPath(string Column, ImmutableArray<string> Segments, DictionaryMemberForm Form) {
  /// <summary>The path as text, for the journal name and a log line.</summary>
  public override string ToString() => Column + ":" + string.Join(".", Segments);
}

/// <summary>One table's dictionary rewrite: its journal name, its table and the statement that converts it.</summary>
/// <param name="Name">The journal name, <c>dictionary-pairs:&lt;table&gt;:&lt;shape&gt;</c>.</param>
/// <param name="Table">The table.</param>
/// <param name="Sql">The <c>DO</c> block.</param>
/// <docs>fundamentals/perspectives/dictionary-members#stored-form-rewrite</docs>
public sealed record DictionaryPairsTable(string Name, string Table, string Sql);

/// <summary>
/// The stored-form rewrite that brings every dictionary a perspective table stores as a JSON object to the array of
/// key/value objects the readers and writers now use (#1911).
/// </summary>
/// <remarks>
/// <para>
/// A document written before #1911 holds each dictionary as an object: a document stored as one value, or a dictionary
/// promoted to a jsonb column. Reads take either form; a query does not, because a key lookup is a containment test
/// over the pairs. So the object form is rewritten once, per table, in the stored-form phase.
/// </para>
/// <para>
/// The paths come from the readers, as the temporal rewrite's do (<see cref="CanonicalTemporalRewrite"/>): Entity
/// Framework's model for a document it maps (a property mapped by <see cref="DictionaryMemberTypeMapping"/>), the
/// serializer's metadata for a column stored as one value, and a dictionary's own value type below it. A key is
/// stored as its type writes it, which the database cannot know, so each path carries its key kind: a string, a
/// number, or a map from an enumeration's names. A dictionary whose key is any other type (a date, a structure) is
/// left as an object, which the reader still reads and counts on <c>whizbang.perspective.dictionary_form_fallbacks</c>.
/// </para>
/// <para>
/// One statement per table, journaled in <c>wh_stored_form_migrations</c> under a name that carries a hash of the
/// paths, so a model that gains a dictionary is scanned again. Settled by the first pass that converts nothing; a
/// table that does not exist yet is settled at once, since this release creates it in the new form.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/dictionary-members#stored-form-rewrite</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DictionaryPairsRewriteTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DictionaryPairsRewriteIntegrationTests.cs</tests>
public static class DictionaryPairsRewrite {
  /// <summary>The journal name's prefix and the journal row's kind.</summary>
  public const string KIND = "dictionary-pairs";

  private const string JSONB = "jsonb";

  /// <summary>A key stored as the name itself, a JSON string.</summary>
  internal const short STRING_KEY = 1;

  /// <summary>A key stored as the name read as a number.</summary>
  internal const short NUMBER_KEY = 2;

  /// <summary>A key stored as the JSON a map gives each name.</summary>
  internal const short MAPPED_KEY = 3;

  /// <summary>The rewrites for every perspective table in a model that stores a dictionary, in table order.</summary>
  /// <param name="model">The context's model.</param>
  /// <param name="documentOptions">The options every document is read with.</param>
  /// <param name="schema">The schema the tables live in.</param>
  /// <returns>One entry per table with a dictionary a key kind can convert.</returns>
  public static ImmutableArray<DictionaryPairsTable> ForModel(IModel model, JsonSerializerOptions documentOptions, string schema) {
    ArgumentNullException.ThrowIfNull(model);
    ArgumentNullException.ThrowIfNull(documentOptions);
    ArgumentNullException.ThrowIfNull(schema);

    var tables = ImmutableArray.CreateBuilder<DictionaryPairsTable>();
    foreach (var entityType in model.GetEntityTypes()) {
      if (!entityType.ClrType.IsGenericType || entityType.ClrType.GetGenericTypeDefinition() != typeof(PerspectiveRow<>)
          || entityType.GetTableName() is not { } table) {
        continue;
      }
      var paths = PathsOf(entityType, documentOptions);
      if (!paths.IsEmpty) {
        tables.Add(new DictionaryPairsTable(JournalName(table, paths), table, StatementFor(schema, table, paths, documentOptions)));
      }
    }

    tables.Sort((a, b) => string.CompareOrdinal(a.Table, b.Table));
    return tables.ToImmutable();
  }

  /// <summary>Every dictionary a reader reads out of one perspective row type, outer before inner.</summary>
  /// <param name="rowType">The row's entity type.</param>
  /// <param name="documentOptions">The options every document is read with.</param>
  /// <returns>The paths.</returns>
  public static ImmutableArray<DictionaryPath> PathsOf(IEntityType rowType, JsonSerializerOptions documentOptions) {
    ArgumentNullException.ThrowIfNull(rowType);
    ArgumentNullException.ThrowIfNull(documentOptions);

    var found = ImmutableArray.CreateBuilder<DictionaryPath>();
    foreach (var complex in rowType.GetComplexProperties()) {
      if (complex.ComplexType.GetContainerColumnName() is { } column) {
        _walkMapped(complex, column, [], found, documentOptions, topLevel: true);
      }
    }

    foreach (var property in rowType.GetProperties()) {
      if (property.GetColumnType() == JSONB && !property.ClrType.IsValueType && property.ClrType != typeof(string)) {
        _walkSerialized(documentOptions, property.ClrType, property.GetColumnName(), [], found, []);
      }
    }

    return found.ToImmutable();
  }

  /// <summary>The journal name of a table's rewrite: the table and a hash of its paths, so a new path rescans.</summary>
  /// <param name="table">The table.</param>
  /// <param name="paths">Its dictionary paths.</param>
  /// <returns>The name.</returns>
  public static string JournalName(string table, ImmutableArray<DictionaryPath> paths) {
    var shape = string.Join("|", paths.Select(p => $"{p}:{Whizbang.Core.TypeNameFormatter.FormatClrTypeName(p.Form.KeyType)}"));
    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shape)))[..12];
    return $"{KIND}:{table}:{hash}";
  }

  /// <summary>The statement that converts one table, as one transaction.</summary>
  /// <param name="schema">The schema the table lives in.</param>
  /// <param name="table">The table.</param>
  /// <param name="paths">Its dictionary paths.</param>
  /// <param name="documentOptions">The options a key's stored JSON is written with.</param>
  /// <returns>A <c>DO</c> block.</returns>
  public static string StatementFor(string schema, string table, ImmutableArray<DictionaryPath> paths, JsonSerializerOptions documentOptions) {
    ArgumentNullException.ThrowIfNull(schema);
    ArgumentNullException.ThrowIfNull(table);
    ArgumentNullException.ThrowIfNull(documentOptions);

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
      $"    RAISE NOTICE USING MESSAGE = format('%s: dictionaries: no journal yet, nothing to convert', {tableLiteral});\n");
    sb.Append("    RETURN;\n");
    sb.Append("  END IF;\n");
    sb.Append(CultureInfo.InvariantCulture, $"  SELECT settled_at INTO v_settled FROM {journal} WHERE name = {name};\n");
    sb.Append("  IF v_settled IS NOT NULL THEN\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"    RAISE NOTICE USING MESSAGE = format('%s: dictionaries: settled, skipped', {tableLiteral});\n");
    sb.Append("    RETURN;\n");
    sb.Append("  END IF;\n");
    // A table this release has not created yet holds no row in the old form: it is settled at once below.
    sb.Append(CultureInfo.InvariantCulture, $"  IF to_regclass({SqlText.Literal(qualifiedTable)}) IS NOT NULL THEN\n");

    foreach (var path in paths) {
      if (KeyFormOf(path.Form, documentOptions) is not { } key) {
        continue;
      }
      var column = SqlText.Identifier(path.Column);
      var segments = "ARRAY[" + string.Join(",", path.Segments.Select(SqlText.Literal)) + "]::text[]";
      var map = key.Map is null ? "NULL::jsonb" : SqlText.Literal(key.Map) + "::jsonb";
      var rewritten = $"{quotedSchema}.wh_dictionary_pairs_at({column}, {segments}, {key.Kind}::smallint, {map})";
      sb.Append(CultureInfo.InvariantCulture, $"    UPDATE {qualifiedTable} SET {column} = {rewritten}\n");
      sb.Append(CultureInfo.InvariantCulture, $"      WHERE {column} IS DISTINCT FROM {rewritten};\n");
      sb.Append("    GET DIAGNOSTICS v_count = ROW_COUNT;\n");
      sb.Append("    v_touched := v_touched + v_count;\n");
    }

    sb.Append("  END IF;\n");
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
    sb.Append("  END IF;\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"  RAISE NOTICE USING MESSAGE = format('%s: dictionaries: converted, %s row update(s)', {tableLiteral}, v_touched);\n");
    sb.Append("END\n");
    sb.Append("$wb$;");
    return sb.ToString();
  }

  /// <summary>
  /// How a dictionary's keys are stored, for the database to rebuild a key from an object's property name: a string,
  /// a number, or a map from each enumeration name to its stored JSON. Null for a key the database cannot rebuild.
  /// </summary>
  /// <param name="form">The dictionary's form.</param>
  /// <param name="documentOptions">The options a key's stored JSON is written with.</param>
  /// <returns>The key kind and, for an enumeration, its map.</returns>
  internal static (short Kind, string? Map)? KeyFormOf(DictionaryMemberForm form, JsonSerializerOptions documentOptions) =>
    form.Accept(new KeyFormVisitor(documentOptions));

  private sealed class KeyFormVisitor(JsonSerializerOptions options) : IDictionaryMemberFormVisitor<(short Kind, string? Map)?> {
    public (short Kind, string? Map)? Visit<TKey, TValue>(DictionaryMemberForm<TKey, TValue> form) where TKey : notnull {
      var keyType = typeof(TKey);
      if (keyType == typeof(string) || keyType == typeof(Guid) || keyType == typeof(char)) {
        return (STRING_KEY, null);
      }
      if (keyType.IsEnum) {
        var map = new JsonObject();
        var names = Enum.GetNames(keyType);
        var values = Enum.GetValuesAsUnderlyingType(keyType);
        for (var i = 0; i < names.Length; i++) {
          var key = (TKey)Enum.ToObject(keyType, values.GetValue(i)!);
          map[names[i]] = JsonNode.Parse(form.KeyToJson(key, options));
        }
        return (MAPPED_KEY, map.ToJsonString());
      }
      return _isNumber(keyType) ? (NUMBER_KEY, null) : null;
    }

    private static bool _isNumber(Type keyType) =>
      keyType == typeof(int) || keyType == typeof(long) || keyType == typeof(short) || keyType == typeof(byte)
      || keyType == typeof(uint) || keyType == typeof(ulong) || keyType == typeof(ushort) || keyType == typeof(sbyte)
      || keyType == typeof(decimal) || keyType == typeof(double) || keyType == typeof(float);
  }

  /// <summary>Walks a JSON-mapped complex property the way Entity Framework reads it.</summary>
  private static void _walkMapped(
      IComplexProperty complex, string column, ImmutableArray<string> prefix,
      ImmutableArray<DictionaryPath>.Builder found, JsonSerializerOptions options, bool topLevel) {
    var here = topLevel ? prefix : prefix.Add(complex.GetJsonPropertyName()!);
    if (complex.IsCollection) {
      here = here.Add(TemporalPath.COLLECTION);
    }

    foreach (var property in complex.ComplexType.GetProperties()) {
      if (property.GetTypeMapping() is DictionaryMemberTypeMapping mapping) {
        _addDictionary(options, mapping.Form, column, here.Add(property.GetJsonPropertyName()!), found, []);
      }
    }

    foreach (var nested in complex.ComplexType.GetComplexProperties()) {
      _walkMapped(nested, column, here, found, options, topLevel: false);
    }
  }

  /// <summary>Adds a dictionary, then walks its values for a dictionary held inside one.</summary>
  private static void _addDictionary(
      JsonSerializerOptions options, DictionaryMemberForm form, string column, ImmutableArray<string> path,
      ImmutableArray<DictionaryPath>.Builder found, HashSet<Type> visiting) {
    found.Add(new DictionaryPath(column, path, form));
    _walkSerialized(options, form.ValueType, column, path.Add(TemporalPath.COLLECTION).Add(DictionaryMemberForm.VALUE), found, visiting);
  }

  /// <summary>Walks a type the way the serializer reads it, through its own metadata.</summary>
  private static void _walkSerialized(
      JsonSerializerOptions options, Type type, string column, ImmutableArray<string> prefix,
      ImmutableArray<DictionaryPath>.Builder found, HashSet<Type> visiting) {
    if (DictionaryMemberForms.TryGet(type, out var form)) {
      _addDictionary(options, form, column, prefix, found, visiting);
      return;
    }

    // The resolver rather than the options: the options throw for a type nothing describes.
    var info = options.TypeInfoResolver?.GetTypeInfo(type, options);
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
