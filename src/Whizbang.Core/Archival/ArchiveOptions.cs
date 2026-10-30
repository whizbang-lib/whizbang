// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// How the event store treats archived detail. Bound from <c>Whizbang:Archive</c>
/// (<c>Whizbang__Archive__ReadThrough</c>).
/// </summary>
/// <docs>fundamentals/events/archive-store#read-through</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/StreamLifecycleOptionsBinderTests.cs</tests>
public sealed class ArchiveOptions {
  /// <summary>The configuration section the options bind from.</summary>
  public const string SECTION = "Whizbang:Archive";

  /// <summary>
  /// Whether a read from the start of a stream reads archived events back when the hot store shows a gap at the start.
  /// Default <see langword="true"/>: a load, a rebuild or a rewind past an archived snapshot returns the same model for an
  /// archived stream as for one never archived. A read that resumes after an event never looks at the archive, whatever
  /// this says. <see langword="false"/> returns only what is hot.
  /// </summary>
  public bool ReadThrough { get; set; } = true;
}
