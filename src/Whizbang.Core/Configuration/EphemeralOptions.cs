// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Configuration;

/// <summary>
/// Options for ephemeral-events runtime behavior — currently how the startup type-definition reconciler
/// handles historical drift when a type's settings change in code (e.g. it gains <c>[Ephemeral]</c>).
/// </summary>
/// <docs>fundamentals/events/type-definition-fingerprint</docs>
public sealed class EphemeralOptions {
  /// <summary>
  /// When <c>true</c>, the startup reconciler ACTS on detected settings drift — e.g. reclassifies a type's
  /// historical Sourced events to Ephemeral (stamp + offload so the reaper can reclaim them). Default
  /// <c>false</c>: drift is only detected and reported (logged + a lineage edge recorded), because
  /// rewriting and reaping historical events on deploy is a deliberate, potentially lossy operation and
  /// keeping the pre-existing history is sometimes the intent. Flip to true, or run the reclassify command
  /// explicitly, to act.
  /// </summary>
  public bool ReconcileHistoricalOnStartup { get; set; }

  /// <summary>
  /// Seconds a consumed ephemeral event body is kept, so an out-of-order straggler can still rewind through it,
  /// before the sweep deletes it. Publishes <c>wh_settings.ephemeral_rewind_grace_seconds</c>; the database default
  /// is 300. Unset (<see langword="null"/>) leaves the stored value alone.
  /// </summary>
  /// <docs>operations/configuration/configuration-reference#database-side-settings</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/RetentionSettingsBindingTests.cs:EphemeralRetention_BindsFromItsEnvironmentVariablesAsync</tests>
  public int? RewindGraceSeconds { get; set; }

  /// <summary>The opt-in prune of ancient ephemeral pointers, under <c>Whizbang:Ephemeral:PointerPrune</c>.</summary>
  /// <docs>operations/configuration/configuration-reference#database-side-settings</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/RetentionSettingsBindingTests.cs:EphemeralRetention_BindsFromItsEnvironmentVariablesAsync</tests>
  public EphemeralPointerPruneOptions PointerPrune { get; set; } = new();
}
