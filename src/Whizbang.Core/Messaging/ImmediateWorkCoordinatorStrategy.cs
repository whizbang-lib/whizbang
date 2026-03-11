using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;
using Whizbang.Core.Tracing;
using Whizbang.Core.Validation;

namespace Whizbang.Core.Messaging;

/// <summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:FlushAsync_ImmediatelyCallsWorkCoordinatorAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:QueueOutboxMessage_FlushesOnCallAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:QueueInboxMessage_FlushesOnCallAsync</tests>
/// Immediate strategy - calls process_work_batch immediately for each operation.
/// Provides lowest latency but highest database load.
/// Best for: Real-time scenarios, low-throughput services, critical operations.
/// </summary>
public partial class ImmediateWorkCoordinatorStrategy : IWorkCoordinatorStrategy {
  private readonly IWorkCoordinator _coordinator;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly WorkCoordinatorOptions _options;
  private readonly ILogger<ImmediateWorkCoordinatorStrategy>? _logger;
  private readonly ILifecycleInvoker? _lifecycleInvoker;
  private readonly ILifecycleMessageDeserializer? _lifecycleMessageDeserializer;
  private readonly IOptionsMonitor<TracingOptions>? _tracingOptions;
  private readonly IDeferredOutboxChannel? _deferredChannel;

  // Immediate strategy queues for single flush cycle
  private readonly List<OutboxMessage> _queuedOutboxMessages = [];
  private readonly List<InboxMessage> _queuedInboxMessages = [];
  private readonly List<MessageCompletion> _queuedOutboxCompletions = [];
  private readonly List<MessageCompletion> _queuedInboxCompletions = [];
  private readonly List<MessageFailure> _queuedOutboxFailures = [];
  private readonly List<MessageFailure> _queuedInboxFailures = [];

  public ImmediateWorkCoordinatorStrategy(
    IWorkCoordinator coordinator,
    IServiceInstanceProvider instanceProvider,
    WorkCoordinatorOptions options,
    ILogger<ImmediateWorkCoordinatorStrategy>? logger = null,
    ILifecycleInvoker? lifecycleInvoker = null,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer = null,
    IOptionsMonitor<TracingOptions>? tracingOptions = null,
    IDeferredOutboxChannel? deferredChannel = null
  ) {
    _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _logger = logger;
    _lifecycleInvoker = lifecycleInvoker;
    _lifecycleMessageDeserializer = lifecycleMessageDeserializer;
    _tracingOptions = tracingOptions;
    _deferredChannel = deferredChannel;
  }

  /// <summary>
  /// Queues an outbox message for immediate flush.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:QueueOutboxMessage_FlushesOnCallAsync</tests>
  public void QueueOutboxMessage(OutboxMessage message) {
    StreamIdGuard.ThrowIfNonNullEmpty(message.StreamId, message.MessageId, "ImmediateStrategy.QueueOutbox");
    _queuedOutboxMessages.Add(message);
    if (_logger != null) {
      LogOutboxMessageQueued(_logger);
    }
  }

  /// <summary>
  /// Queues an inbox message for immediate flush.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:QueueInboxMessage_FlushesOnCallAsync</tests>
  public void QueueInboxMessage(InboxMessage message) {
    StreamIdGuard.ThrowIfNonNullEmpty(message.StreamId, message.MessageId, "ImmediateStrategy.QueueInbox");
    _queuedInboxMessages.Add(message);
    if (_logger != null) {
      LogInboxMessageQueued(_logger);
    }
  }

  /// <summary>
  /// Queues an outbox message completion for immediate flush.
  /// </summary>
  public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
    _queuedOutboxCompletions.Add(new MessageCompletion {
      MessageId = messageId,
      Status = completedStatus
    });
    if (_logger != null) {
      LogOutboxCompletionQueued(_logger);
    }
  }

  /// <summary>
  /// Queues an inbox message completion for immediate flush.
  /// </summary>
  public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
    _queuedInboxCompletions.Add(new MessageCompletion {
      MessageId = messageId,
      Status = completedStatus
    });
    if (_logger != null) {
      LogInboxCompletionQueued(_logger);
    }
  }

  /// <summary>
  /// Queues an outbox message failure for immediate flush.
  /// </summary>
  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) {
    _queuedOutboxFailures.Add(new MessageFailure {
      MessageId = messageId,
      CompletedStatus = completedStatus,
      Error = errorMessage
    });
    if (_logger != null) {
      LogOutboxFailureQueued(_logger);
    }
  }

  /// <summary>
  /// Queues an inbox message failure for immediate flush.
  /// </summary>
  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) {
    _queuedInboxFailures.Add(new MessageFailure {
      MessageId = messageId,
      CompletedStatus = completedStatus,
      Error = errorMessage
    });
    if (_logger != null) {
      LogInboxFailureQueued(_logger);
    }
  }

  /// <summary>
  /// Immediately flushes all queued operations to the work coordinator.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:FlushAsync_ImmediatelyCallsWorkCoordinatorAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorDrainTests.cs:FlushAsync_DrainsDeferredChannel_IncludesInBatchAsync</tests>
  public async Task<WorkBatch> FlushAsync(WorkBatchFlags flags, CancellationToken ct = default) {
    // Drain deferred channel first - these get written in THIS transaction
    // Events that were published outside transaction context (e.g., PostPerspective handlers)
    // are picked up here and included in the current work batch.
    if (_deferredChannel?.HasPending == true) {
      var deferredMessages = _deferredChannel.DrainAll();
      // Prepend deferred messages to the queue
      _queuedOutboxMessages.InsertRange(0, deferredMessages);
      if (_logger != null) {
        LogDeferredChannelDrained(_logger, deferredMessages.Count);
      }
    }

    // Immediate strategy calls process_work_batch with all queued operations
    if (_logger != null) {
      LogFlushStarting(
        _logger,
        _queuedOutboxMessages.Count,
        _queuedInboxMessages.Count,
        _queuedOutboxCompletions.Count + _queuedInboxCompletions.Count,
        _queuedOutboxFailures.Count + _queuedInboxFailures.Count
      );
    }

    // Check if lifecycle tracing is enabled
    var enableLifecycleTracing = _tracingOptions?.CurrentValue.IsEnabled(TraceComponents.Lifecycle) ?? false;

    // PreDistribute lifecycle stages (before ProcessWorkBatchAsync)
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PreDistributeAsync,
      LifecycleStage.PreDistributeInline,
      _queuedOutboxMessages,
      _queuedInboxMessages,
      _lifecycleInvoker,
      _lifecycleMessageDeserializer,
      _logger,
      enableLifecycleTracing: enableLifecycleTracing,
      ct: ct
    );

    // DistributeAsync lifecycle stage (fire in parallel with ProcessWorkBatchAsync, non-blocking)
    LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(
      LifecycleStage.DistributeAsync,
      _queuedOutboxMessages,
      _queuedInboxMessages,
      _lifecycleInvoker,
      _lifecycleMessageDeserializer,
      _logger,
      enableLifecycleTracing: enableLifecycleTracing,
      ct: ct
    );

    var request = new ProcessWorkBatchRequest {
      InstanceId = _instanceProvider.InstanceId,
      ServiceName = _instanceProvider.ServiceName,
      HostName = _instanceProvider.HostName,
      ProcessId = _instanceProvider.ProcessId,
      Metadata = null,
      OutboxCompletions = [.. _queuedOutboxCompletions],
      OutboxFailures = [.. _queuedOutboxFailures],
      InboxCompletions = [.. _queuedInboxCompletions],
      InboxFailures = [.. _queuedInboxFailures],
      ReceptorCompletions = [],  // FUTURE: Add receptor processing support
      ReceptorFailures = [],
      PerspectiveCompletions = [],  // FUTURE: Add perspective checkpoint support
      PerspectiveFailures = [],
      NewOutboxMessages = [.. _queuedOutboxMessages],
      NewInboxMessages = [.. _queuedInboxMessages],
      RenewOutboxLeaseIds = [],
      RenewInboxLeaseIds = [],
      Flags = flags | (_options.DebugMode ? WorkBatchFlags.DebugMode : WorkBatchFlags.None),
      PartitionCount = _options.PartitionCount,
      LeaseSeconds = _options.LeaseSeconds,
      StaleThresholdSeconds = _options.StaleThresholdSeconds
    };
    var workBatch = await _coordinator.ProcessWorkBatchAsync(request, ct);

    // PostDistribute lifecycle stages (after ProcessWorkBatchAsync)
    await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
      LifecycleStage.PostDistributeAsync,
      LifecycleStage.PostDistributeInline,
      _queuedOutboxMessages,
      _queuedInboxMessages,
      _lifecycleInvoker,
      _lifecycleMessageDeserializer,
      _logger,
      enableLifecycleTracing: enableLifecycleTracing,
      ct: ct
    );

    // Clear queues after flush
    _queuedOutboxMessages.Clear();
    _queuedInboxMessages.Clear();
    _queuedOutboxCompletions.Clear();
    _queuedOutboxFailures.Clear();
    _queuedInboxCompletions.Clear();
    _queuedInboxFailures.Clear();

    return workBatch;
  }

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
