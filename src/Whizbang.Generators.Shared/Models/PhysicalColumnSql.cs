using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// The SQL that moves a perspective field's storage on a table that already exists: add a promoted
/// field's column and fill it from the document, or copy a demoted field's column back into the document.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#storage-moves</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PhysicalColumnBackfillIntegrationTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PhysicalColumnSqlTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PhysicalFieldMoveGenerationTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalColumnFillMaintenanceStepTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitPromotionTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalFieldDemotionTests.cs</tests>
public static class PhysicalColumnSql {

  /// <summary>
  /// The columns every perspective table has whatever its model, which a model property sharing the name can
  /// never have been promoted to.
  /// </summary>
  private static readonly HashSet<string> _frameworkColumns = new(StringComparer.Ordinal) {
    "id", "data", "model_data", "metadata", "scope", "created_at", "updated_at", "sys_created_at",
    "sys_updated_at", "expires_at", "version",
  };

  /// <summary>Adds the column when the table predates it; a no-op otherwise.</summary>
  public static string AddColumn(string qualifiedTable, string columnName, string columnType) =>
    $"ALTER TABLE {qualifiedTable} ADD COLUMN IF NOT EXISTS {columnName} {columnType};";

  /// <summary>The suffix a retired text column is renamed with when its field became a jsonb column.</summary>
  public const string TEXT_LEGACY_SUFFIX = "_text_legacy";

  /// <summary>
  /// Moves aside a TEXT column that an earlier release created for a field that is now a jsonb column, so the
  /// column can be added as jsonb and filled; null for any other column.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Such a column held the field's type name rather than its value (an object, a list or a dictionary with no
  /// declared type fell through to TEXT), so nothing in it can be cast. It is renamed to
  /// <c>&lt;column&gt;_text_legacy</c>, never dropped: removing data is an operator's decision. Emitted before
  /// <see cref="Arm"/>, so the column is absent when the arm looks, the jsonb column is armed for the fill and
  /// added, and <see cref="Backfill"/> fills it from the document's copy.
  /// </para>
  /// <para>
  /// Idempotent: it acts only while the column is TEXT and no legacy column exists, so a later start, and a
  /// table created with the jsonb column, find nothing to do. A Split model's document has no copy, so the
  /// warning says the perspective has to be rebuilt to fill the column.
  /// </para>
  /// </remarks>
  /// <param name="qualifiedTable">The table, schema-qualified.</param>
  /// <param name="field">The promoted field.</param>
  /// <returns>One statement, or null.</returns>
  /// <docs>fundamentals/perspectives/physical-fields#jsonb-text-columns</docs>
  public static string? RetireTextColumn(string qualifiedTable, PhysicalFieldInfo field) {
    if (field is null || !PhysicalFieldScalar.IsJsonb(field.ColumnType)) {
      return null;
    }

    var column = field.ColumnName.ToLowerInvariant();
    var legacy = Utilities.PostgresIdentifiers.WithinLimit(column + TEXT_LEGACY_SUFFIX);
    var attribute = $"SELECT 1 FROM pg_attribute WHERE attrelid = '{qualifiedTable}'::regclass AND NOT attisdropped AND attname = ";
    var then = field.IsSplit
      ? "the document holds no copy, so it is empty until the perspective is rebuilt"
      : "it is filled from the copy in the document";
    return "DO $$ BEGIN\n"
      + $"  IF EXISTS ({attribute}'{column}' AND atttypid = 'text'::regtype)\n"
      + $"    AND NOT EXISTS ({attribute}'{legacy}') THEN\n"
      + $"    ALTER TABLE {qualifiedTable} RENAME COLUMN {column} TO {legacy};\n"
      + $"    RAISE WARNING 'Whizbang: {qualifiedTable.Replace("'", "''")}.{column} was text and is now jsonb. The text column is kept as {legacy} (drop it when no longer needed); {then}.';\n"
      + "  END IF;\n"
      + "END $$;";
  }

  /// <summary>
  /// The containment index a jsonb column declares with <c>[Indexed(IndexKinds.Containment)]</c>: GIN with
  /// <c>jsonb_path_ops</c>, the operator class that answers <c>@&gt;</c> and is smaller and faster than the
  /// default one, which also answers key existence that no compiled filter asks.
  /// </summary>
  /// <param name="indexName">The index name.</param>
  /// <param name="qualifiedTable">The table.</param>
  /// <param name="columnName">The jsonb column.</param>
  /// <returns>The statement.</returns>
  /// <docs>fundamentals/perspectives/physical-fields#jsonb-columns</docs>
  public static string ContainmentIndex(string indexName, string qualifiedTable, string columnName) =>
    $"CREATE INDEX IF NOT EXISTS {indexName} ON {qualifiedTable} USING gin ({columnName} jsonb_path_ops);";

  /// <summary>
  /// Fills the column from the document for rows that have the value there but not in the column, or
  /// null when the value cannot be reproduced exactly from the document.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Only rows that have the value in the document and not in the column are touched, so a column the
  /// writer has since filled is never overwritten, and running it again finds nothing to do.
  /// </para>
  /// <para>
  /// The extraction reproduces what the writer stores, type by type; see <see cref="Extraction"/>.
  /// </para>
  /// </remarks>
  public static string? Backfill(string qualifiedTable, PhysicalFieldInfo field) {
    var extraction = Extraction(field);
    return extraction is null
      ? null
      : $"UPDATE {qualifiedTable} SET {field.ColumnName} = {extraction} " +
        $"WHERE {field.ColumnName} IS NULL AND jsonb_typeof(data -> '{field.PropertyName}') <> 'null';";
  }

  /// <summary>The table the schema arms a moving column in, defined by migration 179 and extended by 182.</summary>
  public const string FILLS_TABLE = "wh_physical_column_fills";

  /// <summary>The dollar quote around an armed column's extraction. Chosen so no extraction can contain it.</summary>
  public const string EXTRACTION_QUOTE = "$wbfill$";

  /// <summary>
  /// Arms the column for the move from the document when this pass is the one adding it (migration 182's
  /// <c>wh_arm_physical_column</c>).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Emitted before <see cref="AddColumn"/>, so it sees whether the column exists yet: a table that already
  /// had it (created with it, or promoted by an earlier start) arms nothing. The rows this pass's
  /// <see cref="Backfill"/> fills are not the only ones: during a rolling deploy an instance still on the
  /// previous release writes rows with the value in the document and not in the column, because it does
  /// not know the column. The maintenance step fills those from the armed row (issue #1009).
  /// </para>
  /// <para>
  /// A Split field is armed with its writes synced: the new release reads it from the column and the old one
  /// from the document, so until the move settles every write keeps the two in agreement (issue #1021). A
  /// column the document cannot fill is armed as a rebuild notice when the table has rows. A field promoted
  /// again after a demotion has its column refreshed from the document.
  /// </para>
  /// <para>
  /// The column name is passed as PostgreSQL folds an unquoted identifier, which is how the generated DDL
  /// writes it.
  /// </para>
  /// </remarks>
  /// <param name="qualifiedTable">The table, schema-qualified.</param>
  /// <param name="field">The promoted field.</param>
  /// <returns>One statement, or null for no field.</returns>
  public static string? Arm(string qualifiedTable, PhysicalFieldInfo field) {
    if (field is null) {
      return null;
    }
    var extraction = Extraction(field) is { } e ? $"{EXTRACTION_QUOTE}{e}{EXTRACTION_QUOTE}" : "NULL";
    var sync = field.IsSplit ? "true" : "false";
    return $"SELECT {_schemaPrefix(qualifiedTable)}wh_arm_physical_column('{qualifiedTable}', "
      + $"'{field.ColumnName.ToLowerInvariant()}', '{field.PropertyName}', {extraction}, {sync});";
  }

  /// <summary>
  /// Adds the sync triggers for every move of the table armed with synced writes that lacks them (migration
  /// 182's <c>wh_sync_physical_moves</c>). Emitted after the columns are added and before they are filled,
  /// so a write that lands after the fill is synced.
  /// </summary>
  /// <param name="qualifiedTable">The table, schema-qualified.</param>
  /// <returns>One statement.</returns>
  public static string SyncMoves(string qualifiedTable) =>
    $"SELECT {_schemaPrefix(qualifiedTable)}wh_sync_physical_moves('{qualifiedTable}');";

  /// <summary>
  /// Copies the values of each column a field no longer promoted left behind into the document (migration
  /// 182's <c>wh_demote_physical_columns</c>), or null when the model has no field that could have had one.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The model no longer says which of its fields were promoted, so every field it keeps only in the document
  /// is a candidate, under the column name its promotion would have used. The function demotes only a column
  /// the ledger records the framework created for a promoted field: one recorded with its field is found by
  /// that field, under whatever name it was given; one recorded from the perspective registry by its default
  /// name. A column an operator added is never touched, nor one moved before, nor one a field still promoted
  /// owns (passed as the last argument). A column the framework creates for every table is never a candidate.
  /// </para>
  /// </remarks>
  /// <param name="qualifiedTable">The table, schema-qualified.</param>
  /// <param name="documentProperties">The names of the model's properties kept only in the document.</param>
  /// <param name="physicalFields">The model's promoted fields, whose columns are never candidates.</param>
  /// <returns>One statement, or null.</returns>
  public static string? Demote(
      string qualifiedTable, IEnumerable<string> documentProperties, IEnumerable<PhysicalFieldInfo> physicalFields) {
    var candidates = DemotionCandidates(documentProperties, physicalFields);
    if (candidates.Count == 0) {
      return null;
    }
    var json = new StringBuilder("{");
    foreach (var (column, key) in candidates) {
      if (json.Length > 1) {
        json.Append(", ");
      }
      json.Append('"').Append(column).Append("\": \"").Append(key).Append('"');
    }
    json.Append('}');
    var owned = string.Join(", ", (physicalFields ?? [])
      .Select(f => f.ColumnName.ToLowerInvariant())
      .Distinct(StringComparer.Ordinal)
      .OrderBy(c => c, StringComparer.Ordinal)
      .Select(c => $"'{c}'"));
    return $"SELECT * FROM {_schemaPrefix(qualifiedTable)}wh_demote_physical_columns('{qualifiedTable}', '{json}'::jsonb, "
      + $"ARRAY[{owned}]::text[]);";
  }

  /// <summary>
  /// The columns a field the model keeps only in the document could have left behind, with the document key
  /// each would be copied to, in column order.
  /// </summary>
  /// <param name="documentProperties">The names of the model's properties kept only in the document.</param>
  /// <param name="physicalFields">The model's promoted fields.</param>
  /// <returns>Column and key pairs.</returns>
  public static IReadOnlyList<(string Column, string Key)> DemotionCandidates(
      IEnumerable<string> documentProperties, IEnumerable<PhysicalFieldInfo> physicalFields) {
    var owned = new HashSet<string>(
      (physicalFields ?? []).Select(f => f.ColumnName.ToLowerInvariant()), StringComparer.Ordinal);
    return [.. (documentProperties ?? [])
      .Where(p => !string.IsNullOrEmpty(p) && p.All(c => char.IsLetterOrDigit(c) || c == '_'))
      .Select(p => (Column: NamingConventionUtilities.ToSnakeCase(p).ToLowerInvariant(), Key: p))
      .Where(c => !_frameworkColumns.Contains(c.Column) && !owned.Contains(c.Column))
      .GroupBy(c => c.Column, StringComparer.Ordinal)
      .Select(g => g.First())
      .OrderBy(c => c.Column, StringComparer.Ordinal)];
  }

  /// <summary>
  /// The expression that reads the field's value out of the document as the column's type, or null when the
  /// document cannot reproduce exactly what the writer stores in the column.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Dates and times are microsecond counts in the document, not renderings, so they are rebuilt by exact
  /// integer arithmetic from the epoch (or from midnight) rather than parsed. An enumeration's column and
  /// document both hold its underlying number. A Split field's column is filled from the document the
  /// previous release wrote, which still holds the value (issue #1021).
  /// </para>
  /// <para>
  /// A column type the author chose is covered where the writer's value is reproducible: <c>jsonb</c> and
  /// <c>json</c> take the document's value as it is, a JSON null being the null the writer stores; an array
  /// (<c>uuid[]</c>, <c>text[]</c>, ...) is built element by element, in document order, from a collection of
  /// known scalars, a JSON null being a null array; any other type takes a known
  /// scalar that is not a date or time, cast to the type, which is how the server parses the value the writer
  /// sends. A vector, an unknown type, and a date or time under a type of the author's choosing are not.
  /// </para>
  /// </remarks>
  public static string? Extraction(PhysicalFieldInfo field) {
    if (field?.IsVector != false) {
      return null;
    }
    var key = field.PropertyName;
    var columnType = field.ColumnType?.Trim();
    if (string.IsNullOrEmpty(columnType)) {
      return _scalar(field.EnumScalarType ?? field.TypeName, $"(data ->> '{key}')");
    }
    if (string.Equals(columnType, "jsonb", StringComparison.OrdinalIgnoreCase)) {
      return $"NULLIF(data -> '{key}', 'null'::jsonb)";
    }
    if (string.Equals(columnType, "json", StringComparison.OrdinalIgnoreCase)) {
      return $"NULLIF(data -> '{key}', 'null'::jsonb)::json";
    }
    if (columnType!.EndsWith("[]", StringComparison.Ordinal)) {
      return _elementTypeName(field.TypeName) is { } element && _scalar(element, "wh_e.v") is { } read
        ? $"CASE WHEN jsonb_typeof(data -> '{key}') <> 'null' THEN ARRAY(SELECT {read} FROM jsonb_array_elements_text(data -> '{key}') "
          + $"WITH ORDINALITY AS wh_e(v, n) ORDER BY wh_e.n)::{columnType} END"
        : null;
    }
    return _isTemporal(field.TypeName) || _scalar(field.TypeName, $"(data ->> '{key}')") is not { } scalar
      ? null
      : $"({scalar})::{columnType}";
  }

  /// <summary>The expression reading one known scalar from <paramref name="text"/> (its JSON text), or null.</summary>
  private static string? _scalar(string typeName, string text) {
    var micros = $"{text}::bigint * INTERVAL '1 microsecond'";
    return _normalize(typeName) switch {
      "System.String" or "string" => text,
      "System.Guid" => $"{text}::uuid",
      "System.Int32" or "int" => $"{text}::integer",
      "System.Int64" or "long" => $"{text}::bigint",
      "System.Int16" or "short" => $"{text}::smallint",
      "System.Boolean" or "bool" => $"{text}::boolean",
      "System.Decimal" or "decimal" => $"{text}::numeric",
      "System.Double" or "double" => $"{text}::double precision",
      "System.Single" or "float" => $"{text}::real",
      "System.DateTime" or "System.DateTimeOffset" => $"(TIMESTAMPTZ 'epoch' + {micros})",
      "System.DateOnly" => $"(TIMESTAMP 'epoch' + {micros})::date",
      "System.TimeOnly" => $"(TIME '00:00' + {micros})",
      _ => null,
    };
  }

  private static bool _isTemporal(string typeName) =>
    _normalize(typeName) is "System.DateTime" or "System.DateTimeOffset" or "System.DateOnly" or "System.TimeOnly";

  private static string _normalize(string typeName) => typeName.Replace("global::", "").TrimEnd('?');

  /// <summary>
  /// The element type of an array or of a single-argument generic collection (<c>List&lt;T&gt;</c>,
  /// <c>IReadOnlyList&lt;T&gt;</c>, ...), or null.
  /// </summary>
  private static string? _elementTypeName(string typeName) {
    var name = typeName.TrimEnd('?');
    if (name.EndsWith("[]", StringComparison.Ordinal)) {
      return name[..^2];
    }
    var open = name.IndexOf('<');
    return open > 0 && name.EndsWith(">", StringComparison.Ordinal) && name.IndexOf(',', open) < 0
      ? name.Substring(open + 1, name.Length - open - 2)
      : null;
  }

  /// <summary>The schema of <paramref name="qualifiedTable"/> with its dot, or nothing for an unqualified table.</summary>
  private static string _schemaPrefix(string qualifiedTable) {
    var dot = qualifiedTable.IndexOf('.');
    return dot > 0 ? qualifiedTable[..(dot + 1)] : string.Empty;
  }
}
