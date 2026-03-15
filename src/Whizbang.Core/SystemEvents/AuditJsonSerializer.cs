using System.Text.Json;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Shared serialization helper for audit-related classes.
/// Consolidates the common try/catch + fallback pattern for serializing objects
/// to <see cref="JsonElement"/> in an AOT-compatible way.
/// </summary>
internal static class AuditJsonSerializer {
  /// <summary>
  /// Serializes a value to <see cref="JsonElement"/> using the combined
  /// <see cref="JsonContextRegistry"/> options. Attempts the compile-time type first,
  /// then falls back to the runtime type, and finally returns an empty JSON object.
  /// </summary>
  /// <typeparam name="T">The compile-time type of the value.</typeparam>
  /// <param name="value">The value to serialize.</param>
  /// <param name="jsonOptions">
  /// The <see cref="JsonSerializerOptions"/> to use for serialization.
  /// When null, creates options via <see cref="JsonContextRegistry.CreateCombinedOptions"/>.
  /// </param>
  /// <returns>A <see cref="JsonElement"/> representing the serialized value.</returns>
  internal static JsonElement SerializeToJsonElement<T>(T value, JsonSerializerOptions? jsonOptions = null) {
    if (value is null) {
      return default;
    }

    var options = jsonOptions ?? JsonContextRegistry.CreateCombinedOptions();

    try {
      var typeInfo = options.GetTypeInfo(typeof(T));
      if (typeInfo is not null) {
        var json = JsonSerializer.Serialize(value, typeInfo);
        return JsonDocument.Parse(json).RootElement.Clone();
      }
    } catch (NotSupportedException) {
      // Type not registered with JsonContextRegistry — fall through to fallback
    }

    try {
      // Fallback: try the runtime type (may differ from compile-time type)
      var typeInfo = options.GetTypeInfo(value.GetType());
      if (typeInfo is not null) {
        var json = JsonSerializer.Serialize(value, typeInfo);
        return JsonDocument.Parse(json).RootElement.Clone();
      }
    } catch (NotSupportedException) {
      // Type not registered — return empty object
    }

    return JsonDocument.Parse("{}").RootElement.Clone();
  }
}
