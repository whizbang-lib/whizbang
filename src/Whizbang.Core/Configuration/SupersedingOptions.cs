// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Configuration;

/// <summary>
/// Options for superseding (latest-wins) event types, bound from <c>Whizbang:Superseding</c> (#2110).
/// </summary>
/// <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/ProcessWideOptionsBindingTests.cs:Superseding_Unconfigured_ReconcilesHistory_AndTheKeyTurnsItOffAsync</tests>
public sealed class SupersedingOptions {
  /// <summary>The configuration section these options bind from.</summary>
  public const string SECTION = "Whizbang:Superseding";

  /// <summary>
  /// When <see langword="true"/> (the default), the maintenance cycle brings the events of a superseding type that were
  /// stored before the type was declared superseding under the same rule: it stamps their flag and key and queues every
  /// one but the newest of each stream, type and key for the superseded-event reaper. Nothing is deleted here; the
  /// reaper deletes a queued event only past every one of its gates (consumed, no cursor or snapshot on it, no
  /// destruction hold, stamped, not debug mode). <see langword="false"/> leaves that history as it was stored.
  /// </summary>
  public bool ReconcileHistory { get; set; } = true;
}
