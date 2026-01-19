using System;
using System.Text.Json;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Deserializes messages from serialized formats (JsonElement, byte[], etc.) for lifecycle receptor invocation.
/// Provides consistent deserialization across Distribute, Outbox, and Inbox lifecycle stages.
/// Uses JsonContextRegistry for AOT-safe type resolution with zero reflection.
/// </summary>
/// <remarks>
/// <para>
/// At Distribute/Outbox/Inbox stages, messages are serialized for storage/transport.
/// Lifecycle receptors need the original message objects to invoke properly.
/// </para>
/// <para>
/// <strong>Usage at lifecycle stages:</strong>
/// </para>
/// <code>
/// // At Outbox stage - message is in OutboxMessage format
/// var outboxMessage = ...;  // OutboxMessage with IMessageEnvelope&lt;JsonElement&gt;
/// var deserializedMessage = await deserializer.DeserializeFromEnvelopeAsync(outboxMessage.Envelope);
///
/// // Invoke lifecycle receptors with deserialized message
/// await lifecycleInvoker.InvokeAsync(deserializedMessage, LifecycleStage.PreOutboxAsync, context, ct);
/// </code>
/// </remarks>
/// <docs>core-concepts/lifecycle-stages</docs>
public interface ILifecycleMessageDeserializer {
  /// <summary>
  /// Deserializes a message from a MessageEnvelope containing a JsonElement payload.
  /// Requires the envelope type name to extract the message type.
  /// Uses JsonContextRegistry for AOT-safe type resolution (zero reflection).
  /// </summary>
  /// <param name="envelope">The envelope containing the JsonElement payload and metadata.</param>
  /// <param name="envelopeTypeName">The assembly-qualified envelope type name (e.g., "MessageEnvelope`1[[MyApp.CreateProductCommand, MyApp]], Whizbang.Core").</param>
  /// <returns>The deserialized message object.</returns>
  /// <exception cref="InvalidOperationException">Thrown if the message type cannot be resolved or deserialization fails.</exception>
  object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName);

  /// <summary>
  /// Deserializes a message from a MessageEnvelope containing a JsonElement payload.
  /// This overload is not recommended - use the overload with envelopeTypeName parameter instead.
  /// </summary>
  /// <param name="envelope">The envelope containing the JsonElement payload and metadata.</param>
  /// <returns>The deserialized message object.</returns>
  /// <exception cref="InvalidOperationException">Always throws - use overload with envelopeTypeName.</exception>
  object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope);

  /// <summary>
  /// Deserializes a message from raw JSON bytes.
  /// Used when message is stored as byte array (e.g., in event store).
  /// Uses JsonContextRegistry for AOT-safe type resolution (zero reflection).
  /// </summary>
  /// <param name="jsonBytes">The JSON bytes to deserialize.</param>
  /// <param name="messageTypeName">Assembly-qualified type name of the message (e.g., "MyApp.CreateProductCommand, MyApp").</param>
  /// <returns>The deserialized message object.</returns>
  /// <exception cref="InvalidOperationException">Thrown if the message type cannot be resolved or deserialization fails.</exception>
  object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName);

  /// <summary>
  /// Deserializes a message from a JsonElement.
  /// Used when payload is already parsed as JsonElement.
  /// Uses JsonContextRegistry for AOT-safe type resolution (zero reflection).
  /// </summary>
  /// <param name="jsonElement">The JsonElement containing the message data.</param>
  /// <param name="messageTypeName">Assembly-qualified type name of the message (e.g., "MyApp.CreateProductCommand, MyApp").</param>
  /// <returns>The deserialized message object.</returns>
  /// <exception cref="InvalidOperationException">Thrown if the message type cannot be resolved or deserialization fails.</exception>
  object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName);
}
