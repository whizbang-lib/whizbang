using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Encapsulates the queue state shared across work coordinator strategies.
/// Owns the message/completion/failure lists and provides add, clear, and audit-merge
/// helpers. This is a composition helper -- each strategy owns an instance and delegates
/// queue operations to it while keeping its own FlushAsync, logging, and lifecycle logic.
/// </summary>
internal sealed class WorkCoordinatorQueues(ILogger? logger = null, CoalesceGroupResolver? coalesceResolver = null) {
  private readonly ILogger _logger = logger ?? NullLogger.Instance;
  private readonly CoalesceGroupResolver? _coalesceResolver = coalesceResolver;
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
    // Tag-bound coalescing: a message whose type carries an enabled coalesce-bound tag is
    // stamped with its group + max-delay floor HERE, so the single is durable in the same
    // transaction as its cause but invisible to the claim pump until folded or released.
    message = Stamp(message);
    OutboxMessages.Add(message);

    // Generate audit outbox message for event messages when audit is enabled.
    // Audit messages are collected separately and merged AFTER lifecycle stages
    // to avoid SecurityContextRequiredException during lifecycle processing.
    if (message.IsEvent && systemEventOptions?.EventAuditEnabled == true) {
      var auditMessage = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, systemEventOptions, _logger);
      if (auditMessage != null) {
        // The audit companion rides the SAME generic path — with the built-in sys-audit
        // binding registered, this is where it gains its group + floor.
        PendingAuditMessages.Add(Stamp(auditMessage));
      }
    }
  }

  /// <summary>
  /// Runs a message through the coalesce resolver (no-op when none is wired). Exposed so
  /// strategies can stamp messages that enter queues without passing AddOutboxMessage
  /// (the deferred-channel drain).
  /// </summary>
  internal OutboxMessage Stamp(OutboxMessage message) =>
    _coalesceResolver?.ApplyCoalescePolicy(message) ?? message;

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
