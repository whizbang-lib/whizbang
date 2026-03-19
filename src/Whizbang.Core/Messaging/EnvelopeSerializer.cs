using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Centralizes envelope serialization/deserialization between typed and JsonElement forms.
/// Ensures envelope type metadata is correctly captured before serialization.
/// </summary>
/// <docs>fundamentals/messages/envelope-serialization</docs>
public sealed class EnvelopeSerializer : IEnvelopeSerializer {
  private readonly JsonSerializerOptions _jsonOptions;

  public EnvelopeSerializer(JsonSerializerOptions? jsonOptions = null) {
    _jsonOptions = jsonOptions ?? new JsonSerializerOptions();
  }

  /// <inheritdoc />
  public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
    ArgumentNullException.ThrowIfNull(envelope);

    var payload = envelope.Payload;
    var payloadType = payload?.GetType() ?? typeof(TMessage);

    // DEFENSIVE: Detect if payload is JsonElement (should never happen!)
    // This indicates the envelope was already serialized and is being double-serialized
    if (payloadType == typeof(JsonElement)) {
      throw new InvalidOperationException(
        $"DOUBLE SERIALIZATION DETECTED: Payload is JsonElement, which means the envelope was already serialized. " +
        $"MessageId: {envelope.MessageId}. " +
        $"Envelope type: {envelope.GetType().FullName}. " +
        $"TMessage type parameter: {typeof(TMessage).FullName}. " +
        $"Payload runtime type: {payloadType.FullName}. " +
        $"This is a bug - envelopes should only be serialized once before storage. " +
        $"Check if Dispatcher is being passed a JsonElement instead of a strongly-typed message.");
    }

    // DEFENSIVE: Detect if TMessage is JsonElement (should never happen!)
    if (typeof(TMessage) == typeof(JsonElement)) {
      throw new InvalidOperationException(
        $"WRONG TYPE PARAMETER: TMessage is JsonElement. " +
        $"MessageId: {envelope.MessageId}. " +
        $"Envelope type: {envelope.GetType().FullName}. " +
        $"This indicates SerializeEnvelope was called with wrong type parameter. " +
        $"The envelope should be strongly-typed (e.g., MessageEnvelope<ProductCreatedEvent>), not MessageEnvelope<JsonElement>.");
    }

    // CRITICAL: Construct envelope type from PAYLOAD runtime type, not TMessage
    // When TMessage is an interface (e.g., IEvent from List<IEvent>), envelope.GetType() returns
    // MessageEnvelope<IEvent> instead of MessageEnvelope<ConcreteEvent>. The receiving service
    // won't have JsonTypeInfo for MessageEnvelope<IEvent>, only for concrete types.
    // FIX: Always use the payload's runtime type to construct the envelope type name.
    var envelopeTypeName = $"Whizbang.Core.Observability.MessageEnvelope`1[[{payloadType.AssemblyQualifiedName}]], Whizbang.Core";

    var messageTypeName = payloadType.AssemblyQualifiedName
      ?? throw new InvalidOperationException($"Message type {payloadType.Name} must have an assembly-qualified name");

    // Convert the envelope to MessageEnvelope<JsonElement> for AOT-compatible storage
    // Get type info to serialize the payload to JsonElement
    var payloadTypeInfo = _jsonOptions.GetTypeInfo(payloadType);
    if (payloadTypeInfo == null) {
      throw new InvalidOperationException(
        $"No JSON type info found for payload type '{payloadType.FullName}'. " +
        $"Ensure the type is registered in a JsonSerializerContext. MessageId: {envelope.MessageId}");
    }

    // Serialize the payload to JsonElement
    var payloadJson = JsonSerializer.SerializeToElement(payload, payloadTypeInfo);

    // Create the JsonElement envelope with proper property structure
    var jsonEnvelope = new MessageEnvelope<JsonElement> {
      MessageId = envelope.MessageId,
      Payload = payloadJson,
      Hops = envelope.Hops?.ToList() ?? []
    };

    return new SerializedEnvelope(
      JsonEnvelope: jsonEnvelope,
      EnvelopeType: envelopeTypeName,
      MessageType: messageTypeName
    );
  }

  /// <inheritdoc />
  public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) {
    ArgumentNullException.ThrowIfNull(jsonEnvelope);
    if (string.IsNullOrWhiteSpace(messageTypeName)) {
      throw new ArgumentException("Message type name cannot be null or whitespace.", nameof(messageTypeName));
    }

    var jsonElement = jsonEnvelope.Payload;

    // Use JsonContextRegistry for AOT-safe type resolution (zero reflection)
    var jsonTypeInfo = Serialization.JsonContextRegistry.GetTypeInfoByName(messageTypeName, _jsonOptions);
    if (jsonTypeInfo == null) {
      throw new InvalidOperationException(
        $"Failed to resolve message type '{messageTypeName}'. " +
        $"Ensure the assembly containing this type is loaded and registered via [ModuleInitializer]."
      );
    }

    var message = JsonSerializer.Deserialize(jsonElement, jsonTypeInfo);
    if (message == null) {
      throw new InvalidOperationException(
        $"Deserialization of type '{messageTypeName}' returned null. " +
        $"This may indicate invalid JSON or a serialization configuration issue."
      );
    }

    return message;
  }
}

/// <summary>
/// Interface for envelope serialization/deserialization service.
/// </summary>
/// <docs>fundamentals/messages/envelope-serialization</docs>
public interface IEnvelopeSerializer {
  /// <summary>
  /// Serializes a typed envelope to JsonElement form for storage.
  /// Captures envelope and message type names before serialization.
  /// </summary>
  /// <typeparam name="TMessage">The message payload type</typeparam>
  /// <param name="envelope">The typed envelope to serialize</param>
  /// <returns>Serialized envelope with type metadata</returns>
  SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope);

  /// <summary>
  /// Deserializes a message payload from a JsonElement envelope.
  /// </summary>
  /// <param name="jsonEnvelope">The JsonElement envelope</param>
  /// <param name="messageTypeName">The assembly-qualified message type name</param>
  /// <returns>The deserialized message object</returns>
  object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName);
}

/// <summary>
/// Result of envelope serialization containing JsonElement envelope and type metadata.
/// </summary>
/// <param name="JsonEnvelope">The serialized envelope with JsonElement payload</param>
/// <param name="EnvelopeType">Assembly-qualified name of the original typed envelope (e.g., "MessageEnvelope`1[[MyMessage, MyAssembly]], Whizbang.Core")</param>
/// <param name="MessageType">Assembly-qualified name of the message payload type</param>
/// <docs>fundamentals/messages/envelope-serialization</docs>
public sealed record SerializedEnvelope(
  MessageEnvelope<JsonElement> JsonEnvelope,
  string EnvelopeType,
  string MessageType
);
