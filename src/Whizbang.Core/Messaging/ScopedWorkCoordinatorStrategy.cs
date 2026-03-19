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
public partial class ScopedWorkCoordinatorStrategy : IWorkCoordinatorStrategy, IWorkFlusher, IAsyncDisposable {
  private readonly IWorkCoordinator _coordinator;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly IWorkChannelWriter? _workChannelWriter;
  private readonly WorkCoordinatorOptions _options;
  private readonly ILogger<ScopedWorkCoordinatorStrategy>? _logger;
  private readonly ScopedWorkCoordinatorDependencies _dependencies;
  private readonly WorkCoordinatorMetrics? _metrics;
  private readonly LifecycleMetrics? _lifecycleMetrics;
  private readonly WorkCoordinatorQueues _queues = new();

  private bool _disposed;

  /// <summary>
  /// Initializes a new instance of <see cref="ScopedWorkCoordinatorStrategy"/>.
  /// </summary>
  public ScopedWorkCoordinatorStrategy(
    IWorkCoordinator coordinator,
    IServiceInstanceProvider instanceProvider,
    IWorkChannelWriter? workChannelWriter,
    WorkCoordinatorOptions options,
    ILogger<ScopedWorkCoordinatorStrategy>? logger = null,
    ScopedWorkCoordinatorDependencies? dependencies = null,
    WorkCoordinatorMetrics? metrics = null,
    LifecycleMetrics? lifecycleMetrics = null
  ) {
    _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _workChannelWriter = workChannelWriter;
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _logger = logger;
    _dependencies = dependencies ?? new ScopedWorkCoordinatorDependencies();
    _metrics = metrics;
    _lifecycleMetrics = lifecycleMetrics;
  }

  public void QueueOutboxMessage(OutboxMessage message) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    StreamIdGuard.ThrowIfNonNullEmpty(message.StreamId, message.MessageId, "ScopedStrategy.QueueOutbox", message.MessageType);

    _queues.AddOutboxMessage(message, _dependencies.SystemEventOptions);
    if (_logger != null) {
      LogQueuedOutboxMessage(_logger, message.MessageId, message.Destination);
    }
  }

  public void QueueInboxMessage(InboxMessage message) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    StreamIdGuard.ThrowIfNonNullEmpty(message.StreamId, message.MessageId, "ScopedStrategy.QueueInbox", message.MessageType);

    _queues.AddInboxMessage(message);
    if (_logger != null) {
      LogQueuedInboxMessage(_logger, message.MessageId, message.HandlerName);
    }
  }

  public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queues.AddOutboxCompletion(messageId, completedStatus);
    if (_logger != null) {
      LogQueuedOutboxCompletion(_logger, messageId, completedStatus);
    }
  }

  public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queues.AddInboxCompletion(messageId, completedStatus);
    if (_logger != null) {
      LogQueuedInboxCompletion(_logger, messageId, completedStatus);
    }
  }

  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queues.AddOutboxFailure(messageId, completedStatus, errorMessage);
    if (_logger != null) {
      LogQueuedOutboxFailure(_logger, messageId, errorMessage);
    }
  }

  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queues.AddInboxFailure(messageId, completedStatus, errorMessage);
    if (_logger != null) {
      LogQueuedInboxFailure(_logger, messageId, errorMessage);
    }
  }

  public async Task<WorkBatch> FlushAsync(WorkBatchFlags flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    _metrics?.FlushCalls.Add(1, new KeyValuePair<string, object?>("strategy", "scoped"), new KeyValuePair<string, object?>("flush_mode", mode.ToString()));

    // BestEffort on Scoped strategy: flush immediately anyway.
    // The scope IS the batching boundary — deferring to DisposeAsync is unreliable
    // because the DbContext may already be disposed by the DI container before
    // our DisposeAsync runs (DI disposal order is not guaranteed).

    if (_queues.IsEmpty) {
      _metrics?.EmptyFlushCalls.Add(1, new KeyValuePair<string, object?>("strategy", "scoped"));
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
      _coordinator,
      _dependencies.ScopeFactory,
      _instanceProvider,
      _options,
      "scoped",
      outboxMessages,
      inboxMessages,
      outboxCompletions,
      inboxCompletions,
      outboxFailures,
      inboxFailures,
      flags,
      _dependencies.LifecycleMessageDeserializer,
      _logger,
      _dependencies.TracingOptions,
      _metrics,
      _lifecycleMetrics,
      workChannelWriter: _workChannelWriter,
      pendingAuditMessages: pendingAuditMessages,
      ct
    );

    // Clear queues after successful flush
    _queues.Clear();

    // DIAGNOSTIC: Log what was returned from ProcessWorkBatchAsync
    if (_logger != null) {
      LogProcessWorkBatchResult(_logger, workBatch.OutboxWork.Count, workBatch.InboxWork.Count, workBatch.PerspectiveWork.Count);
      if (workBatch.OutboxWork.Count > 0) {
        foreach (var work in workBatch.OutboxWork.Take(3)) {
          var isNewlyStored = (work.Flags & WorkBatchFlags.NewlyStored) != 0;
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
    FlushAsync(WorkBatchFlags.None, FlushMode.Required, ct);

  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }

    // Flush any remaining queued operations on disposal
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
  static partial void LogQueuedOutboxMessage(ILogger logger, Guid messageId, string? destination);

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
    Level = LogLevel.Debug,
    Message = "Outbox flush: Queued={Queued} | Inbox flush: Queued={InboxQueued}"
  )]
  static partial void LogFlushSummary(ILogger logger, int queued, int inboxQueued);

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
    Level = LogLevel.Debug,
    Message = "Flushing {Count} outbox messages with InstanceId={InstanceId}, Service={ServiceName}"
  )]
  static partial void LogFlushingWithInstanceId(ILogger logger, Guid instanceId, string serviceName, int count);

  [LoggerMessage(
    EventId = 13,
    Level = LogLevel.Debug,
    Message = "ProcessWorkBatchAsync returned: Outbox={OutboxCount}, Inbox={InboxCount}, Perspective={PerspectiveCount}"
  )]
  static partial void LogProcessWorkBatchResult(ILogger logger, int outboxCount, int inboxCount, int perspectiveCount);

  [LoggerMessage(
    EventId = 14,
    Level = LogLevel.Debug,
    Message = "Returned outbox work: MessageId={MessageId}, Destination={Destination}, IsNewlyStored={IsNewlyStored}"
  )]
  static partial void LogReturnedOutboxWork(ILogger logger, Guid messageId, string? destination, bool isNewlyStored);

  [LoggerMessage(
    EventId = 15,
    Level = LogLevel.Error,
    Message = "CRITICAL BUG: Queued {QueuedCount} outbox messages but ProcessWorkBatchAsync returned 0! InstanceId={InstanceId}"
  )]
  static partial void LogNoWorkReturned(ILogger logger, int queuedCount, Guid instanceId);

}
