// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Why a batch of inbox messages the inbox batcher accepted was not stored (#1885).
/// </summary>
/// <docs>extending/internals/inbox-batch-strategy#when-a-flush-fails</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/SlidingWindowInboxFlushFailureTests.cs</tests>
public enum InboxFlushFailureCause {
  /// <summary>Every attempt failed with a transient database failure, and the attempts allowed ran out.</summary>
  RetriesExhausted = 0,

  /// <summary>The store failed with an error a retry cannot fix, so it was not retried.</summary>
  NotRetryable = 1,

  /// <summary>
  /// The host stopped with a deadline that passed while the messages were still buffered, being stored or waiting
  /// to be retried.
  /// </summary>
  AbandonedAtShutdown = 2,
}

/// <summary>
/// Inbox messages the inbox batcher accepted and could not store: the messages, why, and whether they were kept in
/// the dead-letter table, from where dead-letter recovery stores them through the inbox store (#1885).
/// </summary>
/// <param name="StreamId">The stream the batch belongs to; <see cref="Guid.Empty"/> for messages with no stream.</param>
/// <param name="Messages">The messages that were not stored.</param>
/// <param name="Cause">Why they were not stored.</param>
/// <param name="Attempts">How many times the store was attempted; zero when the messages were abandoned before one.</param>
/// <param name="Error">The last failure, or null when the messages were abandoned before a store failed.</param>
/// <docs>extending/internals/inbox-batch-strategy#when-a-flush-fails</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/SlidingWindowInboxFlushFailureTests.cs</tests>
public sealed record InboxFlushFailure(
    Guid StreamId,
    IReadOnlyList<InboxMessage> Messages,
    InboxFlushFailureCause Cause,
    int Attempts,
    Exception? Error) {
  /// <summary>
  /// Whether the messages were written to the dead-letter table. Set before observers see the failure; false means
  /// the messages exist nowhere but in this record and the error log that names their ids.
  /// </summary>
  public bool DeadLettered { get; init; }
}

/// <summary>
/// Records inbox messages the inbox batcher could not store, so they are not lost: the registered one writes them to
/// the dead-letter table, counts them and tells every <see cref="IInboxFlushFailureObserver"/> (#1885).
/// </summary>
/// <param name="failure">What was not stored, and why.</param>
/// <param name="cancellationToken">Cancellation.</param>
/// <returns>True when the messages were written to the dead-letter table.</returns>
/// <docs>extending/internals/inbox-batch-strategy#when-a-flush-fails</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/InboxFlushFailureRecorderTests.cs</tests>
public delegate Task<bool> InboxFlushFailureCallback(InboxFlushFailure failure, CancellationToken cancellationToken);

/// <summary>
/// Told about every batch of inbox messages the inbox batcher could not store, after the framework has tried to keep
/// them in the dead-letter table (<see cref="InboxFlushFailure.DeadLettered"/>). Register any number; each is called
/// in turn and one that throws is logged and does not stop the others (#1885).
/// </summary>
/// <docs>extending/internals/inbox-batch-strategy#when-a-flush-fails</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/InboxFlushFailureRecorderTests.cs</tests>
public interface IInboxFlushFailureObserver {
  /// <summary>Called once for each batch that could not be stored.</summary>
  /// <param name="failure">What was not stored, why, and whether it was dead-lettered.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>A task that completes when the observer is done.</returns>
  ValueTask OnInboxFlushFailedAsync(InboxFlushFailure failure, CancellationToken cancellationToken);
}

/// <summary>
/// Why inbox messages that were never stored are being dead-lettered, and by whom: the audit columns of the dead
/// letters <see cref="IWorkCoordinator.StoreInboxDeadLettersAsync"/> writes (#1885).
/// </summary>
/// <param name="FailureReason">The reason the dead letters carry, which picks their recovery policy.</param>
/// <param name="ErrorText">The failure, as the dead letters keep it; null when there was none.</param>
/// <param name="Attempts">How many times the store was attempted.</param>
/// <param name="InstanceId">The instance recording the dead letters.</param>
/// <param name="Generation">The current generation tag, read by the recovery worker's generation replay.</param>
/// <docs>extending/internals/inbox-batch-strategy#when-a-flush-fails</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxFlushDeadLetterTests.cs</tests>
public sealed record InboxDeadLetterRequest(
    MessageFailureReason FailureReason,
    string? ErrorText,
    int Attempts,
    Guid InstanceId,
    string Generation);
