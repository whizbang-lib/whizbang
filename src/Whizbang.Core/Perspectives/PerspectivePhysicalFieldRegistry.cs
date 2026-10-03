using System.Collections.Concurrent;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Process-wide map of a perspective model's <c>[PhysicalField]</c> and <c>[VectorField]</c> properties to their
/// columns, populated from the generated perspective runner's <c>[ModuleInitializer]</c> exactly as
/// <see cref="PerspectiveTtlRegistry"/> and <see cref="PerspectiveRowCapRegistry"/> are.
/// </summary>
/// <remarks>
/// <para>
/// The collective apply path reads it to send a setter or a condition on a physical property to its column
/// instead of a path inside the <c>data</c> document, and to know whether the document keeps a copy that has to be
/// written too. It is driver-neutral: the EF Core and Dapper drivers read the same registration.
/// </para>
/// <para>
/// Self-registration rather than attribute scanning is an AOT requirement: the generator already knows every
/// physical field, its column name and the model's storage mode at compile time, so nothing has to be discovered
/// by reflection at run time.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectivePhysicalFieldRegistryTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveRunnerPhysicalFieldRegistrationTests.cs</tests>
public static class PerspectivePhysicalFieldRegistry {
  private static readonly ConcurrentDictionary<(Type ModelType, string PropertyName), PerspectivePhysicalField> _fields = new();

  /// <summary>
  /// Registers one physical field of a model. Idempotent; the last registration wins, so two perspectives over
  /// one model registering the same fields is harmless.
  /// </summary>
  /// <param name="modelType">The perspective model.</param>
  /// <param name="propertyName">The model property.</param>
  /// <param name="columnName">The column the property is stored in.</param>
  /// <param name="storageMode">The model's storage mode; <see cref="FieldStorageMode.Split"/> keeps the field out of the document.</param>
  /// <param name="isVector">Whether the field is a <c>[VectorField]</c>.</param>
  /// <param name="scalarType">
  /// For an enumeration, the scalar its column holds (the underlying number, widened as
  /// <see cref="PerspectivePhysicalValues.ColumnScalarType"/> widens it); null otherwise.
  /// </param>
  /// <param name="columnType">The column type the author declared with <c>[PhysicalField(ColumnType = …)]</c>, if any.</param>
  public static void Register(
      Type modelType, string propertyName, string columnName, FieldStorageMode storageMode, bool isVector = false,
      Type? scalarType = null, string? columnType = null) {
    ArgumentNullException.ThrowIfNull(modelType);
    ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
    ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
    _fields[(modelType, propertyName)] = new PerspectivePhysicalField(
      propertyName, columnName, InDocument: storageMode != FieldStorageMode.Split, isVector, scalarType, columnType);
  }

  /// <summary>
  /// The physical field registered for a model's column, if there is one: for a writer that has the column name
  /// rather than the property, such as a store binding a column's value.
  /// </summary>
  /// <param name="modelType">The perspective model.</param>
  /// <param name="columnName">The column, as registered.</param>
  /// <param name="field">The field, when one is registered.</param>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectivePhysicalFieldRegistryTests.cs:TryResolveColumn_FindsTheFieldByItsColumnAsync</tests>
  public static bool TryResolveColumn(Type modelType, string columnName, out PerspectivePhysicalField field) {
    foreach (var ((model, _), registered) in _fields) {
      if (model == modelType && string.Equals(registered.ColumnName, columnName, StringComparison.Ordinal)) {
        field = registered;
        return true;
      }
    }
    field = default;
    return false;
  }

  /// <summary>
  /// The model's jsonb columns whose value the document holds as well (every storage mode but Split), in a
  /// stable order: the writes that cannot produce the document's copy themselves restore it from these.
  /// </summary>
  /// <param name="modelType">The model type.</param>
  /// <returns>The fields, ordered by property name; empty for a model with none.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectivePhysicalFieldRegistryTests.cs</tests>
  public static IReadOnlyList<PerspectivePhysicalField> JsonbDocumentFields(Type modelType) =>
    [.. _fields
      .Where(entry => entry.Key.ModelType == modelType && entry.Value.IsJsonbColumn && entry.Value.InDocument)
      .Select(entry => entry.Value)
      .OrderBy(field => field.PropertyName, StringComparer.Ordinal)];

  /// <summary>
  /// Whether the model's promoted column with this name is a jsonb column, which a writer binds as JSON
  /// text under the persistence profile rather than as the value's own driver type.
  /// </summary>
  /// <param name="modelType">The model type.</param>
  /// <param name="columnName">The column name, as the runner's physical values are keyed.</param>
  /// <returns>True when a field of the model is registered with that column and a jsonb column type.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectivePhysicalFieldRegistryTests.cs</tests>
  public static bool IsJsonbColumn(Type modelType, string columnName) {
    foreach (var ((type, _), field) in _fields) {
      if (type == modelType && field.IsJsonbColumn && string.Equals(field.ColumnName, columnName, StringComparison.Ordinal)) {
        return true;
      }
    }

    return false;
  }

  /// <summary>The physical field registered for a model property, if there is one.</summary>
  public static bool TryResolve(Type modelType, string propertyName, out PerspectivePhysicalField field) {
    if (modelType is null) {
      field = default;
      return false;
    }
    return _fields.TryGetValue((modelType, propertyName), out field);
  }
}

/// <summary>One physical field of a perspective model, as the generated runner registered it.</summary>
/// <param name="PropertyName">The model property.</param>
/// <param name="ColumnName">The column it is stored in.</param>
/// <param name="InDocument">
/// Whether the <c>data</c> document keeps a copy of the value (every storage mode but
/// <see cref="FieldStorageMode.Split"/>), so a write to the column has to write the document path too.
/// </param>
/// <param name="IsVector">Whether the field is a <c>[VectorField]</c>.</param>
/// <param name="ScalarType">For an enumeration, the scalar its column holds; null otherwise.</param>
/// <param name="ColumnType">The declared column type, if the author declared one.</param>
public readonly record struct PerspectivePhysicalField(
    string PropertyName, string ColumnName, bool InDocument, bool IsVector, Type? ScalarType = null, string? ColumnType = null) {
  /// <summary>Whether the column is <c>jsonb</c>, the only column type a keyed-array upsert can target.</summary>
  public bool IsJsonbColumn => string.Equals(ColumnType?.Trim(), "jsonb", StringComparison.OrdinalIgnoreCase);
}
