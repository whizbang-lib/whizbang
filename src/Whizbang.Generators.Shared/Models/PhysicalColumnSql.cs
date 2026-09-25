namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// The SQL that brings a physical column into a perspective table that already exists: add it, then fill
/// it for the rows written before it existed.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#adding-a-physical-field</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PhysicalColumnBackfillIntegrationTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PhysicalColumnSqlTests.cs</tests>
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
