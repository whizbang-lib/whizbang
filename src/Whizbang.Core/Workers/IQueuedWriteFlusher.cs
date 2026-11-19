// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>
/// Something that queues work-table writes and writes them later: a <see cref="BatchFlusher{T}"/>, and each flush worker
/// that owns one (#1899). It says how many queued writes have not landed yet and when that reaches zero.
/// </summary>
/// <remarks>
/// A write is pending from the moment it is offered until the flush carrying it returns, or until it is given up: dropped
/// after its last failed attempt, refused by a stopped flusher, or abandoned at shutdown. A given-up write will never
/// land, so it stops counting rather than holding a waiter forever. A worker hosted with its start point
/// (<c>AddWhizbangWorker</c>, <c>AddWhizbangWorkerAtSchemaReady</c>) that implements this is watched by
/// <see cref="IQueuedWritesLandedSignal"/> without being listed anywhere.
/// </remarks>
/// <docs>fundamentals/work-coordinator/batched-flushers#when-writes-have-landed</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/FlushWorkerIdleSignalTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/QueuedWritesLandedRegistrationTests.cs:EveryTypeThatOwnsAFlusher_ReportsItsQueuedWritesAsync</tests>
public interface IQueuedWriteFlusher {
  /// <summary>The writes offered so far and the ones still pending, from one reading.</summary>
  QueuedWriteActivity Activity { get; }

  /// <summary>True when no write offered to this flusher is still waiting or in progress.</summary>
  bool IsIdle { get; }

  /// <summary>Raised when the last pending write lands or is given up: the pending count reaches zero.</summary>
  /// <remarks>
  /// Raised on the thread that brought the count to zero, after <see cref="WaitForIdleAsync"/> has been completed, so a
  /// caller that needs both waits on each; a handler that runs long holds up that thread's next flush.
  /// </remarks>
  event WorkProcessingIdleHandler? OnWorkProcessingIdle;

  /// <summary>Completes when no write offered to this flusher is pending; at once when none is.</summary>
  /// <param name="cancellationToken">Ends the wait.</param>
  /// <returns>A task that completes when the pending count is zero.</returns>
  Task WaitForIdleAsync(CancellationToken cancellationToken = default);
}

/// <summary>One reading of a <see cref="IQueuedWriteFlusher"/>: how far its offers have run, and what is still pending.</summary>
/// <param name="Version">
/// Advances by one each time a write is offered, whether or not it is accepted (a running count that wraps at 2^32). Two
/// equal readings mean nothing was offered between them, which is what lets several flushers be shown idle at the same
/// moment.
/// </param>
/// <param name="Pending">Writes offered that have neither landed nor been given up.</param>
/// <docs>fundamentals/work-coordinator/batched-flushers#when-writes-have-landed</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/BatchFlusherIdleSignalTests.cs</tests>
public readonly record struct QueuedWriteActivity(long Version, long Pending);
