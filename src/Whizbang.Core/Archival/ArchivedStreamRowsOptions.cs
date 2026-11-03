// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// The configured policy for rows of archived streams, per perspective (#1739). Bound from
/// <c>Whizbang:Perspectives:ArchivedStreamRows:&lt;Perspective&gt;</c>
/// (<c>Whizbang__Perspectives__ArchivedStreamRows__&lt;Perspective&gt;</c>), whose value is <c>Keep</c>, <c>Drop</c> or
/// <c>Archive</c>; it outranks the perspective's <see cref="Attributes.ArchivedStreamRowsAttribute"/>.
/// </summary>
/// <docs>fundamentals/events/archive-store#perspective-rows</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/ArchivedStreamRowsOptionsBinderTests.cs</tests>
public sealed class ArchivedStreamRowsOptions {
  /// <summary>The configuration section the options bind from.</summary>
  public const string SECTION = "Whizbang:Perspectives:ArchivedStreamRows";

  /// <summary>
  /// Each overridden perspective's policy, keyed by the perspective's CLR type name, the form its cursors and snapshots
  /// are keyed by (for example <c>MyApp.Orders.OrderSummaryPerspective</c>, a nested type as <c>Outer+Inner</c>).
  /// </summary>
  public Dictionary<string, ArchivedStreamRowPolicy> Perspectives { get; } = new(StringComparer.Ordinal);
}
