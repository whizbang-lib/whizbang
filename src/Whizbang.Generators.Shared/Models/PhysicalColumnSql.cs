namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// The SQL that brings a physical column into a perspective table that already exists: add it, then fill
/// it for the rows written before it existed.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#adding-a-physical-field</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PhysicalColumnBackfillIntegrationTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PhysicalColumnSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PhysicalColumnFillMaintenanceStepTests.cs</tests>
public static class PhysicalColumnSql {

  /// <summary>Adds the column when the table predates it; a no-op otherwise.</summary>
  public static string AddColumn(string qualifiedTable, string columnName, string columnType) =>
    $"ALTER TABLE {qualifiedTable} ADD COLUMN IF NOT EXISTS {columnName} {columnType};";

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
  /// The extraction reproduces what the writer stores, type by type. Dates and times are microsecond
  /// counts in the document, not renderings, so they are rebuilt by exact integer arithmetic from the
  /// epoch (or from midnight) rather than parsed. A field stored only in the column (Split), a vector, a
  /// column whose type the author chose, and a type outside the known set are not backfilled: the
  /// document either has no copy or the column's encoding of it is not something this can know.
  /// </para>
  /// </remarks>
  public static string? Backfill(string qualifiedTable, PhysicalFieldInfo field) {
    var extraction = Extraction(field);
    return extraction is null
      ? null
      : $"UPDATE {qualifiedTable} SET {field.ColumnName} = {extraction} " +
        $"WHERE {field.ColumnName} IS NULL AND jsonb_typeof(data -> '{field.PropertyName}') <> 'null';";
  }

  /// <summary>The table the schema arms a newly added column in, defined by migration 179.</summary>
  public const string FILLS_TABLE = "wh_physical_column_fills";

  /// <summary>The dollar quote around an armed column's extraction. Chosen so no extraction can contain it.</summary>
  public const string EXTRACTION_QUOTE = "$wbfill$";

  /// <summary>
  /// Arms the column for the physical-column fill when this pass is the one adding it, or null when the
  /// value cannot be reproduced from the document.
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
  /// The column name is stored as PostgreSQL folds an unquoted identifier, which is how the generated DDL
  /// writes it.
  /// </para>
  /// </remarks>
  /// <param name="qualifiedTable">The table, schema-qualified.</param>
  /// <param name="field">The promoted field.</param>
  /// <returns>One statement, or null.</returns>
  public static string? Arm(string qualifiedTable, PhysicalFieldInfo field) {
    if (field is null || Extraction(field) is not { } extraction) {
      return null;
    }
    var dot = qualifiedTable.IndexOf('.');
    var schema = dot > 0 ? qualifiedTable[..(dot + 1)] : string.Empty;
    var column = field.ColumnName.ToLowerInvariant();
    return $"INSERT INTO {schema}{FILLS_TABLE} (table_name, column_name, json_key, extraction) "
      + $"SELECT format('%I.%I', n.nspname, c.relname), '{column}', '{field.PropertyName}', "
      + $"{EXTRACTION_QUOTE}{extraction}{EXTRACTION_QUOTE} "
      + "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace "
      + $"WHERE c.oid = '{qualifiedTable}'::regclass AND NOT EXISTS (SELECT 1 FROM pg_attribute a "
      + $"WHERE a.attrelid = c.oid AND a.attname = '{column}' AND NOT a.attisdropped) "
      + "ON CONFLICT (table_name, column_name) DO UPDATE SET json_key = EXCLUDED.json_key, "
      + "extraction = EXCLUDED.extraction, armed_at = now();";
  }

  /// <summary>The expression that reads the field's value out of the document as the column's type, or null.</summary>
  public static string? Extraction(PhysicalFieldInfo field) {
    if (field.IsSplit || field.IsVector || !string.IsNullOrWhiteSpace(field.ColumnType)) {
      return null;
    }
    var text = $"(data ->> '{field.PropertyName}')";
    var micros = $"{text}::bigint * INTERVAL '1 microsecond'";
    return field.TypeName.Replace("global::", "").TrimEnd('?') switch {
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
}
