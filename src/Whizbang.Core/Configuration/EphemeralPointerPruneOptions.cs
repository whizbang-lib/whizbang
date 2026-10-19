// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Configuration;

/// <summary>
/// The opt-in prune of ancient ephemeral pointers, settable from configuration under
/// <c>Whizbang:Ephemeral:PointerPrune</c>.
/// </summary>
/// <remarks>
/// Each property publishes one <c>wh_settings</c> row, written by the maintenance cycle. Every property defaults to
/// unset (<see langword="null"/>), which leaves the stored value alone; a value that is set is written every cycle.
/// </remarks>
/// <docs>operations/configuration/configuration-reference#database-side-settings</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/RetentionSettingsBridgeTests.cs</tests>
public sealed class EphemeralPointerPruneOptions {
  /// <summary>
  /// Whether the prune runs at all. Publishes <c>ephemeral_deep_maintenance_enabled</c>; the database default is
  /// <see langword="false"/>.
  /// </summary>
  public bool? Enabled { get; set; }

  /// <summary>
  /// Days a reaped ephemeral pointer is kept before it may be pruned. Publishes
  /// <c>ephemeral_pointer_retention_days</c>; the database default is 90. The prune never uses a horizon shorter
  /// than the deduplication window.
  /// </summary>
  public int? RetentionDays { get; set; }

  /// <summary>
  /// Days between prunes, however often maintenance runs. Publishes <c>ephemeral_pointer_prune_interval_days</c>;
  /// the database default is 30.
  /// </summary>
  public int? IntervalDays { get; set; }
}
