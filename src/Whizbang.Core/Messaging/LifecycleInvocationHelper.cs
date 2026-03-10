using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;

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
  /// <param name="enableLifecycleTracing">Whether to create lifecycle OpenTelemetry spans. When false, lifecycle logic still runs but no spans are emitted.</param>
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
  ///   enableLifecycleTracing: true,
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
    bool enableLifecycleTracing = true,
    CancellationToken ct = default) {

    // CRITICAL: Snapshot collections before Task.Run to avoid "Collection was modified" exceptions
    // The main thread may modify the original collections while the background task iterates
    var outboxSnapshot = outboxMessages.ToArray();
    var inboxSnapshot = inboxMessages.ToArray();

    // Invoke async stage (non-blocking, backgrounded)
    _ = _invokeAsyncStageInBackgroundAsync(outboxSnapshot, inboxSnapshot, asyncStage, lifecycleInvoker, lifecycleMessageDeserializer, logger, enableLifecycleTracing, ct);

    // Invoke inline stage (blocking, sequential)
    if (lifecycleInvoker is null || lifecycleMessageDeserializer is null) {
      return;
    }

    await _processOutboxMessagesAsync(outboxSnapshot, inlineStage, lifecycleInvoker, lifecycleMessageDeserializer, enableLifecycleTracing, ct);
    await _processInboxMessagesAsync(inboxSnapshot, inlineStage, lifecycleInvoker, lifecycleMessageDeserializer, enableLifecycleTracing, ct);
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
  /// <param name="enableLifecycleTracing">Whether to create lifecycle OpenTelemetry spans. When false, lifecycle logic still runs but no spans are emitted.</param>
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
  ///   enableLifecycleTracing: true,
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
    bool enableLifecycleTracing = true,
    CancellationToken ct = default) {

    // CRITICAL: Snapshot collections before Task.Run to avoid "Collection was modified" exceptions
    var outboxSnapshot = outboxMessages.ToArray();
    var inboxSnapshot = inboxMessages.ToArray();

    // Invoke async stage (non-blocking, backgrounded) - no inline stage for DistributeAsync
    _ = _invokeAsyncStageInBackgroundAsync(outboxSnapshot, inboxSnapshot, asyncStage, lifecycleInvoker, lifecycleMessageDeserializer, logger, enableLifecycleTracing, ct);
  }

  /// <summary>
  /// Invokes async stage lifecycle receptors in a background task.
  /// Handles exceptions and logs errors without affecting the main thread.
  /// </summary>
  private static Task _invokeAsyncStageInBackgroundAsync(
      OutboxMessage[] outboxSnapshot,
      InboxMessage[] inboxSnapshot,
      LifecycleStage asyncStage,
      ILifecycleInvoker? lifecycleInvoker,
      ILifecycleMessageDeserializer? lifecycleMessageDeserializer,
      ILogger? logger,
      bool enableLifecycleTracing,
      CancellationToken ct) {
    return Task.Run(async () => {
      if (lifecycleInvoker is null || lifecycleMessageDeserializer is null) {
        return;
      }

      try {
        await _processOutboxMessagesAsync(outboxSnapshot, asyncStage, lifecycleInvoker, lifecycleMessageDeserializer, enableLifecycleTracing, ct);
        await _processInboxMessagesAsync(inboxSnapshot, asyncStage, lifecycleInvoker, lifecycleMessageDeserializer, enableLifecycleTracing, ct);
      } catch (Exception ex) {
        _logLifecycleError(logger, asyncStage, ex);
      }
    }, ct);
  }

  /// <summary>
  /// Logs lifecycle invocation errors if a logger is available.
  /// </summary>
  private static void _logLifecycleError(ILogger? logger, LifecycleStage stage, Exception ex) {
    if (logger is null) {
      return;
    }

#pragma warning disable CA1848 // LoggerMessage not applicable for exception handlers in background tasks
    logger.LogError(ex, "Error invoking {Stage} lifecycle receptors", stage);
#pragma warning restore CA1848
  }

  /// <summary>
  /// Processes outbox messages for a given lifecycle stage.
  /// </summary>
  private static async Task _processOutboxMessagesAsync(
      OutboxMessage[] messages,
      LifecycleStage stage,
      ILifecycleInvoker lifecycleInvoker,
      ILifecycleMessageDeserializer lifecycleMessageDeserializer,
      bool enableLifecycleTracing,
      CancellationToken ct) {
    foreach (var outboxMsg in messages) {
      var extracted = EnvelopeContextExtractor.ExtractFromHops(outboxMsg.Envelope.Hops);
      var parentContext = extracted.TraceContext;

      // Establish ambient scope context from envelope data
      if (extracted.Scope is not null) {
        ScopeContextAccessor.CurrentContext = extracted.Scope;
      }

      using (enableLifecycleTracing ? WhizbangActivitySource.Tracing.StartActivity($"Lifecycle {stage}", ActivityKind.Internal, parentContext: parentContext) : null) {
        var context = _createLifecycleContext(stage, MessageSource.Outbox);
        var message = lifecycleMessageDeserializer.DeserializeFromJsonElement(outboxMsg.Envelope.Payload, outboxMsg.MessageType);
        var typedEnvelope = outboxMsg.Envelope.ReconstructWithPayload(message);
        await lifecycleInvoker.InvokeAsync(typedEnvelope, stage, context, ct);
      }
    }
  }

  /// <summary>
  /// Processes inbox messages for a given lifecycle stage.
  /// </summary>
  private static async Task _processInboxMessagesAsync(
      InboxMessage[] messages,
      LifecycleStage stage,
      ILifecycleInvoker lifecycleInvoker,
      ILifecycleMessageDeserializer lifecycleMessageDeserializer,
      bool enableLifecycleTracing,
      CancellationToken ct) {
    foreach (var inboxMsg in messages) {
      var extracted = EnvelopeContextExtractor.ExtractFromHops(inboxMsg.Envelope.Hops);
      var parentContext = extracted.TraceContext;

      // Establish ambient scope context from envelope data
      if (extracted.Scope is not null) {
        ScopeContextAccessor.CurrentContext = extracted.Scope;
      }

      using (enableLifecycleTracing ? WhizbangActivitySource.Tracing.StartActivity($"Lifecycle {stage}", ActivityKind.Internal, parentContext: parentContext) : null) {
        var context = _createLifecycleContext(stage, MessageSource.Inbox);
        var message = lifecycleMessageDeserializer.DeserializeFromJsonElement(inboxMsg.Envelope.Payload, inboxMsg.MessageType);
        var typedEnvelope = inboxMsg.Envelope.ReconstructWithPayload(message);
        await lifecycleInvoker.InvokeAsync(typedEnvelope, stage, context, ct);
      }
    }
  }

  /// <summary>
  /// Creates a LifecycleExecutionContext for the given stage and message source.
  /// </summary>
  private static LifecycleExecutionContext _createLifecycleContext(LifecycleStage stage, MessageSource source) =>
    new() {
      CurrentStage = stage,
      EventId = null,
      StreamId = null,
      LastProcessedEventId = null,
      MessageSource = source,
      AttemptNumber = null
    };
}
