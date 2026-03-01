using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;

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
  /// Invokes lifecycle receptors for outbox and inbox messages at async and inline stages.
  /// Snapshots collections before Task.Run to prevent "Collection was modified" exceptions.
  /// </summary>
  /// <param name="asyncStage">The async lifecycle stage (e.g., PostDistributeAsync)</param>
  /// <param name="inlineStage">The inline lifecycle stage (e.g., PostDistributeInline)</param>
  /// <param name="outboxMessages">Outbox messages to process</param>
  /// <param name="inboxMessages">Inbox messages to process</param>
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
  ///   _queuedInboxMessages,
  ///   _lifecycleInvoker,
  ///   _lifecycleMessageDeserializer,
  ///   _logger,
  ///   ct
  /// );
  /// </code>
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

    // CRITICAL: Snapshot collections before Task.Run to avoid "Collection was modified" exceptions
    // The main thread may modify the original collections while the background task iterates
    var outboxSnapshot = outboxMessages.ToArray();
    var inboxSnapshot = inboxMessages.ToArray();

    // Invoke async stage (non-blocking, backgrounded)
    // Each message gets its own correlated span linked to the original HTTP request trace
    _ = Task.Run(async () => {
      // Early return if lifecycle infrastructure not configured
      if (lifecycleInvoker is null || lifecycleMessageDeserializer is null) {
        return;
      }

      try {
        // Process outbox messages with MessageSource.Outbox context
        foreach (var outboxMsg in outboxSnapshot) {
          // Extract TraceParent from the message's hops to correlate with original request
          var parentContext = _extractParentContext(outboxMsg.Envelope.Hops);

          using (WhizbangActivitySource.Tracing.StartActivity($"Lifecycle {asyncStage}", ActivityKind.Internal, parentContext: parentContext)) {
            var outboxContext = new LifecycleExecutionContext {
              CurrentStage = asyncStage,
              EventId = null,
              StreamId = null,
              LastProcessedEventId = null,
              MessageSource = Messaging.MessageSource.Outbox,
              AttemptNumber = null // Attempt info not available at this stage
            };

            var message = lifecycleMessageDeserializer.DeserializeFromJsonElement(outboxMsg.Envelope.Payload, outboxMsg.MessageType);
            var typedEnvelope = outboxMsg.Envelope.ReconstructWithPayload(message);
            await lifecycleInvoker.InvokeAsync(typedEnvelope, asyncStage, outboxContext, ct);
          }
        }

        // Process inbox messages with MessageSource.Inbox context
        foreach (var inboxMsg in inboxSnapshot) {
          // Extract TraceParent from the message's hops to correlate with original request
          var parentContext = _extractParentContext(inboxMsg.Envelope.Hops);

          using (WhizbangActivitySource.Tracing.StartActivity($"Lifecycle {asyncStage}", ActivityKind.Internal, parentContext: parentContext)) {
            var inboxContext = new LifecycleExecutionContext {
              CurrentStage = asyncStage,
              EventId = null,
              StreamId = null,
              LastProcessedEventId = null,
              MessageSource = Messaging.MessageSource.Inbox,
              AttemptNumber = null // Attempt info not available at this stage
            };

            var message = lifecycleMessageDeserializer.DeserializeFromJsonElement(inboxMsg.Envelope.Payload, inboxMsg.MessageType);
            var typedEnvelope = inboxMsg.Envelope.ReconstructWithPayload(message);
            await lifecycleInvoker.InvokeAsync(typedEnvelope, asyncStage, inboxContext, ct);
          }
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
    // Each message gets its own correlated span linked to the original HTTP request trace
    // Early return if lifecycle infrastructure not configured
    if (lifecycleInvoker is null || lifecycleMessageDeserializer is null) {
      return;
    }

    // Process outbox messages with MessageSource.Outbox context
    foreach (var outboxMsg in outboxSnapshot) {
      // Extract TraceParent from the message's hops to correlate with original request
      var parentContext = _extractParentContext(outboxMsg.Envelope.Hops);

      using (WhizbangActivitySource.Tracing.StartActivity($"Lifecycle {inlineStage}", ActivityKind.Internal, parentContext: parentContext)) {
        var outboxContext = new LifecycleExecutionContext {
          CurrentStage = inlineStage,
          EventId = null,
          StreamId = null,
          LastProcessedEventId = null,
          MessageSource = Messaging.MessageSource.Outbox,
          AttemptNumber = null // Attempt info not available at this stage
        };

        var message = lifecycleMessageDeserializer.DeserializeFromJsonElement(outboxMsg.Envelope.Payload, outboxMsg.MessageType);
        var typedEnvelope = outboxMsg.Envelope.ReconstructWithPayload(message);
        await lifecycleInvoker.InvokeAsync(typedEnvelope, inlineStage, outboxContext, ct);
      }
    }

    // Process inbox messages with MessageSource.Inbox context
    foreach (var inboxMsg in inboxSnapshot) {
      // Extract TraceParent from the message's hops to correlate with original request
      var parentContext = _extractParentContext(inboxMsg.Envelope.Hops);

      using (WhizbangActivitySource.Tracing.StartActivity($"Lifecycle {inlineStage}", ActivityKind.Internal, parentContext: parentContext)) {
        var inboxContext = new LifecycleExecutionContext {
          CurrentStage = inlineStage,
          EventId = null,
          StreamId = null,
          LastProcessedEventId = null,
          MessageSource = Messaging.MessageSource.Inbox,
          AttemptNumber = null // Attempt info not available at this stage
        };

        var message = lifecycleMessageDeserializer.DeserializeFromJsonElement(inboxMsg.Envelope.Payload, inboxMsg.MessageType);
        var typedEnvelope = inboxMsg.Envelope.ReconstructWithPayload(message);
        await lifecycleInvoker.InvokeAsync(typedEnvelope, inlineStage, inboxContext, ct);
      }
    }
  }

  /// <summary>
  /// Invokes lifecycle receptors for outbox and inbox messages at an async-only stage (no inline counterpart).
  /// Snapshots collections before Task.Run to prevent "Collection was modified" exceptions.
  /// </summary>
  /// <param name="asyncStage">The async lifecycle stage (e.g., DistributeAsync)</param>
  /// <param name="outboxMessages">Outbox messages to process</param>
  /// <param name="inboxMessages">Inbox messages to process</param>
  /// <param name="lifecycleInvoker">Lifecycle invoker (null-safe, returns early if null)</param>
  /// <param name="lifecycleMessageDeserializer">Message deserializer (null-safe, returns early if null)</param>
  /// <param name="logger">Optional logger for error reporting</param>
  /// <param name="ct">Cancellation token</param>
  /// <remarks>
  /// <para>
  /// <strong>Usage Pattern (async-only stages like DistributeAsync):</strong>
  /// </para>
  /// <code>
  /// await LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStageAsync(
  ///   LifecycleStage.DistributeAsync,
  ///   _queuedOutboxMessages,
  ///   _queuedInboxMessages,
  ///   _lifecycleInvoker,
  ///   _lifecycleMessageDeserializer,
  ///   _logger,
  ///   ct
  /// );
  /// </code>
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

    // CRITICAL: Snapshot collections before Task.Run to avoid "Collection was modified" exceptions
    var outboxSnapshot = outboxMessages.ToArray();
    var inboxSnapshot = inboxMessages.ToArray();

    // Invoke async stage (non-blocking, backgrounded) - no inline stage for DistributeAsync
    // Each message gets its own correlated span linked to the original HTTP request trace
    _ = Task.Run(async () => {
      // Early return if lifecycle infrastructure not configured
      if (lifecycleInvoker is null || lifecycleMessageDeserializer is null) {
        return;
      }

      try {
        // Process outbox messages with MessageSource.Outbox context
        foreach (var outboxMsg in outboxSnapshot) {
          // Extract TraceParent from the message's hops to correlate with original request
          var parentContext = _extractParentContext(outboxMsg.Envelope.Hops);

          using (WhizbangActivitySource.Tracing.StartActivity($"Lifecycle {asyncStage}", ActivityKind.Internal, parentContext: parentContext)) {
            var outboxContext = new LifecycleExecutionContext {
              CurrentStage = asyncStage,
              EventId = null,
              StreamId = null,
              LastProcessedEventId = null,
              MessageSource = Messaging.MessageSource.Outbox,
              AttemptNumber = null // Attempt info not available at this stage
            };

            var message = lifecycleMessageDeserializer.DeserializeFromJsonElement(outboxMsg.Envelope.Payload, outboxMsg.MessageType);
            var typedEnvelope = outboxMsg.Envelope.ReconstructWithPayload(message);
            await lifecycleInvoker.InvokeAsync(typedEnvelope, asyncStage, outboxContext, ct);
          }
        }

        // Process inbox messages with MessageSource.Inbox context
        foreach (var inboxMsg in inboxSnapshot) {
          // Extract TraceParent from the message's hops to correlate with original request
          var parentContext = _extractParentContext(inboxMsg.Envelope.Hops);

          using (WhizbangActivitySource.Tracing.StartActivity($"Lifecycle {asyncStage}", ActivityKind.Internal, parentContext: parentContext)) {
            var inboxContext = new LifecycleExecutionContext {
              CurrentStage = asyncStage,
              EventId = null,
              StreamId = null,
              LastProcessedEventId = null,
              MessageSource = Messaging.MessageSource.Inbox,
              AttemptNumber = null // Attempt info not available at this stage
            };

            var message = lifecycleMessageDeserializer.DeserializeFromJsonElement(inboxMsg.Envelope.Payload, inboxMsg.MessageType);
            var typedEnvelope = inboxMsg.Envelope.ReconstructWithPayload(message);
            await lifecycleInvoker.InvokeAsync(typedEnvelope, asyncStage, inboxContext, ct);
          }
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

  /// <summary>
  /// Extracts parent ActivityContext from message hops for trace correlation.
  /// Uses the last hop's TraceParent to link lifecycle spans to the original HTTP request.
  /// </summary>
  private static ActivityContext _extractParentContext(IReadOnlyList<MessageHop> hops) {
    var traceParent = hops
      .Select(h => h.TraceParent)
      .LastOrDefault(tp => tp is not null);

    if (traceParent is not null && ActivityContext.TryParse(traceParent, null, out var parentContext)) {
      return parentContext;
    }

    return default;
  }
}
