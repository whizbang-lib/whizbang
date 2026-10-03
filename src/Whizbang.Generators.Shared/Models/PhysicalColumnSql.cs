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
  /// The extraction reproduces what the writer stores, type by type. Dates and times are microsecond
  /// counts in the document, not renderings, so they are rebuilt by exact integer arithmetic from the
  /// epoch (or from midnight) rather than parsed. A jsonb column is copied from the member as it is. A field
  /// stored only in the column (Split), a vector, any other column whose type the author chose, and a type
  /// outside the known set are not backfilled: the
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
    if (field.IsSplit || field.IsVector) {
      return null;
    }
    // A jsonb column holds exactly the JSON the document holds for the member, written by the same
    // serializer under the same profile, so the member is copied as it is.
    if (PhysicalFieldScalar.IsJsonb(field.ColumnType)) {
      return $"(data -> '{field.PropertyName}')";
    }
    if (!string.IsNullOrWhiteSpace(field.ColumnType)) {
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
