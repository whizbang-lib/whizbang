// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Whizbang.Core.Configuration;

namespace Whizbang.Core.Workers;

/// <summary>
/// One database-side setting the host publishes to <c>wh_settings</c>.
/// </summary>
/// <param name="Key">The <c>wh_settings.setting_key</c> the SQL reads, such as <c>dedup_retention_days</c>.</param>
/// <param name="Value">The literal stored in <c>setting_value</c>, in the exact form the SQL casts.</param>
/// <param name="ValueType">The <c>value_type</c> recorded with it: <c>integer</c> or <c>boolean</c>.</param>
/// <param name="Source">The configuration key the value came from, recorded as the row's description when it is first written.</param>
/// <docs>operations/configuration/configuration-reference#database-side-settings</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/RetentionSettingsBridgeTests.cs</tests>
public readonly record struct RetentionSetting(string Key, string Value, string ValueType, string Source);

/// <summary>
/// Carries the host's retention options to the <c>wh_settings</c> rows the maintenance sweep reads.
/// </summary>
/// <remarks>
/// <para>
/// Each retention window the sweep applies is a row in <c>wh_settings</c>. Only <c>debug_mode</c> was ever
/// written from an option (<see cref="DebugRetentionBridge"/>); every other window could be changed only with a
/// hand-written <c>UPDATE</c> in every service database. This is the one table that maps each option to the key
/// the SQL reads and the literal it casts, and the maintenance cycle publishes what it returns in one write.
/// </para>
/// <para>
/// A property left unset (<see langword="null"/>) is not returned, so it is never written: a value an operator
/// already stored in SQL stands until the option is set. A property that is set is published every cycle, so a
/// value changed in SQL behind a configured option is put back on the next cycle. <c>debug_mode</c> is the
/// exception: it is always returned, in both directions, because a stale <see langword="true"/> would disable the
/// purge for good.
/// </para>
/// </remarks>
/// <docs>operations/configuration/configuration-reference#database-side-settings</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/RetentionSettingsBridgeTests.cs</tests>
public static class RetentionSettingsBridge {
  private const string INTEGER = "integer";
  private const string BOOLEAN = "boolean";
  private const string MAINTENANCE = "Whizbang:Workers:Maintenance:Retention:";
  private const string EPHEMERAL = "Whizbang:Ephemeral:";
  private const string POINTER_PRUNE = "Whizbang:Ephemeral:PointerPrune:";
  private const string POINTER_PRUNE_ENABLED_KEY = "ephemeral_deep_maintenance_enabled";

  private sealed record IntegerSetting<TSource>(string Key, string Source, Func<TSource, int?> Read);

  private static readonly IntegerSetting<MaintenanceRetentionOptions>[] _maintenance = [
    new("dedup_retention_days", MAINTENANCE + nameof(MaintenanceRetentionOptions.DeduplicationDays), o => o.DeduplicationDays),
    new("stuck_inbox_retention_days", MAINTENANCE + nameof(MaintenanceRetentionOptions.StuckInboxDays), o => o.StuckInboxDays),
    new("dead_letter_retention_days", MAINTENANCE + nameof(MaintenanceRetentionOptions.RecoveredDeadLetterDays), o => o.RecoveredDeadLetterDays),
    new("abandoned_stream_hours", MAINTENANCE + nameof(MaintenanceRetentionOptions.AbandonedStreamHours), o => o.AbandonedStreamHours),
    new("instance_eviction_retention_hours", MAINTENANCE + nameof(MaintenanceRetentionOptions.InstanceEvictionTombstoneHours), o => o.InstanceEvictionTombstoneHours),
    new("orphan_perspective_grace_hours", MAINTENANCE + nameof(MaintenanceRetentionOptions.OrphanPerspectiveEventGraceHours), o => o.OrphanPerspectiveEventGraceHours),
    new("collective_ordering_head_retention_days", MAINTENANCE + nameof(MaintenanceRetentionOptions.CollectiveOrderingHeadDays), o => o.CollectiveOrderingHeadDays),
    new("log_retention_days", MAINTENANCE + nameof(MaintenanceRetentionOptions.LogDays), o => o.LogDays),
    new("schedule_run_retention_days", MAINTENANCE + nameof(MaintenanceRetentionOptions.ScheduleRunDays), o => o.ScheduleRunDays),
    new("stream_purge_audit_retention_days", MAINTENANCE + nameof(MaintenanceRetentionOptions.StreamPurgeAuditDays), o => o.StreamPurgeAuditDays),
    new("integrity_ledger_max_entries", MAINTENANCE + nameof(MaintenanceRetentionOptions.IntegrityLedgerMaxEntries), o => o.IntegrityLedgerMaxEntries),
  ];

  private static readonly IntegerSetting<EphemeralOptions>[] _ephemeral = [
    new("ephemeral_rewind_grace_seconds", EPHEMERAL + nameof(EphemeralOptions.RewindGraceSeconds), o => o.RewindGraceSeconds),
    new("ephemeral_pointer_retention_days", POINTER_PRUNE + nameof(EphemeralPointerPruneOptions.RetentionDays), o => o.PointerPrune.RetentionDays),
    new("ephemeral_pointer_prune_interval_days", POINTER_PRUNE + nameof(EphemeralPointerPruneOptions.IntervalDays), o => o.PointerPrune.IntervalDays),
  ];

  /// <summary>Every <c>wh_settings</c> key this bridge can publish, <c>debug_mode</c> included.</summary>
  /// <docs>operations/configuration/configuration-reference#database-side-settings</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/RetentionSettingsBridgeTests.cs:EveryKeyInScope_IsMappedExactlyOnceAsync</tests>
  public static IReadOnlyList<string> SettingKeys { get; } = [
    DebugRetentionBridge.SettingKey,
    .. _maintenance.Select(s => s.Key),
    .. _ephemeral.Select(s => s.Key),
    POINTER_PRUNE_ENABLED_KEY,
  ];

  /// <summary>
  /// The settings one maintenance cycle publishes: <c>debug_mode</c> always, and each retention option that is set.
  /// </summary>
  /// <param name="debugMode">The host's <c>WorkCoordinatorOptions.DebugMode</c>.</param>
  /// <param name="retention">The bound <c>Whizbang:Workers:Maintenance:Retention</c> options.</param>
  /// <param name="ephemeral">The bound <c>Whizbang:Ephemeral</c> options.</param>
  /// <returns>One entry per key to write, each key at most once.</returns>
  /// <exception cref="ArgumentNullException">An options object, or <see cref="EphemeralOptions.PointerPrune"/>, is null.</exception>
  /// <docs>operations/configuration/configuration-reference#database-side-settings</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/RetentionSettingsBridgeTests.cs:UnsetOptions_AreNotWritten_SoAValueSetInSqlStandsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/RetentionSettingsBridgeTests.cs:EachIntegerOption_PublishesItsKeyAsAnIntegerLiteralAsync</tests>
  public static IReadOnlyList<RetentionSetting> SettingsFor(
      bool debugMode, MaintenanceRetentionOptions retention, EphemeralOptions ephemeral) {
    ArgumentNullException.ThrowIfNull(retention);
    ArgumentNullException.ThrowIfNull(ephemeral);
    ArgumentNullException.ThrowIfNull(ephemeral.PointerPrune);

    var settings = new List<RetentionSetting>(SettingKeys.Count) { DebugModeSetting(debugMode) };
    _addSet(settings, _maintenance, retention);
    _addSet(settings, _ephemeral, ephemeral);
    if (ephemeral.PointerPrune.Enabled is bool enabled) {
      settings.Add(new RetentionSetting(POINTER_PRUNE_ENABLED_KEY, DebugRetentionBridge.SettingValueFor(enabled), BOOLEAN,
        POINTER_PRUNE + nameof(EphemeralPointerPruneOptions.Enabled)));
    }
    return settings;
  }

  /// <summary>Stub (RED).</summary>
  public static IReadOnlyList<RetentionSetting> SettingsFor(
      bool debugMode, MaintenanceRetentionOptions retention, EphemeralOptions ephemeral, MaintenancePartitioningOptions partitioning) =>
    throw new NotImplementedException();

  /// <summary>The <c>debug_mode</c> setting for a debug-retention option, in both directions.</summary>
  /// <param name="debugMode">Whether completed rows are retained.</param>
  /// <returns>The setting to publish.</returns>
  /// <docs>fundamentals/workers/maintenance-steps</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/RetentionSettingsBridgeTests.cs:DebugModeSetting_MatchesTheDebugBridgeAsync</tests>
  public static RetentionSetting DebugModeSetting(bool debugMode) =>
    new(DebugRetentionBridge.SettingKey, DebugRetentionBridge.SettingValueFor(debugMode), BOOLEAN, "Whizbang:WorkCoordinator:DebugMode");

  /// <summary>Reads the <c>debug_mode</c> entry back from a set of settings.</summary>
  /// <param name="settings">The settings a cycle publishes.</param>
  /// <returns>The debug-retention value, or <see langword="null"/> when the set carries no <c>debug_mode</c> entry.</returns>
  /// <remarks>
  /// Lets an engine that implements only <see cref="Messaging.IWorkCoordinator.SyncDebugRetentionSettingAsync"/> keep
  /// receiving debug retention through the default <see cref="Messaging.IWorkCoordinator.SyncRetentionSettingsAsync"/>.
  /// Absent is not <see langword="false"/>: reading it as false would switch debug retention off unasked.
  /// </remarks>
  /// <docs>fundamentals/workers/maintenance-steps</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/RetentionSettingsBridgeTests.cs:DebugModeIn_WithNoDebugEntry_IsNullAsync</tests>
  public static bool? DebugModeIn(IReadOnlyList<RetentionSetting> settings) {
    ArgumentNullException.ThrowIfNull(settings);
    foreach (var setting in settings) {
      if (setting.Key == DebugRetentionBridge.SettingKey) {
        return setting.Value == DebugRetentionBridge.SettingValueFor(true);
      }
    }
    return null;
  }

  /// <summary>Configuration errors in the maintenance retention options: each negative value, by its key.</summary>
  /// <param name="retention">The bound options.</param>
  /// <returns>One message per invalid value; empty when the options are valid.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Workers/RetentionSettingsValidationTests.cs:ANegativeMaintenanceRetention_IsRejectedByNameAsync</tests>
  internal static List<string> Validate(MaintenanceRetentionOptions? retention) =>
    retention is null
      ? [$"{MAINTENANCE.TrimEnd(':')} must not be null."]
      : _negatives(_maintenance, retention);

  /// <summary>Configuration errors in the ephemeral options: each negative value, by its key.</summary>
  /// <param name="ephemeral">The bound options.</param>
  /// <returns>One message per invalid value; empty when the options are valid.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Workers/RetentionSettingsValidationTests.cs:ANegativeEphemeralSetting_IsRejectedByNameAsync</tests>
  internal static List<string> Validate(EphemeralOptions ephemeral) =>
    ephemeral.PointerPrune is null
      ? [$"{POINTER_PRUNE.TrimEnd(':')} must not be null."]
      : _negatives(_ephemeral, ephemeral);

  private static void _addSet<TSource>(List<RetentionSetting> settings, IntegerSetting<TSource>[] table, TSource source) {
    foreach (var entry in table) {
      if (entry.Read(source) is int value) {
        settings.Add(new RetentionSetting(entry.Key, value.ToString(CultureInfo.InvariantCulture), INTEGER, entry.Source));
      }
    }
  }

  // A negative window puts the sweep's cutoff (NOW() - n * INTERVAL) in the future, so every row of the table
  // would become eligible at once. Zero is allowed: it deliberately keeps nothing past the current cycle.
  private static List<string> _negatives<TSource>(IntegerSetting<TSource>[] table, TSource source) {
    var failures = new List<string>();
    foreach (var entry in table) {
      if (entry.Read(source) is int value && value < 0) {
        failures.Add($"{entry.Source} must be zero or greater (was {value.ToString(CultureInfo.InvariantCulture)}); "
                   + $"it sets wh_settings.{entry.Key}, and a negative window makes every row of that table eligible at once.");
      }
    }
    return failures;
  }
}
