// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Groups the shared parameters for lifecycle invocation methods in <see cref="LifecycleInvocationHelper"/>.
/// </summary>
/// <param name="OutboxMessages">Outbox messages to process</param>
/// <param name="InboxMessages">Inbox messages to process</param>
/// <param name="ScopeFactory">Service scope factory for creating per-message scopes to resolve IReceptorInvoker</param>
/// <param name="LifecycleMessageDeserializer">Message deserializer (null-safe, returns early if null)</param>
/// <param name="Logger">Optional logger for error reporting</param>
/// <param name="EnableLifecycleTracing">Whether to create lifecycle OpenTelemetry spans. When false, lifecycle logic still runs but no spans are emitted.</param>
/// <param name="Metrics">Optional lifecycle metrics for stage/receptor instrumentation</param>
/// <param name="DetachedWork">Where the detached stage's background invocation is recorded, so a stopping host can wait
/// for it before disposing the service provider it resolves receptors from. Without one, nothing waits for it.</param>
public readonly record struct DistributeLifecycleContext(
  IReadOnlyList<OutboxMessage> OutboxMessages,
  IReadOnlyList<InboxMessage> InboxMessages,
  IServiceScopeFactory? ScopeFactory,
  ILifecycleMessageDeserializer? LifecycleMessageDeserializer,
  ILogger? Logger,
  bool EnableLifecycleTracing = true,
  LifecycleMetrics? Metrics = null,
  DetachedLifecycleWork? DetachedWork = null) {
  /// <summary>
  /// What the inline stage does when a receptor throws. The default stops at the first failure and throws it, as an
  /// inline stage that can still refuse the write always has.
  /// </summary>
  internal InlineStageFailureHandling InlineFailures { get; init; }
}

/// <summary>
/// What an inline Distribute stage does when a receptor throws (#2310).
/// </summary>
/// <docs>fundamentals/lifecycle/lifecycle-stages#distribute-failures</docs>
internal enum InlineStageFailureHandling {
  /// <summary>The first failure stops the stage and is thrown: the stage can still refuse the write.</summary>
  StopAtFirstFailure = 0,

  /// <summary>Every receptor of every message runs and each failure is logged; the first is thrown once all have run.</summary>
  RunEveryReceptorThenThrowFirst = 1,

  /// <summary>Every receptor of every message runs and each failure is logged; nothing is thrown.</summary>
  RunEveryReceptorAndLog = 2,
}

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
/// <para>
/// <strong>Failures (#2310):</strong> a flush holds messages from many streams, so one receptor's exception must cost
/// only its own message. The detached stage always runs every receptor of every message and logs each failure. The
/// inline stage does what <see cref="DistributeLifecycleContext"/> asks: stop at the first failure (the default, for a
/// stage that can still refuse the write), or run every receptor of every message, log each failure, and then either
/// throw the first one or nothing.
/// </para>
/// </remarks>
/// <docs>fundamentals/lifecycle/lifecycle-stages#distribute-failures</docs>
/// <docs>operations/diagnostics/error-reference#distribute-receptor-failed</docs>
/// <docs>operations/diagnostics/error-reference#distribute-message-failed</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/LifecycleStageFailureIsolationTests.cs</tests>
public static partial class LifecycleInvocationHelper {
  private const string METRIC_STAGE = "stage";
  private const string METRIC_MESSAGE_TYPE = "message_type";

  /// <summary>
  /// Groups lifecycle invocation dependencies to reduce parameter count in internal methods.
  /// </summary>
  private readonly record struct LifecycleInvocationDependencies(
    IServiceScopeFactory? ScopeFactory,
    ILifecycleMessageDeserializer? LifecycleMessageDeserializer,
    ILogger? Logger,
    bool EnableLifecycleTracing,
    LifecycleMetrics? Metrics);

  /// <summary>
  /// Invokes lifecycle receptors for outbox and inbox messages at async and inline stages.
  /// Snapshots collections before Task.Run to prevent "Collection was modified" exceptions.
  /// </summary>
  /// <param name="asyncStage">The async lifecycle stage (e.g., PostDistributeDetached)</param>
  /// <param name="inlineStage">The inline lifecycle stage (e.g., PostDistributeInline)</param>
  /// <param name="context">The lifecycle context containing messages, scope factory, deserializer, and options</param>
  /// <param name="ct">Cancellation token</param>
  public static async ValueTask InvokeDistributeLifecycleStagesAsync(
    LifecycleStage asyncStage,
    LifecycleStage inlineStage,
    DistributeLifecycleContext context,
    CancellationToken ct = default) {

    // CRITICAL: Snapshot collections before Task.Run to avoid "Collection was modified" exceptions
    // The main thread may modify the original collections while the background task iterates
    var outboxSnapshot = context.OutboxMessages.ToArray();
    var inboxSnapshot = context.InboxMessages.ToArray();
    var deps = new LifecycleInvocationDependencies(context.ScopeFactory, context.LifecycleMessageDeserializer, context.Logger, context.EnableLifecycleTracing, context.Metrics);

    // Invoke async stage (non-blocking, backgrounded), recorded so a stopping host can wait for it
    _startDetached(outboxSnapshot, inboxSnapshot, asyncStage, deps, context.DetachedWork, ct);

    // Invoke inline stage (blocking, sequential)
    if (context.ScopeFactory is null || context.LifecycleMessageDeserializer is null) {
      return;
    }

    context.Metrics?.StageInvocations.Add(1, new KeyValuePair<string, object?>(METRIC_STAGE, inlineStage.ToString()));
    var stageSw = Stopwatch.StartNew();

    // One collector for the whole stage, and only when the stage isolates failures: the default stops at the first
    // failure and needs none, so a flush that never fails allocates nothing per message for this.
    var failures = context.InlineFailures == InlineStageFailureHandling.StopAtFirstFailure
      ? null
      : new StageFailures(context.Logger, context.Metrics, inlineStage);
    var stage = new StageInvocation(inlineStage, context.ScopeFactory, context.LifecycleMessageDeserializer, context.EnableLifecycleTracing, context.Metrics, failures);
    await _processOutboxMessagesAsync(outboxSnapshot, stage, ct);
    await _processInboxMessagesAsync(inboxSnapshot, stage, ct);

    stageSw.Stop();
    context.Metrics?.StageDuration.Record(stageSw.Elapsed.TotalMilliseconds,
      new KeyValuePair<string, object?>(METRIC_STAGE, inlineStage.ToString()),
      new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, "mixed"));

    if (context.InlineFailures == InlineStageFailureHandling.RunEveryReceptorThenThrowFirst) {
      failures!.ThrowFirst();
    }
  }

  /// <summary>
  /// Invokes lifecycle receptors for outbox and inbox messages at an async-only stage (no inline counterpart).
  /// Snapshots collections before Task.Run to prevent "Collection was modified" exceptions.
  /// </summary>
  /// <param name="asyncStage">The async lifecycle stage (e.g., DistributeDetached)</param>
  /// <param name="context">The lifecycle context containing messages, scope factory, deserializer, and options</param>
  /// <param name="ct">Cancellation token</param>
  public static void InvokeAsyncOnlyLifecycleStage(
    LifecycleStage asyncStage,
    DistributeLifecycleContext context,
    CancellationToken ct = default) {

    // CRITICAL: Snapshot collections before Task.Run to avoid "Collection was modified" exceptions
    var outboxSnapshot = context.OutboxMessages.ToArray();
    var inboxSnapshot = context.InboxMessages.ToArray();
    var deps = new LifecycleInvocationDependencies(context.ScopeFactory, context.LifecycleMessageDeserializer, context.Logger, context.EnableLifecycleTracing, context.Metrics);

    // Invoke async stage (non-blocking, backgrounded) - no inline stage for DistributeDetached
    _startDetached(outboxSnapshot, inboxSnapshot, asyncStage, deps, context.DetachedWork, ct);
  }

  /// <summary>
  /// Starts a detached stage in the background and records it in <paramref name="detachedWork"/>: the flush does not
  /// wait for it, but a stopping host must, or it runs against a disposed service provider.
  /// </summary>
  private static void _startDetached(
      OutboxMessage[] outboxSnapshot,
      InboxMessage[] inboxSnapshot,
      LifecycleStage asyncStage,
      LifecycleInvocationDependencies deps,
      DetachedLifecycleWork? detachedWork,
      CancellationToken ct) {
    var invocation = _invokeAsyncStageInBackgroundAsync(outboxSnapshot, inboxSnapshot, asyncStage, deps, ct);
    detachedWork?.Track(invocation);
  }

  /// <summary>
  /// Invokes async stage lifecycle receptors in a background task.
  /// Handles exceptions and logs errors without affecting the main thread.
  /// </summary>
  private static Task _invokeAsyncStageInBackgroundAsync(
      OutboxMessage[] outboxSnapshot,
      InboxMessage[] inboxSnapshot,
      LifecycleStage asyncStage,
      LifecycleInvocationDependencies deps,
      CancellationToken ct) {
    return Task.Run(async () => {
      if (deps.ScopeFactory is null || deps.LifecycleMessageDeserializer is null) {
        return;
      }

      deps.Metrics?.StageInvocations.Add(1, new KeyValuePair<string, object?>(METRIC_STAGE, asyncStage.ToString()));
      var stageSw = Stopwatch.StartNew();

      // Nothing waits on a detached stage, so each failure is logged against its own message and the rest still run.
      var stage = new StageInvocation(asyncStage, deps.ScopeFactory, deps.LifecycleMessageDeserializer, deps.EnableLifecycleTracing, deps.Metrics,
        new StageFailures(deps.Logger, deps.Metrics, asyncStage));
      try {
        await _processOutboxMessagesAsync(outboxSnapshot, stage, ct);
        await _processInboxMessagesAsync(inboxSnapshot, stage, ct);
      } finally {
        stageSw.Stop();
        deps.Metrics?.StageDuration.Record(stageSw.Elapsed.TotalMilliseconds,
          new KeyValuePair<string, object?>(METRIC_STAGE, asyncStage.ToString()),
          new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, "mixed"));
      }
    }, ct);
  }

  /// <summary>What every message of one stage of a flush is invoked with.</summary>
  private sealed record StageInvocation(
    LifecycleStage Stage,
    IServiceScopeFactory ScopeFactory,
    ILifecycleMessageDeserializer Deserializer,
    bool EnableLifecycleTracing,
    LifecycleMetrics? Metrics,
    StageFailures? Failures);

  /// <summary>
  /// Processes outbox messages for a given lifecycle stage using scoped IReceptorInvoker.
  /// ReceptorInvoker handles all context establishment (security, message context, trace parenting).
  /// </summary>
  private static async Task _processOutboxMessagesAsync(OutboxMessage[] messages, StageInvocation stage, CancellationToken ct) {
    foreach (var outboxMsg in messages) {
      await _invokeMessageAsync(outboxMsg.Envelope, outboxMsg.MessageType, outboxMsg.MessageId, MessageSource.Outbox, stage, ct);
    }
  }

  /// <summary>
  /// Processes inbox messages for a given lifecycle stage using scoped IReceptorInvoker.
  /// ReceptorInvoker handles all context establishment (security, message context, trace parenting).
  /// </summary>
  private static async Task _processInboxMessagesAsync(InboxMessage[] messages, StageInvocation stage, CancellationToken ct) {
    foreach (var inboxMsg in messages) {
      await _invokeMessageAsync(inboxMsg.Envelope, inboxMsg.MessageType, inboxMsg.MessageId, MessageSource.Inbox, stage, ct);
    }
  }

  /// <summary>
  /// Runs one message of a flush through the stage. With no collector the first failure is thrown, as an inline stage
  /// that can refuse the write needs; with one, a failure is recorded against this message and the flush goes on to the
  /// next (#2310). A cancellation of the flush is always thrown.
  /// </summary>
  private static async Task _invokeMessageAsync(
      IMessageEnvelope<JsonElement> envelope,
      string messageType,
      Guid messageId,
      MessageSource source,
      StageInvocation stage,
      CancellationToken ct) {
    if (stage.Failures is null) {
      await _invokeMessageCoreAsync(envelope, messageType, messageId, source, stage, ct);
      return;
    }
    try {
      await _invokeMessageCoreAsync(envelope, messageType, messageId, source, stage, ct);
    } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
      // Thrown before any receptor ran (the payload could not be read), or by an invoker that does not isolate its
      // receptors: either way it is this message's failure, and the rest of the flush still runs.
      stage.Failures.RecordMessageFailure(messageType, messageId, ex);
    }
  }

  private static async Task _invokeMessageCoreAsync(
      IMessageEnvelope<JsonElement> envelope,
      string messageType,
      Guid messageId,
      MessageSource source,
      StageInvocation stage,
      CancellationToken ct) {
    var extracted = EnvelopeContextExtractor.ExtractFromHops(envelope.Hops);
    var parentContext = extracted.TraceContext;

    using (stage.EnableLifecycleTracing ? WhizbangActivitySource.Tracing.StartActivity($"Lifecycle {stage.Stage}", ActivityKind.Internal, parentContext: parentContext) : null) {
      var context = _createLifecycleContext(stage.Stage, source);
      // Resolve the invoker before deserializing: a host without one has nothing to invoke, so the
      // payload round trip (and any failure it could raise) is work with no consumer.
      await using var scope = stage.ScopeFactory.CreateAsyncScope();
      var receptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
      if (receptorInvoker is null) {
        return;
      }
      var message = stage.Deserializer.DeserializeFromJsonElement(envelope.Payload, messageType);
      MessageParseDiagnostics.Record(messageId, MessageParseSource.LifecycleStage, MessageParseKind.Payload);
      var typedEnvelope = envelope.ReconstructWithPayload(message);

      stage.Metrics?.ReceptorInvocations.Add(1,
        new KeyValuePair<string, object?>(METRIC_STAGE, stage.Stage.ToString()),
        new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, messageType));
      var receptorSw = Stopwatch.StartNew();
      try {
        if (stage.Failures is null) {
          await receptorInvoker.InvokeAsync(typedEnvelope, stage.Stage, context, ct);
        } else {
          await receptorInvoker.InvokeEveryReceptorAsync(typedEnvelope, stage.Stage, context, stage.Failures, ct);
        }
      } catch (Exception ex) when (stage.Failures is null) {
        // An isolated failure is counted where it is recorded; this one is about to stop the stage.
        _recordReceptorError(stage.Metrics, stage.Stage, messageType, ex);
        throw;
      } finally {
        receptorSw.Stop();
        stage.Metrics?.ReceptorDuration.Record(receptorSw.Elapsed.TotalMilliseconds,
          new KeyValuePair<string, object?>(METRIC_STAGE, stage.Stage.ToString()),
          new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, messageType));
      }
    }
  }

  private static void _recordReceptorError(LifecycleMetrics? metrics, LifecycleStage stage, string messageType, Exception ex) =>
    metrics?.ReceptorErrors.Add(1,
      new KeyValuePair<string, object?>(METRIC_STAGE, stage.ToString()),
      new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, messageType),
      new KeyValuePair<string, object?>("error_type", TypeNameFormatter.ForLog(ex.GetType())));

  /// <summary>
  /// The failures of one stage of a flush: each is logged naming its receptor (when a receptor threw), message type and
  /// message id, counted, and the first is kept for a stage that must still throw it once every message has run.
  /// </summary>
  private sealed class StageFailures(ILogger? logger, LifecycleMetrics? metrics, LifecycleStage flushStage) : IReceptorFailureSink {
    private ExceptionDispatchInfo? _first;

    public void OnReceptorFailed(IMessageEnvelope envelope, LifecycleStage stage, string receptorId, Exception exception) {
      var messageType = TypeNameFormatter.DisplayName(envelope.Payload.GetType());
      if (logger is not null) {
        Log.ReceptorFailed(logger, exception, receptorId, stage, messageType, envelope.MessageId.Value);
      }
      _record(messageType, exception);
    }

    public void RecordMessageFailure(string messageType, Guid messageId, Exception exception) {
      if (logger is not null) {
        Log.MessageFailed(logger, exception, flushStage, messageType, messageId);
      }
      _record(messageType, exception);
    }

    /// <summary>Throws the stage's first failure as it was thrown, or returns when none failed.</summary>
    public void ThrowFirst() => _first?.Throw();

    private void _record(string messageType, Exception exception) {
      _recordReceptorError(metrics, flushStage, messageType, exception);
      _first ??= ExceptionDispatchInfo.Capture(exception);
    }
  }

  private static partial class Log {
    [LoggerMessage(
      EventId = 1,
      Level = LogLevel.Error,
      Message = "Lifecycle receptor {ReceptorId} failed at {Stage} for {MessageType} message {MessageId}; every other receptor and message in the flush still ran")]
    public static partial void ReceptorFailed(ILogger logger, Exception exception, string receptorId, LifecycleStage stage, string messageType, Guid messageId);

    [LoggerMessage(
      EventId = 2,
      Level = LogLevel.Error,
      Message = "Lifecycle stage {Stage} failed for {MessageType} message {MessageId}; every other message in the flush still ran")]
    public static partial void MessageFailed(ILogger logger, Exception exception, LifecycleStage stage, string messageType, Guid messageId);
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
