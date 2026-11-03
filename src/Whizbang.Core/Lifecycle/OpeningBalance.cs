// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// The base of the event that opens a successor stream when a stream's books are closed (#1741, WS-3 S12): a domain event
/// carrying what continues (an opening balance, a carried-forward state), plus the link to the stream it continues.
/// </summary>
/// <remarks>
/// Derive the domain's opening event from it. <see cref="IBookCloser.CloseBooksAsync"/> fills
/// <see cref="SuccessorStreamId"/>, <see cref="PredecessorStreamId"/> and <see cref="PredecessorFinalVersion"/> before it
/// stores the event, so whatever the caller set there is replaced: the link is the framework's, never the caller's.
/// </remarks>
/// <example>
/// <code>
/// public sealed record LedgerOpened : OpeningBalance {
///   public decimal Balance { get; init; }
/// }
/// </code>
/// </example>
/// <docs>fundamentals/events/closing-the-books</docs>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/BookCloserTests.cs</tests>
public abstract record OpeningBalance : IEvent {
  /// <summary>The successor stream this event opens: its stream id, chosen by the framework.</summary>
  [StreamId]
  public Guid SuccessorStreamId { get; init; }

  /// <summary>The stream whose books were closed.</summary>
  public Guid PredecessorStreamId { get; init; }

  /// <summary>The predecessor's final version: the version of its closing summary.</summary>
  public long PredecessorFinalVersion { get; init; }
}
