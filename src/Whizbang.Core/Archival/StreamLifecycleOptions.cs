// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// How streams end, per stream type. Bound from <c>Whizbang:StreamLifecycle</c>; this slice reads
/// <c>Whizbang:StreamLifecycle:Types:&lt;StreamType&gt;:AppendToArchived</c>
/// (<c>Whizbang__StreamLifecycle__Types__&lt;StreamType&gt;__AppendToArchived</c>).
/// </summary>
/// <docs>fundamentals/events/archive-store#the-policy-per-stream-type</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/StreamLifecycleOptionsBinderTests.cs</tests>
public sealed class StreamLifecycleOptions {
  /// <summary>The configuration section the options bind from.</summary>
  public const string SECTION = "Whizbang:StreamLifecycle";

  /// <summary>
  /// The settings of each stream type, keyed by the stream type as the lifecycle ledger records it: the type name of the
  /// stream's first event, without its assembly. A type not listed takes the defaults.
  /// </summary>
  public Dictionary<string, StreamTypeLifecycleOptions> Types { get; } = new(StringComparer.Ordinal);

  /// <summary>Every listed stream type's append-to-archived policy, ordered by type, as the write paths store it.</summary>
  /// <returns>One entry per listed type.</returns>
  public IReadOnlyList<StreamTypePolicy> AppendToArchivedPolicies() =>
    [.. Types.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => new StreamTypePolicy(t.Key, t.Value.AppendToArchived))];
}

/// <summary>The lifecycle settings of one stream type.</summary>
/// <docs>fundamentals/events/archive-store#the-policy-per-stream-type</docs>
public sealed class StreamTypeLifecycleOptions {
  /// <summary>What an append to an archived stream of this type does. Default <see cref="AppendToArchivedPolicy.Refuse"/>.</summary>
  public AppendToArchivedPolicy AppendToArchived { get; set; } = AppendToArchivedPolicy.Refuse;
}
