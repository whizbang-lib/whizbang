// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// The contract of closing the books (#1741): <see cref="OpeningBalance"/> carries its predecessor's link, and
/// <see cref="BooksToClose"/> hands its events to a closer as their own types, so each is stored as the type it is.
/// </summary>
/// <docs>fundamentals/events/closing-the-books#by-policy</docs>
public class BookCloserTests {
  public sealed record PeriodClosed(decimal Balance, [property: StreamId] Guid LedgerId = default) : IEvent;

  public sealed record PeriodOpened : OpeningBalance {
    public decimal Balance { get; init; }
  }

  private sealed class RecordingCloser : IBookCloser {
    public List<(Guid Stream, Type Closing, Type Opening, object ClosingSummary, OpeningBalance OpeningBalance)> Calls { get; } = [];

    public Task<BookCloseResult> CloseBooksAsync<TClosing, TOpening>(
        Guid streamId, TClosing closingSummary, TOpening openingBalance, CancellationToken cancellationToken = default)
        where TClosing : IEvent
        where TOpening : OpeningBalance {
      Calls.Add((streamId, typeof(TClosing), typeof(TOpening), closingSummary, openingBalance));
      return Task.FromResult(new BookCloseResult(BookCloseStatus.Closed, Guid.Empty, 4));
    }
  }

  [Test]
  public async Task AnOpeningBalance_StartsWithNoLink_AndKeepsTheOneItIsGivenAsync() {
    var unlinked = new PeriodOpened { Balance = 30 };
    var stream = new Guid("0192f1a0-0000-7000-8000-000000001741");
    var linked = unlinked with { PredecessorStreamId = stream, PredecessorFinalVersion = 4 };

    await Assert.That(unlinked.SuccessorStreamId).IsEqualTo(Guid.Empty);
    await Assert.That(unlinked.PredecessorStreamId).IsEqualTo(Guid.Empty);
    await Assert.That(unlinked.PredecessorFinalVersion).IsEqualTo(0L);
    await Assert.That(linked.PredecessorStreamId).IsEqualTo(stream);
    await Assert.That(linked.PredecessorFinalVersion).IsEqualTo(4L);
    await Assert.That(linked.Balance).IsEqualTo(30m);
  }

  [Test]
  public async Task BooksToClose_KeepsItsEvents_AndClosesWithThemAsTheirOwnTypesAsync() {
    var closer = new RecordingCloser();
    var stream = new Guid("0192f1a0-0000-7000-8000-000000001742");
    var closing = new PeriodClosed(30);
    var opening = new PeriodOpened { Balance = 30 };
    var books = BooksToClose.Of(closing, opening);

    var result = await books.CloseWithAsync(closer, stream);

    await Assert.That(books.ClosingSummary).IsSameReferenceAs(closing);
    await Assert.That(books.OpeningBalance).IsSameReferenceAs(opening);
    await Assert.That(result.Status).IsEqualTo(BookCloseStatus.Closed);
    await Assert.That(closer.Calls).Count().IsEqualTo(1);
    await Assert.That(closer.Calls[0].Stream).IsEqualTo(stream);
    await Assert.That(closer.Calls[0].Closing).IsEqualTo(typeof(PeriodClosed))
      .Because("a handler returns its events through Of, which keeps their types, so the closer stores each as what it is");
    await Assert.That(closer.Calls[0].Opening).IsEqualTo(typeof(PeriodOpened));
  }

  [Test]
  public async Task BooksToClose_RefusesNullsAsync() {
    await Assert.That(() => BooksToClose.Of<PeriodClosed, PeriodOpened>(null!, new PeriodOpened { Balance = 1 })).Throws<ArgumentNullException>();
    await Assert.That(() => BooksToClose.Of<PeriodClosed, PeriodOpened>(new PeriodClosed(1), null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await BooksToClose.Of(new PeriodClosed(1), new PeriodOpened { Balance = 1 }).CloseWithAsync(null!, Guid.Empty))
      .Throws<ArgumentNullException>();
  }
}
