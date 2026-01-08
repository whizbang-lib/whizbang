using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:BackgroundTimer_FlushesEveryIntervalAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:QueuedMessages_BatchedUntilTimerAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:DisposeAsync_FlushesAndStopsTimerAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:ManualFlushAsync_DoesNotWaitForTimerAsync</tests>
/// Interval strategy - batches operations and flushes on a timer.
/// Provides lowest database load with higher latency.
/// Best for: Background workers with high throughput, batch processing.
/// </summary>
public partial class IntervalWorkCoordinatorStrategy : IWorkCoordinatorStrategy, IAsyncDisposable {
  private readonly IWorkCoordinator _coordinator;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly WorkCoordinatorOptions _options;
  private readonly ILogger<IntervalWorkCoordinatorStrategy>? _logger;
  private readonly ILifecycleInvoker? _lifecycleInvoker;
  private readonly ILifecycleMessageDeserializer? _lifecycleMessageDeserializer;
  private readonly Timer _flushTimer;

  // Queues for batching operations within the interval
  private readonly List<OutboxMessage> _queuedOutboxMessages = [];
  private readonly List<InboxMessage> _queuedInboxMessages = [];
  private readonly List<MessageCompletion> _queuedOutboxCompletions = [];
  private readonly List<MessageCompletion> _queuedInboxCompletions = [];
  private readonly List<MessageFailure> _queuedOutboxFailures = [];
  private readonly List<MessageFailure> _queuedInboxFailures = [];

  private readonly object _lock = new object();
  private bool _disposed;
  private bool _flushing;

  /// <summary>
  /// Constructs an interval-based work coordinator strategy with periodic flushing.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:BackgroundTimer_FlushesEveryIntervalAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:QueuedMessages_BatchedUntilTimerAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:DisposeAsync_FlushesAndStopsTimerAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:ManualFlushAsync_DoesNotWaitForTimerAsync</tests>
  public IntervalWorkCoordinatorStrategy(
    IWorkCoordinator coordinator,
    IServiceInstanceProvider instanceProvider,
    WorkCoordinatorOptions options,
    ILogger<IntervalWorkCoordinatorStrategy>? logger = null,
    ILifecycleInvoker? lifecycleInvoker = null,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer = null
  ) {
    _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _logger = logger;
    _lifecycleInvoker = lifecycleInvoker;
    _lifecycleMessageDeserializer = lifecycleMessageDeserializer;

    // Start the timer for periodic flushing
    _flushTimer = new Timer(
      _flushTimerCallback,
      state: null,
      dueTime: TimeSpan.FromMilliseconds(_options.IntervalMilliseconds),
      period: TimeSpan.FromMilliseconds(_options.IntervalMilliseconds)
    );

    if (_logger != null) {
      LogStrategyStarted(_logger, _options.IntervalMilliseconds);
    }
  }

  /// <summary>
  /// Queues an outbox message for batch processing.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:BackgroundTimer_FlushesEveryIntervalAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:QueuedMessages_BatchedUntilTimerAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:DisposeAsync_FlushesAndStopsTimerAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:ManualFlushAsync_DoesNotWaitForTimerAsync</tests>
  public void QueueOutboxMessage(OutboxMessage message) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    lock (_lock) {
      _queuedOutboxMessages.Add(message);
    }

    if (_logger != null) {
      LogQueuedOutboxMessage(_logger, message.MessageId, message.Destination);
    }
  }

  /// <summary>
  /// Queues an inbox message for batch processing.
  /// </summary>
  public void QueueInboxMessage(InboxMessage message) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    lock (_lock) {
      _queuedInboxMessages.Add(message);
    }

    if (_logger != null) {
      LogQueuedInboxMessage(_logger, message.MessageId, message.HandlerName);
    }
  }

  /// <summary>
  /// Queues an outbox message completion for batch processing.
  /// </summary>
  public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    lock (_lock) {
      _queuedOutboxCompletions.Add(new MessageCompletion {
        MessageId = messageId,
        Status = completedStatus
      });
    }

    if (_logger != null) {
      LogQueuedOutboxCompletion(_logger, messageId, completedStatus);
    }
  }

  /// <summary>
  /// Queues an inbox message completion for batch processing.
  /// </summary>
  public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    lock (_lock) {
      _queuedInboxCompletions.Add(new MessageCompletion {
        MessageId = messageId,
        Status = completedStatus
      });
    }

    if (_logger != null) {
      LogQueuedInboxCompletion(_logger, messageId, completedStatus);
    }
  }

  /// <summary>
  /// Queues an outbox message failure for batch processing.
  /// </summary>
  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    lock (_lock) {
      _queuedOutboxFailures.Add(new MessageFailure {
        MessageId = messageId,
        CompletedStatus = completedStatus,
        Error = errorMessage
      });
    }

    if (_logger != null) {
      LogQueuedOutboxFailure(_logger, messageId, errorMessage);
    }
  }

  /// <summary>
  /// Queues an inbox message failure for batch processing.
  /// </summary>
  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    lock (_lock) {
      _queuedInboxFailures.Add(new MessageFailure {
        MessageId = messageId,
        CompletedStatus = completedStatus,
        Error = errorMessage
      });
    }

    if (_logger != null) {
      LogQueuedInboxFailure(_logger, messageId, errorMessage);
    }
  }

  /// <summary>
  /// Flushes all queued operations to the work coordinator immediately.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:ManualFlushAsync_DoesNotWaitForTimerAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:DisposeAsync_FlushesAndStopsTimerAsync</tests>
  public async Task<WorkBatch> FlushAsync(WorkBatchFlags flags, CancellationToken ct = default) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    // Prevent concurrent flushes
    lock (_lock) {
      if (_flushing) {
        if (_logger != null) {
          LogFlushAlreadyInProgress(_logger);
        }
        return new WorkBatch {
          OutboxWork = [],
          InboxWork = [],
          PerspectiveWork = []
        };
      }
      _flushing = true;
    }

    try {
      // Snapshot current queues under lock
      OutboxMessage[] outboxMessages;
      InboxMessage[] inboxMessages;
      MessageCompletion[] outboxCompletions;
      MessageCompletion[] inboxCompletions;
      MessageFailure[] outboxFailures;
      MessageFailure[] inboxFailures;

      lock (_lock) {
        if (_queuedOutboxMessages.Count == 0 &&
            _queuedInboxMessages.Count == 0 &&
            _queuedOutboxCompletions.Count == 0 &&
            _queuedOutboxFailures.Count == 0 &&
            _queuedInboxCompletions.Count == 0 &&
            _queuedInboxFailures.Count == 0) {
          if (_logger != null) {
            LogNoQueuedOperations(_logger);
          }
          return new WorkBatch {
            OutboxWork = [],
            InboxWork = [],
            PerspectiveWork = []
          };
        }

        // Snapshot and clear queues
        outboxMessages = [.. _queuedOutboxMessages];
        inboxMessages = [.. _queuedInboxMessages];
        outboxCompletions = [.. _queuedOutboxCompletions];
        inboxCompletions = [.. _queuedInboxCompletions];
        outboxFailures = [.. _queuedOutboxFailures];
        inboxFailures = [.. _queuedInboxFailures];

        _queuedOutboxMessages.Clear();
        _queuedInboxMessages.Clear();
        _queuedOutboxCompletions.Clear();
        _queuedOutboxFailures.Clear();
        _queuedInboxCompletions.Clear();
        _queuedInboxFailures.Clear();
      }

      if (_logger != null) {
        LogIntervalFlush(_logger, outboxMessages.Length, inboxMessages.Length, outboxCompletions.Length, outboxFailures.Length, inboxCompletions.Length, inboxFailures.Length);
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
        foreach (var outboxMsg in outboxMessages) {
          var message = _lifecycleMessageDeserializer.DeserializeFromEnvelope(outboxMsg.Envelope, outboxMsg.EnvelopeType);
          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PreDistributeAsync, lifecycleContext, ct);
        }

        foreach (var inboxMsg in inboxMessages) {
          var message = _lifecycleMessageDeserializer.DeserializeFromEnvelope(inboxMsg.Envelope, inboxMsg.EnvelopeType);
          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PreDistributeAsync, lifecycleContext, ct);
        }

        // Invoke PreDistributeInline for all messages
        lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PreDistributeInline };
        foreach (var outboxMsg in outboxMessages) {
          var message = _lifecycleMessageDeserializer.DeserializeFromEnvelope(outboxMsg.Envelope, outboxMsg.EnvelopeType);
          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PreDistributeInline, lifecycleContext, ct);
        }

        foreach (var inboxMsg in inboxMessages) {
          var message = _lifecycleMessageDeserializer.DeserializeFromEnvelope(inboxMsg.Envelope, inboxMsg.EnvelopeType);
          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PreDistributeInline, lifecycleContext, ct);
        }
      }

      // Call process_work_batch with snapshot
      var workBatch = await _coordinator.ProcessWorkBatchAsync(
        _instanceProvider.InstanceId,
        _instanceProvider.ServiceName,
        _instanceProvider.HostName,
        _instanceProvider.ProcessId,
        metadata: null,
        outboxCompletions: outboxCompletions,
        outboxFailures: outboxFailures,
        inboxCompletions: inboxCompletions,
        inboxFailures: inboxFailures,
        receptorCompletions: [],  // TODO: Add receptor processing support
        receptorFailures: [],
        perspectiveCompletions: [],  // TODO: Add perspective checkpoint support
        perspectiveFailures: [],
        newOutboxMessages: outboxMessages,
        newInboxMessages: inboxMessages,
        renewOutboxLeaseIds: [],
        renewInboxLeaseIds: [],
        flags: flags | (_options.DebugMode ? WorkBatchFlags.DebugMode : WorkBatchFlags.None),
        partitionCount: _options.PartitionCount,
        leaseSeconds: _options.LeaseSeconds,
        staleThresholdSeconds: _options.StaleThresholdSeconds,
        cancellationToken: ct
      );

      if (_logger != null) {
        LogIntervalFlushCompleted(_logger, workBatch.OutboxWork.Count, workBatch.InboxWork.Count);
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
        foreach (var outboxMsg in outboxMessages) {
          var message = _lifecycleMessageDeserializer.DeserializeFromEnvelope(outboxMsg.Envelope, outboxMsg.EnvelopeType);
          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PostDistributeAsync, lifecycleContext, ct);
        }

        foreach (var inboxMsg in inboxMessages) {
          var message = _lifecycleMessageDeserializer.DeserializeFromEnvelope(inboxMsg.Envelope, inboxMsg.EnvelopeType);
          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PostDistributeAsync, lifecycleContext, ct);
        }

        // Invoke PostDistributeInline for all messages
        lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PostDistributeInline };
        foreach (var outboxMsg in outboxMessages) {
          var message = _lifecycleMessageDeserializer.DeserializeFromEnvelope(outboxMsg.Envelope, outboxMsg.EnvelopeType);
          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PostDistributeInline, lifecycleContext, ct);
        }

        foreach (var inboxMsg in inboxMessages) {
          var message = _lifecycleMessageDeserializer.DeserializeFromEnvelope(inboxMsg.Envelope, inboxMsg.EnvelopeType);
          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PostDistributeInline, lifecycleContext, ct);
        }
      }

      return workBatch;
    } finally {
      lock (_lock) {
        _flushing = false;
      }
    }
  }

  /// <summary>
  /// Timer callback that triggers periodic flushing of queued operations.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:BackgroundTimer_FlushesEveryIntervalAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:QueuedMessages_BatchedUntilTimerAsync</tests>
  private void _flushTimerCallback(object? state) {
    if (_disposed) {
      return;
    }

    // Fire and forget flush on timer
    _ = Task.Run(async () => {
      try {
        await FlushAsync(WorkBatchFlags.None);
      } catch (Exception ex) {
        if (_logger != null) {
          LogErrorDuringIntervalFlush(_logger, ex);
        }
      }
    });
  }

  /// <summary>
  /// Disposes the strategy, stops the timer, and flushes any remaining queued operations.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:DisposeAsync_FlushesAndStopsTimerAsync</tests>
  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }

    if (_logger != null) {
      LogStrategyDisposing(_logger);
    }

    // Stop the timer first
    await _flushTimer.DisposeAsync();

    // Flush any remaining queued operations
    lock (_lock) {
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
      }
    }

    try {
      await FlushAsync(WorkBatchFlags.None);
    } catch (Exception ex) {
      if (_logger != null) {
        LogErrorFlushingOnDisposal(_logger, ex);
      }
    }

    _disposed = true;
    GC.SuppressFinalize(this);

    if (_logger != null) {
      LogStrategyDisposed(_logger);
    }
  }

  // LoggerMessage definitions
  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Information,
    Message = "Interval work coordinator strategy started with {Interval}ms flush interval"
  )]
  static partial void LogStrategyStarted(ILogger logger, int interval);

  [LoggerMessage(
    EventId = 2,
    Level = LogLevel.Trace,
    Message = "Queued outbox message {MessageId} for {Destination}"
  )]
  static partial void LogQueuedOutboxMessage(ILogger logger, Guid messageId, string destination);

  [LoggerMessage(
    EventId = 3,
    Level = LogLevel.Trace,
    Message = "Queued inbox message {MessageId} for handler {HandlerName}"
  )]
  static partial void LogQueuedInboxMessage(ILogger logger, Guid messageId, string handlerName);

  [LoggerMessage(
    EventId = 4,
    Level = LogLevel.Trace,
    Message = "Queued outbox completion for {MessageId} with status {Status}"
  )]
  static partial void LogQueuedOutboxCompletion(ILogger logger, Guid messageId, MessageProcessingStatus status);

  [LoggerMessage(
    EventId = 5,
    Level = LogLevel.Trace,
    Message = "Queued inbox completion for {MessageId} with status {Status}"
  )]
  static partial void LogQueuedInboxCompletion(ILogger logger, Guid messageId, MessageProcessingStatus status);

  [LoggerMessage(
    EventId = 6,
    Level = LogLevel.Trace,
    Message = "Queued outbox failure for {MessageId}: {Error}"
  )]
  static partial void LogQueuedOutboxFailure(ILogger logger, Guid messageId, string error);

  [LoggerMessage(
    EventId = 7,
    Level = LogLevel.Trace,
    Message = "Queued inbox failure for {MessageId}: {Error}"
  )]
  static partial void LogQueuedInboxFailure(ILogger logger, Guid messageId, string error);

  [LoggerMessage(
    EventId = 8,
    Level = LogLevel.Warning,
    Message = "Flush already in progress, returning empty batch"
  )]
  static partial void LogFlushAlreadyInProgress(ILogger logger);

  [LoggerMessage(
    EventId = 9,
    Level = LogLevel.Trace,
    Message = "Interval flush: No queued operations"
  )]
  static partial void LogNoQueuedOperations(ILogger logger);

  [LoggerMessage(
    EventId = 10,
    Level = LogLevel.Debug,
    Message = "Interval flush: {OutboxMsg} outbox messages, {InboxMsg} inbox messages, {OutboxComp} outbox completions, {OutboxFail} outbox failures, {InboxComp} inbox completions, {InboxFail} inbox failures"
  )]
  static partial void LogIntervalFlush(ILogger logger, int outboxMsg, int inboxMsg, int outboxComp, int outboxFail, int inboxComp, int inboxFail);

  [LoggerMessage(
    EventId = 11,
    Level = LogLevel.Information,
    Message = "Interval flush completed: {OutboxWork} outbox work, {InboxWork} inbox work returned"
  )]
  static partial void LogIntervalFlushCompleted(ILogger logger, int outboxWork, int inboxWork);

  [LoggerMessage(
    EventId = 12,
    Level = LogLevel.Error,
    Message = "Error during interval flush"
  )]
  static partial void LogErrorDuringIntervalFlush(ILogger logger, Exception ex);

  [LoggerMessage(
    EventId = 13,
    Level = LogLevel.Information,
    Message = "Interval work coordinator strategy disposing"
  )]
  static partial void LogStrategyDisposing(ILogger logger);

  [LoggerMessage(
    EventId = 14,
    Level = LogLevel.Warning,
    Message = "Interval strategy disposing with unflushed operations: {OutboxMsg} outbox messages, {InboxMsg} inbox messages, {Completions} completions, {Failures} failures"
  )]
  static partial void LogDisposingWithUnflushedOperations(ILogger logger, int outboxMsg, int inboxMsg, int completions, int failures);

  [LoggerMessage(
    EventId = 15,
    Level = LogLevel.Error,
    Message = "Error flushing interval strategy on disposal"
  )]
  static partial void LogErrorFlushingOnDisposal(ILogger logger, Exception ex);

  [LoggerMessage(
    EventId = 16,
    Level = LogLevel.Information,
    Message = "Interval work coordinator strategy disposed"
  )]
  static partial void LogStrategyDisposed(ILogger logger);
}
