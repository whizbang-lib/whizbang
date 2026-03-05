namespace Whizbang.Core.Tags;

/// <summary>
/// Processes message tags after successful receptor completion.
/// </summary>
/// <remarks>
/// <para>
/// The message tag processor is responsible for discovering and invoking
/// registered tag hooks for messages that have been successfully handled.
/// </para>
/// <para>
/// Hooks are executed in priority order (ascending: -100 → 500) and can
/// optionally modify the payload passed to subsequent hooks.
/// </para>
/// </remarks>
/// <docs>core-concepts/message-tags#processing</docs>
/// <tests>Whizbang.Core.Tests/Tags/MessageTagProcessorTests.cs</tests>
public interface IMessageTagProcessor {
  /// <summary>
  /// Processes all tags for a message after successful handling.
  /// </summary>
  /// <param name="message">The processed message.</param>
  /// <param name="messageType">The message type.</param>
  /// <param name="scope">Optional scope data from message context (tenant, user, etc.).</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>A task representing the asynchronous operation.</returns>
  /// <remarks>
  /// <para>
  /// This method is called by the Dispatcher after a receptor successfully
  /// handles a message. It discovers tags on the message type and invokes
  /// the appropriate hooks.
  /// </para>
  /// <para>
  /// If no hooks are registered or tag processing is disabled, this method
  /// returns immediately without performing any work.
  /// </para>
  /// </remarks>
  ValueTask ProcessTagsAsync(
      object message,
      Type messageType,
      IReadOnlyDictionary<string, object?>? scope = null,
      CancellationToken ct = default);
}
