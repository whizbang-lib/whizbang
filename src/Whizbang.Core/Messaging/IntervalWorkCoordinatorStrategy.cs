using System;
using System.Collections.Generic;
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
public class IntervalWorkCoordinatorStrategy : IWorkCoordinatorStrategy, IAsyncDisposable {
  private readonly IWorkCoordinator _coordinator;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly WorkCoordinatorOptions _options;
  private readonly ILogger<IntervalWorkCoordinatorStrategy>? _logger;
  private readonly Timer _flushTimer;

  // Queues for batching operations within the interval
  private readonly List<OutboxMessage> _queuedOutboxMessages = [];
  private readonly List<InboxMessage> _queuedInboxMessages = [];
  private readonly List<MessageCompletion> _queuedOutboxCompletions = [];
  private readonly List<MessageCompletion> _queuedInboxCompletions = [];
  private readonly List<MessageFailure> _queuedOutboxFailures = [];
  private readonly List<MessageFailure> _queuedInboxFailures = [];

  private readonly object _lock = new object();
  private bool _disposed = false;
  private bool _flushing = false;

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
    ILogger<IntervalWorkCoordinatorStrategy>? logger = null
  ) {
    _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _logger = logger;

    // Start the timer for periodic flushing
    _flushTimer = new Timer(
      FlushTimerCallback,
      state: null,
      dueTime: TimeSpan.FromMilliseconds(_options.IntervalMilliseconds),
      period: TimeSpan.FromMilliseconds(_options.IntervalMilliseconds)
    );

    _logger?.LogInformation(
      "Interval work coordinator strategy started with {Interval}ms flush interval",
      _options.IntervalMilliseconds
    );
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

    _logger?.LogTrace("Queued outbox message {MessageId} for {Destination}", message.MessageId, message.Destination);
  }

  /// <summary>
  /// Queues an inbox message for batch processing.
  /// </summary>
  public void QueueInboxMessage(InboxMessage message) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    lock (_lock) {
      _queuedInboxMessages.Add(message);
    }

    _logger?.LogTrace("Queued inbox message {MessageId} for handler {HandlerName}", message.MessageId, message.HandlerName);
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

    _logger?.LogTrace("Queued outbox completion for {MessageId} with status {Status}", messageId, completedStatus);
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

    _logger?.LogTrace("Queued inbox completion for {MessageId} with status {Status}", messageId, completedStatus);
  }

  /// <summary>
  /// Queues an outbox message failure for batch processing.
  /// </summary>
  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string error) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    lock (_lock) {
      _queuedOutboxFailures.Add(new MessageFailure {
        MessageId = messageId,
        CompletedStatus = completedStatus,
        Error = error
      });
    }

    _logger?.LogTrace("Queued outbox failure for {MessageId}: {Error}", messageId, error);
  }

  /// <summary>
  /// Queues an inbox message failure for batch processing.
  /// </summary>
  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string error) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    lock (_lock) {
      _queuedInboxFailures.Add(new MessageFailure {
        MessageId = messageId,
        CompletedStatus = completedStatus,
        Error = error
      });
    }

    _logger?.LogTrace("Queued inbox failure for {MessageId}: {Error}", messageId, error);
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
        _logger?.LogWarning("Flush already in progress, returning empty batch");
        return new WorkBatch {
          OutboxWork = [],
          InboxWork = []
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
          _logger?.LogTrace("Interval flush: No queued operations");
          return new WorkBatch {
            OutboxWork = [],
            InboxWork = []
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

      _logger?.LogDebug(
        "Interval flush: {OutboxMsg} outbox messages, {InboxMsg} inbox messages, {OutboxComp} outbox completions, {OutboxFail} outbox failures, {InboxComp} inbox completions, {InboxFail} inbox failures",
        outboxMessages.Length,
        inboxMessages.Length,
        outboxCompletions.Length,
        outboxFailures.Length,
        inboxCompletions.Length,
        inboxFailures.Length
      );

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
        maxPartitionsPerInstance: _options.MaxPartitionsPerInstance,
        leaseSeconds: _options.LeaseSeconds,
        staleThresholdSeconds: _options.StaleThresholdSeconds,
        cancellationToken: ct
      );

      _logger?.LogInformation(
        "Interval flush completed: {OutboxWork} outbox work, {InboxWork} inbox work returned",
        workBatch.OutboxWork.Count,
        workBatch.InboxWork.Count
      );

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
  private void FlushTimerCallback(object? state) {
    if (_disposed) {
      return;
    }

    // Fire and forget flush on timer
    _ = Task.Run(async () => {
      try {
        await FlushAsync(WorkBatchFlags.None);
      } catch (Exception ex) {
        _logger?.LogError(ex, "Error during interval flush");
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

    _logger?.LogInformation("Interval work coordinator strategy disposing");

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
        _logger?.LogWarning(
          "Interval strategy disposing with unflushed operations: {OutboxMsg} outbox messages, {InboxMsg} inbox messages, {Completions} completions, {Failures} failures",
          _queuedOutboxMessages.Count,
          _queuedInboxMessages.Count,
          _queuedOutboxCompletions.Count + _queuedInboxCompletions.Count,
          _queuedOutboxFailures.Count + _queuedInboxFailures.Count
        );
      }
    }

    try {
      await FlushAsync(WorkBatchFlags.None);
    } catch (Exception ex) {
      _logger?.LogError(ex, "Error flushing interval strategy on disposal");
    }

    _disposed = true;
    _logger?.LogInformation("Interval work coordinator strategy disposed");
  }
}
