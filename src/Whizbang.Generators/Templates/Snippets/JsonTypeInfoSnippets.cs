namespace Whizbang.Generators.Templates.Snippets;

/// <summary>
/// Reusable code snippets for JsonTypeInfo generation.
/// These snippets are extracted and used by the JsonTypeInfoGenerator.
/// </summary>
internal static class JsonTypeInfoSnippets {

  #region LAZY_FIELD
  private JsonTypeInfo<__TYPE__>? ___TYPE_FIELD__;
  #endregion

  #region LAZY_PROPERTY
  private JsonTypeInfo<__TYPE__> __TYPE_PROPERTY__ => ___TYPE_FIELD__ ??= Create___TYPE_METHOD__(Options);
  #endregion

  #region GET_TYPE_INFO_CASE
  if (type == typeof(__TYPE__)) return __TYPE_PROPERTY__;
  #endregion

  #region VALUE_OBJECT_FACTORY
  private JsonTypeInfo<__TYPE__> Create___TYPE_METHOD__(JsonSerializerOptions options) {
    var converter = new __CONVERTER__();
    var jsonTypeInfo = JsonMetadataServices.CreateValueInfo<__TYPE__>(options, converter);
    jsonTypeInfo.OriginatingResolver = this;
    return jsonTypeInfo;
  }
  #endregion

  #region MESSAGE_ENVELOPE_LAZY_FIELD
  private JsonTypeInfo<MessageEnvelope<__PAYLOAD_TYPE__>>? _MessageEnvelope___PAYLOAD_NAME__;
  #endregion

  #region MESSAGE_ENVELOPE_LAZY_PROPERTY
  private JsonTypeInfo<MessageEnvelope<__PAYLOAD_TYPE__>> MessageEnvelope___PAYLOAD_NAME__ => _MessageEnvelope___PAYLOAD_NAME__ ??= CreateMessageEnvelope<__PAYLOAD_TYPE__>(Options, __PAYLOAD_NAME__);
  #endregion

  #region MESSAGE_ENVELOPE_GET_TYPE_INFO_CASE
  if (type == typeof(MessageEnvelope<__PAYLOAD_TYPE__>)) return MessageEnvelope___PAYLOAD_NAME__;
  #endregion

  #region GENERIC_CREATE_MESSAGE_ENVELOPE
  private JsonTypeInfo<MessageEnvelope<T>> CreateMessageEnvelope<T>(JsonSerializerOptions options, JsonTypeInfo<T> payloadTypeInfo) where T : class {
    var properties = new JsonPropertyInfo[3];

    properties[0] = CreateProperty<MessageId>(
        options,
        "MessageId",
        obj => ((MessageEnvelope<T>)obj).MessageId,
        (JsonTypeInfo<MessageId>)options.GetTypeInfo(typeof(MessageId)));

    properties[1] = CreateProperty<T>(
        options,
        "Payload",
        obj => ((MessageEnvelope<T>)obj).Payload,
        payloadTypeInfo);

    properties[2] = CreateProperty<List<MessageHop>>(
        options,
        "Hops",
        obj => ((MessageEnvelope<T>)obj).Hops,
        (JsonTypeInfo<List<MessageHop>>)options.GetTypeInfo(typeof(List<MessageHop>)));

    var ctorParams = new JsonParameterInfoValues[3];
    ctorParams[0] = new JsonParameterInfoValues {
      Name = "messageId",
      ParameterType = typeof(MessageId),
      Position = 0,
      HasDefaultValue = false,
      DefaultValue = null
    };
    ctorParams[1] = new JsonParameterInfoValues {
      Name = "payload",
      ParameterType = typeof(T),
      Position = 1,
      HasDefaultValue = false,
      DefaultValue = null
    };
    ctorParams[2] = new JsonParameterInfoValues {
      Name = "hops",
      ParameterType = typeof(List<MessageHop>),
      Position = 2,
      HasDefaultValue = false,
      DefaultValue = null
    };

    var objectInfo = new JsonObjectInfoValues<MessageEnvelope<T>> {
      ObjectCreator = null,
      PropertyMetadataInitializer = _ => properties,
      ConstructorParameterMetadataInitializer = () => ctorParams
    };

    var jsonTypeInfo = JsonMetadataServices.CreateObjectInfo(options, objectInfo);
    jsonTypeInfo.OriginatingResolver = this;
    return jsonTypeInfo;
  }
  #endregion

  #region CREATE_PROPERTY_HELPER
  private JsonPropertyInfo CreateProperty<TProperty>(
      JsonSerializerOptions options,
      string propertyName,
      Func<object, TProperty> getter,
      JsonTypeInfo<TProperty> propertyTypeInfo) {

    var propertyInfo = new JsonPropertyInfoValues<TProperty> {
      IsProperty = true,
      IsPublic = true,
      DeclaringType = typeof(MessageEnvelope<>),
      PropertyTypeInfo = propertyTypeInfo,
      Getter = getter,
      Setter = null,
      PropertyName = propertyName,
      JsonPropertyName = propertyName
    };

    return JsonMetadataServices.CreatePropertyInfo(options, propertyInfo);
  }
  #endregion
}
