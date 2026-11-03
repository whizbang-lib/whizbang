// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// The developer's side of closing the books by policy (#1741): when a stream type's end-of-life
/// <see cref="Archival.StreamLifecycleAction.CloseBooks"/> action picks a stream, the framework asks this handler for the
/// closing summary and the opening balance, which only the domain can compute, and then closes the books with
/// <see cref="IBookCloser"/>. A host that declares <c>CloseBooks</c> without registering a handler fails its start.
/// </summary>
/// <docs>fundamentals/events/closing-the-books#by-policy</docs>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/StreamDispositionStepTests.cs</tests>
public interface IBookClosingHandler {
  /// <summary>
  /// The closing summary and opening balance for the stream in <paramref name="context"/>, or <see langword="null"/> to
  /// leave the stream open for now (it is offered again after the retry backoff).
  /// </summary>
  /// <param name="context">The stream a policy picked, and why.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The events, or null.</returns>
  ValueTask<BooksToClose?> PrepareAsync(BookClosingContext context, CancellationToken cancellationToken = default);
}

/// <summary>A stream an end-of-life policy picked to close the books of (#1741).</summary>
/// <param name="StreamId">The stream.</param>
/// <param name="StreamType">Its type, as the lifecycle ledger records it.</param>
/// <param name="Reason">The trigger that picked it.</param>
/// <param name="LastVersion">Its newest version when it was picked.</param>
/// <docs>fundamentals/events/closing-the-books#by-policy</docs>
public sealed record BookClosingContext(Guid StreamId, string StreamType, DestructionReason Reason, long LastVersion);

/// <summary>
/// The events that close a stream's books (#1741), built with <see cref="Of{TClosing, TOpening}"/> so each is stored as its
/// own type.
/// </summary>
/// <docs>fundamentals/events/closing-the-books#by-policy</docs>
public sealed class BooksToClose {
  private readonly Func<IBookCloser, Guid, CancellationToken, Task<BookCloseResult>> _close;

  private BooksToClose(IEvent closingSummary, OpeningBalance openingBalance, Func<IBookCloser, Guid, CancellationToken, Task<BookCloseResult>> close) {
    ClosingSummary = closingSummary;
    OpeningBalance = openingBalance;
    _close = close;
  }

  /// <summary>The last event of the stream.</summary>
  public IEvent ClosingSummary { get; }

  /// <summary>The first event of its successor.</summary>
  public OpeningBalance OpeningBalance { get; }

  /// <summary>The closing summary and the opening balance.</summary>
  /// <typeparam name="TClosing">The closing summary's type.</typeparam>
  /// <typeparam name="TOpening">The opening balance's type.</typeparam>
  /// <param name="closingSummary">The last event of the stream.</param>
  /// <param name="openingBalance">The first event of its successor.</param>
  /// <returns>The events to close the books with.</returns>
  public static BooksToClose Of<TClosing, TOpening>(TClosing closingSummary, TOpening openingBalance)
      where TClosing : IEvent
      where TOpening : OpeningBalance {
    ArgumentNullException.ThrowIfNull(closingSummary);
    ArgumentNullException.ThrowIfNull(openingBalance);
    return new BooksToClose(closingSummary, openingBalance,
      (closer, streamId, ct) => closer.CloseBooksAsync(streamId, closingSummary, openingBalance, ct));
  }

  /// <summary>Closes <paramref name="streamId"/>'s books with these events.</summary>
  /// <param name="closer">The closer.</param>
  /// <param name="streamId">The stream.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>What happened.</returns>
  public Task<BookCloseResult> CloseWithAsync(IBookCloser closer, Guid streamId, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(closer);
    return _close(closer, streamId, cancellationToken);
  }
}
