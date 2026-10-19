// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Every retention knob the maintenance sweep reads from <c>wh_settings</c> is reachable from configuration.
/// </summary>
/// <remarks>
/// <para>
/// The sweep decides each retention window from a row in <c>wh_settings</c>. Only <c>debug_mode</c> was ever
/// written from an option, so every other window could be changed only with a hand-written <c>UPDATE</c> in
/// every service database, and a value set that way was invisible to anyone reading the configuration.
/// </para>
/// <para>
/// The bridge is the one table that maps an option to the key the SQL reads and to the literal it casts.
/// A property left unset is not written, so a value an operator already stored in SQL stands; a property
/// that is set is published every cycle.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/RetentionSettingsBridge.cs</code-under-test>
[Category("Workers")]
public class RetentionSettingsBridgeTests {

  private static readonly string[] _debugOnly = ["debug_mode"];
  private static readonly string[] _debugStuckInboxAndInterval =
    ["debug_mode", "stuck_inbox_retention_days", "ephemeral_pointer_prune_interval_days"];
  private static readonly string[] _everyKey = [
    "debug_mode",
    "dedup_retention_days", "stuck_inbox_retention_days", "dead_letter_retention_days", "abandoned_stream_hours",
    "instance_eviction_retention_hours", "orphan_perspective_grace_hours", "collective_ordering_head_retention_days",
    "ephemeral_rewind_grace_seconds", "ephemeral_deep_maintenance_enabled",
    "ephemeral_pointer_retention_days", "ephemeral_pointer_prune_interval_days",
  ];

  private static MaintenanceRetentionOptions _allRetentionSet() => new() {
    DeduplicationDays = 11,
    StuckInboxDays = 12,
    RecoveredDeadLetterDays = 13,
    AbandonedStreamHours = 14,
    InstanceEvictionTombstoneHours = 15,
    OrphanPerspectiveEventGraceHours = 16,
    CollectiveOrderingHeadDays = 17,
  };

  private static EphemeralOptions _allEphemeralSet() => new() {
    RewindGraceSeconds = 18,
    PointerPrune = new EphemeralPointerPruneOptions {
      Enabled = true,
      RetentionDays = 19,
      IntervalDays = 20,
    },
  };

  private static Dictionary<string, RetentionSetting> _byKey(IReadOnlyList<RetentionSetting> settings)
    => settings.ToDictionary(s => s.Key, StringComparer.Ordinal);

  [Test]
  [Arguments("dedup_retention_days", "11", "Whizbang:Workers:Maintenance:Retention:DeduplicationDays")]
  [Arguments("stuck_inbox_retention_days", "12", "Whizbang:Workers:Maintenance:Retention:StuckInboxDays")]
  [Arguments("dead_letter_retention_days", "13", "Whizbang:Workers:Maintenance:Retention:RecoveredDeadLetterDays")]
  [Arguments("abandoned_stream_hours", "14", "Whizbang:Workers:Maintenance:Retention:AbandonedStreamHours")]
  [Arguments("instance_eviction_retention_hours", "15", "Whizbang:Workers:Maintenance:Retention:InstanceEvictionTombstoneHours")]
  [Arguments("orphan_perspective_grace_hours", "16", "Whizbang:Workers:Maintenance:Retention:OrphanPerspectiveEventGraceHours")]
  [Arguments("collective_ordering_head_retention_days", "17", "Whizbang:Workers:Maintenance:Retention:CollectiveOrderingHeadDays")]
  [Arguments("ephemeral_rewind_grace_seconds", "18", "Whizbang:Ephemeral:RewindGraceSeconds")]
  [Arguments("ephemeral_pointer_retention_days", "19", "Whizbang:Ephemeral:PointerPrune:RetentionDays")]
  [Arguments("ephemeral_pointer_prune_interval_days", "20", "Whizbang:Ephemeral:PointerPrune:IntervalDays")]
  public async Task EachIntegerOption_PublishesItsKeyAsAnIntegerLiteralAsync(string key, string value, string source) {
    var settings = _byKey(RetentionSettingsBridge.SettingsFor(false, _allRetentionSet(), _allEphemeralSet()));

    await Assert.That(settings.ContainsKey(key)).IsTrue()
      .Because("the sweep looks up this exact key; a value written under any other name leaves the default in force");
    await Assert.That(settings[key].Value).IsEqualTo(value)
      .Because("the sweep casts the text with ::INTEGER, so the literal must be the plain invariant number");
    await Assert.That(settings[key].ValueType).IsEqualTo("integer");
    await Assert.That(settings[key].Source).IsEqualTo(source)
      .Because("the stored row names the configuration key it came from, so an operator reading wh_settings can find it");
  }

  [Test]
  [Arguments(true, "true")]
  [Arguments(false, "false")]
  public async Task PointerPruneEnabled_PublishesALowercaseBooleanAsync(bool enabled, string literal) {
    var ephemeral = new EphemeralOptions { PointerPrune = new EphemeralPointerPruneOptions { Enabled = enabled } };
    var settings = _byKey(RetentionSettingsBridge.SettingsFor(false, new MaintenanceRetentionOptions(), ephemeral));

    await Assert.That(settings["ephemeral_deep_maintenance_enabled"].Value).IsEqualTo(literal)
      .Because("the prune casts the text with ::BOOLEAN, and switching it off must propagate as surely as switching it on");
    await Assert.That(settings["ephemeral_deep_maintenance_enabled"].ValueType).IsEqualTo("boolean");
    await Assert.That(settings["ephemeral_deep_maintenance_enabled"].Source)
      .IsEqualTo("Whizbang:Ephemeral:PointerPrune:Enabled");
  }

  [Test]
  public async Task UnsetOptions_AreNotWritten_SoAValueSetInSqlStandsAsync() {
    var settings = RetentionSettingsBridge.SettingsFor(false, new MaintenanceRetentionOptions(), new EphemeralOptions());

    await Assert.That(settings.Select(s => s.Key).ToArray()).IsEquivalentTo(_debugOnly)
      .Because("an unset option has no opinion: writing the framework default would silently overwrite a value "
             + "an operator set in SQL before this option existed");
  }

  [Test]
  public async Task OnlyTheSetOptions_AreWrittenAsync() {
    var retention = new MaintenanceRetentionOptions { StuckInboxDays = 3 };
    var ephemeral = new EphemeralOptions { PointerPrune = new EphemeralPointerPruneOptions { IntervalDays = 4 } };

    var keys = RetentionSettingsBridge.SettingsFor(false, retention, ephemeral).Select(s => s.Key).ToArray();

    await Assert.That(keys).IsEquivalentTo(_debugStuckInboxAndInterval);
  }

  [Test]
  public async Task EveryKeyInScope_IsMappedExactlyOnceAsync() {
    var keys = RetentionSettingsBridge.SettingsFor(true, _allRetentionSet(), _allEphemeralSet()).Select(s => s.Key).ToArray();

    await Assert.That(keys).IsEquivalentTo(_everyKey).Because("one upsert carries every set value; a key listed twice would fail it with "
             + "'ON CONFLICT DO UPDATE command cannot affect row a second time'");
    await Assert.That(RetentionSettingsBridge.SettingKeys).IsEquivalentTo(keys);
  }

  [Test]
  [Arguments(true, "true")]
  [Arguments(false, "false")]
  public async Task DebugMode_IsAlwaysPublishedInBothDirectionsAsync(bool debugMode, string literal) {
    var settings = _byKey(RetentionSettingsBridge.SettingsFor(debugMode, new MaintenanceRetentionOptions(), new EphemeralOptions()));

    await Assert.That(settings["debug_mode"].Value).IsEqualTo(literal)
      .Because("a stale true would disable the purge forever, so debug retention is pushed both ways every cycle");
    await Assert.That(settings["debug_mode"].ValueType).IsEqualTo("boolean");
    await Assert.That(settings["debug_mode"].Source).IsEqualTo("Whizbang:WorkCoordinator:DebugMode");
  }

  [Test]
  public async Task DebugModeSetting_MatchesTheDebugBridgeAsync() {
    var setting = RetentionSettingsBridge.DebugModeSetting(true);

    await Assert.That(setting.Key).IsEqualTo(DebugRetentionBridge.SettingKey);
    await Assert.That(setting.Value).IsEqualTo(DebugRetentionBridge.SettingValueFor(true));
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task DebugModeIn_ReadsTheDebugEntryBackAsync(bool debugMode) {
    var settings = RetentionSettingsBridge.SettingsFor(debugMode, _allRetentionSet(), _allEphemeralSet());

    await Assert.That(RetentionSettingsBridge.DebugModeIn(settings)).IsEqualTo(debugMode);
  }

  [Test]
  public async Task DebugModeIn_WithNoDebugEntry_IsNullAsync() {
    var settings = new[] { new RetentionSetting("dedup_retention_days", "3", "integer", "x") };

    await Assert.That(RetentionSettingsBridge.DebugModeIn(settings)).IsNull()
      .Because("absent is not false: reading it as false would switch debug retention off on an engine that was never asked");
  }

  [Test]
  public async Task SettingsFor_RejectsMissingOptionObjectsAsync() {
    await Assert.That(() => RetentionSettingsBridge.SettingsFor(false, null!, new EphemeralOptions()))
      .Throws<ArgumentNullException>();
    await Assert.That(() => RetentionSettingsBridge.SettingsFor(false, new MaintenanceRetentionOptions(), null!))
      .Throws<ArgumentNullException>();
    await Assert.That(() => RetentionSettingsBridge.SettingsFor(false, new MaintenanceRetentionOptions(),
        new EphemeralOptions { PointerPrune = null! }))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Defaults_LeaveEveryRetentionOptionUnsetAsync() {
    var retention = new MaintenanceWorkerOptions().Retention;
    var ephemeral = new EphemeralOptions();

    await Assert.That(retention).IsNotNull()
      .Because("the nested object is bound in place under Whizbang:Workers:Maintenance:Retention, so it must exist");
    await Assert.That(ephemeral.PointerPrune).IsNotNull();
    await Assert.That(RetentionSettingsBridge.SettingsFor(false, retention, ephemeral).Count).IsEqualTo(1)
      .Because("a host that configures nothing must change nothing in the database beyond what it did before");
  }
}
