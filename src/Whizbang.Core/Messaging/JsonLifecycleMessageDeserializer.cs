using System;
using System.Text.Json;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Messaging;

/// <summary>
/// JSON-based implementation of ILifecycleMessageDeserializer.
/// Uses JsonContextRegistry for AOT-safe deserialization with zero reflection.
/// </summary>
/// <docs>core-concepts/lifecycle-stages</docs>
public sealed class JsonLifecycleMessageDeserializer : ILifecycleMessageDeserializer {
  private readonly JsonSerializerOptions _jsonOptions;

  /// <summary>
  /// Creates a new JSON lifecycle message deserializer.
  /// </summary>
  /// <param name="jsonOptions">JSON serializer options for deserialization. If null, uses default options.</param>
  public JsonLifecycleMessageDeserializer(JsonSerializerOptions? jsonOptions = null) {
    _jsonOptions = jsonOptions ?? new JsonSerializerOptions();
  }

  /// <summary>
  /// Deserializes a message from an OutboxMessage or InboxMessage envelope.
  /// Extracts the message type from the EnvelopeType string and deserializes the JsonElement payload.
  /// Uses JsonContextRegistry for AOT-safe type resolution (zero reflection).
  /// </summary>
  /// <param name="envelope">The envelope containing the JsonElement payload.</param>
  /// <param name="envelopeTypeName">The assembly-qualified envelope type name (e.g., "MessageEnvelope`1[[MyApp.CreateProductCommand, MyApp]], Whizbang.Core").</param>
  /// <returns>The deserialized message object.</returns>
  public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) {
    ArgumentNullException.ThrowIfNull(envelope);

    if (string.IsNullOrWhiteSpace(envelopeTypeName)) {
      throw new ArgumentException("Envelope type name cannot be null or whitespace.", nameof(envelopeTypeName));
    }

    // Extract the message type name from the envelope type name
    // Pattern: "MessageEnvelope`1[[MyApp.CreateProductCommand, MyApp]], Whizbang.Core"
    // We need: "MyApp.CreateProductCommand, MyApp"
    var messageTypeName = _extractMessageTypeFromEnvelopeType(envelopeTypeName);

    // Deserialize the JsonElement payload to the message type
    return DeserializeFromJsonElement(envelope.Payload, messageTypeName);
  }

  /// <inheritdoc />
  public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) {
    throw new InvalidOperationException(
      "DeserializeFromEnvelope requires the envelope type name to be provided. " +
      "Use DeserializeFromEnvelope(envelope, envelopeTypeName) overload with the EnvelopeType from OutboxMessage/InboxMessage."
    );
  }

  /// <summary>
  /// Extracts the message type name from an envelope type name.
  /// Parses "MessageEnvelope`1[[MyApp.CreateProductCommand, MyApp]], Whizbang.Core"
  /// and returns "MyApp.CreateProductCommand, MyApp".
  /// </summary>
  private static string _extractMessageTypeFromEnvelopeType(string envelopeTypeName) {
    // Find the opening [[ and closing ]]
    var startIndex = envelopeTypeName.IndexOf("[[", StringComparison.Ordinal);
    var endIndex = envelopeTypeName.IndexOf("]]", StringComparison.Ordinal);

    if (startIndex == -1 || endIndex == -1 || startIndex >= endIndex) {
      throw new InvalidOperationException(
        $"Invalid envelope type name format: '{envelopeTypeName}'. " +
        $"Expected format: 'MessageEnvelope`1[[MessageType, Assembly]], EnvelopeAssembly'"
      );
    }

    // Extract the substring between [[ and ]]
    var messageTypeName = envelopeTypeName.Substring(startIndex + 2, endIndex - startIndex - 2);

    if (string.IsNullOrWhiteSpace(messageTypeName)) {
      throw new InvalidOperationException(
        $"Failed to extract message type name from envelope type: '{envelopeTypeName}'"
      );
    }

    return messageTypeName;
  }

  /// <inheritdoc />
  public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) {
    ArgumentNullException.ThrowIfNull(jsonBytes);

    if (string.IsNullOrWhiteSpace(messageTypeName)) {
      throw new ArgumentException("Message type name cannot be null or whitespace.", nameof(messageTypeName));
    }

    // Parse bytes to JsonElement first (AOT-safe, no reflection)
    using var doc = JsonDocument.Parse(jsonBytes);
    var jsonElement = doc.RootElement.Clone(); // Clone to avoid disposal issues

    // Then deserialize to the target type
    return DeserializeFromJsonElement(jsonElement, messageTypeName);
  }

  /// <inheritdoc />
  public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) {
    if (string.IsNullOrWhiteSpace(messageTypeName)) {
      throw new ArgumentException("Message type name cannot be null or whitespace.", nameof(messageTypeName));
    }

    // Use JsonContextRegistry for AOT-safe type resolution (zero reflection)
    var jsonTypeInfo = JsonContextRegistry.GetTypeInfoByName(messageTypeName, _jsonOptions);

    if (jsonTypeInfo is null) {
      throw new InvalidOperationException(
        $"Failed to resolve message type '{messageTypeName}'. " +
        $"Ensure the assembly containing this type is loaded and registered via [ModuleInitializer]."
      );
    }

    // Deserialize the JsonElement to the target type
    try {
      var message = JsonSerializer.Deserialize(jsonElement, jsonTypeInfo);

      if (message is null) {
        throw new InvalidOperationException(
          $"Deserialization of type '{messageTypeName}' returned null. " +
          $"This may indicate invalid JSON or a serialization configuration issue."
        );
      }

      return message;
    } catch (JsonException ex) {
      throw new InvalidOperationException(
        $"Failed to deserialize message of type '{messageTypeName}': {ex.Message}",
        ex
      );
    }
  }
}
