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
#pragma warning disable S107 // Constructor uses DI injection — many parameters are idiomatic
public partial class ImmediateWorkCoordinatorStrategy(
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
  IOptions<SystemEventOptions>? systemEventOptions = null,
  IWorkChannelWriter? workChannelWriter = null
  ) : IWorkCoordinatorStrategy, IWorkFlusher {
#pragma warning restore S107
  private readonly IWorkCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly WorkCoordinatorOptions _options = options ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<ImmediateWorkCoordinatorStrategy>? _logger = logger;
  private readonly IServiceScopeFactory? _scopeFactory = scopeFactory;
  private readonly ILifecycleMessageDeserializer? _lifecycleMessageDeserializer = lifecycleMessageDeserializer;
  private readonly IOptionsMonitor<TracingOptions>? _tracingOptions = tracingOptions;
  private readonly IDeferredOutboxChannel? _deferredChannel = deferredChannel;
  private readonly IWorkChannelWriter? _workChannelWriter = workChannelWriter;
  private readonly WorkCoordinatorMetrics? _metrics = metrics;
  private readonly LifecycleMetrics? _lifecycleMetrics = lifecycleMetrics;
  private readonly SystemEventOptions? _systemEventOptions = systemEventOptions?.Value;
  private readonly WorkCoordinatorQueues _queues = new();

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
  public async Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
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

    // Snapshot arrays from queues + pending audit messages
    var outboxMessages = _queues.OutboxMessages.ToArray();
    var inboxMessages = _queues.InboxMessages.ToArray();
    var outboxCompletions = _queues.OutboxCompletions.ToArray();
    var inboxCompletions = _queues.InboxCompletions.ToArray();
    var outboxFailures = _queues.OutboxFailures.ToArray();
    var inboxFailures = _queues.InboxFailures.ToArray();
    var pendingAuditMessages = _queues.PendingAuditMessages.Count > 0
      ? _queues.PendingAuditMessages.ToArray()
      : null;

    var workBatch = await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      new FlushContext(
        _coordinator, _scopeFactory, _instanceProvider, _options, "immediate",
        outboxMessages, inboxMessages, outboxCompletions, inboxCompletions,
        outboxFailures, inboxFailures, flags, _lifecycleMessageDeserializer,
        _logger, _tracingOptions, _metrics, _lifecycleMetrics,
        WorkChannelWriter: _workChannelWriter, PendingAuditMessages: pendingAuditMessages),
      ct
    );

    // Clear queues after flush
    _queues.Clear();

    return workBatch;
  }

  /// <inheritdoc />
  Task IWorkFlusher.FlushAsync(CancellationToken ct) =>
    FlushAsync(WorkBatchOptions.SkipInboxClaiming, FlushMode.Required, ct);

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
