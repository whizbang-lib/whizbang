// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2319, against real Postgres: the post-rewrite floor. The estimate cannot see page headers, alignment or the
/// fillfactor reserve, so a freshly rewritten table can read 1.5x to 2x and stay there; with a threshold below that it
/// was offered again every cycle for nothing. A table whose latest rewrite left it at <c>bloat_after</c> F is offered
/// only once its estimate reaches F times <c>table_rewrite_regrowth_factor</c> (default 1.5), or the threshold if that
/// is higher. The ratios are set relative to the table's own estimate, so the test does not depend on how a page packs.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/430_BloatIsMeasuredWithItsToast.sql</code-under-test>
/// <docs>operations/infrastructure/storage-maintenance#post-rewrite-floor</docs>
[Category("Shard5")]
public class RewriteRegrowthFloorSqlTests : EFCoreTestBase {
  private const string SETTINGS = "wh_settings";

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private async Task _executeAsync(string sql, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  private async Task<T> _scalarAsync<T>(string sql, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return (T)(await cmd.ExecuteScalarAsync(cancellationToken))!;
  }

  private static string _sql(decimal value) => value.ToString(CultureInfo.InvariantCulture);

  /// <summary>Bloats <c>wh_settings</c>, puts the threshold just under its estimate, and returns the estimate.</summary>
  private async Task<decimal> _bloatedUnderALowThresholdAsync(CancellationToken cancellationToken) {
    await TableBloat.ManufactureAsync(ConnectionString, cancellationToken);
    var estimate = await _scalarAsync<decimal>($"SELECT wh_estimate_table_bloat('{SETTINGS}')", cancellationToken);
    await _executeAsync(
      $"UPDATE wh_settings SET setting_value = '{_sql(estimate - 0.1m)}' WHERE setting_key = 'table_rewrite_bloat_threshold'",
      cancellationToken);
    return estimate;
  }

  /// <summary>Records a completed rewrite of <c>wh_settings</c> that left it at <paramref name="bloatAfter"/>.</summary>
  private Task _rewrittenToAsync(decimal bloatAfter, CancellationToken cancellationToken) =>
    _executeAsync($"""
      INSERT INTO wh_table_rewrite_history (table_name, strategy, reason, bloat_before, bloat_after)
      VALUES ('{SETTINGS}', 'PgSqueeze', 'Bloat', 9.99, {_sql(Math.Round(bloatAfter, 2))})
      """, cancellationToken);

  private async Task<bool> _offeredAsync(CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();
    return (await _coordinator(ctx).GetTablesNeedingRewriteAsync(cancellationToken)).Any(c => c.TableName == SETTINGS);
  }

  [Test]
  [Timeout(120000)]
  public async Task ARewrittenTable_BelowItsRegrowthFloor_IsHeldBackAsync(CancellationToken cancellationToken) {
    // The spec's case scaled to the table: rewritten to 1.8 and now at 2.1, which is under 1.8 * 1.5 = 2.7.
    var estimate = await _bloatedUnderALowThresholdAsync(cancellationToken);
    await _rewrittenToAsync(estimate * 1.8m / 2.1m, cancellationToken);

    await Assert.That(await _offeredAsync(cancellationToken)).IsFalse()
      .Because("its last rewrite left it near where it reads now, so another rewrite would return nothing");
  }

  [Test]
  [Timeout(120000)]
  public async Task ARewrittenTable_ThatRegrewPastItsFloor_IsOfferedAsync(CancellationToken cancellationToken) {
    // Rewritten to 1.8 and now at 2.8, past 1.8 * 1.5 = 2.7: real regrowth is still caught.
    var estimate = await _bloatedUnderALowThresholdAsync(cancellationToken);
    await _rewrittenToAsync(estimate * 1.8m / 2.8m, cancellationToken);

    await Assert.That(await _offeredAsync(cancellationToken)).IsTrue();
  }

  [Test]
  [Timeout(120000)]
  public async Task OnlyTheLatestRewrite_SetsTheFloorAsync(CancellationToken cancellationToken) {
    var estimate = await _bloatedUnderALowThresholdAsync(cancellationToken);
    await _rewrittenToAsync(estimate * 1.8m / 2.1m, cancellationToken);
    await _rewrittenToAsync(estimate * 1.8m / 2.8m, cancellationToken);

    await Assert.That(await _offeredAsync(cancellationToken)).IsTrue()
      .Because("the newest history row is where the table was left last");
  }

  [Test]
  [Timeout(120000)]
  public async Task TheFactor_IsReadFromItsSettingAsync(CancellationToken cancellationToken) {
    var estimate = await _bloatedUnderALowThresholdAsync(cancellationToken);
    await _rewrittenToAsync(estimate * 1.8m / 2.1m, cancellationToken);
    await _executeAsync("""
      INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES ('table_rewrite_regrowth_factor', '1.1', 'string')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """, cancellationToken);

    await Assert.That(await _offeredAsync(cancellationToken)).IsTrue()
      .Because("at a factor of 1.1 the floor is 1.98 on the spec's scale, and the table reads 2.1");
  }

  [Test]
  [Timeout(120000)]
  public async Task ATableWithoutHistory_IsOfferedAtTheThresholdAsync(CancellationToken cancellationToken) {
    await _bloatedUnderALowThresholdAsync(cancellationToken);

    await Assert.That(await _offeredAsync(cancellationToken)).IsTrue()
      .Because("no rewrite has set a floor, so the threshold alone decides, as before");
  }

  [Test]
  [Timeout(120000)]
  public async Task AFillFactorDecrease_IsNotHeldBackByTheFloorAsync(CancellationToken cancellationToken) {
    var estimate = await _bloatedUnderALowThresholdAsync(cancellationToken);
    await _rewrittenToAsync(estimate * 10m, cancellationToken);
    await _executeAsync($"SELECT wh_request_fillfactor_rewrite('{SETTINGS}', 100, 90)", cancellationToken);
    await using var ctx = CreateDbContext();

    var candidate = (await _coordinator(ctx).GetTablesNeedingRewriteAsync(cancellationToken)).Where(c => c.TableName == SETTINGS).Cast<Whizbang.Core.Messaging.TableRewriteCandidate?>().SingleOrDefault();

    await Assert.That(candidate.HasValue).IsTrue()
      .Because("a lowered fillfactor is owed one rewrite whatever the table's bloat");
    await Assert.That(candidate!.Value.Reason).IsEqualTo(TableRewriteReason.FillFactorDecrease);
  }

  [Test]
  [Timeout(120000)]
  public async Task TheQueueStatus_CountsTheQueue_AndTheTablesTheFloorHeldBackAsync(CancellationToken cancellationToken) {
    var estimate = await _bloatedUnderALowThresholdAsync(cancellationToken);
    await _rewrittenToAsync(estimate * 1.8m / 2.1m, cancellationToken);
    await _executeAsync("SELECT wh_request_table_rewrite('wh_settings'), wh_request_table_rewrite('wh_outbox')", cancellationToken);

    var status = await _scalarAsync<string>(
      "SELECT requested || '/' || threshold || '/' || held_back FROM wh_table_rewrite_queue_status()", cancellationToken);

    await Assert.That(status).IsEqualTo($"{2 + await _alreadyQueuedAsync(cancellationToken)}/{_sql(estimate - 0.1m)}/1")
      .Because("the queue's length, the line, and the one table at or above it that its floor held back");
  }

  [Test]
  [Timeout(120000)]
  public async Task TheCoordinator_ReadsTheQueueStatusAsync(CancellationToken cancellationToken) {
    var estimate = await _bloatedUnderALowThresholdAsync(cancellationToken);
    await _rewrittenToAsync(estimate * 1.8m / 2.1m, cancellationToken);
    await _executeAsync("SELECT wh_request_table_rewrite('wh_settings')", cancellationToken);
    await using var ctx = CreateDbContext();

    var status = await _coordinator(ctx).GetTableRewriteQueueStatusAsync(cancellationToken);

    await Assert.That(status).IsEqualTo(new TableRewriteQueueStatus(
      1 + await _alreadyQueuedAsync(cancellationToken), (double)(estimate - 0.1m), 1));
  }

  [Test]
  [Timeout(120000)]
  public async Task TheCoordinator_ReadsATablesTotalBytesAsync(CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    var total = await coordinator.GetTableTotalBytesAsync(SETTINGS, cancellationToken);
    var missing = await coordinator.GetTableTotalBytesAsync("wh_no_such_table", cancellationToken);

    await Assert.That(total).IsEqualTo(await _scalarAsync<long>($"SELECT pg_total_relation_size('{SETTINGS}')", cancellationToken))
      .Because("the reclaimed-bytes meter counts the heap, the TOAST relation and the indexes");
    await Assert.That(missing).IsNull();
  }

  /// <summary>
  /// The settings' source of truth is configuration: the boot step writes the effective value of each on every start,
  /// so a value edited by hand in <c>wh_settings</c> is put back, and the online default (2.0) reaches a database whose
  /// row migration 089 seeded at 3.0.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task EveryStart_PutsBackAHandEditedSetting_WithTheEffectiveValueAsync(CancellationToken cancellationToken) {
    await _executeAsync("""
      UPDATE wh_settings SET setting_value = '9.9' WHERE setting_key = 'table_rewrite_bloat_threshold';
      UPDATE wh_settings SET setting_value = '5' WHERE setting_key = 'table_rewrite_min_rows';
      """, cancellationToken);
    await using var ctx = CreateDbContext();
    var services = new ServiceCollection().AddSingleton<IWorkCoordinator>(_coordinator(ctx)).BuildServiceProvider();
    var step = new TableRewriteStartupStep(services.GetRequiredService<IServiceScopeFactory>(), [],
      Options.Create(new MaintenanceWorkerOptions { TableRewriteStrategy = TableRewriteStrategy.PgSqueeze, TableRewriteMinRows = 2000 }),
      null, NullLogger<TableRewriteStartupStep>.Instance);

    await step.ExecuteAsync(cancellationToken);

    var stored = await _scalarAsync<string>("""
      SELECT string_agg(setting_key || '=' || setting_value, ',' ORDER BY setting_key) FROM wh_settings
      WHERE setting_key IN ('table_rewrite_bloat_threshold', 'table_rewrite_min_rows', 'table_rewrite_regrowth_factor')
      """, cancellationToken);
    await Assert.That(stored).IsEqualTo("table_rewrite_bloat_threshold=2,table_rewrite_min_rows=2000,table_rewrite_regrowth_factor=1.5")
      .Because("the hand edits are overwritten by the configured row floor and by the defaults for an online tool");
  }

  /// <summary>Requests a fresh database already carries (migrations queue some), apart from the two the test adds.</summary>
  private async Task<int> _alreadyQueuedAsync(CancellationToken cancellationToken) {
    var pending = await _scalarAsync<string>(
      "SELECT COALESCE(setting_value, '') FROM wh_settings WHERE setting_key = 'pending_table_rewrites'", cancellationToken);
    return pending.Split(',', StringSplitOptions.RemoveEmptyEntries).Count(t => t is not ("wh_settings" or "wh_outbox"));
  }
}
