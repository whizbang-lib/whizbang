#pragma warning disable IDE1006 // Naming Styles
// Whizbang JsonContext Snippets
// Reusable C# code blocks for JsonContext generation

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Whizbang.Generators.Templates.Snippets;

internal class JsonContextSnippets {

  #region LAZY_FIELD_VALUE_OBJECT

  private JsonTypeInfo<global::Whizbang.Core.ValueObjects.__TYPE_NAME__>? ___TYPE_NAME__;

  #endregion

  #region LAZY_FIELD_MESSAGE
  private JsonTypeInfo<__FULLY_QUALIFIED_NAME__>? ___UNIQUE_IDENTIFIER__;
  #endregion

  #region LAZY_FIELD_MESSAGE_ENVELOPE
  private JsonTypeInfo<MessageEnvelope<__FULLY_QUALIFIED_NAME__>>? _MessageEnvelope___UNIQUE_IDENTIFIER__;
  #endregion

  #region GET_TYPE_INFO_VALUE_OBJECT
if (type == typeof(global::Whizbang.Core.ValueObjects.__TYPE_NAME__)) return Create___TYPE_NAME__(options);
  #endregion

  #region GET_TYPE_INFO_MESSAGE
if (type == typeof(__FULLY_QUALIFIED_NAME__)) {
  return Create___UNIQUE_IDENTIFIER__(options);
}
  #endregion

  #region GET_TYPE_INFO_MESSAGE_ENVELOPE
if (type == typeof(MessageEnvelope<__FULLY_QUALIFIED_NAME__>)) {
  return CreateMessageEnvelope___UNIQUE_IDENTIFIER__(options);
}
  #endregion

  #region CORE_TYPE_FACTORY
private JsonTypeInfo<global::Whizbang.Core.ValueObjects.__TYPE_NAME__> Create___TYPE_NAME__(JsonSerializerOptions options) {
  var converter = new global::Whizbang.Core.ValueObjects.__TYPE_NAME__JsonConverter();
  var jsonTypeInfo = JsonMetadataServices.CreateValueInfo<global::Whizbang.Core.ValueObjects.__TYPE_NAME__>(options, converter);
  jsonTypeInfo.OriginatingResolver = this;
  return jsonTypeInfo;
}
#endregion

#region LAZY_FIELD_LIST
private JsonTypeInfo<global::System.Collections.Generic.List<__ELEMENT_TYPE__>>? _List___ELEMENT_UNIQUE_IDENTIFIER__;
#endregion

#region GET_TYPE_INFO_LIST
if (type == typeof(global::System.Collections.Generic.List<__ELEMENT_TYPE__>)) {
  return CreateList___ELEMENT_UNIQUE_IDENTIFIER__(options);
}
#endregion

#region LIST_TYPE_FACTORY
private JsonTypeInfo<global::System.Collections.Generic.List<__ELEMENT_TYPE__>> CreateList___ELEMENT_UNIQUE_IDENTIFIER__(JsonSerializerOptions options) {
  // Get element type info - use TryGetOrCreateTypeInfo to handle circular references gracefully
  // (e.g., List<MyEvent> where MyEvent contains a List<MyEvent> property)
  var elementInfo = TryGetOrCreateTypeInfo<__ELEMENT_TYPE__>(options)
    ?? throw new InvalidOperationException(
        "No JsonTypeInfo found for element type __ELEMENT_TYPE__. " +
        "This may indicate a circular type reference. Ensure the element type is properly registered.");
  var collectionInfo = new JsonCollectionInfoValues<global::System.Collections.Generic.List<__ELEMENT_TYPE__>> {
    ObjectCreator = static () => new global::System.Collections.Generic.List<__ELEMENT_TYPE__>(),
    ElementInfo = elementInfo
  };
  var jsonTypeInfo = JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<__ELEMENT_TYPE__>, __ELEMENT_TYPE__>(options, collectionInfo);
  jsonTypeInfo.OriginatingResolver = this;
  return jsonTypeInfo;
}
#endregion

#region LAZY_FIELD_IREADONLYLIST
private JsonTypeInfo<global::System.Collections.Generic.IReadOnlyList<__ELEMENT_TYPE__>>? _IReadOnlyList___ELEMENT_UNIQUE_IDENTIFIER__;
#endregion

#region GET_TYPE_INFO_IREADONLYLIST
if (type == typeof(global::System.Collections.Generic.IReadOnlyList<__ELEMENT_TYPE__>)) {
  return CreateIReadOnlyList___ELEMENT_UNIQUE_IDENTIFIER__(options);
}
#endregion

#region IREADONLYLIST_TYPE_FACTORY
private JsonTypeInfo<global::System.Collections.Generic.IReadOnlyList<__ELEMENT_TYPE__>> CreateIReadOnlyList___ELEMENT_UNIQUE_IDENTIFIER__(JsonSerializerOptions options) {
  // Get element type info - use TryGetOrCreateTypeInfo to handle circular references gracefully
  var elementInfo = TryGetOrCreateTypeInfo<__ELEMENT_TYPE__>(options)
    ?? throw new InvalidOperationException(
        "No JsonTypeInfo found for element type __ELEMENT_TYPE__. " +
        "This may indicate a circular type reference. Ensure the element type is properly registered.");
  // IReadOnlyList<T> doesn't implement IList<T>, so we can't use CreateListInfo.
  // Use CreateIEnumerableInfo which works with any IEnumerable<T> (IReadOnlyList<T> extends it).
  // ObjectCreator returns List<T> which implements IReadOnlyList<T> for deserialization.
  var collectionInfo = new JsonCollectionInfoValues<global::System.Collections.Generic.IReadOnlyList<__ELEMENT_TYPE__>> {
    ObjectCreator = static () => new global::System.Collections.Generic.List<__ELEMENT_TYPE__>(),
    ElementInfo = elementInfo
  };
  var jsonTypeInfo = JsonMetadataServices.CreateIEnumerableInfo<global::System.Collections.Generic.IReadOnlyList<__ELEMENT_TYPE__>, __ELEMENT_TYPE__>(options, collectionInfo);
  jsonTypeInfo.OriginatingResolver = this;
  return jsonTypeInfo;
}
#endregion

#region LAZY_FIELD_ENUM
private JsonTypeInfo<__FULLY_QUALIFIED_NAME__>? _Enum___UNIQUE_IDENTIFIER__;
#endregion

#region GET_TYPE_INFO_ENUM
if (type == typeof(__FULLY_QUALIFIED_NAME__)) {
  return CreateEnum___UNIQUE_IDENTIFIER__(options);
}
#endregion

#region ENUM_TYPE_FACTORY
private JsonTypeInfo<__FULLY_QUALIFIED_NAME__> CreateEnum___UNIQUE_IDENTIFIER__(JsonSerializerOptions options) {
  var converter = JsonMetadataServices.GetEnumConverter<__FULLY_QUALIFIED_NAME__>(options);
  var jsonTypeInfo = JsonMetadataServices.CreateValueInfo<__FULLY_QUALIFIED_NAME__>(options, converter);
  jsonTypeInfo.OriginatingResolver = this;
  return jsonTypeInfo;
}
#endregion

#region LAZY_FIELD_NULLABLE_ENUM
private JsonTypeInfo<__FULLY_QUALIFIED_NAME__?>? _NullableEnum___UNIQUE_IDENTIFIER__;
#endregion

#region GET_TYPE_INFO_NULLABLE_ENUM
if (type == typeof(__FULLY_QUALIFIED_NAME__?)) {
  return CreateNullableEnum___UNIQUE_IDENTIFIER__(options);
}
#endregion

#region NULLABLE_ENUM_TYPE_FACTORY
private JsonTypeInfo<__FULLY_QUALIFIED_NAME__?> CreateNullableEnum___UNIQUE_IDENTIFIER__(JsonSerializerOptions options) {
  var nullableConverter = JsonMetadataServices.GetNullableConverter<__FULLY_QUALIFIED_NAME__>(options);
  var jsonTypeInfo = JsonMetadataServices.CreateValueInfo<__FULLY_QUALIFIED_NAME__?>(options, nullableConverter);
  jsonTypeInfo.OriginatingResolver = this;
  return jsonTypeInfo;
}
#endregion

#region LAZY_FIELD_ARRAY
private JsonTypeInfo<__ELEMENT_TYPE__[]>? _Array___ELEMENT_UNIQUE_IDENTIFIER__;
#endregion

#region GET_TYPE_INFO_ARRAY
if (type == typeof(__ELEMENT_TYPE__[])) {
  return CreateArray___ELEMENT_UNIQUE_IDENTIFIER__(options);
}
#endregion

#region ARRAY_TYPE_FACTORY
private JsonTypeInfo<__ELEMENT_TYPE__[]> CreateArray___ELEMENT_UNIQUE_IDENTIFIER__(JsonSerializerOptions options) {
  // Get element type info - use TryGetOrCreateTypeInfo to handle circular references gracefully
  var elementInfo = TryGetOrCreateTypeInfo<__ELEMENT_TYPE__>(options)
    ?? throw new InvalidOperationException(
        "No JsonTypeInfo found for element type __ELEMENT_TYPE__. " +
        "This may indicate a circular type reference. Ensure the element type is properly registered.");
  var arrayInfo = new JsonCollectionInfoValues<__ELEMENT_TYPE__[]> {
    ObjectCreator = null,  // Arrays use default array creation
    ElementInfo = elementInfo
  };
  var jsonTypeInfo = JsonMetadataServices.CreateArrayInfo<__ELEMENT_TYPE__>(options, arrayInfo);
  jsonTypeInfo.OriginatingResolver = this;
  return jsonTypeInfo;
}
#endregion

#region LAZY_FIELD_DICTIONARY
private JsonTypeInfo<global::System.Collections.Generic.Dictionary<__KEY_TYPE__, __VALUE_TYPE__>>? _Dictionary___UNIQUE_IDENTIFIER__;
#endregion

#region GET_TYPE_INFO_DICTIONARY
if (type == typeof(global::System.Collections.Generic.Dictionary<__KEY_TYPE__, __VALUE_TYPE__>)) {
  return CreateDictionary___UNIQUE_IDENTIFIER__(options);
}
#endregion

#region DICTIONARY_TYPE_FACTORY
private JsonTypeInfo<global::System.Collections.Generic.Dictionary<__KEY_TYPE__, __VALUE_TYPE__>> CreateDictionary___UNIQUE_IDENTIFIER__(JsonSerializerOptions options) {
  // Get key and value type info - use TryGetOrCreateTypeInfo to handle circular references gracefully
  var keyInfo = TryGetOrCreateTypeInfo<__KEY_TYPE__>(options)
    ?? throw new InvalidOperationException(
        "No JsonTypeInfo found for key type __KEY_TYPE__. " +
        "This may indicate a circular type reference. Ensure the key type is properly registered.");
  var valueInfo = TryGetOrCreateTypeInfo<__VALUE_TYPE__>(options)
    ?? throw new InvalidOperationException(
        "No JsonTypeInfo found for value type __VALUE_TYPE__. " +
        "This may indicate a circular type reference. Ensure the value type is properly registered.");
  var dictionaryInfo = new JsonCollectionInfoValues<global::System.Collections.Generic.Dictionary<__KEY_TYPE__, __VALUE_TYPE__>> {
    ObjectCreator = static () => new global::System.Collections.Generic.Dictionary<__KEY_TYPE__, __VALUE_TYPE__>(),
    KeyInfo = keyInfo,
    ElementInfo = valueInfo
  };
  var jsonTypeInfo = JsonMetadataServices.CreateDictionaryInfo<global::System.Collections.Generic.Dictionary<__KEY_TYPE__, __VALUE_TYPE__>, __KEY_TYPE__, __VALUE_TYPE__>(options, dictionaryInfo);
  jsonTypeInfo.OriginatingResolver = this;
  return jsonTypeInfo;
}
#endregion

#region LAZY_FIELD_POLYMORPHIC
private JsonTypeInfo<__BASE_TYPE__>? _Polymorphic___UNIQUE_IDENTIFIER__;
#endregion

#region GET_TYPE_INFO_POLYMORPHIC
if (type == typeof(__BASE_TYPE__)) {
  return CreatePolymorphic___UNIQUE_IDENTIFIER__(options);
}
#endregion

#region POLYMORPHIC_TYPE_FACTORY
private JsonTypeInfo<__BASE_TYPE__> CreatePolymorphic___UNIQUE_IDENTIFIER__(JsonSerializerOptions options) {
  var polyOptions = new JsonPolymorphismOptions {
    TypeDiscriminatorPropertyName = "$type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor
  };

__DERIVED_TYPE_REGISTRATIONS__

  var objectInfo = new JsonObjectInfoValues<__BASE_TYPE__> {
    ObjectCreator = null,  // Base type may be abstract or interface
    ObjectWithParameterizedConstructorCreator = null,
    PropertyMetadataInitializer = _ => Array.Empty<JsonPropertyInfo>(),
    ConstructorParameterMetadataInitializer = null,
    SerializeHandler = null
  };

  var jsonTypeInfo = JsonMetadataServices.CreateObjectInfo<__BASE_TYPE__>(options, objectInfo);
  jsonTypeInfo.PolymorphismOptions = polyOptions;
  jsonTypeInfo.OriginatingResolver = this;
  return jsonTypeInfo;
}
#endregion

#region POLYMORPHIC_DERIVED_REGISTRATION
  polyOptions.DerivedTypes.Add(new JsonDerivedType(typeof(__DERIVED_TYPE__), "__DERIVED_TYPE_DISCRIMINATOR__"));
#endregion

#region LAZY_FIELD_INTERFACE
private JsonTypeInfo<__INTERFACE_TYPE__>? ___INTERFACE_NAME__;
#endregion

#region INTERFACE_PROPERTY
/// <summary>
/// Gets JsonTypeInfo for __INTERFACE_TYPE__ with polymorphic support.
/// Delegates to JsonContextRegistry to aggregate derived types from all assemblies.
/// </summary>
public JsonTypeInfo<__INTERFACE_TYPE__> __INTERFACE_NAME__ => ___INTERFACE_NAME__ ??=
  global::Whizbang.Core.Serialization.JsonContextRegistry.GetPolymorphicTypeInfo<__INTERFACE_TYPE__>(Options)
  ?? throw new InvalidOperationException("No __INTERFACE_NAME__ implementations registered. Ensure at least one assembly with message types is loaded.");
#endregion

#region GET_TYPE_INFO_INTERFACE
if (type == typeof(__INTERFACE_TYPE__)) {
  return __INTERFACE_NAME__;
}
#endregion

#region LAZY_FIELD_MESSAGE_ENVELOPE_INTERFACE
private JsonTypeInfo<global::Whizbang.Core.Observability.MessageEnvelope<__INTERFACE_TYPE__>>? _MessageEnvelope___INTERFACE_NAME__;
#endregion

#region MESSAGE_ENVELOPE_INTERFACE_PROPERTY
/// <summary>
/// Gets JsonTypeInfo for MessageEnvelope&lt;__INTERFACE_TYPE__&gt; with polymorphic payload support.
/// Delegates to JsonContextRegistry to aggregate derived types from all assemblies.
/// </summary>
public JsonTypeInfo<global::Whizbang.Core.Observability.MessageEnvelope<__INTERFACE_TYPE__>> MessageEnvelope___INTERFACE_NAME__ => _MessageEnvelope___INTERFACE_NAME__ ??=
  global::Whizbang.Core.Serialization.JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<__INTERFACE_TYPE__>(Options)
  ?? throw new InvalidOperationException("No __INTERFACE_NAME__ implementations registered. Ensure at least one assembly with message types is loaded.");
#endregion

#region GET_TYPE_INFO_MESSAGE_ENVELOPE_INTERFACE
if (type == typeof(global::Whizbang.Core.Observability.MessageEnvelope<__INTERFACE_TYPE__>)) {
  return MessageEnvelope___INTERFACE_NAME__;
}
#endregion

#region LAZY_FIELD_LIST_INTERFACE
private JsonTypeInfo<global::System.Collections.Generic.List<__INTERFACE_TYPE__>>? _List___INTERFACE_NAME__;
#endregion

#region LIST_INTERFACE_PROPERTY
/// <summary>
/// Gets JsonTypeInfo for List&lt;__INTERFACE_TYPE__&gt; with polymorphic element support.
/// Delegates to JsonContextRegistry to aggregate derived types from all assemblies.
/// </summary>
public JsonTypeInfo<global::System.Collections.Generic.List<__INTERFACE_TYPE__>> List___INTERFACE_NAME__ => _List___INTERFACE_NAME__ ??=
  global::Whizbang.Core.Serialization.JsonContextRegistry.GetPolymorphicListTypeInfo<__INTERFACE_TYPE__>(Options)
  ?? throw new InvalidOperationException("No __INTERFACE_NAME__ implementations registered. Ensure at least one assembly with message types is loaded.");
#endregion

#region GET_TYPE_INFO_LIST_INTERFACE
if (type == typeof(global::System.Collections.Generic.List<__INTERFACE_TYPE__>)) {
  return List___INTERFACE_NAME__;
}
#endregion

#region HELPER_CREATE_PROPERTY
private JsonPropertyInfo CreateProperty<TProperty>(
    JsonSerializerOptions options,
    string propertyName,
    Func<object, TProperty> getter,
    Action<object, TProperty?>? setter,
    JsonTypeInfo<TProperty> propertyTypeInfo) {

  var propertyInfo = new JsonPropertyInfoValues<TProperty> {
    IsProperty = true,
    IsPublic = true,
    DeclaringType = typeof(object),  // Generic - not specific to MessageEnvelope
    PropertyTypeInfo = propertyTypeInfo,
    Getter = getter,
    Setter = setter,
    PropertyName = propertyName,
    JsonPropertyName = propertyName
  };

  return JsonMetadataServices.CreatePropertyInfo(options, propertyInfo);
}
#endregion

#region TYPES_BEING_CREATED_FIELD
// Thread-local tracking of types currently being created to prevent infinite recursion
// When type A has a property of type B, and type B has a property of type A,
// we detect this circular reference and fall back to the resolver chain.
[global::System.ThreadStaticAttribute]
private static global::System.Collections.Generic.HashSet<global::System.Type>? _typesBeingCreated;
private static global::System.Collections.Generic.HashSet<global::System.Type> TypesBeingCreated => _typesBeingCreated ??= new();

// Thread-local cache for type infos that are being created or have been created
// This enables circular references to work: when creating type A which needs type B,
// and type B needs type A, type A's (incomplete) info can be found in this cache.
[global::System.ThreadStaticAttribute]
private static global::System.Collections.Generic.Dictionary<global::System.Type, global::System.Text.Json.Serialization.Metadata.JsonTypeInfo>? _typeInfoCache;
private static global::System.Collections.Generic.Dictionary<global::System.Type, global::System.Text.Json.Serialization.Metadata.JsonTypeInfo> TypeInfoCache => _typeInfoCache ??= new();
#endregion

#region HELPER_TRY_GET_OR_CREATE_TYPE_INFO
/// <summary>
/// Gets JsonTypeInfo for a type, handling circular references gracefully.
/// Returns a cached type info if available during circular reference.
/// Used by collection factories where circular references are common (e.g., List&lt;T&gt; where T contains List&lt;T&gt;).
/// With deferred property initialization, the type info is cached before properties are created,
/// so self-referencing types can find themselves in the cache.
/// </summary>
private JsonTypeInfo<T>? TryGetOrCreateTypeInfo<T>(JsonSerializerOptions options) {
  var type = typeof(T);

  // Check cache first - with deferred initialization, the type info is cached
  // before properties are created, so self-referencing types will find themselves here
  if (TypeInfoCache.TryGetValue(type, out var cached)) {
    return cached as JsonTypeInfo<T>;
  }

  // If we're already creating this type but it's not in cache, we have a genuine
  // circular reference that can't be resolved. Return null to let the caller handle it.
  if (TypesBeingCreated.Contains(type)) {
    // This shouldn't happen with deferred initialization, but handle gracefully
    return null;
  }

  // Not a circular reference - delegate to the full GetOrCreateTypeInfo
  return GetOrCreateTypeInfo<T>(options);
}
#endregion

#region HELPER_GET_OR_CREATE_TYPE_INFO
/// <summary>
/// Gets JsonTypeInfo for a type, handling primitives in AOT-compatible way.
/// For complex types, queries the full resolver chain.
/// Includes circular reference detection and caching for self-referencing types.
/// </summary>
private JsonTypeInfo<T> GetOrCreateTypeInfo<T>(JsonSerializerOptions options) {
  var type = typeof(T);

  // Check cache first - handles cases where we've already created this type
  if (TypeInfoCache.TryGetValue(type, out var cached)) {
    return (JsonTypeInfo<T>)cached;
  }

  // Check for circular reference - if we're already creating this type,
  // we have a circular type dependency (e.g., type A has property of type B,
  // and type B has property of type A). This requires special handling.
  // Throw a clear error - use TryGetOrCreateTypeInfo for graceful handling.
  if (TypesBeingCreated.Contains(type)) {
    throw new InvalidOperationException(
        $"Circular type reference detected while creating JsonTypeInfo for {type.FullName}. " +
        "Your type graph has a cycle (e.g., type A references type B, and type B references type A). " +
        "To resolve this, use [JsonIgnore] on one of the properties to break the cycle, " +
        "or use a custom JsonConverter for one of the types.");
  }

  // Mark this type as being created to detect circular references
  TypesBeingCreated.Add(type);
  try {
    // Try our own resolver first (MessageId, CorrelationId, discovered types, etc.)
    var typeInfo = GetTypeInfoInternal(type, options);
    if (typeInfo != null) {
      // Cache the result for circular reference support
      TypeInfoCache[type] = typeInfo;
      return (JsonTypeInfo<T>)typeInfo;
    }

    // Handle common primitive types using JsonMetadataServices (AOT-compatible)
    // Note: Nullable primitives (decimal?, int?, etc.) are handled by the resolver chain below
    if (type == typeof(string)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<string>(options, JsonMetadataServices.StringConverter);
    }

    if (type == typeof(int)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<int>(options, JsonMetadataServices.Int32Converter);
    }

    if (type == typeof(long)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<long>(options, JsonMetadataServices.Int64Converter);
    }

    if (type == typeof(bool)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<bool>(options, JsonMetadataServices.BooleanConverter);
    }

    if (type == typeof(DateTime)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<DateTime>(options, JsonMetadataServices.DateTimeConverter);
    }

    if (type == typeof(DateTimeOffset)) {
      // Use lenient converter to handle dates with or without timezone offsets
      // This is necessary because some serializers (like PostgreSQL JSONB) may store timestamps without explicit timezone offsets
      var converter = new global::Whizbang.Core.Serialization.LenientDateTimeOffsetConverter();
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<DateTimeOffset>(options, converter);
    }

    if (type == typeof(TimeSpan)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<TimeSpan>(options, JsonMetadataServices.TimeSpanConverter);
    }

    if (type == typeof(DateOnly)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<DateOnly>(options, JsonMetadataServices.DateOnlyConverter);
    }

    if (type == typeof(TimeOnly)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<TimeOnly>(options, JsonMetadataServices.TimeOnlyConverter);
    }

    if (type == typeof(Guid)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<Guid>(options, JsonMetadataServices.GuidConverter);
    }

    if (type == typeof(decimal)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<decimal>(options, JsonMetadataServices.DecimalConverter);
    }

    if (type == typeof(double)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<double>(options, JsonMetadataServices.DoubleConverter);
    }

    if (type == typeof(float)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<float>(options, JsonMetadataServices.SingleConverter);
    }

    if (type == typeof(byte)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<byte>(options, JsonMetadataServices.ByteConverter);
    }

    if (type == typeof(sbyte)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<sbyte>(options, JsonMetadataServices.SByteConverter);
    }

    if (type == typeof(short)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<short>(options, JsonMetadataServices.Int16Converter);
    }

    if (type == typeof(ushort)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<ushort>(options, JsonMetadataServices.UInt16Converter);
    }

    if (type == typeof(uint)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<uint>(options, JsonMetadataServices.UInt32Converter);
    }

    if (type == typeof(ulong)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<ulong>(options, JsonMetadataServices.UInt64Converter);
    }

    if (type == typeof(char)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<char>(options, JsonMetadataServices.CharConverter);
    }

    // Handle nullable primitive types (Guid?, int?, etc.)
    // These are common in message types and must be handled without relying on resolver chain
    if (type == typeof(Guid?)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<Guid?>(options, JsonMetadataServices.GetNullableConverter<Guid>(options));
    }

    if (type == typeof(int?)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<int?>(options, JsonMetadataServices.GetNullableConverter<int>(options));
    }

    if (type == typeof(long?)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<long?>(options, JsonMetadataServices.GetNullableConverter<long>(options));
    }

    if (type == typeof(bool?)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<bool?>(options, JsonMetadataServices.GetNullableConverter<bool>(options));
    }

    if (type == typeof(DateTime?)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<DateTime?>(options, JsonMetadataServices.GetNullableConverter<DateTime>(options));
    }

    if (type == typeof(DateTimeOffset?)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<DateTimeOffset?>(options, JsonMetadataServices.GetNullableConverter<DateTimeOffset>(options));
    }

    if (type == typeof(TimeSpan?)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<TimeSpan?>(options, JsonMetadataServices.GetNullableConverter<TimeSpan>(options));
    }

    if (type == typeof(DateOnly?)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<DateOnly?>(options, JsonMetadataServices.GetNullableConverter<DateOnly>(options));
    }

    if (type == typeof(TimeOnly?)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<TimeOnly?>(options, JsonMetadataServices.GetNullableConverter<TimeOnly>(options));
    }

    if (type == typeof(decimal?)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<decimal?>(options, JsonMetadataServices.GetNullableConverter<decimal>(options));
    }

    if (type == typeof(double?)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<double?>(options, JsonMetadataServices.GetNullableConverter<double>(options));
    }

    if (type == typeof(float?)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<float?>(options, JsonMetadataServices.GetNullableConverter<float>(options));
    }

    if (type == typeof(byte?)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<byte?>(options, JsonMetadataServices.GetNullableConverter<byte>(options));
    }

    if (type == typeof(sbyte?)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<sbyte?>(options, JsonMetadataServices.GetNullableConverter<sbyte>(options));
    }

    if (type == typeof(short?)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<short?>(options, JsonMetadataServices.GetNullableConverter<short>(options));
    }

    if (type == typeof(ushort?)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<ushort?>(options, JsonMetadataServices.GetNullableConverter<ushort>(options));
    }

    if (type == typeof(uint?)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<uint?>(options, JsonMetadataServices.GetNullableConverter<uint>(options));
    }

    if (type == typeof(ulong?)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<ulong?>(options, JsonMetadataServices.GetNullableConverter<ulong>(options));
    }

    if (type == typeof(char?)) {
      return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<char?>(options, JsonMetadataServices.GetNullableConverter<char>(options));
    }

    // For complex types (List, Dictionary, etc.), query the full resolver chain
    // This will check InfrastructureJsonContext and user-provided resolvers
    var chainTypeInfo = options.GetTypeInfo(type);
    if (chainTypeInfo != null) {
      return (JsonTypeInfo<T>)chainTypeInfo;
    }

    // If still null, type is not registered anywhere - throw helpful error
    throw new InvalidOperationException($"No JsonTypeInfo found for type {type.FullName}. " +
      "Ensure you pass a resolver for this type to CreateOptions(), or add [JsonSerializable] to a JsonSerializable attribute.");
  } finally {
    // Always clean up the tracking set, even if an exception was thrown
    TypesBeingCreated.Remove(type);
  }
}
#endregion

#region HELPER_CREATE_OPTIONS
/// <summary>
/// Module initializer that registers this assembly's JsonSerializerContext instances.
/// Runs automatically when the assembly is loaded - no explicit call needed.
/// Registers WhizbangIdJsonContext and MessageJsonContext with the global JsonContextRegistry.
/// </summary>
[global::System.Runtime.CompilerServices.ModuleInitializer]
public static void Initialize() {
  // Register local contexts with the global registry
  // These will be combined with Core's contexts (InfrastructureJsonContext, etc.)
  global::Whizbang.Core.Serialization.JsonContextRegistry.RegisterContext(WhizbangIdJsonContext.Default);
  global::Whizbang.Core.Serialization.JsonContextRegistry.RegisterContext(MessageJsonContext.Default);

  // Register WhizbangId converter instances from this assembly (no reflection - AOT compatible!)
  // This allows InfrastructureJsonContext to find them via TryGetTypeInfoForRuntimeCustomConverter
__CONVERTER_REGISTRATIONS__
}
#endregion

var __INDEX__ = 0;
var properties = new JsonPropertyInfo[0];
var options = new JsonSerializerOptions();
var __SETTER__ = (Action<object, __PROPERTY_TYPE__>?)null;
var ctorParams = new JsonParameterInfoValues[0];

#region PROPERTY_CREATION_CALL
properties[__INDEX__] = CreateProperty<__PROPERTY_TYPE__>(
    options,
    "__PROPERTY_NAME__",
    obj => ((__MESSAGE_TYPE__)obj).__PROPERTY_NAME__,
    __SETTER__,
    GetOrCreateTypeInfo<__PROPERTY_TYPE__>(options));
#endregion

#region PARAMETER_INFO_VALUES
ctorParams[__INDEX__] = new JsonParameterInfoValues {
  Name = "__PARAMETER_NAME__",
  ParameterType = typeof(__PROPERTY_TYPE__),
  Position = __INDEX__,
  HasDefaultValue = false,
  DefaultValue = null
};
  #endregion

  #region GET_TYPE_INFO_BY_NAME_FALLBACK
  // Fallback: Try to resolve using the options' resolver chain
  // This allows types from other registered contexts to be found
  // (e.g., test types registered via JsonContextRegistry)
  // Note: Uses Type.GetType() and Assembly.GetType() which trigger AOT warnings - suppressed as this is intentional
  // This fallback is primarily for testing and cross-assembly scenarios
  if (typeInfo == null) {
#pragma warning disable IL2026  // RequiresUnreferencedCode on Assembly.GetType
#pragma warning disable IL2057  // Unrecognized type passed to Type.GetType()
    var runtimeType = Type.GetType(assemblyQualifiedTypeName, throwOnError: false);

    // If Type.GetType() fails, try searching all loaded assemblies
    // This is needed for test assemblies that aren't referenced by the current assembly
    if (runtimeType == null) {
      // Extract just the type name (before first comma)
      var commaIndex = assemblyQualifiedTypeName.IndexOf(',');
      if (commaIndex > 0) {
        var typeName = assemblyQualifiedTypeName.Substring(0, commaIndex).Trim();
        // Search all loaded assemblies
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
          runtimeType = assembly.GetType(typeName, throwOnError: false);
          if (runtimeType != null) break;
        }
      }
    }
#pragma warning restore IL2057
#pragma warning restore IL2026

    if (runtimeType != null) {
      typeInfo = options.GetTypeInfo(runtimeType);
    }
  }
  #endregion
}
internal class __MESSAGE_TYPE__ {
  internal __PROPERTY_TYPE__ __PROPERTY_NAME__;
}

internal class __PROPERTY_TYPE__ {
}

internal class __BASE_TYPE__ {
}

internal class __DERIVED_TYPE__ {
}

#pragma warning restore IDE1006 // Naming Styles
