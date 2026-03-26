using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
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
  private const string METRIC_STAGE = "stage";
  private const string METRIC_MESSAGE_TYPE = "message_type";

  /// <summary>
  /// Invokes lifecycle receptors for outbox and inbox messages at async and inline stages.
  /// Snapshots collections before Task.Run to prevent "Collection was modified" exceptions.
  /// </summary>
  /// <param name="asyncStage">The async lifecycle stage (e.g., PostDistributeAsync)</param>
  /// <param name="inlineStage">The inline lifecycle stage (e.g., PostDistributeInline)</param>
  /// <param name="outboxMessages">Outbox messages to process</param>
  /// <param name="inboxMessages">Inbox messages to process</param>
  /// <param name="scopeFactory">Service scope factory for creating per-message scopes to resolve IReceptorInvoker</param>
  /// <param name="lifecycleMessageDeserializer">Message deserializer (null-safe, returns early if null)</param>
  /// <param name="logger">Optional logger for error reporting</param>
  /// <param name="enableLifecycleTracing">Whether to create lifecycle OpenTelemetry spans. When false, lifecycle logic still runs but no spans are emitted.</param>
  /// <param name="metrics">Optional lifecycle metrics for stage/receptor instrumentation</param>
  /// <param name="ct">Cancellation token</param>
  public static async ValueTask InvokeDistributeLifecycleStagesAsync(
    LifecycleStage asyncStage,
    LifecycleStage inlineStage,
    IReadOnlyList<OutboxMessage> outboxMessages,
    IReadOnlyList<InboxMessage> inboxMessages,
    IServiceScopeFactory? scopeFactory,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer,
    ILogger? logger,
    bool enableLifecycleTracing = true,
    LifecycleMetrics? metrics = null,
    CancellationToken ct = default) {

    // CRITICAL: Snapshot collections before Task.Run to avoid "Collection was modified" exceptions
    // The main thread may modify the original collections while the background task iterates
    var outboxSnapshot = outboxMessages.ToArray();
    var inboxSnapshot = inboxMessages.ToArray();

    // Invoke async stage (non-blocking, backgrounded)
    _ = _invokeAsyncStageInBackgroundAsync(outboxSnapshot, inboxSnapshot, asyncStage, scopeFactory, lifecycleMessageDeserializer, logger, enableLifecycleTracing, metrics, ct);

    // Invoke inline stage (blocking, sequential)
    if (scopeFactory is null || lifecycleMessageDeserializer is null) {
      return;
    }

    metrics?.StageInvocations.Add(1, new KeyValuePair<string, object?>(METRIC_STAGE, inlineStage.ToString()));
    var stageSw = Stopwatch.StartNew();

    await _processOutboxMessagesAsync(outboxSnapshot, inlineStage, scopeFactory, lifecycleMessageDeserializer, enableLifecycleTracing, metrics, ct);
    await _processInboxMessagesAsync(inboxSnapshot, inlineStage, scopeFactory, lifecycleMessageDeserializer, enableLifecycleTracing, metrics, ct);

    stageSw.Stop();
    metrics?.StageDuration.Record(stageSw.Elapsed.TotalMilliseconds,
      new KeyValuePair<string, object?>(METRIC_STAGE, inlineStage.ToString()),
      new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, "mixed"));
  }

  /// <summary>
  /// Invokes lifecycle receptors for outbox and inbox messages at an async-only stage (no inline counterpart).
  /// Snapshots collections before Task.Run to prevent "Collection was modified" exceptions.
  /// </summary>
  /// <param name="asyncStage">The async lifecycle stage (e.g., DistributeAsync)</param>
  /// <param name="outboxMessages">Outbox messages to process</param>
  /// <param name="inboxMessages">Inbox messages to process</param>
  /// <param name="scopeFactory">Service scope factory for creating per-message scopes to resolve IReceptorInvoker</param>
  /// <param name="lifecycleMessageDeserializer">Message deserializer (null-safe, returns early if null)</param>
  /// <param name="logger">Optional logger for error reporting</param>
  /// <param name="enableLifecycleTracing">Whether to create lifecycle OpenTelemetry spans. When false, lifecycle logic still runs but no spans are emitted.</param>
  /// <param name="metrics">Optional lifecycle metrics for stage/receptor instrumentation</param>
  /// <param name="ct">Cancellation token</param>
  public static void InvokeAsyncOnlyLifecycleStage(
    LifecycleStage asyncStage,
    IReadOnlyList<OutboxMessage> outboxMessages,
    IReadOnlyList<InboxMessage> inboxMessages,
    IServiceScopeFactory? scopeFactory,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer,
    ILogger? logger,
    bool enableLifecycleTracing = true,
    LifecycleMetrics? metrics = null,
    CancellationToken ct = default) {

    // CRITICAL: Snapshot collections before Task.Run to avoid "Collection was modified" exceptions
    var outboxSnapshot = outboxMessages.ToArray();
    var inboxSnapshot = inboxMessages.ToArray();

    // Invoke async stage (non-blocking, backgrounded) - no inline stage for DistributeAsync
    _ = _invokeAsyncStageInBackgroundAsync(outboxSnapshot, inboxSnapshot, asyncStage, scopeFactory, lifecycleMessageDeserializer, logger, enableLifecycleTracing, metrics, ct);
  }

  /// <summary>
  /// Invokes async stage lifecycle receptors in a background task.
  /// Handles exceptions and logs errors without affecting the main thread.
  /// </summary>
  private static Task _invokeAsyncStageInBackgroundAsync(
      OutboxMessage[] outboxSnapshot,
      InboxMessage[] inboxSnapshot,
      LifecycleStage asyncStage,
      IServiceScopeFactory? scopeFactory,
      ILifecycleMessageDeserializer? lifecycleMessageDeserializer,
      ILogger? logger,
      bool enableLifecycleTracing,
      LifecycleMetrics? metrics,
      CancellationToken ct) {
    return Task.Run(async () => {
      if (scopeFactory is null || lifecycleMessageDeserializer is null) {
        return;
      }

      metrics?.StageInvocations.Add(1, new KeyValuePair<string, object?>(METRIC_STAGE, asyncStage.ToString()));
      var stageSw = Stopwatch.StartNew();

      try {
        await _processOutboxMessagesAsync(outboxSnapshot, asyncStage, scopeFactory, lifecycleMessageDeserializer, enableLifecycleTracing, metrics, ct);
        await _processInboxMessagesAsync(inboxSnapshot, asyncStage, scopeFactory, lifecycleMessageDeserializer, enableLifecycleTracing, metrics, ct);
      } catch (Exception ex) {
        _logLifecycleError(logger, asyncStage, ex);
        metrics?.ReceptorErrors.Add(1,
          new KeyValuePair<string, object?>(METRIC_STAGE, asyncStage.ToString()),
          new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, "mixed"),
          new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
      } finally {
        stageSw.Stop();
        metrics?.StageDuration.Record(stageSw.Elapsed.TotalMilliseconds,
          new KeyValuePair<string, object?>(METRIC_STAGE, asyncStage.ToString()),
          new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, "mixed"));
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
  /// Processes outbox messages for a given lifecycle stage using scoped IReceptorInvoker.
  /// ReceptorInvoker handles all context establishment (security, message context, trace parenting).
  /// </summary>
  private static async Task _processOutboxMessagesAsync(
      OutboxMessage[] messages,
      LifecycleStage stage,
      IServiceScopeFactory scopeFactory,
      ILifecycleMessageDeserializer lifecycleMessageDeserializer,
      bool enableLifecycleTracing,
      LifecycleMetrics? metrics,
      CancellationToken ct) {
    foreach (var outboxMsg in messages) {
      var extracted = EnvelopeContextExtractor.ExtractFromHops(outboxMsg.Envelope.Hops);
      var parentContext = extracted.TraceContext;

      using (enableLifecycleTracing ? WhizbangActivitySource.Tracing.StartActivity($"Lifecycle {stage}", ActivityKind.Internal, parentContext: parentContext) : null) {
        var context = _createLifecycleContext(stage, MessageSource.Outbox);
        var message = lifecycleMessageDeserializer.DeserializeFromJsonElement(outboxMsg.Envelope.Payload, outboxMsg.MessageType);
        var typedEnvelope = outboxMsg.Envelope.ReconstructWithPayload(message);

        metrics?.ReceptorInvocations.Add(1,
          new KeyValuePair<string, object?>(METRIC_STAGE, stage.ToString()),
          new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, outboxMsg.MessageType));
        var receptorSw = Stopwatch.StartNew();
        try {
          await using var scope = scopeFactory.CreateAsyncScope();
          var receptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
          if (receptorInvoker is not null) {
            await receptorInvoker.InvokeAsync(typedEnvelope, stage, context, ct);
          }
        } catch (Exception ex) {
          metrics?.ReceptorErrors.Add(1,
            new KeyValuePair<string, object?>(METRIC_STAGE, stage.ToString()),
            new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, outboxMsg.MessageType),
            new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
          throw;
        } finally {
          receptorSw.Stop();
          metrics?.ReceptorDuration.Record(receptorSw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>(METRIC_STAGE, stage.ToString()),
            new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, outboxMsg.MessageType));
        }
      }
    }
  }

  /// <summary>
  /// Processes inbox messages for a given lifecycle stage using scoped IReceptorInvoker.
  /// ReceptorInvoker handles all context establishment (security, message context, trace parenting).
  /// </summary>
  private static async Task _processInboxMessagesAsync(
      InboxMessage[] messages,
      LifecycleStage stage,
      IServiceScopeFactory scopeFactory,
      ILifecycleMessageDeserializer lifecycleMessageDeserializer,
      bool enableLifecycleTracing,
      LifecycleMetrics? metrics,
      CancellationToken ct) {
    foreach (var inboxMsg in messages) {
      var extracted = EnvelopeContextExtractor.ExtractFromHops(inboxMsg.Envelope.Hops);
      var parentContext = extracted.TraceContext;

      using (enableLifecycleTracing ? WhizbangActivitySource.Tracing.StartActivity($"Lifecycle {stage}", ActivityKind.Internal, parentContext: parentContext) : null) {
        var context = _createLifecycleContext(stage, MessageSource.Inbox);
        var message = lifecycleMessageDeserializer.DeserializeFromJsonElement(inboxMsg.Envelope.Payload, inboxMsg.MessageType);
        var typedEnvelope = inboxMsg.Envelope.ReconstructWithPayload(message);

        metrics?.ReceptorInvocations.Add(1,
          new KeyValuePair<string, object?>(METRIC_STAGE, stage.ToString()),
          new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, inboxMsg.MessageType));
        var receptorSw = Stopwatch.StartNew();
        try {
          await using var scope = scopeFactory.CreateAsyncScope();
          var receptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
          if (receptorInvoker is not null) {
            await receptorInvoker.InvokeAsync(typedEnvelope, stage, context, ct);
          }
        } catch (Exception ex) {
          metrics?.ReceptorErrors.Add(1,
            new KeyValuePair<string, object?>(METRIC_STAGE, stage.ToString()),
            new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, inboxMsg.MessageType),
            new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
          throw;
        } finally {
          receptorSw.Stop();
          metrics?.ReceptorDuration.Record(receptorSw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>(METRIC_STAGE, stage.ToString()),
            new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, inboxMsg.MessageType));
        }
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
