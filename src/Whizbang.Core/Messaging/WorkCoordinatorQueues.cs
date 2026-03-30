using System;
using System.Collections.Generic;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Encapsulates the queue state shared across work coordinator strategies.
/// Owns the message/completion/failure lists and provides add, clear, audit merge,
/// and <see cref="ProcessWorkBatchRequest"/> construction helpers.
/// This is a composition helper -- each strategy owns an instance and delegates queue
/// operations to it while keeping its own FlushAsync, logging, and lifecycle logic.
/// </summary>
internal sealed class WorkCoordinatorQueues {
  internal readonly List<OutboxMessage> OutboxMessages = [];
  internal readonly List<OutboxMessage> PendingAuditMessages = [];
  internal readonly List<InboxMessage> InboxMessages = [];
  internal readonly List<MessageCompletion> OutboxCompletions = [];
  internal readonly List<MessageCompletion> InboxCompletions = [];
  internal readonly List<MessageFailure> OutboxFailures = [];
  internal readonly List<MessageFailure> InboxFailures = [];

  /// <summary>
  /// Adds an outbox message to the queue. When audit is enabled and the message
  /// is an event, a corresponding audit message is generated and queued separately.
  /// </summary>
  internal void AddOutboxMessage(OutboxMessage message, SystemEventOptions? systemEventOptions) {
    OutboxMessages.Add(message);

    // Generate audit outbox message for event messages when audit is enabled.
    // Audit messages are collected separately and merged AFTER lifecycle stages
    // to avoid SecurityContextRequiredException during lifecycle processing.
    if (message.IsEvent && systemEventOptions?.EventAuditEnabled == true) {
      var auditMessage = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, systemEventOptions);
      if (auditMessage != null) {
        PendingAuditMessages.Add(auditMessage);
      }
    }
  }

  /// <summary>
  /// Adds an inbox message to the queue.
  /// </summary>
  internal void AddInboxMessage(InboxMessage message) {
    InboxMessages.Add(message);
  }

  /// <summary>
  /// Adds an outbox completion to the queue.
  /// </summary>
  internal void AddOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
    OutboxCompletions.Add(new MessageCompletion {
      MessageId = messageId,
      Status = completedStatus
    });
  }

  /// <summary>
  /// Adds an inbox completion to the queue.
  /// </summary>
  internal void AddInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) {
    InboxCompletions.Add(new MessageCompletion {
      MessageId = messageId,
      Status = completedStatus
    });
  }

  /// <summary>
  /// Adds an outbox failure to the queue.
  /// </summary>
  internal void AddOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) {
    OutboxFailures.Add(new MessageFailure {
      MessageId = messageId,
      CompletedStatus = completedStatus,
      Error = errorMessage
    });
  }

  /// <summary>
  /// Adds an inbox failure to the queue.
  /// </summary>
  internal void AddInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) {
    InboxFailures.Add(new MessageFailure {
      MessageId = messageId,
      CompletedStatus = completedStatus,
      Error = errorMessage
    });
  }

  /// <summary>
  /// Merges pending audit messages into the outbox queue.
  /// Call this AFTER lifecycle stages and BEFORE building the work batch request.
  /// </summary>
  internal void MergeAuditMessages() {
    if (PendingAuditMessages.Count > 0) {
      OutboxMessages.AddRange(PendingAuditMessages);
      PendingAuditMessages.Clear();
    }
  }

  /// <summary>
  /// Returns <c>true</c> when every queue is empty.
  /// </summary>
  internal bool IsEmpty =>
    OutboxMessages.Count == 0 &&
    InboxMessages.Count == 0 &&
    OutboxCompletions.Count == 0 &&
    OutboxFailures.Count == 0 &&
    InboxCompletions.Count == 0 &&
    InboxFailures.Count == 0;

  /// <summary>
  /// Builds a <see cref="ProcessWorkBatchRequest"/> from the current queue contents
  /// and the provided instance/options metadata.
  /// </summary>
  internal ProcessWorkBatchRequest BuildRequest(
    IServiceInstanceProvider instanceProvider,
    WorkCoordinatorOptions options,
    WorkBatchOptions flags
  ) {
    return new ProcessWorkBatchRequest {
      InstanceId = instanceProvider.InstanceId,
      ServiceName = instanceProvider.ServiceName,
      HostName = instanceProvider.HostName,
      ProcessId = instanceProvider.ProcessId,
      Metadata = null,
      OutboxCompletions = [.. OutboxCompletions],
      OutboxFailures = [.. OutboxFailures],
      InboxCompletions = [.. InboxCompletions],
      InboxFailures = [.. InboxFailures],
      ReceptorCompletions = [],
      ReceptorFailures = [],
      PerspectiveCompletions = [],
      PerspectiveEventCompletions = [],
      PerspectiveFailures = [],
      NewOutboxMessages = [.. OutboxMessages],
      NewInboxMessages = [.. InboxMessages],
      RenewOutboxLeaseIds = [],
      RenewInboxLeaseIds = [],
      Flags = flags | (options.DebugMode ? WorkBatchOptions.DebugMode : WorkBatchOptions.None),
      PartitionCount = options.PartitionCount,
      LeaseSeconds = options.LeaseSeconds,
      StaleThresholdSeconds = options.StaleThresholdSeconds
    };
  }

  /// <summary>
  /// Clears all queues after a successful flush,
  /// including pending audit messages to prevent stale accumulation across flushes.
  /// </summary>
  internal void Clear() {
    OutboxMessages.Clear();
    InboxMessages.Clear();
    OutboxCompletions.Clear();
    OutboxFailures.Clear();
    InboxCompletions.Clear();
    InboxFailures.Clear();
    PendingAuditMessages.Clear();
  }
}
