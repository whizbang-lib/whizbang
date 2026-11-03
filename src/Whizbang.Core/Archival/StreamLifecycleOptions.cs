// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// How streams end. Bound from <c>Whizbang:StreamLifecycle</c>: <c>FinalizeGrace</c> and <c>SweepBatchSize</c> (#1734),
/// <c>Enabled</c> (#1740), and per stream type <c>Whizbang:StreamLifecycle:Types:&lt;StreamType&gt;:AppendToArchived</c>
/// (<c>Whizbang__StreamLifecycle__Types__&lt;StreamType&gt;__AppendToArchived</c>) and the end-of-life policy's
/// <c>IdleTtl</c>, <c>MaxAge</c>, <c>KeepTailVersions</c>, <c>TenantByteCap</c> and <c>Action</c> (#1740).
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

  /// <summary>
  /// Whether the end-of-life policy triggers (<see cref="StreamTypeLifecycleOptions.IdleTtl"/>,
  /// <see cref="StreamTypeLifecycleOptions.MaxAge"/>, <see cref="StreamTypeLifecycleOptions.TenantByteCap"/> and
  /// <see cref="StreamTypeLifecycleOptions.KeepTailVersions"/>) are evaluated (#1740)
  /// (<c>Whizbang__StreamLifecycle__Enabled</c>). Default <see langword="false"/>. The disposition of sealed streams runs
  /// either way.
  /// </summary>
  public bool Enabled { get; set; }

  /// <summary>The <see cref="StreamTypeLifecycleOptions.Action"/> of <paramref name="streamType"/>, or
  /// <see cref="StreamLifecycleAction.Archive"/> for a type not listed or with no type.</summary>
  /// <param name="streamType">The stream type as the lifecycle ledger records it.</param>
  /// <returns>The action.</returns>
  public StreamLifecycleAction ActionFor(string? streamType) => throw new NotImplementedException($"{streamType} of {Types.Count}");

  /// <summary>
  /// Every listed stream type that sets at least one end-of-life trigger, ordered by type, as the disposition step passes
  /// them to the ledger (#1740).
  /// </summary>
  /// <returns>One entry per type with a trigger.</returns>
  public IReadOnlyList<StreamEndOfLifePolicy> EndOfLifePolicies() => throw new NotImplementedException($"{Types.Count}");

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

  /// <summary>
  /// Acts on a stream of this type once nothing has been appended to it for this long (#1740), counted from the end of
  /// the UTC hour of its newest append. Unset: no idle trigger.
  /// </summary>
  public TimeSpan? IdleTtl { get; set; }

  /// <summary>Acts on a stream of this type once this long has passed since its first append (#1740). Unset: no age trigger.</summary>
  public TimeSpan? MaxAge { get; set; }

  /// <summary>
  /// Keeps at most this many of a stream's newest versions hot (#1740): the prefix before them is archived, compacted or
  /// discarded by <see cref="Action"/>. Unset: no version cap.
  /// </summary>
  public long? KeepTailVersions { get; set; }

  /// <summary>
  /// The most bytes (the ledger's <c>approx_bytes</c>) the open streams of this type may hold per tenant (#1740); beyond it
  /// the least recently appended streams are acted on until the rest fit. Unset: no cap.
  /// </summary>
  public long? TenantByteCap { get; set; }

  /// <summary>What happens to a stream of this type at its end of life (#1740). Default <see cref="StreamLifecycleAction.Archive"/>.</summary>
  public StreamLifecycleAction Action { get; set; } = StreamLifecycleAction.Archive;
}

/// <summary>One stream type's end-of-life triggers and action (#1740), as the disposition step passes them to the ledger.</summary>
/// <param name="StreamType">The stream type as the lifecycle ledger records it.</param>
/// <param name="IdleTtl">The idle trigger, or null.</param>
/// <param name="MaxAge">The age trigger, or null.</param>
/// <param name="KeepTailVersions">The version cap, or null.</param>
/// <param name="TenantByteCap">The per-tenant byte cap, or null.</param>
/// <param name="Action">What happens to a stream a trigger picks.</param>
/// <docs>fundamentals/events/stream-end-of-life#policies</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/StreamLifecycleOptionsBinderTests.cs</tests>
public sealed record StreamEndOfLifePolicy(
  string StreamType, TimeSpan? IdleTtl, TimeSpan? MaxAge, long? KeepTailVersions, long? TenantByteCap, StreamLifecycleAction Action);
