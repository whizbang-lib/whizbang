using System.Text.Json;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Observability;

/// <summary>
/// Extension methods for working with message envelopes.
/// </summary>
/// <docs>fundamentals/persistence/observability</docs>
public static class MessageEnvelopeExtensions {
  /// <summary>
  /// Reconstructs a message envelope with a deserialized payload while preserving all envelope metadata.
  /// Used when workers deserialize JSON payloads and need to pass the full envelope to invokers.
  /// </summary>
  /// <param name="jsonEnvelope">The original envelope containing a JsonElement payload.</param>
  /// <param name="deserializedPayload">The deserialized strongly-typed payload.</param>
  /// <returns>A new envelope with the deserialized payload and all original metadata preserved.</returns>
  /// <remarks>
  /// <para>
  /// This method is critical for maintaining security context through the message pipeline.
  /// Workers receive envelopes with <see cref="JsonElement"/> payloads from serialized messages.
  /// After deserializing the payload, they need to reconstruct the envelope to pass to
  /// <see cref="Whizbang.Core.Messaging.IReceptorInvoker"/> so security context can be established.
  /// </para>
  /// <para>
  /// The reconstructed envelope preserves:
  /// <list type="bullet">
  /// <item><description>MessageId - unique identifier for tracing</description></item>
  /// <item><description>Hops - routing history, security context, policy decisions</description></item>
  /// </list>
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// // Worker receives envelope with JsonElement payload
  /// var jsonPayload = work.Envelope.Payload; // JsonElement
  /// var deserializedMessage = deserializer.Deserialize(jsonPayload, messageType);
  ///
  /// // Reconstruct envelope with typed payload for invoker
  /// var typedEnvelope = work.Envelope.ReconstructWithPayload(deserializedMessage);
  /// await invoker.InvokeAsync(typedEnvelope, stage, context, ct);
  /// </code>
  /// </example>
  /// <docs>fundamentals/security/message-security#envelope-reconstruction</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/MessageEnvelopeExtensionsTests.cs</tests>
  public static IMessageEnvelope ReconstructWithPayload(
      this IMessageEnvelope<JsonElement> jsonEnvelope,
      object deserializedPayload) {
    ArgumentNullException.ThrowIfNull(jsonEnvelope);
    ArgumentNullException.ThrowIfNull(deserializedPayload);

    return new MessageEnvelope<object> {
      MessageId = jsonEnvelope.MessageId,
      Payload = deserializedPayload,
      Hops = jsonEnvelope.Hops,
      DispatchContext = jsonEnvelope.DispatchContext
    };
  }

  /// <summary>
  /// Generic overload for compile-time type preservation.
  /// Use when the payload type is known at compile time.
  /// </summary>
  /// <typeparam name="T">The message type.</typeparam>
  /// <param name="jsonEnvelope">The original envelope containing a JsonElement payload.</param>
  /// <param name="deserializedPayload">The deserialized strongly-typed payload.</param>
  /// <returns>A strongly-typed envelope with the deserialized payload and all original metadata preserved.</returns>
  /// <remarks>
  /// <para>
  /// This generic overload provides compile-time type safety when the message type is known.
  /// Use this when deserializing to a specific type:
  /// </para>
  /// <code>
  /// var order = JsonSerializer.Deserialize&lt;CreateOrder&gt;(jsonPayload);
  /// var envelope = jsonEnvelope.ReconstructWithPayload(order);
  /// // envelope is MessageEnvelope&lt;CreateOrder&gt;
  /// </code>
  /// <para>
  /// For runtime-typed scenarios (e.g., workers that deserialize based on type names),
  /// use the non-generic overload which returns <c>MessageEnvelope&lt;object&gt;</c>.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/security/message-security#envelope-reconstruction</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/MessageEnvelopeExtensionsTests.cs</tests>
  public static MessageEnvelope<T> ReconstructWithPayload<T>(
      this IMessageEnvelope<JsonElement> jsonEnvelope,
      T deserializedPayload) where T : notnull {
    ArgumentNullException.ThrowIfNull(jsonEnvelope);
    ArgumentNullException.ThrowIfNull(deserializedPayload);

    return new MessageEnvelope<T> {
      MessageId = jsonEnvelope.MessageId,
      Payload = deserializedPayload,
      Hops = jsonEnvelope.Hops,
      DispatchContext = jsonEnvelope.DispatchContext
    };
  }
}
