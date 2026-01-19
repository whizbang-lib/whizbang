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
  private JsonTypeInfo<__FULLY_QUALIFIED_NAME__>? ___SIMPLE_NAME__;
  #endregion

  #region LAZY_FIELD_MESSAGE_ENVELOPE
  private JsonTypeInfo<MessageEnvelope<__FULLY_QUALIFIED_NAME__>>? _MessageEnvelope___SIMPLE_NAME__;
  #endregion

  #region GET_TYPE_INFO_VALUE_OBJECT
if (type == typeof(global::Whizbang.Core.ValueObjects.__TYPE_NAME__)) return Create___TYPE_NAME__(options);
  #endregion

  #region GET_TYPE_INFO_MESSAGE
if (type == typeof(__FULLY_QUALIFIED_NAME__)) {
  return Create___SIMPLE_NAME__(options);
}
  #endregion

  #region GET_TYPE_INFO_MESSAGE_ENVELOPE
if (type == typeof(MessageEnvelope<__FULLY_QUALIFIED_NAME__>)) {
  return CreateMessageEnvelope___SIMPLE_NAME__(options);
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
private JsonTypeInfo<global::System.Collections.Generic.List<__ELEMENT_TYPE__>>? _List___ELEMENT_SIMPLE_NAME__;
#endregion

#region GET_TYPE_INFO_LIST
if (type == typeof(global::System.Collections.Generic.List<__ELEMENT_TYPE__>)) {
  return CreateList___ELEMENT_SIMPLE_NAME__(options);
}
#endregion

#region LIST_TYPE_FACTORY
private JsonTypeInfo<global::System.Collections.Generic.List<__ELEMENT_TYPE__>> CreateList___ELEMENT_SIMPLE_NAME__(JsonSerializerOptions options) {
  var elementInfo = GetOrCreateTypeInfo<__ELEMENT_TYPE__>(options);
  var collectionInfo = new JsonCollectionInfoValues<global::System.Collections.Generic.List<__ELEMENT_TYPE__>> {
    ObjectCreator = static () => new global::System.Collections.Generic.List<__ELEMENT_TYPE__>(),
    ElementInfo = elementInfo
  };
  var jsonTypeInfo = JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<__ELEMENT_TYPE__>, __ELEMENT_TYPE__>(options, collectionInfo);
  jsonTypeInfo.OriginatingResolver = this;
  return jsonTypeInfo;
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

#region HELPER_GET_OR_CREATE_TYPE_INFO
/// <summary>
/// Gets JsonTypeInfo for a type, handling primitives in AOT-compatible way.
/// For complex types, queries the full resolver chain.
/// </summary>
private JsonTypeInfo<T> GetOrCreateTypeInfo<T>(JsonSerializerOptions options) {
  var type = typeof(T);

  // Try our own resolver first (MessageId, CorrelationId, discovered types, etc.)
  var typeInfo = GetTypeInfoInternal(type, options);
  if (typeInfo != null) {
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
    return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<DateTimeOffset>(options, JsonMetadataServices.DateTimeOffsetConverter);
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

  // For complex types (List, Dictionary, etc.), query the full resolver chain
  // This will check InfrastructureJsonContext and user-provided resolvers
  var chainTypeInfo = options.GetTypeInfo(type);
  if (chainTypeInfo != null) {
    return (JsonTypeInfo<T>)chainTypeInfo;
  }

  // If still null, type is not registered anywhere - throw helpful error
  throw new InvalidOperationException($"No JsonTypeInfo found for type {type.FullName}. " +
    "Ensure you pass a resolver for this type to CreateOptions(), or add [JsonSerializable] to a JsonSerializable attribute.");
}
#endregion

#region HELPER_CREATE_OPTIONS
/// <summary>
/// Module initializer that registers this assembly's JsonSerializerContext instances.
/// Runs automatically when the assembly is loaded - no explicit call needed.
/// Registers WhizbangIdJsonContext and MessageJsonContext with the global JsonContextRegistry.
/// </summary>
[System.Runtime.CompilerServices.ModuleInitializer]
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

#pragma warning restore IDE1006 // Naming Styles
