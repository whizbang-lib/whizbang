using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Whizbang.Core.Serialization;

/// <summary>
/// Registry for automatically collecting JsonSerializerContext instances from all loaded assemblies.
/// Uses ModuleInitializer pattern to allow libraries to self-register their contexts.
/// AOT-compatible - no reflection, all contexts are source-generated and registered at module load time.
/// </summary>
/// <remarks>
/// Each library (Whizbang.Core, ECommerce.Contracts, etc.) uses [ModuleInitializer] to register
/// its source-generated JsonSerializerContext classes. This ensures infrastructure types
/// (MessageHop, MessageEnvelope) from Core take precedence over application types.
/// </remarks>
public static class JsonContextRegistry {
  /// <summary>
  /// Thread-safe collection of registered resolvers.
  /// Populated via [ModuleInitializer] methods in each assembly.
  /// </summary>
  private static readonly ConcurrentBag<IJsonTypeInfoResolver> _resolvers = new();

  /// <summary>
  /// Registers a JsonSerializerContext resolver.
  /// Called from [ModuleInitializer] methods - runs before Main().
  /// </summary>
  /// <param name="resolver">Source-generated JsonSerializerContext to register</param>
  public static void RegisterContext(IJsonTypeInfoResolver resolver) {
    if (resolver == null) {
      throw new ArgumentNullException(nameof(resolver));
    }

    _resolvers.Add(resolver);
  }

  /// <summary>
  /// Creates JsonSerializerOptions combining all registered contexts.
  /// Contexts are combined in registration order - Core contexts should register first
  /// to ensure infrastructure types (MessageHop, MessageId) take precedence.
  /// </summary>
  /// <returns>JsonSerializerOptions with all registered contexts</returns>
  public static JsonSerializerOptions CreateCombinedOptions() {
    if (_resolvers.IsEmpty) {
      throw new InvalidOperationException(
        "No JsonSerializerContext instances registered. " +
        "Ensure Whizbang.Core and application assemblies are loaded before calling CreateCombinedOptions().");
    }

    var options = new JsonSerializerOptions {
      TypeInfoResolver = JsonTypeInfoResolver.Combine(_resolvers.ToArray()),
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Register WhizbangId converters as runtime converters in addition to resolvers.
    // This allows InfrastructureJsonContext's TryGetTypeInfoForRuntimeCustomConverter
    // to find them when deserializing MessageHop properties (MessageId?, CorrelationId?).
    // Without this, STJ falls back to treating MessageId as an empty object {}.
    options.Converters.Add(new ValueObjects.MessageIdJsonConverter());
    options.Converters.Add(new ValueObjects.CorrelationIdJsonConverter());

    return options;
  }

  /// <summary>
  /// Gets the count of registered resolvers (for diagnostics/testing).
  /// </summary>
  public static int RegisteredCount => _resolvers.Count;
}
