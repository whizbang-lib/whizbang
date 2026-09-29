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
/// The column value is bound as a typed parameter (the CLR value, which Npgsql types from its runtime type), the
/// same way the per-event upsert binds physical values. Shapes a raw parameter cannot carry faithfully are
/// refused with <see cref="NotSupportedException"/>: a vector field (its column form is a conversion the core data
/// layer does not reference) and an enumeration (its column form depends on the conversion the store mapping
/// chose).
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/CollectivePhysicalColumnCompilerTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectivePhysicalColumnIntegrationTests.cs</tests>
public static class CollectivePhysicalColumns {
  /// <summary>
  /// The physical field a collective setter or condition on <paramref name="propertyName"/> targets, or null when
  /// the property is a document path. Throws <see cref="NotSupportedException"/> for a physical field the collective
  /// path cannot bind as a typed parameter.
  /// </summary>
  /// <param name="modelType">The model the property belongs to.</param>
  /// <param name="propertyName">The property.</param>
  /// <param name="propertyType">The property's declared type, checked for shapes a raw parameter cannot carry.</param>
  public static PerspectivePhysicalField? Resolve(Type modelType, string propertyName, Type propertyType) {
    ArgumentNullException.ThrowIfNull(propertyType);
    if (!PerspectivePhysicalFieldRegistry.TryResolve(modelType, propertyName, out var field)) {
      return null;
    }
    if (field.IsVector) {
      throw new NotSupportedException(
        $"{modelType.Name}.{propertyName} is a [VectorField]. A collective cannot set or filter a vector column; " +
        "write it through the perspective's per-event Apply instead.");
    }
    if ((Nullable.GetUnderlyingType(propertyType) ?? propertyType).IsEnum) {
      throw new NotSupportedException(
        $"{modelType.Name}.{propertyName} is an enumeration stored in a physical column. Its column form depends on " +
        "the store's value conversion, which a collective's raw parameter does not apply; write it through the " +
        "perspective's per-event Apply, or keep the field in the document.");
    }
    return field;
  }

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
