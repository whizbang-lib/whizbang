using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// One temporal inside a stored document: the column it lives in, the path to it, and its kind.
/// </summary>
/// <param name="Column">The jsonb column: <c>data</c>, <c>metadata</c> or <c>scope</c>.</param>
/// <param name="Segments">The keys from the document root, with <see cref="COLLECTION"/> for every
/// element of a collection.</param>
/// <param name="Kind">Which temporal it is, which decides how a stored value is read.</param>
/// <docs>operations/infrastructure/migrations</docs>
public readonly record struct TemporalPath(string Column, ImmutableArray<string> Segments, StoredTemporalKind Kind) {
  /// <summary>The segment that stands for every element of a collection.</summary>
  public const string COLLECTION = "[]";

  /// <summary>The path as a PostgreSQL jsonpath, which is how a row holding it is found.</summary>
  public string JsonPath {
    get {
      var sb = new StringBuilder("$");
      foreach (var segment in Segments) {
        if (segment == COLLECTION) {
          sb.Append("[*]");
        } else {
          sb.Append(".\"").Append(segment.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
        }
      }
      return sb.ToString();
    }
  }
}

/// <summary>
/// The rewrite that brings every stored temporal of every perspective table into the canonical
/// form, derived at startup from the things that read the documents.
/// </summary>
/// <remarks>
/// <para>
/// A rewrite that converts a list of paths a generator discovered converts exactly those, and the
/// generator's discovery over a model type's own members missed a member inherited from a base
/// class, a nested object, an element of a collection and the framework's own metadata. Each was
/// a place where a writer that converts everything and a reader that expects a number could meet a
/// rendering. So the paths come from the readers themselves: Entity Framework's model for a
/// document mapped property by property, and the serializer's metadata for a document stored as
/// one value. What a reader reads is what the rewrite converts, by construction.
/// </para>
/// <para>
/// One statement per table, and the statement is one transaction: guarded against the table not
/// existing yet, gated by the ledger's form for the table, one update per path through
/// <c>wh_canonicalize_temporal</c>, and the ledger written last. Interrupted anywhere inside,
/// nothing is recorded and the table runs again; the ledger cannot say "converted" about a table
/// that is not. A row is touched only when it holds a rendering at the path, or a number in a unit
/// the ledger says has changed, which is what keeps a converted table from being rewritten on every
/// start; and a table a pass has found clean is marked settled and never scanned again.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/CanonicalTemporalRewriteTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/CanonicalTemporalRewriteIntegrationTests.cs</tests>
public static class CanonicalTemporalRewrite {
  private const string JSONB = "jsonb";
  private const short MICROSECOND_FORM = 2;

  /// <summary>
  /// The rewrites for every perspective table in a model, named by table, in table order.
  /// </summary>
  /// <param name="model">The context's model.</param>
  /// <param name="documentOptions">The options every opaque document is read with.</param>
  /// <param name="schema">The schema the tables live in.</param>
  /// <returns>One entry per table that holds a temporal anywhere a reader reads.</returns>
  public static ImmutableArray<(string Name, string Sql)> ForModel(
      IModel model, JsonSerializerOptions documentOptions, string schema) {
    ArgumentNullException.ThrowIfNull(model);
    ArgumentNullException.ThrowIfNull(documentOptions);
    ArgumentNullException.ThrowIfNull(schema);

    var rewrites = ImmutableArray.CreateBuilder<(string Name, string Sql)>();
    foreach (var entityType in model.GetEntityTypes()) {
      if (!entityType.ClrType.IsGenericType || entityType.ClrType.GetGenericTypeDefinition() != typeof(PerspectiveRow<>)) {
        continue;
      }
      var table = entityType.GetTableName();
      if (table is null) {
        continue;
      }
      var paths = PathsOf(entityType, documentOptions);
      if (paths.IsEmpty) {
        continue;
      }
      rewrites.Add((table, StatementFor(schema, table, paths)));
    }

    rewrites.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
    return rewrites.ToImmutable();
  }

  /// <summary>
  /// Every temporal a reader reads out of one perspective row type.
  /// </summary>
  /// <param name="rowType">The row's entity type.</param>
  /// <param name="documentOptions">The options every opaque document is read with.</param>
  /// <returns>The paths, in the order the readers declare them.</returns>
  /// <remarks>
  /// A column mapped as a complex property is walked through Entity Framework's model, which is
  /// what reads it. A jsonb column mapped as one value is walked through the serializer's metadata
  /// for its type, which is what reads that. Both walks reach inherited members, nested objects
  /// and collection elements, because their readers do.
  /// </remarks>
  public static ImmutableArray<TemporalPath> PathsOf(IEntityType rowType, JsonSerializerOptions documentOptions) {
    ArgumentNullException.ThrowIfNull(rowType);
    ArgumentNullException.ThrowIfNull(documentOptions);

    var found = ImmutableArray.CreateBuilder<TemporalPath>();

    foreach (var complex in rowType.GetComplexProperties()) {
      var column = complex.ComplexType.GetContainerColumnName();
      if (column is not null) {
        _walkMapped(complex, column, [], found, topLevel: true);
      }
    }

    foreach (var property in rowType.GetProperties()) {
      if (property.GetColumnType() != JSONB || property.ClrType.IsValueType || property.ClrType == typeof(string)) {
        continue;
      }
      _walkSerialized(documentOptions, property.ClrType, property.GetColumnName(), [], found, []);
    }

    return found.ToImmutable();
  }

  /// <summary>
  /// The statement that converts one table, as one transaction.
  /// </summary>
  /// <param name="schema">The schema the table lives in.</param>
  /// <param name="table">The table.</param>
  /// <param name="paths">Its temporal paths.</param>
  /// <returns>A <c>DO</c> block.</returns>
  public static string StatementFor(string schema, string table, ImmutableArray<TemporalPath> paths) {
    ArgumentNullException.ThrowIfNull(schema);
    ArgumentNullException.ThrowIfNull(table);

    var quotedSchema = _quoteIdentifier(schema);
    var quotedTable = _quoteIdentifier(table);
    var tableLiteral = _quoteLiteral(table);
    var ledger = $"{quotedSchema}.wh_perspective_forms";

    var sb = new StringBuilder();
    sb.Append("DO $wb$\n");
    sb.Append("DECLARE\n");
    sb.Append("  v_form smallint;\n");
    sb.Append("  v_settled timestamptz;\n");
    sb.Append("  v_count bigint;\n");
    sb.Append("  v_touched bigint := 0;\n");
    sb.Append("BEGIN\n");
    // At this point in startup the table frequently does not exist yet, and neither may the ledger
    // on a database that has not bootstrapped.
    // Every exit says what became of the table. Only the statement knows why it stopped early or how
    // many row updates it made, and a pass that was silent on success once left a fleet unable to
    // tell "converted" from "never ran".
    sb.Append(CultureInfo.InvariantCulture,
      $"  IF to_regclass('{quotedSchema}.{quotedTable}') IS NULL OR to_regclass('{ledger}') IS NULL THEN\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"    RAISE NOTICE USING MESSAGE = format('%s: table absent, nothing to convert', {tableLiteral});\n");
    sb.Append("    RETURN;\n");
    sb.Append("  END IF;\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"  SELECT temporal_form, settled_at INTO v_form, v_settled FROM {ledger} WHERE table_name = {tableLiteral};\n");
    // A table a pass has already found clean is skipped without a scan.
    sb.Append("  IF v_settled IS NOT NULL THEN\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"    RAISE NOTICE USING MESSAGE = format('%s: settled, skipped', {tableLiteral});\n");
    sb.Append("    RETURN;\n");
    sb.Append("  END IF;\n");
    sb.Append("  v_form := coalesce(v_form, 1);\n");

    foreach (var path in paths) {
      var column = _quoteIdentifier(path.Column);
      var segments = "ARRAY[" + string.Join(",", path.Segments.Select(_quoteLiteral)) + "]";
      var jsonPath = path.JsonPath.Replace("'", "''", StringComparison.Ordinal);

      // A rendering is converted in every form. A number is touched only in the mixed-unit form,
      // and only for a kind whose unit changed; an instant was microseconds in every form.
      var predicate = $"jsonb_path_exists({column}, '{jsonPath} ? (@.type() == \"string\")')";
      if (path.Kind is StoredTemporalKind.Day or StoredTemporalKind.Duration) {
        predicate += $"\n       OR (v_form < {MICROSECOND_FORM} AND jsonb_path_exists({column}, '{jsonPath} ? (@.type() == \"number\")'))";
      }

      sb.Append(CultureInfo.InvariantCulture, $"  UPDATE {quotedSchema}.{quotedTable}\n");
      // Cast, because an integer literal does not resolve to a smallint parameter: function
      // resolution has no implicit integer-to-smallint step, and 42883 says the function does not
      // exist rather than that the argument is the wrong width.
      sb.Append(CultureInfo.InvariantCulture,
        $"    SET {column} = {quotedSchema}.wh_canonicalize_temporal({column}, {segments}, {(int)path.Kind}::smallint, v_form)\n");
      sb.Append(CultureInfo.InvariantCulture, $"    WHERE {predicate};\n");
      sb.Append("  GET DIAGNOSTICS v_count = ROW_COUNT;\n");
      sb.Append("  v_touched := v_touched + v_count;\n");
    }

    // Last, in the same transaction. A pass at the microsecond form that converted nothing is the
    // evidence the table is clean.
    sb.Append(CultureInfo.InvariantCulture, $"  INSERT INTO {ledger} (table_name, temporal_form, applied_at, settled_at)\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"  VALUES ({tableLiteral}, {MICROSECOND_FORM}, now(), CASE WHEN v_form >= {MICROSECOND_FORM} AND v_touched = 0 THEN now() END)\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"  ON CONFLICT (table_name) DO UPDATE SET temporal_form = {MICROSECOND_FORM}, applied_at = now(), settled_at = EXCLUDED.settled_at;\n");
    // One update per row and path, so a row with three converted keys counts three times.
    sb.Append(CultureInfo.InvariantCulture,
      $"  RAISE NOTICE USING MESSAGE = format('%s: converted, %s row update(s)', {tableLiteral}, v_touched);\n");
    sb.Append("END\n");
    sb.Append("$wb$;");
    return sb.ToString();
  }

  /// <summary>Walks a JSON-mapped complex property the way Entity Framework reads it.</summary>
  private static void _walkMapped(
      IComplexProperty complex, string column, ImmutableArray<string> prefix,
      ImmutableArray<TemporalPath>.Builder found, bool topLevel) {
    // The top-level complex property is the document itself; a nested one is a key in it, and a
    // collection is every element under that key.
    var here = topLevel ? prefix : prefix.Add(complex.GetJsonPropertyName() ?? complex.Name);
    if (complex.IsCollection) {
      here = here.Add(TemporalPath.COLLECTION);
    }

    foreach (var property in complex.ComplexType.GetProperties()) {
      var kind = CanonicalTemporalConvention.KindOf(property.ClrType);
      if (kind is { } temporal) {
        found.Add(new TemporalPath(column, here.Add(property.GetJsonPropertyName() ?? property.Name), temporal));
      }
    }

    foreach (var nested in complex.ComplexType.GetComplexProperties()) {
      _walkMapped(nested, column, here, found, topLevel: false);
    }
  }

  /// <summary>Walks a type the way the serializer reads it, through its own metadata.</summary>
  private static void _walkSerialized(
      JsonSerializerOptions options, Type type, string column, ImmutableArray<string> prefix,
      ImmutableArray<TemporalPath>.Builder found, HashSet<Type> visiting) {
    var kind = CanonicalTemporalConvention.KindOf(type);
    if (kind is { } temporal) {
      found.Add(new TemporalPath(column, prefix, temporal));
      return;
    }

    // The resolver rather than the options, because the options throw for a type nothing
    // describes and a type nothing describes is simply not a document the serializer reads.
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
      // A dictionary's values are reachable by no path this rewrite can name. The reader accepts a
      // rendering there and counts it.
    } finally {
      visiting.Remove(type);
    }
  }

  private static string _quoteIdentifier(string name) =>
    "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

  private static string _quoteLiteral(string value) =>
    "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
