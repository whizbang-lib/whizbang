using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Static helper for invoking lifecycle receptors at async and inline stages.
/// Encapsulates thread-safe collection snapshotting to prevent "Collection was modified" exceptions.
/// </summary>
/// <remarks>
/// <para>
/// This helper is used by all WorkCoordinatorStrategy implementations (Immediate, Scoped, Interval)
/// to ensure consistent and safe lifecycle invocation patterns.
/// </para>
/// <para>
/// <strong>Thread Safety:</strong> Snapshots collections before Task.Run to avoid race conditions
/// when the main thread modifies collections during background iteration.
/// </para>
/// </remarks>
public static class LifecycleInvocationHelper {

  /// <summary>
  /// Invokes Distribute lifecycle receptors for outbox messages ONLY at async and inline stages.
  /// Distribute stages fire when PUBLISHING events (outbox), not when CONSUMING events (inbox).
  /// Snapshots collections before Task.Run to prevent "Collection was modified" exceptions.
  /// </summary>
  /// <param name="asyncStage">The async lifecycle stage (e.g., PostDistributeAsync)</param>
  /// <param name="inlineStage">The inline lifecycle stage (e.g., PostDistributeInline)</param>
  /// <param name="outboxMessages">Outbox messages to process</param>
  /// <param name="inboxMessages">Inbox messages to process (IGNORED for Distribute stages)</param>
  /// <param name="lifecycleInvoker">Lifecycle invoker (null-safe, returns early if null)</param>
  /// <param name="lifecycleMessageDeserializer">Message deserializer (null-safe, returns early if null)</param>
  /// <param name="logger">Optional logger for error reporting</param>
  /// <param name="ct">Cancellation token</param>
  /// <remarks>
  /// <para>
  /// <strong>Usage Pattern:</strong>
  /// </para>
  /// <code>
  /// await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
  ///   LifecycleStage.PostDistributeAsync,
  ///   LifecycleStage.PostDistributeInline,
  ///   _queuedOutboxMessages,
  ///   _queuedInboxMessages,  // Ignored by Distribute stages
  ///   _lifecycleInvoker,
  ///   _lifecycleMessageDeserializer,
  ///   _logger,
  ///   ct
  /// );
  /// </code>
  /// <para>
  /// <strong>IMPORTANT:</strong> Distribute stages (PreDistribute, Distribute, PostDistribute) only fire
  /// for OUTBOX messages (when publishing). They do NOT fire for INBOX messages (when consuming).
  /// This prevents duplicate lifecycle invocations when the same event is published locally and then
  /// received via transport on another service.
  /// </para>
  /// <para>
  /// <strong>Thread Safety Guarantee:</strong> Collections are snapshotted before backgrounding,
  /// so the main thread can safely modify the original collections while the background task iterates.
  /// </para>
  /// </remarks>
  public static async ValueTask InvokeDistributeLifecycleStagesAsync(
    LifecycleStage asyncStage,
    LifecycleStage inlineStage,
    IReadOnlyList<OutboxMessage> outboxMessages,
    IReadOnlyList<InboxMessage> inboxMessages,
    ILifecycleInvoker? lifecycleInvoker,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer,
    ILogger? logger,
    CancellationToken ct = default) {

    // Early return if lifecycle infrastructure not configured
    if (lifecycleInvoker is null || lifecycleMessageDeserializer is null) {
      return;
    }

    // Create separate context instances for async and inline stages
    // CRITICAL: Must be separate variables to avoid C# closure bug where Task.Run
    // captures the variable by reference and sees the reassigned value
    var asyncContext = new LifecycleExecutionContext {
      CurrentStage = asyncStage,
      EventId = null,
      StreamId = null,
      PerspectiveName = null,
      LastProcessedEventId = null
    };

    var inlineContext = asyncContext with { CurrentStage = inlineStage };

    // CRITICAL: Snapshot collections before Task.Run to avoid "Collection was modified" exceptions
    // The main thread may modify the original collections while the background task iterates
    var outboxSnapshot = outboxMessages.ToArray();

    // Invoke async stage (non-blocking, backgrounded)
    _ = Task.Run(async () => {
      try {
        // IMPORTANT: Only process OUTBOX messages for Distribute stages
        // Distribute stages fire when PUBLISHING (outbox), not when CONSUMING (inbox)
        foreach (var outboxMsg in outboxSnapshot) {
          var message = lifecycleMessageDeserializer.DeserializeFromJsonElement(outboxMsg.Envelope.Payload, outboxMsg.MessageType);
          await lifecycleInvoker.InvokeAsync(message, asyncStage, asyncContext, ct);
        }
      } catch (Exception ex) {
        if (logger != null) {
#pragma warning disable CA1848 // LoggerMessage not applicable for exception handlers in background tasks
          logger.LogError(ex, "Error invoking {Stage} lifecycle receptors", asyncStage);
#pragma warning restore CA1848
        }
      }
    }, ct);

    // Invoke inline stage (blocking, sequential)
    // IMPORTANT: Only process OUTBOX messages for Distribute stages
    // Distribute stages fire when PUBLISHING (outbox), not when CONSUMING (inbox)
    foreach (var outboxMsg in outboxSnapshot) {
      var message = lifecycleMessageDeserializer.DeserializeFromJsonElement(outboxMsg.Envelope.Payload, outboxMsg.MessageType);
      await lifecycleInvoker.InvokeAsync(message, inlineStage, inlineContext, ct);
    }
  }

  /// <summary>
  /// Invokes Distribute lifecycle receptors for outbox messages ONLY at an async-only stage (no inline counterpart).
  /// Distribute stages fire when PUBLISHING events (outbox), not when CONSUMING events (inbox).
  /// Snapshots collections before Task.Run to prevent "Collection was modified" exceptions.
  /// </summary>
  /// <param name="asyncStage">The async lifecycle stage (e.g., DistributeAsync)</param>
  /// <param name="outboxMessages">Outbox messages to process</param>
  /// <param name="inboxMessages">Inbox messages to process (IGNORED for Distribute stages)</param>
  /// <param name="lifecycleInvoker">Lifecycle invoker (null-safe, returns early if null)</param>
  /// <param name="lifecycleMessageDeserializer">Message deserializer (null-safe, returns early if null)</param>
  /// <param name="logger">Optional logger for error reporting</param>
  /// <param name="ct">Cancellation token</param>
  /// <remarks>
  /// <para>
  /// <strong>Usage Pattern (async-only stages like DistributeAsync):</strong>
  /// </para>
  /// <code>
  /// LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(
  ///   LifecycleStage.DistributeAsync,
  ///   _queuedOutboxMessages,
  ///   _queuedInboxMessages,  // Ignored by Distribute stages
  ///   _lifecycleInvoker,
  ///   _lifecycleMessageDeserializer,
  ///   _logger,
  ///   ct
  /// );
  /// </code>
  /// <para>
  /// <strong>IMPORTANT:</strong> Distribute stages (PreDistribute, Distribute, PostDistribute) only fire
  /// for OUTBOX messages (when publishing). They do NOT fire for INBOX messages (when consuming).
  /// This prevents duplicate lifecycle invocations when the same event is published locally and then
  /// received via transport on another service.
  /// </para>
  /// <para>
  /// <strong>Thread Safety Guarantee:</strong> Collections are snapshotted before backgrounding,
  /// so the main thread can safely modify the original collections while the background task iterates.
  /// </para>
  /// </remarks>
  public static void InvokeAsyncOnlyLifecycleStage(
    LifecycleStage asyncStage,
    IReadOnlyList<OutboxMessage> outboxMessages,
    IReadOnlyList<InboxMessage> inboxMessages,
    ILifecycleInvoker? lifecycleInvoker,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer,
    ILogger? logger,
    CancellationToken ct = default) {

    // Early return if lifecycle infrastructure not configured
    if (lifecycleInvoker is null || lifecycleMessageDeserializer is null) {
      return;
    }

    var lifecycleContext = new LifecycleExecutionContext {
      CurrentStage = asyncStage,
      EventId = null,
      StreamId = null,
      PerspectiveName = null,
      LastProcessedEventId = null
    };

    // CRITICAL: Snapshot collections before Task.Run to avoid "Collection was modified" exceptions
    var outboxSnapshot = outboxMessages.ToArray();

    // Invoke async stage (non-blocking, backgrounded) - no inline stage for DistributeAsync
    _ = Task.Run(async () => {
      try {
        // IMPORTANT: Only process OUTBOX messages for Distribute stages
        // Distribute stages fire when PUBLISHING (outbox), not when CONSUMING (inbox)
        foreach (var outboxMsg in outboxSnapshot) {
          var message = lifecycleMessageDeserializer.DeserializeFromJsonElement(outboxMsg.Envelope.Payload, outboxMsg.MessageType);
          await lifecycleInvoker.InvokeAsync(message, asyncStage, lifecycleContext, ct);
        }
      } catch (Exception ex) {
        if (logger != null) {
#pragma warning disable CA1848 // LoggerMessage not applicable for exception handlers in background tasks
          logger.LogError(ex, "Error invoking {Stage} lifecycle receptors", asyncStage);
#pragma warning restore CA1848
        }
      }
    }, ct);
  }
}
