using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Scoped strategy - batches operations within a scope (e.g., HTTP request, message handler).
/// Flushes on scope disposal (IAsyncDisposable pattern).
/// Provides a good balance of latency and efficiency.
/// Best for: Web APIs, message handlers, transactional operations.
/// </summary>
public class ScopedWorkCoordinatorStrategy : IWorkCoordinatorStrategy, IAsyncDisposable {
  private readonly IWorkCoordinator _coordinator;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly WorkCoordinatorOptions _options;
  private readonly ILogger<ScopedWorkCoordinatorStrategy>? _logger;

  // Queues for batching operations within the scope
  private readonly List<NewOutboxMessage> _queuedOutboxMessages = [];
  private readonly List<NewInboxMessage> _queuedInboxMessages = [];
  private readonly List<MessageCompletion> _queuedOutboxCompletions = [];
  private readonly List<MessageCompletion> _queuedInboxCompletions = [];
  private readonly List<MessageFailure> _queuedOutboxFailures = [];
  private readonly List<MessageFailure> _queuedInboxFailures = [];

  private bool _disposed = false;

  public ScopedWorkCoordinatorStrategy(
    IWorkCoordinator coordinator,
    IServiceInstanceProvider instanceProvider,
    WorkCoordinatorOptions options,
    ILogger<ScopedWorkCoordinatorStrategy>? logger = null
  ) {
    _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _logger = logger;
  }

  public void QueueOutboxMessage(NewOutboxMessage message) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queuedOutboxMessages.Add(message);
    _logger?.LogTrace("Queued outbox message {MessageId} for {Destination}", message.MessageId, message.Destination);
  }

  public void QueueInboxMessage(NewInboxMessage message) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queuedInboxMessages.Add(message);
    _logger?.LogTrace("Queued inbox message {MessageId} for handler {HandlerName}", message.MessageId, message.HandlerName);
  }

  public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queuedOutboxCompletions.Add(new MessageCompletion {
      MessageId = messageId,
      Status = completedStatus
    });
    _logger?.LogTrace("Queued outbox completion for {MessageId} with status {Status}", messageId, completedStatus);
  }

  public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queuedInboxCompletions.Add(new MessageCompletion {
      MessageId = messageId,
      Status = completedStatus
    });
    _logger?.LogTrace("Queued inbox completion for {MessageId} with status {Status}", messageId, completedStatus);
  }

  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string error) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queuedOutboxFailures.Add(new MessageFailure {
      MessageId = messageId,
      CompletedStatus = completedStatus,
      Error = error
    });
    _logger?.LogTrace("Queued outbox failure for {MessageId}: {Error}", messageId, error);
  }

  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string error) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    _queuedInboxFailures.Add(new MessageFailure {
      MessageId = messageId,
      CompletedStatus = completedStatus,
      Error = error
    });
    _logger?.LogTrace("Queued inbox failure for {MessageId}: {Error}", messageId, error);
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
        InboxWork = []
      };
    }

    // Log a summary of what's being flushed to the database
    _logger?.LogInformation(
      "Outbox flush: Queued={Queued} | Inbox flush: Queued={InboxQueued}",
      _queuedOutboxMessages.Count,
      _queuedInboxMessages.Count
    );

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
      newOutboxMessages: [.. _queuedOutboxMessages],
      newInboxMessages: [.. _queuedInboxMessages],
      flags: flags | (_options.DebugMode ? WorkBatchFlags.DebugMode : WorkBatchFlags.None),
      partitionCount: _options.PartitionCount,
      maxPartitionsPerInstance: _options.MaxPartitionsPerInstance,
      leaseSeconds: _options.LeaseSeconds,
      staleThresholdSeconds: _options.StaleThresholdSeconds,
      cancellationToken: ct
    );

    // Clear queues after successful flush
    _queuedOutboxMessages.Clear();
    _queuedInboxMessages.Clear();
    _queuedOutboxCompletions.Clear();
    _queuedOutboxFailures.Clear();
    _queuedInboxCompletions.Clear();
    _queuedInboxFailures.Clear();

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
      _logger?.LogWarning(
        "Scoped strategy disposing with unflushed operations: {OutboxMsg} outbox messages, {InboxMsg} inbox messages, {Completions} completions, {Failures} failures",
        _queuedOutboxMessages.Count,
        _queuedInboxMessages.Count,
        _queuedOutboxCompletions.Count + _queuedInboxCompletions.Count,
        _queuedOutboxFailures.Count + _queuedInboxFailures.Count
      );

      try {
        await FlushAsync(WorkBatchFlags.None);
      } catch (Exception ex) {
        _logger?.LogError(ex, "Error flushing scoped strategy on disposal");
      }
    }

    _disposed = true;
  }
}
