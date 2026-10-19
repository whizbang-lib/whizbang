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
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A retention option set in configuration reaches the <c>wh_settings</c> row the sweep reads, on the EF Core engine.
/// </summary>
/// <remarks>
/// <para>
/// Each test takes the path a maintenance cycle takes: configuration bound by the turnkey pipeline, the bridge
/// turning the options into settings, one write by the coordinator. Only a real database proves the write lands
/// under the exact key the sweep looks up, survives the cast the sweep applies, and leaves alone a value an
/// operator stored in SQL for an option left unset.
/// </para>
/// <para>The Dapper engine has the same tests in <c>DapperRetentionSettingsSyncTests</c>.</para>
/// </remarks>
/// <docs>fundamentals/workers/maintenance-steps</docs>
[Category("Integration")]
[Category("Shard3")]
public class RetentionSettingsSyncSqlTests : EFCoreTestBase {

  private static readonly string[] _publisher = ["whizbang"];

  private static readonly Dictionary<string, string?> _everyOption = new() {
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
  };

  private static IReadOnlyList<RetentionSetting> _settingsFromConfiguration(Dictionary<string, string?> values) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    services.AddWhizbangWorkers();
    using var provider = services.BuildServiceProvider();
    return RetentionSettingsBridge.SettingsFor(
      debugMode: false,
      provider.GetRequiredService<IOptions<MaintenanceWorkerOptions>>().Value.Retention,
      provider.GetRequiredService<IOptions<EphemeralOptions>>().Value);
  }

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

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
    await using var ctx = CreateDbContext();
    await _coordinator(ctx).SyncRetentionSettingsAsync(_settingsFromConfiguration(_everyOption));

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
    }).Because("the sweep reads each key with ::INTEGER; a row under another name, or text the cast rejects, "
             + "leaves the default in force or fails the whole sweep");

    var enabled = await conn.ExecuteScalarAsync<bool>(
      "SELECT setting_value::BOOLEAN FROM wh_settings WHERE setting_key = 'ephemeral_deep_maintenance_enabled'");
    await Assert.That(enabled).IsTrue();

    var publishedBy = (await conn.QueryAsync<string>(
      "SELECT DISTINCT updated_by FROM wh_settings WHERE setting_key = ANY(@keys)",
      new { keys = RetentionSettingsBridge.SettingKeys.ToArray() })).ToArray();
    await Assert.That(publishedBy).IsEquivalentTo(_publisher)
      .Because("a row the framework published says so, which tells it apart from one an operator wrote by hand");
  }

  [Test]
  public async Task AnUnsetOption_LeavesTheValueAnOperatorSetInSqlAsync() {
    await using (var conn = await _openAsync()) {
      await conn.ExecuteAsync("UPDATE wh_settings SET setting_value = '45', updated_by = 'operator' WHERE setting_key = 'dedup_retention_days'");
    }

    await using var ctx = CreateDbContext();
    await _coordinator(ctx).SyncRetentionSettingsAsync(_settingsFromConfiguration(new() {
      ["Whizbang:Workers:Maintenance:Retention:StuckInboxDays"] = "2",
    }));

    await Assert.That(await _valueAsync("dedup_retention_days")).IsEqualTo("45")
      .Because("an option left unset has no opinion; overwriting the operator's value with a default would undo it every cycle");
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

    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
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
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
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
    await Assert.That(rows).IsEqualTo(1L)
      .Because("this runs every cycle; an insert that did not upsert would fail on the key or leave duplicates");
    await Assert.That(await _valueAsync("dead_letter_retention_days")).IsEqualTo("6");
  }

  [Test]
  public async Task NothingToWrite_ChangesNothingAsync() {
    await using var conn = await _openAsync();
    var before = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_settings WHERE updated_by = 'whizbang'");

    await using var ctx = CreateDbContext();
    await _coordinator(ctx).SyncRetentionSettingsAsync([]);

    var after = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_settings WHERE updated_by = 'whizbang'");
    await Assert.That(after).IsEqualTo(before);
  }
}
