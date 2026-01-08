using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;

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
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer = null
  ) {
    _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _logger = logger;
    _lifecycleInvoker = lifecycleInvoker;
    _lifecycleMessageDeserializer = lifecycleMessageDeserializer;
  }

  /// <summary>
  /// Queues an outbox message for immediate flush.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:QueueOutboxMessage_FlushesOnCallAsync</tests>
  public void QueueOutboxMessage(OutboxMessage message) {
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
  public async Task<WorkBatch> FlushAsync(WorkBatchFlags flags, CancellationToken ct = default) {
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

    // PreDistribute lifecycle stages (before ProcessWorkBatchAsync)
    if (_lifecycleInvoker is not null && _lifecycleMessageDeserializer is not null) {
      var lifecycleContext = new LifecycleExecutionContext {
        CurrentStage = LifecycleStage.PreDistributeAsync,
        EventId = null,
        StreamId = null,
        PerspectiveName = null,
        LastProcessedEventId = null
      };

      // Invoke PreDistributeAsync for all messages
      foreach (var outboxMsg in _queuedOutboxMessages) {
        var message = _lifecycleMessageDeserializer.DeserializeFromEnvelope(outboxMsg.Envelope, outboxMsg.EnvelopeType);
        await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PreDistributeAsync, lifecycleContext, ct);
      }

      foreach (var inboxMsg in _queuedInboxMessages) {
        var message = _lifecycleMessageDeserializer.DeserializeFromEnvelope(inboxMsg.Envelope, inboxMsg.EnvelopeType);
        await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PreDistributeAsync, lifecycleContext, ct);
      }

      // Invoke PreDistributeInline for all messages
      lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PreDistributeInline };
      foreach (var outboxMsg in _queuedOutboxMessages) {
        var message = _lifecycleMessageDeserializer.DeserializeFromEnvelope(outboxMsg.Envelope, outboxMsg.EnvelopeType);
        await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PreDistributeInline, lifecycleContext, ct);
      }

      foreach (var inboxMsg in _queuedInboxMessages) {
        var message = _lifecycleMessageDeserializer.DeserializeFromEnvelope(inboxMsg.Envelope, inboxMsg.EnvelopeType);
        await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PreDistributeInline, lifecycleContext, ct);
      }
    }

    var workBatch = await _coordinator.ProcessWorkBatchAsync(
      _instanceProvider.InstanceId,
      _instanceProvider.ServiceName,
      _instanceProvider.HostName,
      _instanceProvider.ProcessId,
      metadata: null,
      outboxCompletions: [.. _queuedOutboxCompletions],
      outboxFailures: [.. _queuedOutboxFailures],
      inboxCompletions: [.. _queuedInboxCompletions],
      inboxFailures: [.. _queuedInboxFailures],
      receptorCompletions: [],  // TODO: Add receptor processing support
      receptorFailures: [],
      perspectiveCompletions: [],  // TODO: Add perspective checkpoint support
      perspectiveFailures: [],
      newOutboxMessages: [.. _queuedOutboxMessages],
      newInboxMessages: [.. _queuedInboxMessages],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      flags: flags | (_options.DebugMode ? WorkBatchFlags.DebugMode : WorkBatchFlags.None),
      partitionCount: _options.PartitionCount,
      leaseSeconds: _options.LeaseSeconds,
      staleThresholdSeconds: _options.StaleThresholdSeconds,
      cancellationToken: ct
    );

    // PostDistribute lifecycle stages (after ProcessWorkBatchAsync)
    if (_lifecycleInvoker is not null && _lifecycleMessageDeserializer is not null) {
      var lifecycleContext = new LifecycleExecutionContext {
        CurrentStage = LifecycleStage.PostDistributeAsync,
        EventId = null,
        StreamId = null,
        PerspectiveName = null,
        LastProcessedEventId = null
      };

      // Invoke PostDistributeAsync for all messages
      foreach (var outboxMsg in _queuedOutboxMessages) {
        var message = _lifecycleMessageDeserializer.DeserializeFromEnvelope(outboxMsg.Envelope, outboxMsg.EnvelopeType);
        await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PostDistributeAsync, lifecycleContext, ct);
      }

      foreach (var inboxMsg in _queuedInboxMessages) {
        var message = _lifecycleMessageDeserializer.DeserializeFromEnvelope(inboxMsg.Envelope, inboxMsg.EnvelopeType);
        await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PostDistributeAsync, lifecycleContext, ct);
      }

      // Invoke PostDistributeInline for all messages
      lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PostDistributeInline };
      foreach (var outboxMsg in _queuedOutboxMessages) {
        var message = _lifecycleMessageDeserializer.DeserializeFromEnvelope(outboxMsg.Envelope, outboxMsg.EnvelopeType);
        await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PostDistributeInline, lifecycleContext, ct);
      }

      foreach (var inboxMsg in _queuedInboxMessages) {
        var message = _lifecycleMessageDeserializer.DeserializeFromEnvelope(inboxMsg.Envelope, inboxMsg.EnvelopeType);
        await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PostDistributeInline, lifecycleContext, ct);
      }
    }

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
}
