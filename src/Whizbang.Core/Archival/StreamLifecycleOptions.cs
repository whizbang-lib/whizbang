// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// How streams end. Bound from <c>Whizbang:StreamLifecycle</c>: <c>FinalizeGrace</c> and <c>SweepBatchSize</c> (#1734), and
/// per stream type <c>Whizbang:StreamLifecycle:Types:&lt;StreamType&gt;:AppendToArchived</c>
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

  /// <summary>
  /// How long a sealed stream stays hot before its disposition runs (#1734), counted from the later of the seal and the
  /// first maintenance cycle that sees it (<c>Whizbang__StreamLifecycle__FinalizeGrace</c>). Default 7 days; zero disposes on the next
  /// maintenance cycle after the seal is seen.
  /// </summary>
  public TimeSpan FinalizeGrace { get; set; } = TimeSpan.FromDays(7);

  /// <summary>
  /// The most streams one maintenance cycle schedules, and the most it disposes of (#1734)
  /// (<c>Whizbang__StreamLifecycle__SweepBatchSize</c>). Default 500.
  /// </summary>
  public int SweepBatchSize { get; set; } = 500;

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
