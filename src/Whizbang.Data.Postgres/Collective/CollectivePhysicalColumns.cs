using System.Linq;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres.Collective;

/// <summary>
/// Driver-neutral pieces of a collective apply that targets <c>[PhysicalField]</c> columns rather than paths in the
/// <c>data</c> document: resolving which model properties are columns (from the generator-emitted
/// <see cref="PerspectivePhysicalFieldRegistry"/>), quoting a column, the null-safe comparison a computed setter
/// makes against a column, and the <c>SET</c> list that assigns <c>data</c> only when a document path changes.
/// </summary>
/// <remarks>
/// <para>
/// Postgres writes a complete new copy of a jsonb value on any change to it, including its TOAST storage and every
/// index entry over the document. A setter on a column that the document does not also hold (a
/// <see cref="FieldStorageMode.Split"/> field) must therefore leave <c>data</c> out of the statement entirely; a field
/// kept in both places (<see cref="FieldStorageMode.Extracted"/>, or <c>JsonOnly</c> with a <c>[PhysicalField]</c>)
/// writes the column and the document path in the same statement.
/// </para>
/// <para>
/// The column value is bound as a typed parameter, the same scalar the per-event upsert writes: an enumeration as
/// its underlying number (<see cref="PerspectivePhysicalValues"/>), a vector in the driver's pgvector form, a
/// keyed array in a jsonb column through the same upsert expression a document path uses.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/CollectivePhysicalColumnCompilerTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectivePhysicalColumnIntegrationTests.cs</tests>
public static class CollectivePhysicalColumns {
  /// <summary>
  /// The physical field a collective setter or condition on <paramref name="propertyName"/> targets, or null when
  /// the property is a document path.
  /// </summary>
  /// <param name="modelType">The model the property belongs to.</param>
  /// <param name="propertyName">The property.</param>
  public static PerspectivePhysicalField? Resolve(Type modelType, string propertyName) =>
    PerspectivePhysicalFieldRegistry.TryResolve(modelType, propertyName, out var field) ? field : null;

  /// <summary>
  /// Refuses a vector column where a value is compared (a <c>Where</c> condition or a computed comparison):
  /// equality over an embedding is not a meaningful cohort, and similarity search is not a collective shape.
  /// </summary>
  public static void EnsureComparable(Type modelType, PerspectivePhysicalField field) {
    ArgumentNullException.ThrowIfNull(modelType);
    if (field.IsVector) {
      throw new NotSupportedException(
        $"{modelType.Name}.{field.PropertyName} is a [VectorField]. A collective can set a vector column but cannot " +
        "compare one; filter the cohort on another field.");
    }
  }

  /// <summary>
  /// Refuses a keyed-array upsert into a column that is not <c>jsonb</c>: only a jsonb column holds elements with
  /// a key member (a native array holds scalars, which have none).
  /// </summary>
  public static void EnsureKeyedArrayColumn(Type modelType, PerspectivePhysicalField field) {
    ArgumentNullException.ThrowIfNull(modelType);
    if (!field.IsJsonbColumn) {
      throw new NotSupportedException(
        $"UpsertElement on {modelType.Name}.{field.PropertyName} targets a physical column that is not jsonb. Keyed " +
        "elements need a jsonb column, which a promoted list is unless another ColumnType is declared; drop the " +
        "declared type, or keep the array in the document.");
    }
  }

  /// <summary>
  /// The value a physical column is bound with: an enumeration becomes its underlying number (the registered
  /// scalar), the same scalar the per-event upsert writes; anything else is bound as it is. An enumeration in a
  /// column whose type the author declared is refused, since its stored form is then the author's choice.
  /// </summary>
  public static object? ColumnValue(PerspectivePhysicalField field, object? value) {
    if (value is Enum && !string.IsNullOrWhiteSpace(field.ColumnType)) {
      throw new NotSupportedException(
        $"{field.PropertyName} is an enumeration in a column declared as {field.ColumnType}. A collective stores an " +
        "enumeration as its underlying number and cannot know the declared column's form; write it through the " +
        "perspective's per-event Apply.");
    }
    return PerspectivePhysicalValues.ToColumnScalar(value, field.ScalarType);
  }

  /// <summary>
  /// A vector's text form, <c>[1,2.5,-3]</c>, as pgvector parses it and as its client library prints it. Used where
  /// the driver sends the vector as text for the column to parse (the Dapper path).
  /// </summary>
  public static string? VectorText(float[]? vector) =>
    vector is null
      ? null
      : "[" + string.Join(",", vector.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";

  /// <summary>A column name as a quoted Postgres identifier.</summary>
  public static string Quote(string column) {
    ArgumentNullException.ThrowIfNull(column);
    return "\"" + column.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
  }

  /// <summary>
  /// A computed setter's comparison of a column against a parameter, null-safe so it agrees with the C# comparison
  /// the in-memory replay makes: <c>=</c> becomes <c>IS NOT DISTINCT FROM</c>, <c>&lt;&gt;</c> becomes
  /// <c>IS DISTINCT FROM</c>. Always a boolean, never null.
  /// </summary>
  public static string NullSafeComparison(string columnSql, string sqlOperator, string parameterSql) =>
    "(" + columnSql + (sqlOperator == "=" ? " IS NOT DISTINCT FROM " : " IS DISTINCT FROM ") + parameterSql + ")";

  /// <summary>
  /// The body of the <c>SET</c> list: <c>data = &lt;expression&gt;</c> when a document path changes, then each column
  /// assignment. A column assigned more than once keeps its last assignment (Postgres refuses two assignments to one
  /// column, and on a document path the last setter wins as well). With nothing to assign, falls back to
  /// <c>data = data</c> so the statement stays valid ahead of the store-column tail.
  /// </summary>
  /// <param name="dataExpression">The new document expression, or null when no document path changes.</param>
  /// <param name="columns">Column assignments in setter order, as (unquoted column, value SQL).</param>
  public static string RenderSetList(string? dataExpression, IReadOnlyList<(string Column, string ValueSql)> columns) {
    ArgumentNullException.ThrowIfNull(columns);
    var parts = new List<string>(columns.Count + 1);
    if (dataExpression is not null) {
      parts.Add("data = " + dataExpression);
    }
    var lastIndex = new Dictionary<string, int>(StringComparer.Ordinal);
    for (var i = 0; i < columns.Count; i++) {
      lastIndex[columns[i].Column] = i;
    }
    for (var i = 0; i < columns.Count; i++) {
      if (lastIndex[columns[i].Column] == i) {
        parts.Add(Quote(columns[i].Column) + " = " + columns[i].ValueSql);
      }
    }
    return parts.Count == 0 ? "data = data" : string.Join(", ", parts);
  }
}
