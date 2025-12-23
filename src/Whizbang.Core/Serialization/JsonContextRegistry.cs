using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Whizbang.Core.Serialization;

/// <summary>
/// <tests>tests/Whizbang.Core.Tests/JsonContextRegistryTests.cs:RegisterConverter_WithConverterInstance_AddsToConverterCollectionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/JsonContextRegistryTests.cs:RegisterConverter_WithNull_ThrowsArgumentNullExceptionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/JsonContextRegistryTests.cs:CreateCombinedOptions_WithRegisteredConverters_IncludesConvertersInOptionsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/JsonContextRegistryTests.cs:CreateCombinedOptions_IsAOTCompatible_NoReflectionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/JsonContextRegistryTests.cs:RegisteredConverters_AreInstantiatedAtCompileTime_NotRuntimeAsync</tests>
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
  /// Thread-safe collection of converter instances to add to JsonSerializerOptions.
  /// Populated via [ModuleInitializer] methods in each assembly.
  /// Needed for WhizbangId converters due to STJ source generation limitations.
  /// Converters are instantiated at compile-time by source generators for AOT compatibility.
  /// </summary>
  private static readonly ConcurrentBag<JsonConverter> _converters = new();

  /// <summary>
  /// Registers a JsonSerializerContext resolver.
  /// Called from [ModuleInitializer] methods - runs before Main().
  /// </summary>
  /// <param name="resolver">Source-generated JsonSerializerContext to register</param>
  public static void RegisterContext(IJsonTypeInfoResolver resolver) {
    ArgumentNullException.ThrowIfNull(resolver);

    _resolvers.Add(resolver);
  }

  /// <summary>
  /// Registers a JsonConverter instance to be added to JsonSerializerOptions.
  /// Called from [ModuleInitializer] methods for WhizbangId converters.
  /// This is needed because STJ source generation has trouble finding custom converters
  /// for value types in nested properties without them being in options.Converters.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/JsonContextRegistryTests.cs:RegisterConverter_WithConverterInstance_AddsToConverterCollectionAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/JsonContextRegistryTests.cs:RegisterConverter_WithNull_ThrowsArgumentNullExceptionAsync</tests>
  /// <param name="converter">The JsonConverter instance to register (instantiated at compile-time by source generators for AOT compatibility)</param>
  public static void RegisterConverter(JsonConverter converter) {
    ArgumentNullException.ThrowIfNull(converter);

    _converters.Add(converter);
  }

  /// <summary>
  /// Creates JsonSerializerOptions combining all registered contexts.
  /// Contexts are combined in registration order - Core contexts should register first
  /// to ensure infrastructure types (MessageHop, MessageId) take precedence.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/JsonContextRegistryTests.cs:CreateCombinedOptions_WithRegisteredConverters_IncludesConvertersInOptionsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/JsonContextRegistryTests.cs:CreateCombinedOptions_IsAOTCompatible_NoReflectionAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/JsonContextRegistryTests.cs:RegisteredConverters_AreInstantiatedAtCompileTime_NotRuntimeAsync</tests>
  /// <returns>JsonSerializerOptions with all registered contexts</returns>
  public static JsonSerializerOptions CreateCombinedOptions() {
    if (_resolvers.IsEmpty) {
      throw new InvalidOperationException(
        "No JsonSerializerContext instances registered. " +
        "Ensure Whizbang.Core and application assemblies are loaded before calling CreateCombinedOptions().");
    }

    var options = new JsonSerializerOptions {
      TypeInfoResolver = JsonTypeInfoResolver.Combine(_resolvers.ToArray()),
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Register WhizbangId converters as runtime converters in addition to resolvers.
    // This allows InfrastructureJsonContext's TryGetTypeInfoForRuntimeCustomConverter
    // to find them when deserializing MessageHop properties (MessageId?, CorrelationId?).
    // Without this, STJ falls back to treating MessageId as an empty object {}.
    //
    // Converters are instantiated at compile-time by source generators (no reflection!)
    // and registered via RegisterConverter() from [ModuleInitializer] methods.
    // This includes ProductId, OrderId, CustomerId, etc. from all application assemblies.
    foreach (var converter in _converters) {
      options.Converters.Add(converter);
    }

    return options;
  }

  /// <summary>
  /// Gets the count of registered resolvers (for diagnostics/testing).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/JsonContextRegistryTests.cs:RegisterConverter_WithConverterInstance_AddsToConverterCollectionAsync</tests>
  public static int RegisteredCount => _resolvers.Count;
}
