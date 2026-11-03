// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Lifecycle;

namespace Whizbang.Core.Archival;

/// <summary>
/// What happens to a stream of a type when its end of life comes (#1740, WS-3 S11), declared per stream type
/// (<c>Whizbang:StreamLifecycle:Types:&lt;StreamType&gt;:Action</c>): when one of its policy triggers fires, and when a
/// sealed stream's grace has passed. The PreDestruction hook sees it as the declared disposition and may return another.
/// </summary>
/// <docs>fundamentals/events/stream-end-of-life#policies</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/StreamLifecycleOptionsBinderTests.cs</tests>
public enum StreamLifecycleAction {
  /// <summary>
  /// The default. The whole stream (or, for the version cap, its prefix) is written through the <see cref="IArchiveStore"/>
  /// and stays readable through read-through.
  /// </summary>
  Archive = 0,

  /// <summary>
  /// The registered <see cref="ICompactionHandler"/> runs, then the detail is discarded; with no handler registered nothing
  /// is discarded.
  /// </summary>
  Compact = 1,

  /// <summary>The whole stream is purged (<see cref="Messaging.IStreamPurger"/>), or, for the version cap, its prefix discarded.</summary>
  Purge = 2,

  /// <summary>
  /// The stream's books are closed (#1741): the registered <see cref="IBookClosingHandler"/> supplies the closing summary
  /// and the opening balance, and <see cref="IBookCloser"/> appends the summary, seals the stream and opens its successor.
  /// The sealed predecessor is then archived once its grace has passed. A host that declares it without a handler fails
  /// its start.
  /// </summary>
  CloseBooks = 3,
}

/// <summary>Maps a <see cref="StreamLifecycleAction"/> to the <see cref="Disposition"/> every destruction routes on.</summary>
/// <docs>fundamentals/events/stream-end-of-life#policies</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/StreamLifecycleOptionsBinderTests.cs</tests>
public static class StreamLifecycleActionExtensions {
  /// <summary>The disposition the action runs: Archive, Compact, or Delete for Purge.</summary>
  /// <param name="action">The action.</param>
  /// <returns>The disposition.</returns>
  public static Disposition ToDisposition(this StreamLifecycleAction action) => action switch {
    StreamLifecycleAction.Compact => Disposition.Compact,
    StreamLifecycleAction.Purge => Disposition.Delete,
    _ => Disposition.Archive,
  };
}
