using Whizbang.Core.Messaging;

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
  /// Processes all tags for a message at the specified lifecycle stage.
  /// </summary>
  /// <param name="message">The processed message.</param>
  /// <param name="messageType">The message type.</param>
  /// <param name="stage">The lifecycle stage at which tags are being processed.</param>
  /// <param name="scope">Optional scope data from message context (tenant, user, etc.).</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>A task representing the asynchronous operation.</returns>
  /// <remarks>
  /// <para>
  /// This method is called at various lifecycle stages (AfterReceptorCompletion,
  /// PostInbox, PostPerspective, PostOutbox, etc.) to invoke hooks configured
  /// for that specific stage.
  /// </para>
  /// <para>
  /// Only hooks registered for the specified lifecycle stage will be invoked.
  /// If no hooks are registered for the stage or tag processing is disabled,
  /// this method returns immediately without performing any work.
  /// </para>
  /// </remarks>
  ValueTask ProcessTagsAsync(
      object message,
      Type messageType,
      LifecycleStage stage,
      IReadOnlyDictionary<string, object?>? scope = null,
      CancellationToken ct = default);
}
