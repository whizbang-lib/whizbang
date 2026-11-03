// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// Closes a stream's books (#1741, WS-3 S12): in one transaction, appends the closing summary to the stream, seals it, and
/// opens a successor stream whose first event is the opening balance, linked back to the stream it continues. The sealed
/// predecessor is then disposed of by its type's action once its grace has passed, like any sealed stream.
/// </summary>
/// <docs>fundamentals/events/closing-the-books</docs>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/BookCloserTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CloseBooksSqlTests.cs</tests>
public interface IBookCloser {
  /// <summary>
  /// Closes the books of <paramref name="streamId"/>: <paramref name="closingSummary"/> is appended to it, the stream is
  /// sealed, and a new stream (its id chosen by the framework) is opened with <paramref name="openingBalance"/>, whose
  /// <see cref="OpeningBalance.PredecessorStreamId"/> and <see cref="OpeningBalance.PredecessorFinalVersion"/> the framework
  /// sets. All three happen or none does.
  /// </summary>
  /// <typeparam name="TClosing">The closing summary's type, which the event is stored as.</typeparam>
  /// <typeparam name="TOpening">The opening balance's type, which the event is stored as.</typeparam>
  /// <param name="streamId">The stream whose books close.</param>
  /// <param name="closingSummary">The last event of the stream.</param>
  /// <param name="openingBalance">The first event of the successor.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>What happened, with the successor's id when the books closed.</returns>
  Task<BookCloseResult> CloseBooksAsync<TClosing, TOpening>(
    Guid streamId, TClosing closingSummary, TOpening openingBalance, CancellationToken cancellationToken = default)
    where TClosing : IEvent
    where TOpening : OpeningBalance;
}

/// <summary>The outcome of <see cref="IBookCloser.CloseBooksAsync"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="SuccessorStreamId">The successor stream when the books closed; otherwise null.</param>
/// <param name="PredecessorFinalVersion">The closing summary's version when the books closed; otherwise null.</param>
/// <docs>fundamentals/events/closing-the-books</docs>
public sealed record BookCloseResult(BookCloseStatus Status, Guid? SuccessorStreamId = null, long? PredecessorFinalVersion = null);

/// <summary>What <see cref="IBookCloser.CloseBooksAsync"/> did.</summary>
/// <docs>fundamentals/events/closing-the-books</docs>
public enum BookCloseStatus {
  /// <summary>The summary was appended, the stream sealed and the successor opened.</summary>
  Closed = 0,

  /// <summary>The stream is sealed already; nothing changed.</summary>
  AlreadySealed = 1,

  /// <summary>The stream holds no Sourced event (never written, or ephemeral), or is the empty stream id; nothing changed.</summary>
  NothingToClose = 2,

  /// <summary>An event was appended to the stream while the books were being closed; nothing changed, and a retry closes them.</summary>
  StreamChanged = 3,

  /// <summary>The stream is archived whole; nothing changed. Restore it first.</summary>
  Archived = 4,
}
