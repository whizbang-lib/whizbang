using System.Text.Json;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Atomic transactional bundle for one handler's commit. Carries the handler's
/// inbox completion (or failure) plus any new outbox/inbox messages it emitted.
/// All effects commit together; if any step fails the whole bundle rolls back.
/// </summary>
/// <param name="HandlerId">Caller-supplied id used to correlate per-handler results in batched calls.</param>
/// <param name="InstanceId">Calling service instance.</param>
/// <param name="ServiceName">Service name (diagnostics).</param>
/// <param name="HostName">Pod / host name (diagnostics).</param>
/// <param name="ProcessId">OS process id (diagnostics).</param>
/// <param name="PartitionCount">Partition count to use when storing emitted messages. Must match the publisher's PartitionCount.</param>
/// <param name="InboxCompletion">The inbox row this handler is acknowledging. Required.</param>
/// <param name="NewOutboxMessages">Outbox messages emitted by the handler. May be empty.</param>
/// <param name="NewInboxMessages">Inbox messages emitted by the handler (rare). May be empty.</param>
/// <param name="DebugMode">From IWorkCoordinatorStrategy.DebugMode at the call site. When true,
/// process_inbox_completions retains the row with processed_at stamped instead of DELETEing it
/// (production semantic). Serialized into the commit_handler_result JSONB request as <c>debug_mode</c>.</param>
/// <docs>fundamentals/work-coordinator/handler-commit</docs>
public sealed record HandlerCommitRequest(
  Guid HandlerId,
  Guid InstanceId,
  string ServiceName,
  string HostName,
  int ProcessId,
  int PartitionCount,
  HandlerInboxCompletion InboxCompletion,
  IReadOnlyList<OutboxMessage>? NewOutboxMessages = null,
  IReadOnlyList<InboxMessage>? NewInboxMessages = null,
  bool DebugMode = false);

/// <summary>The inbox row being acknowledged by a handler.</summary>
/// <param name="MessageId">The wh_inbox.message_id row to mark.</param>
/// <param name="Status">Status flags to OR into the row's existing status (e.g., bit 1 = Stored, bit 2 = EventStored).</param>
public sealed record HandlerInboxCompletion(Guid MessageId, int Status);

/// <summary>
/// Per-handler result returned from <see cref="IWorkCoordinator.CommitHandlerBatchAsync"/>.
/// Each handler's bundle runs inside its own implicit subtransaction (savepoint); a failure
/// rolls back only that handler's effects.
/// </summary>
/// <param name="HandlerId">Mirrors HandlerCommitRequest.HandlerId for correlation.</param>
/// <param name="Success">True if this handler's bundle committed.</param>
/// <param name="ErrorMessage">SQLERRM text when Success is false; null otherwise.</param>
public sealed record HandlerBatchResult(Guid HandlerId, bool Success, string? ErrorMessage);
