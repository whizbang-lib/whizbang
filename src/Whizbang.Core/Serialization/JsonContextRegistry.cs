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
  private static readonly ConcurrentBag<IJsonTypeInfoResolver> _resolvers = [];

  /// <summary>
  /// Thread-safe collection of converter instances to add to JsonSerializerOptions.
  /// Populated via [ModuleInitializer] methods in each assembly.
  /// Needed for WhizbangId converters due to STJ source generation limitations.
  /// Converters are instantiated at compile-time by source generators for AOT compatibility.
  /// </summary>
  private static readonly ConcurrentBag<JsonConverter> _converters = [];

  /// <summary>
  /// Thread-safe dictionary mapping normalized type names to (Type, Resolver) tuples.
  /// Populated via [ModuleInitializer] methods in each assembly.
  /// Supports fuzzy matching on "TypeName, AssemblyName" portion (strips Version/Culture/PublicKeyToken).
  /// This allows cross-assembly type resolution without reflection.
  /// </summary>
  private static readonly ConcurrentDictionary<string, (Type type, IJsonTypeInfoResolver resolver)> _typeNameMappings = new();

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
  /// Registers a type name mapping for AOT-safe type resolution by string name.
  /// Called from [ModuleInitializer] methods - runs before Main().
  /// Uses compile-time typeof() for AOT compatibility (no reflection).
  /// Normalizes type name to support fuzzy matching (strips Version/Culture/PublicKeyToken).
  /// </summary>
  /// <param name="assemblyQualifiedName">Assembly-qualified type name (e.g., "MyApp.Commands.CreateOrder, MyApp.Contracts")</param>
  /// <param name="type">The Type object (obtained via typeof() at compile-time)</param>
  /// <param name="resolver">The resolver that can provide JsonTypeInfo for this type</param>
  public static void RegisterTypeName(string assemblyQualifiedName, Type type, IJsonTypeInfoResolver resolver) {
    ArgumentNullException.ThrowIfNull(assemblyQualifiedName);
    ArgumentNullException.ThrowIfNull(type);
    ArgumentNullException.ThrowIfNull(resolver);

    // Use centralized type name normalization from EventTypeMatchingHelper
    var normalizedName = Messaging.EventTypeMatchingHelper.NormalizeTypeName(assemblyQualifiedName);
    _typeNameMappings[normalizedName] = (type, resolver);
  }

  /// <summary>
  /// Gets JsonTypeInfo for a type by its assembly-qualified name.
  /// Supports fuzzy matching on "TypeName, AssemblyName" portion (strips Version/Culture/PublicKeyToken).
  /// This allows short-form names to match full AssemblyQualifiedNames.
  /// </summary>
  /// <param name="assemblyQualifiedName">Assembly-qualified type name (can be short or full form)</param>
  /// <param name="options">JsonSerializerOptions to use for creating JsonTypeInfo</param>
  /// <returns>JsonTypeInfo for the type, or null if not registered</returns>
  public static JsonTypeInfo? GetTypeInfoByName(string assemblyQualifiedName, JsonSerializerOptions options) {
    if (string.IsNullOrEmpty(assemblyQualifiedName)) {
      return null;
    }

    if (options == null) {
      return null;
    }

    // Use centralized type name normalization from EventTypeMatchingHelper
    var normalizedName = Messaging.EventTypeMatchingHelper.NormalizeTypeName(assemblyQualifiedName);

    if (_typeNameMappings.TryGetValue(normalizedName, out var entry)) {
      return entry.resolver.GetTypeInfo(entry.type, options);
    }

    return null;
  }

  /// <summary>
  /// Gets the count of registered resolvers (for diagnostics/testing).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/JsonContextRegistryTests.cs:RegisterConverter_WithConverterInstance_AddsToConverterCollectionAsync</tests>
  public static int RegisteredCount => _resolvers.Count;

  /// <summary>
  /// Gets the count of registered type name mappings (for diagnostics/testing).
  /// </summary>
  public static int RegisteredTypeNameCount => _typeNameMappings.Count;
}
