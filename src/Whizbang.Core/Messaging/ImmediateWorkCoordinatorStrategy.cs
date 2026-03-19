using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tracing;
using Whizbang.Core.Validation;

namespace Whizbang.Core.Messaging;

/// <summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:FlushAsync_ImmediatelyCallsWorkCoordinatorAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:QueueOutboxMessage_FlushesOnCallAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:QueueInboxMessage_FlushesOnCallAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/WorkFlusherTests.cs:ImmediateStrategy_FlushAsync_DelegatesToStrategyWithRequiredModeAsync</tests>
/// Immediate strategy - calls process_work_batch immediately for each operation.
/// Provides lowest latency but highest database load.
/// Best for: Real-time scenarios, low-throughput services, critical operations.
/// </summary>
public partial class ImmediateWorkCoordinatorStrategy : IWorkCoordinatorStrategy, IWorkFlusher {
  private readonly IWorkCoordinator _coordinator;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly WorkCoordinatorOptions _options;
  private readonly ILogger<ImmediateWorkCoordinatorStrategy>? _logger;
  private readonly IServiceScopeFactory? _scopeFactory;
  private readonly ILifecycleMessageDeserializer? _lifecycleMessageDeserializer;
  private readonly IOptionsMonitor<TracingOptions>? _tracingOptions;
  private readonly IDeferredOutboxChannel? _deferredChannel;
  private readonly WorkCoordinatorMetrics? _metrics;
  private readonly LifecycleMetrics? _lifecycleMetrics;
  private readonly SystemEventOptions? _systemEventOptions;
  private readonly WorkCoordinatorQueues _queues = new();

  public ImmediateWorkCoordinatorStrategy(
    IWorkCoordinator coordinator,
    IServiceInstanceProvider instanceProvider,
    WorkCoordinatorOptions options,
    ILogger<ImmediateWorkCoordinatorStrategy>? logger = null,
    IServiceScopeFactory? scopeFactory = null,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer = null,
    IOptionsMonitor<TracingOptions>? tracingOptions = null,
    IDeferredOutboxChannel? deferredChannel = null,
    WorkCoordinatorMetrics? metrics = null,
    LifecycleMetrics? lifecycleMetrics = null,
    IOptions<SystemEventOptions>? systemEventOptions = null
  ) {
    _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _logger = logger;
    _scopeFactory = scopeFactory;
    _lifecycleMessageDeserializer = lifecycleMessageDeserializer;
    _tracingOptions = tracingOptions;
    _deferredChannel = deferredChannel;
    _metrics = metrics;
    _lifecycleMetrics = lifecycleMetrics;
    _systemEventOptions = systemEventOptions?.Value;
  }

  /// <summary>
  /// Queues an outbox message for immediate flush.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:QueueOutboxMessage_FlushesOnCallAsync</tests>
  public void QueueOutboxMessage(OutboxMessage message) {
    StreamIdGuard.ThrowIfNonNullEmpty(message.StreamId, message.MessageId, "ImmediateStrategy.QueueOutbox", message.MessageType);
    _queues.AddOutboxMessage(message, _systemEventOptions);
    if (_logger != null) {
      LogOutboxMessageQueued(_logger);
    }
  }

  /// <summary>
  /// Queues an inbox message for immediate flush.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:QueueInboxMessage_FlushesOnCallAsync</tests>
  public void QueueInboxMessage(InboxMessage message) {
    StreamIdGuard.ThrowIfNonNullEmpty(message.StreamId, message.MessageId, "ImmediateStrategy.QueueInbox", message.MessageType);
    _queues.AddInboxMessage(message);
    if (_logger != null) {
      LogInboxMessageQueued(_logger);
    }
  }

  /// <summary>
  /// Queues an outbox message completion for immediate flush.
  /// </summary>
  public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
    _queues.AddOutboxCompletion(messageId, completedStatus);
    if (_logger != null) {
      LogOutboxCompletionQueued(_logger);
    }
  }

  /// <summary>
  /// Queues an inbox message completion for immediate flush.
  /// </summary>
  public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
    _queues.AddInboxCompletion(messageId, completedStatus);
    if (_logger != null) {
      LogInboxCompletionQueued(_logger);
    }
  }

  /// <summary>
  /// Queues an outbox message failure for immediate flush.
  /// </summary>
  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) {
    _queues.AddOutboxFailure(messageId, completedStatus, errorMessage);
    if (_logger != null) {
      LogOutboxFailureQueued(_logger);
    }
  }

  /// <summary>
  /// Queues an inbox message failure for immediate flush.
  /// </summary>
  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) {
    _queues.AddInboxFailure(messageId, completedStatus, errorMessage);
    if (_logger != null) {
      LogInboxFailureQueued(_logger);
    }
  }

  /// <summary>
  /// Immediately flushes all queued operations to the work coordinator.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:FlushAsync_ImmediatelyCallsWorkCoordinatorAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorDrainTests.cs:FlushAsync_DrainsDeferredChannel_IncludesInBatchAsync</tests>
  public async Task<WorkBatch> FlushAsync(WorkBatchFlags flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
    _metrics?.FlushCalls.Add(1, new KeyValuePair<string, object?>("strategy", "immediate"), new KeyValuePair<string, object?>("flush_mode", mode.ToString()));
    // Immediate strategy always flushes regardless of FlushMode
    // Drain deferred channel first - these get written in THIS transaction
    // Events that were published outside transaction context (e.g., PostPerspective handlers)
    // are picked up here and included in the current work batch.
    if (_deferredChannel?.HasPending == true) {
      var deferredMessages = _deferredChannel.DrainAll();
      // Prepend deferred messages to the queue
      _queues.OutboxMessages.InsertRange(0, deferredMessages);
      if (_logger != null) {
        LogDeferredChannelDrained(_logger, deferredMessages.Count);
      }
    }

    // Immediate strategy calls process_work_batch with all queued operations
    if (_logger != null) {
      LogFlushStarting(
        _logger,
        _queues.OutboxMessages.Count,
        _queues.InboxMessages.Count,
        _queues.OutboxCompletions.Count + _queues.InboxCompletions.Count,
        _queues.OutboxFailures.Count + _queues.InboxFailures.Count
      );
    }

    // Check if lifecycle tracing is enabled
    var enableLifecycleTracing = _tracingOptions?.CurrentValue.IsEnabled(TraceComponents.Lifecycle) ?? false;

    // PreDistribute lifecycle stages (before ProcessWorkBatchAsync)
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PreDistributeAsync,
      LifecycleStage.PreDistributeInline,
      _queues.OutboxMessages,
      _queues.InboxMessages,
      _scopeFactory,
      _lifecycleMessageDeserializer,
      _logger,
      enableLifecycleTracing: enableLifecycleTracing,
      metrics: _lifecycleMetrics,
      ct: ct
    );

    // DistributeAsync lifecycle stage (fire in parallel with ProcessWorkBatchAsync, non-blocking)
    LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(
      LifecycleStage.DistributeAsync,
      _queues.OutboxMessages,
      _queues.InboxMessages,
      _scopeFactory,
      _lifecycleMessageDeserializer,
      _logger,
      enableLifecycleTracing: enableLifecycleTracing,
      metrics: _lifecycleMetrics,
      ct: ct
    );

    // Merge pending audit messages after lifecycle stages
    _queues.MergeAuditMessages();

    var request = _queues.BuildRequest(_instanceProvider, _options, flags);
    var flushSw = System.Diagnostics.Stopwatch.StartNew();
    var workBatch = await _coordinator.ProcessWorkBatchAsync(request, ct);
    flushSw.Stop();
    _metrics?.FlushDuration.Record(flushSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("strategy", "immediate"));

    // PostDistribute lifecycle stages (after ProcessWorkBatchAsync)
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeAsync,
      LifecycleStage.PostDistributeInline,
      _queues.OutboxMessages,
      _queues.InboxMessages,
      _scopeFactory,
      _lifecycleMessageDeserializer,
      _logger,
      enableLifecycleTracing: enableLifecycleTracing,
      metrics: _lifecycleMetrics,
      ct: ct
    );

    // Clear queues after flush
    _queues.Clear();

    return workBatch;
  }

  /// <inheritdoc />
  Task IWorkFlusher.FlushAsync(CancellationToken ct) =>
    FlushAsync(WorkBatchFlags.None, FlushMode.Required, ct);

  // ========================================
  // High-Performance LoggerMessage Delegates
  // ========================================

  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Trace,
    Message = "Immediate strategy: Outbox message queued (will be sent on next Flush)"
  )]
  static partial void LogOutboxMessageQueued(ILogger logger);

  [LoggerMessage(
    EventId = 2,
    Level = LogLevel.Trace,
    Message = "Immediate strategy: Inbox message queued (will be stored on next Flush)"
  )]
  static partial void LogInboxMessageQueued(ILogger logger);

  [LoggerMessage(
    EventId = 3,
    Level = LogLevel.Trace,
    Message = "Immediate strategy: Outbox completion queued (will be reported on next Flush)"
  )]
  static partial void LogOutboxCompletionQueued(ILogger logger);

  [LoggerMessage(
    EventId = 4,
    Level = LogLevel.Trace,
    Message = "Immediate strategy: Inbox completion queued (will be reported on next Flush)"
  )]
  static partial void LogInboxCompletionQueued(ILogger logger);

  [LoggerMessage(
    EventId = 5,
    Level = LogLevel.Trace,
    Message = "Immediate strategy: Outbox failure queued (will be reported on next Flush)"
  )]
  static partial void LogOutboxFailureQueued(ILogger logger);

  [LoggerMessage(
    EventId = 6,
    Level = LogLevel.Trace,
    Message = "Immediate strategy: Inbox failure queued (will be reported on next Flush)"
  )]
  static partial void LogInboxFailureQueued(ILogger logger);

  [LoggerMessage(
    EventId = 7,
    Level = LogLevel.Trace,
    Message = "Immediate strategy flush: {OutboxMsgCount} outbox, {InboxMsgCount} inbox, {CompletionCount} completions, {FailureCount} failures"
  )]
  static partial void LogFlushStarting(
    ILogger logger,
    int outboxMsgCount,
    int inboxMsgCount,
    int completionCount,
    int failureCount
  );

  [LoggerMessage(
    EventId = 8,
    Level = LogLevel.Debug,
    Message = "Immediate strategy: Drained {Count} deferred messages from channel into current work batch"
  )]
  static partial void LogDeferredChannelDrained(ILogger logger, int count);
}
