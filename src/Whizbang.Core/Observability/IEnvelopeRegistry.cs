namespace Whizbang.Core.Observability;

/// <summary>
/// Registry for tracking message envelopes by their message payload.
/// Enables looking up the envelope for a message when only the message is available.
/// This allows APIs to accept either raw messages or envelopes, with the registry
/// providing the envelope when needed.
/// </summary>
/// <remarks>
/// <para>
/// The registry uses object reference identity (not equality) to look up messages.
/// This means the exact same message instance must be used for registration and lookup.
/// </para>
/// <para>
/// Typical flow:
/// 1. Dispatcher creates envelope, calls Register(envelope)
/// 2. Receptor processes message, may call eventStore.AppendAsync(streamId, message)
/// 3. EventStore calls TryGetEnvelope(message) to get the envelope
/// 4. Processing completes, Unregister is called (or scope disposes)
/// </para>
/// </remarks>
/// <docs>core-concepts/envelope-registry</docs>
public interface IEnvelopeRegistry {
  /// <summary>
  /// Registers an envelope in the registry.
  /// The envelope's Payload is used as the key for later lookup.
  /// </summary>
  /// <typeparam name="T">The message payload type</typeparam>
  /// <param name="envelope">The envelope to register</param>
  void Register<T>(MessageEnvelope<T> envelope);

  /// <summary>
  /// Attempts to get the envelope for a message.
  /// Returns null if the message is not registered (does not throw).
  /// </summary>
  /// <typeparam name="T">The message payload type</typeparam>
  /// <param name="message">The message to look up</param>
  /// <returns>The envelope if found, null otherwise</returns>
  MessageEnvelope<T>? TryGetEnvelope<T>(T message) where T : notnull;

  /// <summary>
  /// Unregisters a message from the registry.
  /// </summary>
  /// <typeparam name="T">The message payload type</typeparam>
  /// <param name="message">The message to unregister</param>
  void Unregister<T>(T message) where T : notnull;

  /// <summary>
  /// Unregisters an envelope from the registry.
  /// Uses the envelope's Payload as the key.
  /// </summary>
  /// <typeparam name="T">The message payload type</typeparam>
  /// <param name="envelope">The envelope to unregister</param>
  void Unregister<T>(MessageEnvelope<T> envelope);
}
