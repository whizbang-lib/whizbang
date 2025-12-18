using System;
using System.Collections.Generic;
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
public class ImmediateWorkCoordinatorStrategy : IWorkCoordinatorStrategy {
  private readonly IWorkCoordinator _coordinator;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly WorkCoordinatorOptions _options;
  private readonly ILogger<ImmediateWorkCoordinatorStrategy>? _logger;

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
    ILogger<ImmediateWorkCoordinatorStrategy>? logger = null
  ) {
    _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _logger = logger;
  }

  /// <summary>
  /// Queues an outbox message for immediate flush.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:QueueOutboxMessage_FlushesOnCallAsync</tests>
  public void QueueOutboxMessage(OutboxMessage message) {
    _queuedOutboxMessages.Add(message);
    _logger?.LogTrace("Immediate strategy: Outbox message queued (will be sent on next Flush)");
  }

  /// <summary>
  /// Queues an inbox message for immediate flush.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:QueueInboxMessage_FlushesOnCallAsync</tests>
  public void QueueInboxMessage(InboxMessage message) {
    _queuedInboxMessages.Add(message);
    _logger?.LogTrace("Immediate strategy: Inbox message queued (will be stored on next Flush)");
  }

  /// <summary>
  /// Queues an outbox message completion for immediate flush.
  /// </summary>
  public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
    _queuedOutboxCompletions.Add(new MessageCompletion {
      MessageId = messageId,
      Status = completedStatus
    });
    _logger?.LogTrace("Immediate strategy: Outbox completion queued (will be reported on next Flush)");
  }

  /// <summary>
  /// Queues an inbox message completion for immediate flush.
  /// </summary>
  public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
    _queuedInboxCompletions.Add(new MessageCompletion {
      MessageId = messageId,
      Status = completedStatus
    });
    _logger?.LogTrace("Immediate strategy: Inbox completion queued (will be reported on next Flush)");
  }

  /// <summary>
  /// Queues an outbox message failure for immediate flush.
  /// </summary>
  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string error) {
    _queuedOutboxFailures.Add(new MessageFailure {
      MessageId = messageId,
      CompletedStatus = completedStatus,
      Error = error
    });
    _logger?.LogTrace("Immediate strategy: Outbox failure queued (will be reported on next Flush)");
  }

  /// <summary>
  /// Queues an inbox message failure for immediate flush.
  /// </summary>
  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string error) {
    _queuedInboxFailures.Add(new MessageFailure {
      MessageId = messageId,
      CompletedStatus = completedStatus,
      Error = error
    });
    _logger?.LogTrace("Immediate strategy: Inbox failure queued (will be reported on next Flush)");
  }

  /// <summary>
  /// Immediately flushes all queued operations to the work coordinator.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:FlushAsync_ImmediatelyCallsWorkCoordinatorAsync</tests>
  public async Task<WorkBatch> FlushAsync(WorkBatchFlags flags, CancellationToken ct = default) {
    // Immediate strategy calls process_work_batch with all queued operations
    _logger?.LogTrace(
      "Immediate strategy flush: {OutboxMsg} outbox, {InboxMsg} inbox, {Completions} completions, {Failures} failures",
      _queuedOutboxMessages.Count,
      _queuedInboxMessages.Count,
      _queuedOutboxCompletions.Count + _queuedInboxCompletions.Count,
      _queuedOutboxFailures.Count + _queuedInboxFailures.Count
    );

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

    // Clear queues after flush
    _queuedOutboxMessages.Clear();
    _queuedInboxMessages.Clear();
    _queuedOutboxCompletions.Clear();
    _queuedOutboxFailures.Clear();
    _queuedInboxCompletions.Clear();
    _queuedInboxFailures.Clear();

    return workBatch;
  }
}
