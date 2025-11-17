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

#region HELPER_CREATE_PROPERTY
private JsonPropertyInfo CreateProperty<TProperty>(
    JsonSerializerOptions options,
    string propertyName,
    Func<object, TProperty> getter,
    Action<object, TProperty>? setter,
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

  // For complex types (List<T>, Dictionary<K,V>, etc.), query the full resolver chain
  // This will check InfrastructureJsonContext and user-provided resolvers
  var chainTypeInfo = options.GetTypeInfo(type);
  if (chainTypeInfo != null) {
    return (JsonTypeInfo<T>)chainTypeInfo;
  }

  // If still null, type is not registered anywhere - throw helpful error
  throw new InvalidOperationException($"No JsonTypeInfo found for type {type.FullName}. " +
    "Ensure you pass a resolver for this type to CreateOptions(), or add [JsonSerializable] to a JsonSerializerContext.");
}
#endregion

#region HELPER_CREATE_OPTIONS
/// <summary>
/// Creates JsonSerializerOptions with all required contexts for Whizbang serialization.
/// Includes Whizbang types (MessageId, CorrelationId, MessageEnvelope), discovered message types,
/// and primitive types (string, int, etc.). For complex types (List, Dictionary, custom classes),
/// pass a JsonSerializerContext with [JsonSerializable] attributes as userResolvers.
/// </summary>
/// <param name="userResolvers">Optional user JsonSerializerContext instances for complex types</param>
/// <returns>AOT-compatible JsonSerializerOptions ready for use</returns>
public static JsonSerializerOptions CreateOptions(params IJsonTypeInfoResolver[] userResolvers) {
  // Create fully AOT-compatible resolver chain:
  // 1. WhizbangJsonContext (message types, MessageEnvelope<T>, MessageId, CorrelationId)
  // 2. User resolvers (custom application types)
  // 3. InfrastructureJsonContext (MessageHop, SecurityContext, etc.)
  var resolvers = new List<IJsonTypeInfoResolver> { Default };
  resolvers.AddRange(userResolvers);
  resolvers.Add(global::Whizbang.Core.Generated.InfrastructureJsonContext.Default);

  return new JsonSerializerOptions {
    TypeInfoResolver = JsonTypeInfoResolver.Combine(resolvers.ToArray()),
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
  };
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
}
internal class __MESSAGE_TYPE__ {
  internal __PROPERTY_TYPE__ __PROPERTY_NAME__;
}

internal class __PROPERTY_TYPE__ {
}

#pragma warning restore IDE1006 // Naming Styles
