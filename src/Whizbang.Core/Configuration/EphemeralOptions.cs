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

  /// <summary>
  /// The report of what keeps each ephemeral type's stored history from being reclassified, under
  /// <c>Whizbang:Ephemeral:BlockerReport</c>. On by default: it is recorded by the history walk
  /// (<see cref="ReconcileHistoricalOnStartup"/>) from the streams it already reads, and changes no event.
  /// </summary>
  /// <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
  /// <tests>tests/Whizbang.Core.Tests/Configuration/EphemeralBlockerOptionsTests.cs:Defaults_TheReportIsOn_AndNothingIsReleasedAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Configuration/EphemeralBlockerOptionsTests.cs:BlockerReport_AndReleaseBlockers_BindFromConfigurationAsync</tests>
  public EphemeralBlockerReportOptions BlockerReport { get; set; } = new();

  /// <summary>
  /// Durable event types released for an ephemeral type, under <c>Whizbang:Ephemeral:ReleaseBlockers:&lt;EphemeralType&gt;</c>:
  /// the key is the ephemeral type's name, with or without its assembly, and the value the list of blocking types, each
  /// as the report prints it (<c>Namespace.Type, Assembly</c>) or without its assembly. A released type no longer keeps
  /// the ephemeral type's history Sourced, and its Sourced events in the streams that hold the ephemeral type are
  /// reclassified with it, and only there: never in any other stream. Default empty: no durable event changes without a
  /// setting. Changing a type's list walks its history again.
  /// </summary>
  /// <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
  /// <tests>tests/Whizbang.Core.Tests/Configuration/EphemeralBlockerOptionsTests.cs:Defaults_TheReportIsOn_AndNothingIsReleasedAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Release_StampsTheBlockingEvents_OnlyInStreamsHoldingTheEphemeralType_AndTheDigestsEqualARecountAsync</tests>
  public Dictionary<string, List<string>> ReleaseBlockers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
