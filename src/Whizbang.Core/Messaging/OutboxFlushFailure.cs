// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Why a batch of outbox messages the batcher accepted was not stored (#1871).
/// </summary>
/// <docs>extending/internals/outbox-batch-strategy#when-a-flush-fails</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/SlidingWindowOutboxFlushFailureTests.cs</tests>
public enum OutboxFlushFailureCause {
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
/// Outbox messages the batcher accepted and could not store: the messages, why, and whether they were kept in the
/// dead-letter table, from where dead-letter recovery stores them again (#1871).
/// </summary>
/// <param name="StreamId">The stream the batch belongs to; <see cref="Guid.Empty"/> for messages with no stream.</param>
/// <param name="Messages">The messages that were not stored.</param>
/// <param name="Cause">Why they were not stored.</param>
/// <param name="Attempts">How many times the store was attempted; zero when the messages were abandoned before one.</param>
/// <param name="Error">The last failure, or null when the messages were abandoned before a store failed.</param>
/// <docs>extending/internals/outbox-batch-strategy#when-a-flush-fails</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/SlidingWindowOutboxFlushFailureTests.cs</tests>
public sealed record OutboxFlushFailure(
    Guid StreamId,
    IReadOnlyList<OutboxMessage> Messages,
    OutboxFlushFailureCause Cause,
    int Attempts,
    Exception? Error) {
  /// <summary>
  /// Whether the messages were written to the dead-letter table. Set before observers see the failure; false means
  /// the messages exist nowhere but in this record and the error log that names their ids.
  /// </summary>
  public bool DeadLettered { get; init; }
}

/// <summary>
/// Records outbox messages the batcher could not store, so they are not lost: the registered one writes them to the
/// dead-letter table, counts them and tells every <see cref="IOutboxFlushFailureObserver"/> (#1871).
/// </summary>
/// <param name="failure">What was not stored, and why.</param>
/// <param name="cancellationToken">Cancellation.</param>
/// <returns>True when the messages were written to the dead-letter table.</returns>
/// <docs>extending/internals/outbox-batch-strategy#when-a-flush-fails</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/OutboxFlushFailureCallbackTests.cs</tests>
public delegate Task<bool> OutboxFlushFailureCallback(OutboxFlushFailure failure, CancellationToken cancellationToken);

/// <summary>
/// Told about every batch of outbox messages that could not be stored, after the framework has tried to keep them in
/// the dead-letter table (<see cref="OutboxFlushFailure.DeadLettered"/>). Register any number; each is called in turn
/// and one that throws is logged and does not stop the others (#1871).
/// </summary>
/// <remarks>
/// The event an application answers when an event it returned or published could not be written: alert, keep the
/// messages somewhere of its own when they were not dead-lettered, or re-send them once the database recovers.
/// </remarks>
/// <docs>extending/internals/outbox-batch-strategy#when-a-flush-fails</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/OutboxFlushFailureCallbackTests.cs</tests>
public interface IOutboxFlushFailureObserver {
  /// <summary>Called once for each batch that could not be stored.</summary>
  /// <param name="failure">What was not stored, why, and whether it was dead-lettered.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>A task that completes when the observer is done.</returns>
  ValueTask OnOutboxFlushFailedAsync(OutboxFlushFailure failure, CancellationToken cancellationToken);
}
