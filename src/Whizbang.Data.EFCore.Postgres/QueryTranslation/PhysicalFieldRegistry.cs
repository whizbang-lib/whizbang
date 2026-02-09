using System.Collections.Concurrent;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation;

/// <summary>
/// Runtime registry mapping model properties to physical column names.
/// Source generators populate this at startup via Register calls.
/// Used by <see cref="PhysicalFieldMemberTranslator"/> to redirect
/// r.Data.PropertyName queries to physical columns.
/// </summary>
/// <remarks>
/// <para>
/// This registry enables unified query syntax where users write:
/// <code>
/// .Where(r => r.Data.Price >= 20.00m)  // Looks like JSONB access
/// </code>
/// But the query translator redirects to physical column access:
/// <code>
/// WHERE price >= 20.00  // Uses indexed physical column
/// </code>
/// </para>
/// <para>
/// Thread-safe for concurrent registration and lookup.
/// Designed for startup initialization by generated code.
/// </para>
/// </remarks>
/// <docs>perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/PhysicalFieldRegistryTests.cs</tests>
public static class PhysicalFieldRegistry {
  private static readonly ConcurrentDictionary<(Type ModelType, string PropertyName), PhysicalFieldMapping> _mappings = new();

  /// <summary>
  /// Registers a physical field mapping for a model property.
  /// Called by generated code at startup.
  /// </summary>
  /// <typeparam name="TModel">The model type containing the property</typeparam>
  /// <param name="propertyName">The property name (e.g., "Price")</param>
  /// <param name="columnName">The physical column name (e.g., "price")</param>
  /// <param name="shadowPropertyName">Optional shadow property name if different from column</param>
  public static void Register<TModel>(string propertyName, string columnName, string? shadowPropertyName = null) {
    Register(typeof(TModel), propertyName, columnName, shadowPropertyName);
  }

  /// <summary>
  /// Registers a physical field mapping for a model property.
  /// Non-generic version for dynamic registration.
  /// </summary>
  /// <param name="modelType">The model type containing the property</param>
  /// <param name="propertyName">The property name (e.g., "Price")</param>
  /// <param name="columnName">The physical column name (e.g., "price")</param>
  /// <param name="shadowPropertyName">Optional shadow property name if different from column</param>
  public static void Register(Type modelType, string propertyName, string columnName, string? shadowPropertyName = null) {
    ArgumentNullException.ThrowIfNull(modelType);
    ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
    ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

    var mapping = new PhysicalFieldMapping(columnName, shadowPropertyName ?? columnName);
    _mappings[(modelType, propertyName)] = mapping;
  }

  /// <summary>
  /// Attempts to get the column mapping for a model property.
  /// </summary>
  /// <param name="modelType">The model type</param>
  /// <param name="propertyName">The property name</param>
  /// <param name="mapping">The mapping if found</param>
  /// <returns>True if the property is a registered physical field</returns>
  public static bool TryGetMapping(Type modelType, string propertyName, out PhysicalFieldMapping mapping) {
    return _mappings.TryGetValue((modelType, propertyName), out mapping);
  }

  /// <summary>
  /// Checks if a property is registered as a physical field.
  /// </summary>
  /// <param name="modelType">The model type</param>
  /// <param name="propertyName">The property name</param>
  /// <returns>True if the property is a physical field</returns>
  public static bool IsPhysicalField(Type modelType, string propertyName) {
    return _mappings.ContainsKey((modelType, propertyName));
  }

  /// <summary>
  /// Gets all registered mappings for a model type.
  /// </summary>
  /// <param name="modelType">The model type</param>
  /// <returns>Dictionary of property name to mapping</returns>
  public static IReadOnlyDictionary<string, PhysicalFieldMapping> GetMappingsForModel(Type modelType) {
    ArgumentNullException.ThrowIfNull(modelType);

    var result = new Dictionary<string, PhysicalFieldMapping>();
    foreach (var ((type, propertyName), mapping) in _mappings) {
      if (type == modelType) {
        result[propertyName] = mapping;
      }
    }

    return result;
  }

  /// <summary>
  /// Clears all registered mappings. For testing purposes only.
  /// </summary>
  public static void Clear() {
    _mappings.Clear();
  }

  /// <summary>
  /// Gets the count of registered mappings. For diagnostics.
  /// </summary>
  public static int Count => _mappings.Count;
}

/// <summary>
/// Represents the mapping from a model property to a physical column.
/// </summary>
/// <param name="ColumnName">The physical database column name</param>
/// <param name="ShadowPropertyName">The EF Core shadow property name (may differ from column)</param>
public readonly record struct PhysicalFieldMapping(string ColumnName, string ShadowPropertyName);
