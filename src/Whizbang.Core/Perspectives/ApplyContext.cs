// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Facts about the event a perspective's <c>Apply</c> is applying, read inside the <c>Apply</c> through
/// <see cref="Current"/> (#1878). Every fact comes from the event's own place in its stream, never from the row, so a
/// live apply, a rewind, a rebuild and a replay of the stream all reach the same answer.
/// </summary>
/// <remarks>
/// <para>
/// The runner never passes <c>Apply</c> a null model (a stream with no row gets an empty one), so
/// <c>current is null</c> cannot tell a stream's first event, and the analyzer reports it (WHIZ107). Row state is the
/// wrong signal anyway: a purge, an eviction, a rebuild or a replay changes it. Set first-seen values from
/// <see cref="IsFirstEventInStream"/>:
/// </para>
/// <code>
/// public OrderView Apply(OrderView current, OrderPlaced @event) =>
///   ApplyContext.Current.IsFirstEventInStream
///     ? current with { CreatedAt = @event.PlacedAt }
///     : current;
/// </code>
/// <para>
/// The generated runner sets the facts for the duration of each <c>Apply</c>, on the thread the <c>Apply</c> runs on
/// (an <c>Apply</c> is synchronous). Outside an <c>Apply</c> the context is empty and
/// <see cref="IsFirstEventInStream"/> is <see langword="false"/>. A unit test that calls an <c>Apply</c> directly sets
/// the facts it needs with <see cref="Enter"/>.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives-with-actions#no-row-contract</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/ApplyContextTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/FirstEventInStreamTests.cs</tests>
public readonly struct ApplyContext {
  [ThreadStatic]
  private static bool _isFirstEventInStream;

  private ApplyContext(bool isFirstEventInStream) => IsFirstEventInStream = isFirstEventInStream;

  /// <summary>The facts for the <c>Apply</c> running on this thread; empty outside an <c>Apply</c>.</summary>
  /// <docs>fundamentals/perspectives/perspectives-with-actions#no-row-contract</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/ApplyContextTests.cs:OutsideAnApply_NoEventIsTheFirstInItsStreamAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/FirstEventInStreamTests.cs:TheEventThatOpensAStream_IsTheFirstEventInStreamAsync</tests>
  public static ApplyContext Current => new(_isFirstEventInStream);

  /// <summary>
  /// True when the event is the first in its stream. It is decided by the event's position in its stream, so every
  /// replay of the stream gives the same answer, whatever row the perspective holds at the time. A stream's first
  /// event is the one at its first position: version 1, or version 0 for a stream whose numbering began there, so a
  /// version-1 event of a stream that also holds a version 0 is not first. An event read from a store that does not
  /// record stream positions is never first.
  /// </summary>
  /// <docs>fundamentals/perspectives/perspectives-with-actions#no-row-contract</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/FirstEventInStreamTests.cs:ALaterEventOnAStreamWithNoRow_IsNotTheFirstEventInStreamAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/FirstEventInStreamTests.cs:ARewind_ReproducesTheFirstEventFactAsync</tests>
  public bool IsFirstEventInStream { get; }

  /// <summary>
  /// Sets the facts for one <c>Apply</c> on this thread until the returned scope is disposed, which restores the
  /// facts that were current before. The generated runner calls it around every <c>Apply</c>; a test calls it to
  /// drive an <c>Apply</c> directly.
  /// </summary>
  /// <param name="isFirstEventInStream">Whether the event being applied is the first in its stream.</param>
  /// <returns>The scope to dispose when the <c>Apply</c> returns.</returns>
  /// <docs>fundamentals/perspectives/perspectives-with-actions#no-row-contract</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/ApplyContextTests.cs:Enter_MakesTheFactCurrentUntilTheScopeIsDisposedAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/ApplyContextTests.cs:ANestedScope_RestoresTheEnclosingFactOnDisposeAsync</tests>
  public static Scope Enter(bool isFirstEventInStream) {
    var previous = _isFirstEventInStream;
    _isFirstEventInStream = isFirstEventInStream;
    return new Scope(previous);
  }

  /// <summary>Restores, when disposed, the facts that were current before <see cref="Enter"/>.</summary>
  /// <docs>fundamentals/perspectives/perspectives-with-actions#no-row-contract</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/ApplyContextTests.cs:ANestedScope_RestoresTheEnclosingFactOnDisposeAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/ApplyContextTests.cs:ADefaultScope_DisposesToNotFirstAsync</tests>
  public readonly struct Scope : IDisposable {
    private readonly bool _previous;

    internal Scope(bool previous) => _previous = previous;

    /// <summary>Restores the enclosing facts.</summary>
    /// <docs>fundamentals/perspectives/perspectives-with-actions#no-row-contract</docs>
    /// <tests>tests/Whizbang.Core.Tests/Perspectives/ApplyContextTests.cs:Enter_MakesTheFactCurrentUntilTheScopeIsDisposedAsync</tests>
    public void Dispose() => _restore(_previous);

    private static void _restore(bool isFirstEventInStream) => _isFirstEventInStream = isFirstEventInStream;
  }
}
