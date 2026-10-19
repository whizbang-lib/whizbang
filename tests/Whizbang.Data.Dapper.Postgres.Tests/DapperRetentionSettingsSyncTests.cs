// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Serialization;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// A retention option set in configuration reaches the <c>wh_settings</c> row the sweep reads, on the Dapper engine.
/// </summary>
/// <remarks>
/// <para>
/// The Dapper coordinator inherited the no-op default for the debug-retention sync, so on this engine even
/// <c>debug_mode</c> never reached the sweep: debug retention marked rows the next cycle deleted. Both the
/// retention settings and <c>debug_mode</c> are now written here, by the same statement the EF Core engine
/// uses (<c>RetentionSettingsSyncSqlTests</c> holds its twin of each test).
/// </para>
/// </remarks>
/// <docs>fundamentals/workers/maintenance-steps</docs>
[Category("Integration")]
public class DapperRetentionSettingsSyncTests : PostgresTestBase {

  private static IReadOnlyList<RetentionSetting> _settingsFromConfiguration(Dictionary<string, string?> values, bool debugMode = false) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    services.AddWhizbangWorkers();
    using var provider = services.BuildServiceProvider();
    return RetentionSettingsBridge.SettingsFor(
      debugMode,
      provider.GetRequiredService<IOptions<MaintenanceWorkerOptions>>().Value.Retention,
      provider.GetRequiredService<IOptions<EphemeralOptions>>().Value);
  }

  private DapperWorkCoordinator _coordinator() => new(ConnectionString, JsonContextRegistry.CreateCombinedOptions());

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private async Task<string?> _valueAsync(string key) {
    await using var conn = await _openAsync();
    return await conn.ExecuteScalarAsync<string?>(
      "SELECT setting_value FROM wh_settings WHERE setting_key = @key", new { key });
  }

  [Test]
  public async Task EveryConfiguredValue_LandsUnderTheKeyAndCastTheSweepUsesAsync() {
    await _coordinator().SyncRetentionSettingsAsync(_settingsFromConfiguration(new() {
      ["Whizbang:Workers:Maintenance:Retention:DeduplicationDays"] = "41",
      ["Whizbang:Workers:Maintenance:Retention:StuckInboxDays"] = "42",
      ["Whizbang:Workers:Maintenance:Retention:RecoveredDeadLetterDays"] = "43",
      ["Whizbang:Workers:Maintenance:Retention:AbandonedStreamHours"] = "44",
      ["Whizbang:Workers:Maintenance:Retention:InstanceEvictionTombstoneHours"] = "45",
      ["Whizbang:Workers:Maintenance:Retention:OrphanPerspectiveEventGraceHours"] = "46",
      ["Whizbang:Workers:Maintenance:Retention:CollectiveOrderingHeadDays"] = "47",
      ["Whizbang:Ephemeral:RewindGraceSeconds"] = "48",
      ["Whizbang:Ephemeral:PointerPrune:Enabled"] = "true",
      ["Whizbang:Ephemeral:PointerPrune:RetentionDays"] = "49",
      ["Whizbang:Ephemeral:PointerPrune:IntervalDays"] = "50",
    }));

    await using var conn = await _openAsync();
    // The ::INTEGER cast is the one the sweep applies; the pair is rendered as text only to read it back.
    var integers = (await conn.QueryAsync<string>(@"
      SELECT setting_key || '=' || (setting_value::INTEGER)::TEXT FROM wh_settings
      WHERE setting_key IN ('dedup_retention_days', 'stuck_inbox_retention_days', 'dead_letter_retention_days',
        'abandoned_stream_hours', 'instance_eviction_retention_hours', 'orphan_perspective_grace_hours',
        'collective_ordering_head_retention_days', 'ephemeral_rewind_grace_seconds',
        'ephemeral_pointer_retention_days', 'ephemeral_pointer_prune_interval_days')"))
      .Select(r => r.Split('='))
      .ToDictionary(r => r[0], r => int.Parse(r[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
    await Assert.That(integers).IsEquivalentTo(new Dictionary<string, int> {
      ["dedup_retention_days"] = 41,
      ["stuck_inbox_retention_days"] = 42,
      ["dead_letter_retention_days"] = 43,
      ["abandoned_stream_hours"] = 44,
      ["instance_eviction_retention_hours"] = 45,
      ["orphan_perspective_grace_hours"] = 46,
      ["collective_ordering_head_retention_days"] = 47,
      ["ephemeral_rewind_grace_seconds"] = 48,
      ["ephemeral_pointer_retention_days"] = 49,
      ["ephemeral_pointer_prune_interval_days"] = 50,
    });
    var enabled = await conn.ExecuteScalarAsync<bool>(
      "SELECT setting_value::BOOLEAN FROM wh_settings WHERE setting_key = 'ephemeral_deep_maintenance_enabled'");
    await Assert.That(enabled).IsTrue();
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task DebugMode_ReachesTheSweepOnThisEngineAsync(bool debugMode) {
    await _coordinator().SyncRetentionSettingsAsync(_settingsFromConfiguration([], debugMode));

    await using var conn = await _openAsync();
    var stored = await conn.ExecuteScalarAsync<bool>("SELECT setting_value::BOOLEAN FROM wh_settings WHERE setting_key = 'debug_mode'");
    var publishedBy = await conn.ExecuteScalarAsync<string?>("SELECT updated_by FROM wh_settings WHERE setting_key = 'debug_mode'");
    await Assert.That(stored).IsEqualTo(debugMode)
      .Because("on this engine debug_mode was never written, so the sweep deleted the rows debug retention kept");
    await Assert.That(publishedBy).IsEqualTo("whizbang")
      .Because("the seeded row says false too; only the publisher stamp proves the write happened");
  }

  [Test]
  public async Task TheOlderDebugOnlySync_NowWritesOnThisEngineAsync() {
    await _coordinator().SyncDebugRetentionSettingAsync(debugMode: true);

    await Assert.That(await _valueAsync("debug_mode")).IsEqualTo("true")
      .Because("a caller of the debug-only method on this engine got the no-op default and nothing was stored");
  }

  [Test]
  public async Task AnUnsetOption_LeavesTheValueAnOperatorSetInSqlAsync() {
    await using (var conn = await _openAsync()) {
      await conn.ExecuteAsync("UPDATE wh_settings SET setting_value = '45', updated_by = 'operator' WHERE setting_key = 'dedup_retention_days'");
    }

    await _coordinator().SyncRetentionSettingsAsync(_settingsFromConfiguration(new() {
      ["Whizbang:Workers:Maintenance:Retention:StuckInboxDays"] = "2",
    }));

    await Assert.That(await _valueAsync("dedup_retention_days")).IsEqualTo("45");
    await Assert.That(await _valueAsync("stuck_inbox_retention_days")).IsEqualTo("2");
  }

  [Test]
  public async Task AConfiguredWindow_IsTheOneTheSweepAppliesAsync() {
    var twoDaysOld = Guid.CreateVersion7();
    await using (var conn = await _openAsync()) {
      await conn.ExecuteAsync(
        "INSERT INTO wh_message_deduplication (message_id, first_seen_at) VALUES (@id, NOW() - INTERVAL '2 days')",
        new { id = twoDaysOld });
    }

    var coordinator = _coordinator();
    await coordinator.SyncRetentionSettingsAsync(_settingsFromConfiguration(new() {
      ["Whizbang:Workers:Maintenance:Retention:DeduplicationDays"] = "1",
    }));
    await coordinator.PerformMaintenanceAsync();

    await using var verify = await _openAsync();
    var remaining = await verify.ExecuteScalarAsync<long>(
      "SELECT count(*) FROM wh_message_deduplication WHERE message_id = @id", new { id = twoDaysOld });
    await Assert.That(remaining).IsEqualTo(0L)
      .Because("with the 30-day default the row would stay; it goes only if the sweep read the configured one-day window");
  }

  [Test]
  public async Task RepeatedCycles_UpsertOneRowPerKey_AndAChangeOverwritesAsync() {
    var coordinator = _coordinator();
    for (var i = 0; i < 3; i++) {
      await coordinator.SyncRetentionSettingsAsync(_settingsFromConfiguration(new() {
        ["Whizbang:Workers:Maintenance:Retention:RecoveredDeadLetterDays"] = "5",
      }));
    }
    await coordinator.SyncRetentionSettingsAsync(_settingsFromConfiguration(new() {
      ["Whizbang:Workers:Maintenance:Retention:RecoveredDeadLetterDays"] = "6",
    }));

    await using var conn = await _openAsync();
    var rows = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_settings WHERE setting_key = 'dead_letter_retention_days'");
    await Assert.That(rows).IsEqualTo(1L);
    await Assert.That(await _valueAsync("dead_letter_retention_days")).IsEqualTo("6");
  }

  [Test]
  public async Task NothingToWrite_ChangesNothingAsync() {
    await using var conn = await _openAsync();
    var before = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_settings WHERE updated_by = 'whizbang'");

    await _coordinator().SyncRetentionSettingsAsync([]);

    var after = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_settings WHERE updated_by = 'whizbang'");
    await Assert.That(after).IsEqualTo(before);
  }
}
