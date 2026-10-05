// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;
using Whizbang.Core.Security;

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
/// <docs>fundamentals/messages/message-tags#processing</docs>
/// <tests>tests/Whizbang.Core.Tests/Tags/MessageTagProcessorTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/MessageTagProcessorTests.cs:ProcessAsync_InvokesMatchingHookAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/MessageTagProcessorTests.cs:ProcessTagsAsync_WithMatchingTag_InvokesHookAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/DispatcherTagProcessingTests.cs:LocalInvokeAsync_WithTagProcessingEnabled_InvokesTagProcessorAsync</tests>
public interface IMessageTagProcessor {
  /// <summary>
  /// Processes all tags for a message at the specified lifecycle stage.
  /// </summary>
  /// <param name="message">The processed message.</param>
  /// <param name="messageType">The message type.</param>
  /// <param name="stage">The lifecycle stage at which tags are being processed.</param>
  /// <param name="scope">Optional security scope context from message context (tenant, user, roles, permissions, etc.).</param>
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
      IScopeContext? scope = null,
      CancellationToken ct = default);

  /// <summary>
  /// Processes all tags for a message at the specified lifecycle stage, with the fields it changed (#1045): a
  /// collective event's applied specs report them here, and hooks read them from
  /// <see cref="TagContext{TAttribute}.Changes"/>.
  /// </summary>
  /// <param name="message">The processed message.</param>
  /// <param name="messageType">The message type.</param>
  /// <param name="stage">The lifecycle stage at which tags are being processed.</param>
  /// <param name="scope">Optional security scope context.</param>
  /// <param name="changes">The message's changes, or null for what the message itself describes.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <docs>fundamentals/messages/message-tags#changed-properties</docs>
  /// <tests>tests/Whizbang.Core.Tests/Tags/MessageTagProcessorTests.cs:ProcessTagsAsync_WithChanges_TheHookReadsThemAsync</tests>
  ValueTask ProcessTagsAsync(
      object message,
      Type messageType,
      LifecycleStage stage,
      IScopeContext? scope,
      MessageChanges? changes,
      CancellationToken ct = default) =>
    ProcessTagsAsync(message, messageType, stage, scope, ct);
}
