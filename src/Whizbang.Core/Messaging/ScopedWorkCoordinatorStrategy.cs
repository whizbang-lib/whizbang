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
/// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyTests.cs:DisposeAsync_FlushesQueuedMessagesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyTests.cs:FlushAsync_BeforeDisposal_FlushesImmediatelyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyTests.cs:MultipleQueues_FlushedTogetherOnDisposalAsync</tests>
/// Scoped strategy - batches operations within a scope (e.g., HTTP request, message handler).
/// Flushes on scope disposal (IAsyncDisposable pattern).
/// Provides a good balance of latency and efficiency.
/// Best for: Web APIs, message handlers, transactional operations.
/// </summary>
public partial class ScopedWorkCoordinatorStrategy : IWorkCoordinatorStrategy, IAsyncDisposable {
  private readonly IWorkCoordinator _coordinator;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly IWorkChannelWriter? _workChannelWriter;
  private readonly WorkCoordinatorOptions _options;
  private readonly ILogger<ScopedWorkCoordinatorStrategy>? _logger;
  private readonly ILifecycleInvoker? _lifecycleInvoker;
  private readonly ILifecycleMessageDeserializer? _lifecycleMessageDeserializer;

  // Queues for batching operations within the scope
  private readonly List<OutboxMessage> _queuedOutboxMessages = [];
  private readonly List<InboxMessage> _queuedInboxMessages = [];
  private readonly List<MessageCompletion> _queuedOutboxCompletions = [];
  private readonly List<MessageCompletion> _queuedInboxCompletions = [];
  private readonly List<MessageFailure> _queuedOutboxFailures = [];
  private readonly List<MessageFailure> _queuedInboxFailures = [];

  private bool _disposed;

  public ScopedWorkCoordinatorStrategy(
    IWorkCoordinator coordinator,
    IServiceInstanceProvider instanceProvider,
    IWorkChannelWriter? workChannelWriter,
    WorkCoordinatorOptions options,
    ILogger<ScopedWorkCoordinatorStrategy>? logger = null,
    ILifecycleInvoker? lifecycleInvoker = null,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer = null
  ) {
    _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _workChannelWriter = workChannelWriter;
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _logger = logger;
    _lifecycleInvoker = lifecycleInvoker;
    _lifecycleMessageDeserializer = lifecycleMessageDeserializer;
  }

  public void QueueOutboxMessage(OutboxMessage message) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queuedOutboxMessages.Add(message);
    if (_logger != null) {
      LogQueuedOutboxMessage(_logger, message.MessageId, message.Destination);
    }
  }

  public void QueueInboxMessage(InboxMessage message) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queuedInboxMessages.Add(message);
    if (_logger != null) {
      LogQueuedInboxMessage(_logger, message.MessageId, message.HandlerName);
    }
  }

  public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queuedOutboxCompletions.Add(new MessageCompletion {
      MessageId = messageId,
      Status = completedStatus
    });
    if (_logger != null) {
      LogQueuedOutboxCompletion(_logger, messageId, completedStatus);
    }
  }

  public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queuedInboxCompletions.Add(new MessageCompletion {
      MessageId = messageId,
      Status = completedStatus
    });
    if (_logger != null) {
      LogQueuedInboxCompletion(_logger, messageId, completedStatus);
    }
  }

  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queuedOutboxFailures.Add(new MessageFailure {
      MessageId = messageId,
      CompletedStatus = completedStatus,
      Error = errorMessage
    });
    if (_logger != null) {
      LogQueuedOutboxFailure(_logger, messageId, errorMessage);
    }
  }

  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queuedInboxFailures.Add(new MessageFailure {
      MessageId = messageId,
      CompletedStatus = completedStatus,
      Error = errorMessage
    });
    if (_logger != null) {
      LogQueuedInboxFailure(_logger, messageId, errorMessage);
    }
  }

  public async Task<WorkBatch> FlushAsync(WorkBatchFlags flags, CancellationToken ct = default) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    if (_queuedOutboxMessages.Count == 0 &&
        _queuedInboxMessages.Count == 0 &&
        _queuedOutboxCompletions.Count == 0 &&
        _queuedOutboxFailures.Count == 0 &&
        _queuedInboxCompletions.Count == 0 &&
        _queuedInboxFailures.Count == 0) {
      return new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      };
    }

    // Log a summary of what's being flushed to the database
    if (_logger != null) {
      LogFlushSummary(_logger, _queuedOutboxMessages.Count, _queuedInboxMessages.Count);
      LogFlushingWithInstanceId(_logger, _instanceProvider.InstanceId, _instanceProvider.ServiceName, _queuedOutboxMessages.Count);
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

    // Call process_work_batch with all queued operations
    var workBatch = await _coordinator.ProcessWorkBatchAsync(
      _instanceProvider.InstanceId,
      _instanceProvider.ServiceName,
      _instanceProvider.HostName,
      _instanceProvider.ProcessId,
      metadata: null,  // TODO: Add metadata support to WorkCoordinatorOptions
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

    // DIAGNOSTIC: Log what was returned from ProcessWorkBatchAsync
    if (_logger != null) {
      LogProcessWorkBatchResult(_logger, workBatch.OutboxWork.Count, workBatch.InboxWork.Count, workBatch.PerspectiveWork.Count);
      if (workBatch.OutboxWork.Count > 0) {
        foreach (var work in workBatch.OutboxWork.Take(3)) {
          var isNewlyStored = (work.Flags & WorkBatchFlags.NewlyStored) != 0;
          LogReturnedOutboxWork(_logger, work.MessageId, work.Destination, isNewlyStored);
        }
      } else if (_queuedOutboxMessages.Count > 0) {
        // CRITICAL: We queued messages but got 0 back - this is the bug!
        LogNoWorkReturned(_logger, _queuedOutboxMessages.Count, _instanceProvider.InstanceId);
      }
    }

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

    // Clear queues after successful flush
    _queuedOutboxMessages.Clear();
    _queuedInboxMessages.Clear();
    _queuedOutboxCompletions.Clear();
    _queuedOutboxFailures.Clear();
    _queuedInboxCompletions.Clear();
    _queuedInboxFailures.Clear();

    // DIAGNOSTIC: Check if WorkChannelWriter is available
    if (workBatch.OutboxWork.Count > 0 && _logger != null) {
      LogWorkChannelWriterStatus(_logger, _workChannelWriter == null ? "NULL" : "AVAILABLE", workBatch.OutboxWork.Count);
    }

    // Write returned work to channel for immediate processing
    // This is the critical fix: work returned from process_work_batch should be
    // queued for processing immediately, not returned to the caller (Dispatcher)
    if (_workChannelWriter != null && workBatch.OutboxWork.Count > 0) {
      if (_logger != null) {
        LogWritingReturnedWork(_logger, workBatch.OutboxWork.Count);
      }

      foreach (var work in workBatch.OutboxWork) {
        await _workChannelWriter.WriteAsync(work, ct);
      }
    }

    return workBatch;
  }

  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }

    // Flush any remaining queued operations on disposal
    if (_queuedOutboxMessages.Count > 0 ||
        _queuedInboxMessages.Count > 0 ||
        _queuedOutboxCompletions.Count > 0 ||
        _queuedOutboxFailures.Count > 0 ||
        _queuedInboxCompletions.Count > 0 ||
        _queuedInboxFailures.Count > 0) {
      if (_logger != null) {
        LogDisposingWithUnflushedOperations(
          _logger,
          _queuedOutboxMessages.Count,
          _queuedInboxMessages.Count,
          _queuedOutboxCompletions.Count + _queuedInboxCompletions.Count,
          _queuedOutboxFailures.Count + _queuedInboxFailures.Count
        );
      }

      try {
        await FlushAsync(WorkBatchFlags.None);
      } catch (Exception ex) {
        if (_logger != null) {
          LogErrorFlushingOnDisposal(_logger, ex);
        }
      }
    }

    _disposed = true;
    GC.SuppressFinalize(this);
  }

  // LoggerMessage definitions
  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Trace,
    Message = "Queued outbox message {MessageId} for {Destination}"
  )]
  static partial void LogQueuedOutboxMessage(ILogger logger, Guid messageId, string destination);

  [LoggerMessage(
    EventId = 2,
    Level = LogLevel.Trace,
    Message = "Queued inbox message {MessageId} for handler {HandlerName}"
  )]
  static partial void LogQueuedInboxMessage(ILogger logger, Guid messageId, string handlerName);

  [LoggerMessage(
    EventId = 3,
    Level = LogLevel.Trace,
    Message = "Queued outbox completion for {MessageId} with status {Status}"
  )]
  static partial void LogQueuedOutboxCompletion(ILogger logger, Guid messageId, MessageProcessingStatus status);

  [LoggerMessage(
    EventId = 4,
    Level = LogLevel.Trace,
    Message = "Queued inbox completion for {MessageId} with status {Status}"
  )]
  static partial void LogQueuedInboxCompletion(ILogger logger, Guid messageId, MessageProcessingStatus status);

  [LoggerMessage(
    EventId = 5,
    Level = LogLevel.Trace,
    Message = "Queued outbox failure for {MessageId}: {Error}"
  )]
  static partial void LogQueuedOutboxFailure(ILogger logger, Guid messageId, string error);

  [LoggerMessage(
    EventId = 6,
    Level = LogLevel.Trace,
    Message = "Queued inbox failure for {MessageId}: {Error}"
  )]
  static partial void LogQueuedInboxFailure(ILogger logger, Guid messageId, string error);

  [LoggerMessage(
    EventId = 7,
    Level = LogLevel.Information,
    Message = "Outbox flush: Queued={Queued} | Inbox flush: Queued={InboxQueued}"
  )]
  static partial void LogFlushSummary(ILogger logger, int queued, int inboxQueued);

  [LoggerMessage(
    EventId = 8,
    Level = LogLevel.Warning,
    Message = "DIAGNOSTIC: WorkChannelWriter is {Status}, returned work count: {Count}"
  )]
  static partial void LogWorkChannelWriterStatus(ILogger logger, string status, int count);

  [LoggerMessage(
    EventId = 9,
    Level = LogLevel.Debug,
    Message = "Writing {Count} returned outbox messages to channel for immediate processing"
  )]
  static partial void LogWritingReturnedWork(ILogger logger, int count);

  [LoggerMessage(
    EventId = 10,
    Level = LogLevel.Warning,
    Message = "Scoped strategy disposing with unflushed operations: {OutboxMsg} outbox messages, {InboxMsg} inbox messages, {Completions} completions, {Failures} failures"
  )]
  static partial void LogDisposingWithUnflushedOperations(ILogger logger, int outboxMsg, int inboxMsg, int completions, int failures);

  [LoggerMessage(
    EventId = 11,
    Level = LogLevel.Error,
    Message = "Error flushing scoped strategy on disposal"
  )]
  static partial void LogErrorFlushingOnDisposal(ILogger logger, Exception ex);

  [LoggerMessage(
    EventId = 12,
    Level = LogLevel.Warning,
    Message = "DIAGNOSTIC: Flushing {Count} outbox messages with InstanceId={InstanceId}, Service={ServiceName}"
  )]
  static partial void LogFlushingWithInstanceId(ILogger logger, Guid instanceId, string serviceName, int count);

  [LoggerMessage(
    EventId = 13,
    Level = LogLevel.Warning,
    Message = "DIAGNOSTIC: ProcessWorkBatchAsync returned: Outbox={OutboxCount}, Inbox={InboxCount}, Perspective={PerspectiveCount}"
  )]
  static partial void LogProcessWorkBatchResult(ILogger logger, int outboxCount, int inboxCount, int perspectiveCount);

  [LoggerMessage(
    EventId = 14,
    Level = LogLevel.Warning,
    Message = "DIAGNOSTIC: Returned outbox work: MessageId={MessageId}, Destination={Destination}, IsNewlyStored={IsNewlyStored}"
  )]
  static partial void LogReturnedOutboxWork(ILogger logger, Guid messageId, string destination, bool isNewlyStored);

  [LoggerMessage(
    EventId = 15,
    Level = LogLevel.Error,
    Message = "CRITICAL BUG: Queued {QueuedCount} outbox messages but ProcessWorkBatchAsync returned 0! InstanceId={InstanceId}"
  )]
  static partial void LogNoWorkReturned(ILogger logger, int queuedCount, Guid instanceId);
}
