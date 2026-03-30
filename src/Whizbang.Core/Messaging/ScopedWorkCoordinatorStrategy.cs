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
using Whizbang.Core.Tracing;
using Whizbang.Core.Validation;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Groups optional lifecycle and tracing dependencies for <see cref="ScopedWorkCoordinatorStrategy"/>.
/// </summary>
public record ScopedWorkCoordinatorDependencies {
  /// <summary>Service scope factory for resolving lifecycle invokers.</summary>
  public IServiceScopeFactory? ScopeFactory { get; init; }
  /// <summary>Deserializer for lifecycle messages.</summary>
  public ILifecycleMessageDeserializer? LifecycleMessageDeserializer { get; init; }
  /// <summary>Tracing options monitor for controlling span emission.</summary>
  public IOptionsMonitor<TracingOptions>? TracingOptions { get; init; }
  /// <summary>System event options for audit event generation.</summary>
  public SystemEvents.SystemEventOptions? SystemEventOptions { get; init; }
}

/// <summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyTests.cs:DisposeAsync_FlushesQueuedMessagesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyTests.cs:FlushAsync_BeforeDisposal_FlushesImmediatelyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyTests.cs:MultipleQueues_FlushedTogetherOnDisposalAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/WorkFlusherTests.cs:ScopedStrategy_FlushAsync_DelegatesToStrategyWithRequiredModeAsync</tests>
/// Scoped strategy - batches operations within a scope (e.g., HTTP request, message handler).
/// Flushes on scope disposal (IAsyncDisposable pattern).
/// Provides a good balance of latency and efficiency.
/// Best for: Web APIs, message handlers, transactional operations.
/// </summary>
/// <remarks>
/// Initializes a new instance of <see cref="ScopedWorkCoordinatorStrategy"/>.
/// </remarks>
#pragma warning disable S107 // Constructor uses DI injection — many parameters are idiomatic
public partial class ScopedWorkCoordinatorStrategy(
  IWorkCoordinator coordinator,
  IServiceInstanceProvider instanceProvider,
  IWorkChannelWriter? workChannelWriter,
  WorkCoordinatorOptions options,
  ILogger<ScopedWorkCoordinatorStrategy>? logger = null,
  ScopedWorkCoordinatorDependencies? dependencies = null,
  WorkCoordinatorMetrics? metrics = null,
  LifecycleMetrics? lifecycleMetrics = null
  ) : IWorkCoordinatorStrategy, IWorkFlusher, IAsyncDisposable {
#pragma warning restore S107
  private const string STRATEGY_NAME = "scoped";

  private readonly IWorkCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly IWorkChannelWriter? _workChannelWriter = workChannelWriter;
  private readonly WorkCoordinatorOptions _options = options ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<ScopedWorkCoordinatorStrategy>? _logger = logger;
  private readonly ScopedWorkCoordinatorDependencies _dependencies = dependencies ?? new ScopedWorkCoordinatorDependencies();
  private readonly WorkCoordinatorMetrics? _metrics = metrics;
  private readonly LifecycleMetrics? _lifecycleMetrics = lifecycleMetrics;
  private readonly WorkCoordinatorQueues _queues = new();

  private bool _disposed;

  /// <inheritdoc />
  public void QueueOutboxMessage(OutboxMessage message) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    StreamIdGuard.ThrowIfNonNullEmpty(message.StreamId, message.MessageId, "ScopedStrategy.QueueOutbox", message.MessageType);

    _queues.AddOutboxMessage(message, _dependencies.SystemEventOptions);
    if (_logger != null) {
      LogQueuedOutboxMessage(_logger, message.MessageId, message.Destination);
    }
  }

  /// <inheritdoc />
  public void QueueInboxMessage(InboxMessage message) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    StreamIdGuard.ThrowIfNonNullEmpty(message.StreamId, message.MessageId, "ScopedStrategy.QueueInbox", message.MessageType);

    _queues.AddInboxMessage(message);
    if (_logger != null) {
      LogQueuedInboxMessage(_logger, message.MessageId, message.HandlerName);
    }
  }

  /// <inheritdoc />
  public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queues.AddOutboxCompletion(messageId, completedStatus);
    if (_logger != null) {
      LogQueuedOutboxCompletion(_logger, messageId, completedStatus);
    }
  }

  /// <inheritdoc />
  public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queues.AddInboxCompletion(messageId, completedStatus);
    if (_logger != null) {
      LogQueuedInboxCompletion(_logger, messageId, completedStatus);
    }
  }

  /// <inheritdoc />
  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queues.AddOutboxFailure(messageId, completedStatus, errorMessage);
    if (_logger != null) {
      LogQueuedOutboxFailure(_logger, messageId, errorMessage);
    }
  }

  /// <inheritdoc />
  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queues.AddInboxFailure(messageId, completedStatus, errorMessage);
    if (_logger != null) {
      LogQueuedInboxFailure(_logger, messageId, errorMessage);
    }
  }

  /// <inheritdoc />
  public async Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    _metrics?.FlushCalls.Add(1, new KeyValuePair<string, object?>("strategy", STRATEGY_NAME), new KeyValuePair<string, object?>("flush_mode", mode.ToString()));

    // BestEffort on Scoped strategy: flush immediately anyway.
    // The scope IS the batching boundary — deferring to DisposeAsync is unreliable
    // because the DbContext may already be disposed by the DI container before
    // our DisposeAsync runs (DI disposal order is not guaranteed).

    if (_queues.IsEmpty) {
      _metrics?.EmptyFlushCalls.Add(1, new KeyValuePair<string, object?>("strategy", STRATEGY_NAME));
      return new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      };
    }

    // Log a summary of what's being flushed to the database
    if (_logger != null) {
      LogFlushSummary(_logger, _queues.OutboxMessages.Count, _queues.InboxMessages.Count);
      LogFlushingWithInstanceId(_logger, _instanceProvider.InstanceId, _instanceProvider.ServiceName, _queues.OutboxMessages.Count);
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
        _coordinator, _dependencies.ScopeFactory, _instanceProvider, _options, STRATEGY_NAME,
        outboxMessages, inboxMessages, outboxCompletions, inboxCompletions,
        outboxFailures, inboxFailures, flags, _dependencies.LifecycleMessageDeserializer,
        _logger, _dependencies.TracingOptions, _metrics, _lifecycleMetrics,
        WorkChannelWriter: _workChannelWriter, PendingAuditMessages: pendingAuditMessages),
      ct
    );

    // Clear queues after successful flush
    _queues.Clear();

    // DIAGNOSTIC: Log what was returned from ProcessWorkBatchAsync
    if (_logger != null) {
      LogProcessWorkBatchResult(_logger, workBatch.OutboxWork.Count, workBatch.InboxWork.Count, workBatch.PerspectiveWork.Count);
      if (workBatch.OutboxWork.Count > 0) {
        foreach (var work in workBatch.OutboxWork.Take(3)) {
          var isNewlyStored = (work.Flags & WorkBatchOptions.NewlyStored) != 0;
          LogReturnedOutboxWork(_logger, work.MessageId, work.Destination, isNewlyStored);
        }
      } else if (outboxMessages.Length > 0) {
        LogNoWorkReturned(_logger, outboxMessages.Length, _instanceProvider.InstanceId);
      }
    }

    return workBatch;
  }

  /// <inheritdoc />
  Task IWorkFlusher.FlushAsync(CancellationToken ct) =>
    FlushAsync(WorkBatchOptions.None, FlushMode.Required, ct);

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }

    // Safety net: persist any remaining queued data WITHOUT lifecycle stages.
    // In normal operation, the middleware (or explicit FlushAsync) has already flushed
    // with full lifecycle while the scope was alive. By the time DisposeAsync runs,
    // ambient resources like HttpContext may be disposed — so skip lifecycle to avoid
    // ObjectDisposedException.
    if (!_queues.IsEmpty) {
      if (_logger != null) {
        LogDisposingWithUnflushedOperations(
          _logger,
          _queues.OutboxMessages.Count,
          _queues.InboxMessages.Count,
          _queues.OutboxCompletions.Count + _queues.InboxCompletions.Count,
          _queues.OutboxFailures.Count + _queues.InboxFailures.Count
        );
      }

      try {
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

        await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
          new FlushContext(
            _coordinator, _dependencies.ScopeFactory, _instanceProvider, _options, STRATEGY_NAME,
            outboxMessages, inboxMessages, outboxCompletions, inboxCompletions,
            outboxFailures, inboxFailures, WorkBatchOptions.None, _dependencies.LifecycleMessageDeserializer,
            _logger, _dependencies.TracingOptions, _metrics, _lifecycleMetrics,
            WorkChannelWriter: _workChannelWriter, PendingAuditMessages: pendingAuditMessages,
            SkipLifecycle: true),
          ct: default
        );

        _queues.Clear();
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

  /// <summary>Logs a queued outbox message with its destination.</summary>
  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Trace,
    Message = "Queued outbox message {MessageId} for {Destination}"
  )]
  static partial void LogQueuedOutboxMessage(ILogger logger, Guid messageId, string? destination);

  /// <summary>Logs a queued inbox message with its handler name.</summary>
  [LoggerMessage(
    EventId = 2,
    Level = LogLevel.Trace,
    Message = "Queued inbox message {MessageId} for handler {HandlerName}"
  )]
  static partial void LogQueuedInboxMessage(ILogger logger, Guid messageId, string handlerName);

  /// <summary>Logs a queued outbox completion with its processing status.</summary>
  [LoggerMessage(
    EventId = 3,
    Level = LogLevel.Trace,
    Message = "Queued outbox completion for {MessageId} with status {Status}"
  )]
  static partial void LogQueuedOutboxCompletion(ILogger logger, Guid messageId, MessageProcessingStatus status);

  /// <summary>Logs a queued inbox completion with its processing status.</summary>
  [LoggerMessage(
    EventId = 4,
    Level = LogLevel.Trace,
    Message = "Queued inbox completion for {MessageId} with status {Status}"
  )]
  static partial void LogQueuedInboxCompletion(ILogger logger, Guid messageId, MessageProcessingStatus status);

  /// <summary>Logs a queued outbox failure with the error message.</summary>
  [LoggerMessage(
    EventId = 5,
    Level = LogLevel.Trace,
    Message = "Queued outbox failure for {MessageId}: {Error}"
  )]
  static partial void LogQueuedOutboxFailure(ILogger logger, Guid messageId, string error);

  /// <summary>Logs a queued inbox failure with the error message.</summary>
  [LoggerMessage(
    EventId = 6,
    Level = LogLevel.Trace,
    Message = "Queued inbox failure for {MessageId}: {Error}"
  )]
  static partial void LogQueuedInboxFailure(ILogger logger, Guid messageId, string error);

  /// <summary>Logs a summary of outbox and inbox queue counts before flushing.</summary>
  [LoggerMessage(
    EventId = 7,
    Level = LogLevel.Debug,
    Message = "Outbox flush: Queued={Queued} | Inbox flush: Queued={InboxQueued}"
  )]
  static partial void LogFlushSummary(ILogger logger, int queued, int inboxQueued);

  /// <summary>Logs a warning when the scoped strategy is disposing with unflushed operations.</summary>
  [LoggerMessage(
    EventId = 10,
    Level = LogLevel.Warning,
    Message = "Scoped strategy disposing with unflushed operations: {OutboxMsg} outbox messages, {InboxMsg} inbox messages, {Completions} completions, {Failures} failures"
  )]
  static partial void LogDisposingWithUnflushedOperations(ILogger logger, int outboxMsg, int inboxMsg, int completions, int failures);

  /// <summary>Logs an error that occurred while flushing during disposal.</summary>
  [LoggerMessage(
    EventId = 11,
    Level = LogLevel.Error,
    Message = "Error flushing scoped strategy on disposal"
  )]
  static partial void LogErrorFlushingOnDisposal(ILogger logger, Exception ex);

  /// <summary>Logs the instance ID and service name during a flush operation.</summary>
  [LoggerMessage(
    EventId = 12,
    Level = LogLevel.Debug,
    Message = "Flushing {Count} outbox messages with InstanceId={InstanceId}, Service={ServiceName}"
  )]
  static partial void LogFlushingWithInstanceId(ILogger logger, Guid instanceId, string serviceName, int count);

  /// <summary>Logs the work batch result counts returned from ProcessWorkBatchAsync.</summary>
  [LoggerMessage(
    EventId = 13,
    Level = LogLevel.Debug,
    Message = "ProcessWorkBatchAsync returned: Outbox={OutboxCount}, Inbox={InboxCount}, Perspective={PerspectiveCount}"
  )]
  static partial void LogProcessWorkBatchResult(ILogger logger, int outboxCount, int inboxCount, int perspectiveCount);

  /// <summary>Logs details of returned outbox work items for diagnostics.</summary>
  [LoggerMessage(
    EventId = 14,
    Level = LogLevel.Debug,
    Message = "Returned outbox work: MessageId={MessageId}, Destination={Destination}, IsNewlyStored={IsNewlyStored}"
  )]
  static partial void LogReturnedOutboxWork(ILogger logger, Guid messageId, string? destination, bool isNewlyStored);

  /// <summary>Logs a critical error when queued outbox messages produce zero work items.</summary>
  [LoggerMessage(
    EventId = 15,
    Level = LogLevel.Error,
    Message = "CRITICAL BUG: Queued {QueuedCount} outbox messages but ProcessWorkBatchAsync returned 0! InstanceId={InstanceId}"
  )]
  static partial void LogNoWorkReturned(ILogger logger, int queuedCount, Guid instanceId);

}
