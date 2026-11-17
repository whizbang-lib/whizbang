// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Configuration;

/// <summary>
/// The ephemeral blocker report, settable under <c>Whizbang:Ephemeral:BlockerReport</c>: which durable (Sourced) event
/// types keep each ephemeral type's stored history from being reclassified, with counts, a sample stream, the route that
/// writes them and what to do about each.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/EphemeralBlockerOptionsTests.cs</tests>
public sealed class EphemeralBlockerReportOptions {
  /// <summary>
  /// Whether the history walk records what holds each ephemeral type back, logs it when the type's walk finishes and at
  /// startup, and publishes the <c>whizbang.ephemeral.blocked_events</c> gauge. Default <see langword="true"/>: the
  /// report reads only the streams the walk already locks and changes no event. When <see langword="false"/> nothing is
  /// recorded, logged or gauged, and the walk runs exactly as it does without the report.
  /// </summary>
  public bool Enabled { get; set; } = true;
}
