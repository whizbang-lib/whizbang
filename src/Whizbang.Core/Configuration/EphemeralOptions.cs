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
  /// When <c>true</c>, the historical Sourced events of every ephemeral event type are reclassified as
  /// Ephemeral so the reaper can reclaim them. Since #2206 the maintenance cycle does it (the
  /// <c>ephemeral-history-reclassify</c> step), in bounded batches through the per-type history walker, so
  /// startup never waits on it; the name is kept. Default <c>false</c>: drift is only detected and reported
  /// (logged + a lineage edge recorded), because rewriting and reaping historical events is a deliberate,
  /// potentially lossy operation and keeping the pre-existing history is sometimes the intent.
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
