using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;
using Whizbang.Core.Tracing;
using Whizbang.Core.Validation;

namespace Whizbang.Core.Messaging;

/// <summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:BackgroundTimer_FlushesEveryIntervalAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:QueuedMessages_BatchedUntilTimerAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:DisposeAsync_FlushesAndStopsTimerAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:ManualFlushAsync_DoesNotWaitForTimerAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/WorkFlusherTests.cs:IntervalStrategy_FlushAsync_DelegatesToStrategyWithRequiredModeAsync</tests>
/// Interval strategy - batches operations and flushes on a timer.
/// Provides lowest database load with higher latency.
/// Best for: Background workers with high throughput, batch processing.
/// </summary>
public partial class IntervalWorkCoordinatorStrategy : IWorkCoordinatorStrategy, IWorkFlusher, IAsyncDisposable {
  private readonly IWorkCoordinator? _coordinator;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly WorkCoordinatorOptions _options;
  private readonly ILogger<IntervalWorkCoordinatorStrategy>? _logger;
  private readonly IServiceScopeFactory? _scopeFactory;
  private readonly ILifecycleMessageDeserializer? _lifecycleMessageDeserializer;
  private readonly IOptionsMonitor<TracingOptions>? _tracingOptions;
  private readonly IWorkChannelWriter? _workChannelWriter;
  private readonly IInboxChannelWriter? _inboxChannelWriter;
  private readonly WorkCoordinatorMetrics? _metrics;
  private readonly LifecycleMetrics? _lifecycleMetrics;
  private readonly Timer _flushTimer;

  // Queues for batching operations within the interval
  private readonly List<OutboxMessage> _queuedOutboxMessages = [];
  private readonly List<InboxMessage> _queuedInboxMessages = [];
  private readonly List<MessageCompletion> _queuedOutboxCompletions = [];
  private readonly List<MessageCompletion> _queuedInboxCompletions = [];
  private readonly List<MessageFailure> _queuedOutboxFailures = [];
  private readonly List<MessageFailure> _queuedInboxFailures = [];

  private readonly Lock _lock = new();
  private bool _disposed;
  private bool _flushing;

  /// <summary>
  /// Constructs an interval-based work coordinator strategy with periodic flushing.
  /// Pass <paramref name="coordinator"/> directly for scoped usage (one strategy per scope).
  /// For singleton usage, pass <c>null</c> for <paramref name="coordinator"/> and provide
  /// <paramref name="scopeFactory"/> — a new scope is created per flush to resolve IWorkCoordinator.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:BackgroundTimer_FlushesEveryIntervalAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:QueuedMessages_BatchedUntilTimerAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:DisposeAsync_FlushesAndStopsTimerAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:ManualFlushAsync_DoesNotWaitForTimerAsync</tests>
#pragma warning disable S107 // Constructor uses DI injection — many parameters are idiomatic
  public IntervalWorkCoordinatorStrategy(
    IWorkCoordinator? coordinator,
    IServiceInstanceProvider instanceProvider,
    WorkCoordinatorOptions options,
    ILogger<IntervalWorkCoordinatorStrategy>? logger = null,
    IServiceScopeFactory? scopeFactory = null,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer = null,
    IOptionsMonitor<TracingOptions>? tracingOptions = null,
    WorkCoordinatorMetrics? metrics = null,
    LifecycleMetrics? lifecycleMetrics = null,
    IWorkChannelWriter? workChannelWriter = null,
    IInboxChannelWriter? inboxChannelWriter = null
  ) {
#pragma warning restore S107
    if (coordinator == null && scopeFactory == null) {
      throw new ArgumentNullException(nameof(coordinator), "Either coordinator or scopeFactory must be provided.");
    }
    _coordinator = coordinator;
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _logger = logger;
    _scopeFactory = scopeFactory;
    _lifecycleMessageDeserializer = lifecycleMessageDeserializer;
    _tracingOptions = tracingOptions;
    _workChannelWriter = workChannelWriter;
    _inboxChannelWriter = inboxChannelWriter;
    _metrics = metrics;
    _lifecycleMetrics = lifecycleMetrics;

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
    StreamIdGuard.ThrowIfNonNullEmpty(message.StreamId, message.MessageId, "IntervalStrategy.QueueOutbox", message.MessageType);

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
    StreamIdGuard.ThrowIfNonNullEmpty(message.StreamId, message.MessageId, "IntervalStrategy.QueueInbox", message.MessageType);

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
  public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
    // IntervalWorkCoordinatorStrategy handles outbox work only — skip inbox claiming
    // to prevent stealing inbox messages from WorkCoordinatorPublisherWorker
    return _flushCoreAsync(flags | WorkBatchOptions.SkipInboxClaiming, mode, skipLifecycle: false, ct);
  }

  private async Task<WorkBatch> _flushCoreAsync(WorkBatchOptions flags, FlushMode mode, bool skipLifecycle, CancellationToken ct) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    _metrics?.FlushCalls.Add(1, new KeyValuePair<string, object?>("strategy", "interval"), new KeyValuePair<string, object?>("flush_mode", mode.ToString()));

    // BestEffort: defer flush to timer cycle - items already in queues will be picked up
    if (mode == FlushMode.BestEffort) {
      return new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      };
    }

    // Required flush with optional coalescing window
    if (_options.CoalesceWindowMilliseconds > 0) {
      await Task.Delay(_options.CoalesceWindowMilliseconds, ct);
    }

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
          _metrics?.EmptyFlushCalls.Add(1, new KeyValuePair<string, object?>("strategy", "interval"));
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

      var workBatch = await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
        new FlushContext(
          _coordinator, _scopeFactory, _instanceProvider, _options, "interval",
          outboxMessages, inboxMessages, outboxCompletions, inboxCompletions,
          outboxFailures, inboxFailures, flags, _lifecycleMessageDeserializer,
          _logger, _tracingOptions, _metrics, _lifecycleMetrics,
          WorkChannelWriter: _workChannelWriter, PendingAuditMessages: null,
          SkipLifecycle: skipLifecycle),
        ct
      );

      if (_logger != null) {
        LogIntervalFlushCompleted(_logger, workBatch.OutboxWork.Count, workBatch.InboxWork.Count);
      }

      // Route claimed inbox work to publisher worker via channel (dedup by IsInFlight)
      if (_inboxChannelWriter is not null && workBatch.InboxWork.Count > 0) {
        foreach (var inboxWork in workBatch.InboxWork) {
          if (!_inboxChannelWriter.IsInFlight(inboxWork.MessageId)) {
            _inboxChannelWriter.TryWrite(inboxWork);
          }
        }
      }

      return workBatch;
    } finally {
      lock (_lock) {
        _flushing = false;
      }
    }
  }

  /// <inheritdoc />
  Task IWorkFlusher.FlushAsync(CancellationToken ct) =>
    FlushAsync(WorkBatchOptions.SkipInboxClaiming, FlushMode.Required, ct);

  /// <summary>
  /// Timer callback that triggers periodic flushing of queued operations.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:BackgroundTimer_FlushesEveryIntervalAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:QueuedMessages_BatchedUntilTimerAsync</tests>
  private void _flushTimerCallback(object? state) {
    if (_disposed) {
      return;
    }

    // Fire and forget flush on timer — skip lifecycle (background thread, no ambient context)
    _ = Task.Run(async () => {
      try {
        await _flushCoreAsync(WorkBatchOptions.SkipInboxClaiming, FlushMode.Required, skipLifecycle: true, ct: default);
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
      if (_logger != null &&
          (_queuedOutboxMessages.Count > 0 ||
          _queuedInboxMessages.Count > 0 ||
          _queuedOutboxCompletions.Count > 0 ||
          _queuedOutboxFailures.Count > 0 ||
          _queuedInboxCompletions.Count > 0 ||
          _queuedInboxFailures.Count > 0)) {
        LogDisposingWithUnflushedOperations(
          _logger,
          _queuedOutboxMessages.Count,
          _queuedInboxMessages.Count,
          _queuedOutboxCompletions.Count + _queuedInboxCompletions.Count,
          _queuedOutboxFailures.Count + _queuedInboxFailures.Count
        );
      }
    }

    try {
      await _flushCoreAsync(WorkBatchOptions.SkipInboxClaiming, FlushMode.Required, skipLifecycle: true, ct: default);
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
  static partial void LogQueuedOutboxMessage(ILogger logger, Guid messageId, string? destination);

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
    Level = LogLevel.Debug,
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
