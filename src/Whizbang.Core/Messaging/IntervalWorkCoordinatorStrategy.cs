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
  /// Fire-and-forget flush for Interval strategy: items stay queued and are flushed on the next
  /// timer tick. Use for cascade-to-outbox and routed publish/send paths that do not consume
  /// the WorkBatch.
  /// </summary>
  /// <docs>data/work-coordinator-strategies</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/FlushApiTests.cs:Interval_FlushAsync_WithQueuedMessages_DefersToTimer_NoDbCallAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCascadeFlushTests.cs</tests>
  public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    _metrics?.FlushCalls.Add(1,
      new KeyValuePair<string, object?>("strategy", "interval"),
      new KeyValuePair<string, object?>("trigger", "signal"));
    // Interval batches until the timer fires. Nothing to do here beyond the metric.
    return Task.CompletedTask;
  }

  /// <summary>
  /// Forces an immediate flush and returns the resulting WorkBatch. Bypasses the interval timer.
  /// Use for dedup callers that must consume the WorkBatch, or for end-of-scope drains.
  /// </summary>
  /// <docs>data/work-coordinator-strategies</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/FlushApiTests.cs:Interval_FlushAndGetBatchAsync_WithQueuedMessages_FlushesImmediately_BypassesTimerAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:ManualFlushAsync_DoesNotWaitForTimerAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:DisposeAsync_FlushesAndStopsTimerAsync</tests>
  public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) {
    // IntervalWorkCoordinatorStrategy handles outbox work only — skip inbox claiming
    // to prevent stealing inbox messages from WorkCoordinatorPublisherWorker
    return _flushCoreAsync(flags | WorkBatchOptions.SkipInboxClaiming, trigger: "api", skipLifecycle: false, ct);
  }

  private async Task<WorkBatch> _flushCoreAsync(WorkBatchOptions flags, string trigger, bool skipLifecycle, CancellationToken ct) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    _metrics?.FlushCalls.Add(1,
      new KeyValuePair<string, object?>("strategy", "interval"),
      new KeyValuePair<string, object?>("trigger", trigger));

    // Forced-flush with optional coalescing window
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
      if (!_trySnapshotAndClearQueues(out var snapshot)) {
        return new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] };
      }

      if (_logger != null) {
        LogIntervalFlush(_logger,
          snapshot.OutboxMessages.Length, snapshot.InboxMessages.Length,
          snapshot.OutboxCompletions.Length, snapshot.OutboxFailures.Length,
          snapshot.InboxCompletions.Length, snapshot.InboxFailures.Length);
      }

      var workBatch = await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
        new FlushContext(
          _coordinator, _scopeFactory, _instanceProvider, _options, "interval",
          snapshot.OutboxMessages, snapshot.InboxMessages,
          snapshot.OutboxCompletions, snapshot.InboxCompletions,
          snapshot.OutboxFailures, snapshot.InboxFailures,
          flags, _lifecycleMessageDeserializer,
          _logger, _tracingOptions, _metrics, _lifecycleMetrics,
          WorkChannelWriter: _workChannelWriter, PendingAuditMessages: null,
          SkipLifecycle: skipLifecycle),
        ct
      );

      if (_logger != null) {
        LogIntervalFlushCompleted(_logger, workBatch.OutboxWork.Count, workBatch.InboxWork.Count);
      }

      _routeClaimedInboxWorkToChannel(workBatch);
      return workBatch;
    } finally {
      lock (_lock) {
        _flushing = false;
      }
    }
  }

  /// <summary>
  /// Takes one lock-protected snapshot of the six queues and clears them in-place. Returns
  /// false (with default snapshot) when every queue is empty so the caller can skip the flush
  /// pipeline and log the "no queued operations" branch. Keeps the `return new WorkBatch{...}`
  /// shortcut inside a single lock acquisition.
  /// </summary>
  private bool _trySnapshotAndClearQueues(out QueueSnapshot snapshot) {
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
        snapshot = default;
        return false;
      }

      snapshot = new QueueSnapshot(
        [.. _queuedOutboxMessages],
        [.. _queuedInboxMessages],
        [.. _queuedOutboxCompletions],
        [.. _queuedInboxCompletions],
        [.. _queuedOutboxFailures],
        [.. _queuedInboxFailures]);

      _queuedOutboxMessages.Clear();
      _queuedInboxMessages.Clear();
      _queuedOutboxCompletions.Clear();
      _queuedOutboxFailures.Clear();
      _queuedInboxCompletions.Clear();
      _queuedInboxFailures.Clear();
      return true;
    }
  }

  /// <summary>Routes claimed inbox work to the publisher worker via the in-memory channel,
  /// deduplicating by IsInFlight. No-op when no channel writer is configured or the batch has
  /// no inbox rows.</summary>
  private void _routeClaimedInboxWorkToChannel(WorkBatch workBatch) {
    if (_inboxChannelWriter is null || workBatch.InboxWork.Count == 0) {
      return;
    }
    // S3267: Loop body has side effects (channel writer mutation) — LINQ not appropriate
#pragma warning disable S3267
    foreach (var inboxWork in workBatch.InboxWork) {
      if (!_inboxChannelWriter.IsInFlight(inboxWork.MessageId)) {
        _inboxChannelWriter.TryWrite(inboxWork);
      }
    }
#pragma warning restore S3267
  }

  /// <summary>Lock-free snapshot of every queue taken at flush time so
  /// <see cref="WorkCoordinatorFlushHelper.ExecuteFlushAsync"/> can run outside the lock.</summary>
  private readonly record struct QueueSnapshot(
    OutboxMessage[] OutboxMessages,
    InboxMessage[] InboxMessages,
    MessageCompletion[] OutboxCompletions,
    MessageCompletion[] InboxCompletions,
    MessageFailure[] OutboxFailures,
    MessageFailure[] InboxFailures);

  /// <inheritdoc />
  Task IWorkFlusher.FlushAsync(CancellationToken ct) =>
    FlushAndGetBatchAsync(WorkBatchOptions.SkipInboxClaiming, ct);

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
        await _flushCoreAsync(WorkBatchOptions.SkipInboxClaiming, trigger: "timer", skipLifecycle: true, ct: default);
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
      await _flushCoreAsync(WorkBatchOptions.SkipInboxClaiming, trigger: "disposal", skipLifecycle: true, ct: default);
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
