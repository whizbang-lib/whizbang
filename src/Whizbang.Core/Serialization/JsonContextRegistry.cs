using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core.Observability;

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
  private static readonly ConcurrentQueue<IJsonTypeInfoResolver> _resolvers = new();

  /// <summary>
  /// Thread-safe collection of converter instances to add to JsonSerializerOptions.
  /// Populated via [ModuleInitializer] methods in each assembly.
  /// Needed for WhizbangId converters due to STJ source generation limitations.
  /// Converters are instantiated at compile-time by source generators for AOT compatibility.
  /// </summary>
  private static readonly ConcurrentQueue<JsonConverter> _converters = new();

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

    _resolvers.Enqueue(resolver);
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

    _converters.Enqueue(converter);
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

  // ===========================
  // Polymorphic Interface Support
  // ===========================

  /// <summary>
  /// Thread-safe collection mapping base types to their derived types and discriminators.
  /// Populated via [ModuleInitializer] methods in each assembly.
  /// Used to build polymorphic JsonTypeInfo at runtime for interface types (IEvent, ICommand, IMessage).
  /// </summary>
  private static readonly ConcurrentDictionary<Type, ConcurrentBag<(Type derivedType, string discriminator)>> _derivedTypes = new();

  /// <summary>
  /// Cached polymorphic JsonTypeInfo instances (built lazily at runtime).
  /// Key is a tuple of (baseType, optionsHashCode) to support multiple options instances.
  /// </summary>
  private static readonly ConcurrentDictionary<(Type baseType, int optionsHash), object> _polymorphicTypeInfoCache = new();

  /// <summary>
  /// Registers a derived type for polymorphic serialization.
  /// Called from [ModuleInitializer] methods in each assembly to register concrete types
  /// that implement IEvent, ICommand, or IMessage interfaces.
  /// </summary>
  /// <typeparam name="TBase">The base interface type (e.g., IEvent, ICommand)</typeparam>
  /// <typeparam name="TDerived">The concrete derived type implementing TBase</typeparam>
  /// <param name="discriminator">Optional type discriminator for JSON serialization. Defaults to type name.</param>
  public static void RegisterDerivedType<TBase, TDerived>(string? discriminator = null)
    where TDerived : TBase {
    var bag = _derivedTypes.GetOrAdd(typeof(TBase), _ => new ConcurrentBag<(Type, string)>());
    var actualDiscriminator = discriminator ?? typeof(TDerived).Name;

    // Avoid duplicate registrations
    if (!bag.Any(x => x.derivedType == typeof(TDerived))) {
      bag.Add((typeof(TDerived), actualDiscriminator));
    }
  }

  /// <summary>
  /// Gets all registered derived types for a base type.
  /// Used for testing and diagnostics.
  /// </summary>
  /// <typeparam name="TBase">The base interface type (e.g., IEvent, ICommand)</typeparam>
  /// <returns>Collection of registered derived types</returns>
  public static IEnumerable<Type> GetRegisteredDerivedTypes<TBase>() {
    if (_derivedTypes.TryGetValue(typeof(TBase), out var bag)) {
      return bag.Select(x => x.derivedType).ToArray();
    }
    return [];
  }

  /// <summary>
  /// Gets the discriminator for a specific derived type.
  /// Used for testing and diagnostics.
  /// </summary>
  /// <typeparam name="TBase">The base interface type</typeparam>
  /// <typeparam name="TDerived">The concrete derived type</typeparam>
  /// <returns>The discriminator string, or null if not registered</returns>
  public static string? GetDiscriminator<TBase, TDerived>()
    where TDerived : TBase {
    if (_derivedTypes.TryGetValue(typeof(TBase), out var bag)) {
      var entry = bag.FirstOrDefault(x => x.derivedType == typeof(TDerived));
      return entry.discriminator;
    }
    return null;
  }

  /// <summary>
  /// Gets or creates a polymorphic JsonTypeInfo for an interface type at runtime.
  /// Aggregates all derived types registered from all assemblies.
  /// </summary>
  /// <typeparam name="TBase">The base interface type (e.g., IEvent, ICommand)</typeparam>
  /// <param name="options">JsonSerializerOptions to use for creating the type info</param>
  /// <returns>Polymorphic JsonTypeInfo for the interface, or null if no derived types are registered</returns>
  public static JsonTypeInfo<TBase>? GetPolymorphicTypeInfo<TBase>(JsonSerializerOptions options)
    where TBase : notnull {
    ArgumentNullException.ThrowIfNull(options);

    if (!_derivedTypes.TryGetValue(typeof(TBase), out var bag) || bag.IsEmpty) {
      return null;
    }

    var cacheKey = (typeof(TBase), options.GetHashCode());
    var cached = _polymorphicTypeInfoCache.GetOrAdd(cacheKey, _ => _createPolymorphicTypeInfo<TBase>(options, bag));
    return (JsonTypeInfo<TBase>)cached;
  }

  /// <summary>
  /// Gets or creates a polymorphic JsonTypeInfo for List&lt;T&gt; where T is an interface type.
  /// </summary>
  /// <typeparam name="TBase">The base interface type (e.g., IEvent, ICommand)</typeparam>
  /// <param name="options">JsonSerializerOptions to use for creating the type info</param>
  /// <returns>JsonTypeInfo for List&lt;TBase&gt; with polymorphic element handling</returns>
  public static JsonTypeInfo<List<TBase>>? GetPolymorphicListTypeInfo<TBase>(JsonSerializerOptions options)
    where TBase : notnull {
    ArgumentNullException.ThrowIfNull(options);

    var elementTypeInfo = GetPolymorphicTypeInfo<TBase>(options);
    if (elementTypeInfo == null) {
      return null;
    }

    var cacheKey = (typeof(List<TBase>), options.GetHashCode());
    var cached = _polymorphicTypeInfoCache.GetOrAdd(cacheKey, _ =>
      JsonMetadataServices.CreateListInfo<List<TBase>, TBase>(
        options,
        collectionInfo: new JsonCollectionInfoValues<List<TBase>> {
          ObjectCreator = () => new List<TBase>(),
          ElementInfo = elementTypeInfo
        }));
    return (JsonTypeInfo<List<TBase>>)cached;
  }

  /// <summary>
  /// Gets or creates a polymorphic JsonTypeInfo for MessageEnvelope&lt;T&gt; where T is an interface type.
  /// </summary>
  /// <typeparam name="TBase">The base interface type (e.g., IEvent, ICommand)</typeparam>
  /// <param name="options">JsonSerializerOptions to use for creating the type info</param>
  /// <returns>JsonTypeInfo for MessageEnvelope&lt;TBase&gt; with polymorphic payload handling</returns>
  public static JsonTypeInfo<MessageEnvelope<TBase>>? GetPolymorphicEnvelopeTypeInfo<TBase>(JsonSerializerOptions options)
    where TBase : class {
    ArgumentNullException.ThrowIfNull(options);

    var payloadTypeInfo = GetPolymorphicTypeInfo<TBase>(options);
    if (payloadTypeInfo == null) {
      return null;
    }

    var cacheKey = (typeof(MessageEnvelope<TBase>), options.GetHashCode());
    var cached = _polymorphicTypeInfoCache.GetOrAdd(cacheKey, _ =>
      _createPolymorphicEnvelopeTypeInfo<TBase>(options, payloadTypeInfo));
    return (JsonTypeInfo<MessageEnvelope<TBase>>)cached;
  }

  /// <summary>
  /// Creates a polymorphic JsonTypeInfo for an interface type with all registered derived types.
  /// Types that cannot be resolved (e.g., have unsupported property types) are skipped.
  /// </summary>
  private static JsonTypeInfo<TBase> _createPolymorphicTypeInfo<TBase>(
    JsonSerializerOptions _options,
    ConcurrentBag<(Type derivedType, string discriminator)> _derivedTypes)
    where TBase : notnull {
    // Create object info for interface (can't be instantiated directly)
    var objectInfo = new JsonObjectInfoValues<TBase> {
      ObjectCreator = null,
      ObjectWithParameterizedConstructorCreator = null,
      PropertyMetadataInitializer = _ => [],
      SerializeHandler = null
    };

    var jsonTypeInfo = JsonMetadataServices.CreateObjectInfo<TBase>(_options, objectInfo);

    // Configure polymorphism after creation
    jsonTypeInfo.PolymorphismOptions = new JsonPolymorphismOptions {
      TypeDiscriminatorPropertyName = "$type",
      UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor,
      IgnoreUnrecognizedTypeDiscriminators = true
    };

    // Filter out duplicate discriminators - keep only the first registration for each discriminator
    var seenDiscriminators = new HashSet<string>(StringComparer.Ordinal);

    foreach (var (derivedType, discriminator) in _derivedTypes.Distinct()) {
      // Skip duplicate discriminators to avoid "type discriminator is already specified" errors
      if (!seenDiscriminators.Add(discriminator)) {
        continue;
      }

      // Try to verify the type can be fully resolved and configured, skip if not
      try {
        // Get the type info and force full configuration by:
        // 1. Getting the type info
        // 2. Making it read-only (which triggers initial configuration)
        // 3. Accessing Properties to force property metadata resolution
        // This ensures any errors (e.g., unsupported property types like IReadOnlySet<string>)
        // are caught here rather than later when the polymorphism options are processed
        var derivedTypeInfo = _options.GetTypeInfo(derivedType);
        if (!derivedTypeInfo.IsReadOnly) {
          derivedTypeInfo.MakeReadOnly();
        }
        // Force property metadata resolution - this will throw if any property type can't be resolved
        _ = derivedTypeInfo.Properties;
        jsonTypeInfo.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(derivedType, discriminator));
      } catch {
        // Skip types that can't be resolved (e.g., have unsupported property types)
        // This is expected in test assemblies where not all types have complete metadata
      }
    }

    return jsonTypeInfo;
  }

  /// <summary>
  /// Creates a polymorphic JsonTypeInfo for MessageEnvelope&lt;T&gt; with polymorphic payload.
  /// </summary>
  private static JsonTypeInfo<MessageEnvelope<TBase>> _createPolymorphicEnvelopeTypeInfo<TBase>(
    JsonSerializerOptions _options,
    JsonTypeInfo<TBase> _payloadTypeInfo)
    where TBase : class {
    // Create property metadata - the key is specifying PropertyTypeInfo for Payload
    var properties = new JsonPropertyInfo[3];

    properties[0] = _createProperty<ValueObjects.MessageId, MessageEnvelope<TBase>>(
      _options,
      "MessageId",
      obj => obj.MessageId,
      null);

    // CRITICAL: Use the polymorphic type info for the Payload property
    properties[1] = _createPropertyWithTypeInfo(
      _options,
      "Payload",
      (MessageEnvelope<TBase> obj) => obj.Payload,
      null,
      _payloadTypeInfo);

    properties[2] = _createProperty<List<MessageHop>, MessageEnvelope<TBase>>(
      _options,
      "Hops",
      obj => obj.Hops?.ToList() ?? [],
      null);

    // Constructor parameters for deserialization
    var ctorParams = new JsonParameterInfoValues[] {
      new() {
        Name = "MessageId",
        ParameterType = typeof(ValueObjects.MessageId),
        Position = 0,
        HasDefaultValue = false,
        DefaultValue = default!
      },
      new() {
        Name = "Payload",
        ParameterType = typeof(TBase),
        Position = 1,
        HasDefaultValue = false,
        DefaultValue = default!
      },
      new() {
        Name = "Hops",
        ParameterType = typeof(List<MessageHop>),
        Position = 2,
        HasDefaultValue = false,
        DefaultValue = default!
      }
    };

    var objectInfo = new JsonObjectInfoValues<MessageEnvelope<TBase>> {
      ObjectCreator = null,
      ObjectWithParameterizedConstructorCreator = args => new MessageEnvelope<TBase>(
        (ValueObjects.MessageId)args[0]!,
        (TBase)args[1]!,
        (List<MessageHop>)args[2]!),
      ConstructorParameterMetadataInitializer = () => ctorParams,
      PropertyMetadataInitializer = _ => properties
    };

    return JsonMetadataServices.CreateObjectInfo<MessageEnvelope<TBase>>(_options, objectInfo);
  }

  /// <summary>
  /// Helper to create a JsonPropertyInfo without requiring typed JsonTypeInfo.
  /// Uses options to resolve the property's type info.
  /// </summary>
  private static JsonPropertyInfo _createProperty<TProperty, TDeclaringType>(
    JsonSerializerOptions _options,
    string _propertyName,
    Func<TDeclaringType, TProperty> _getter,
    Action<TDeclaringType, TProperty>? _setter) {
    var propertyInfo = new JsonPropertyInfoValues<TProperty> {
      IsProperty = true,
      IsPublic = true,
      IsVirtual = false,
      DeclaringType = typeof(TDeclaringType),
      Getter = obj => _getter((TDeclaringType)obj!),
      Setter = _setter != null ? (obj, value) => _setter((TDeclaringType)obj!, value!) : null,
      JsonPropertyName = _propertyName,
      PropertyName = _propertyName
    };

    return JsonMetadataServices.CreatePropertyInfo<TProperty>(_options, propertyInfo);
  }

  /// <summary>
  /// Helper to create a JsonPropertyInfo with a specific JsonTypeInfo.
  /// This is critical for polymorphic properties where we need to use
  /// a custom polymorphic type info instead of the default.
  /// </summary>
  private static JsonPropertyInfo _createPropertyWithTypeInfo<TProperty, TDeclaringType>(
    JsonSerializerOptions _options,
    string _propertyName,
    Func<TDeclaringType, TProperty> _getter,
    Action<TDeclaringType, TProperty>? _setter,
    JsonTypeInfo<TProperty> _propertyTypeInfo) {
    var propertyInfo = new JsonPropertyInfoValues<TProperty> {
      IsProperty = true,
      IsPublic = true,
      IsVirtual = false,
      DeclaringType = typeof(TDeclaringType),
      Getter = obj => _getter((TDeclaringType)obj!),
      Setter = _setter != null ? (obj, value) => _setter((TDeclaringType)obj!, value!) : null,
      JsonPropertyName = _propertyName,
      PropertyName = _propertyName,
      PropertyTypeInfo = _propertyTypeInfo // CRITICAL: Use the custom polymorphic type info
    };

    return JsonMetadataServices.CreatePropertyInfo<TProperty>(_options, propertyInfo);
  }
}
