using System.Text.Json;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Shared serialization helper for audit-related classes.
/// Consolidates the common try/catch + fallback pattern for serializing objects
/// to <see cref="JsonElement"/> in an AOT-compatible way.
/// </summary>
[global::System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Diagnostic logging fires only when an audit payload can't be serialized — a rare failure path where LoggerMessage overhead isn't justified.")]
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
  internal static JsonElement SerializeToJsonElement<T>(T value, JsonSerializerOptions? jsonOptions = null, ILogger? logger = null) {
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
      // Type not registered — return empty object (logged below).
    }

    // Neither the compile-time nor the runtime type resolved — the audit payload is written as an
    // empty object. That's a silent compliance hole, so surface it (best-effort: a logger is only
    // present when the caller threaded one through).
    logger?.LogWarning(
      "Audit payload for type '{Type}' could not be serialized (no JsonTypeInfo in any registered JsonSerializerContext); persisting an empty '{{}}' audit payload.",
      value.GetType().FullName ?? value.GetType().Name);
    return JsonDocument.Parse("{}").RootElement.Clone();
  }
}
