// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Issue #1592: <c>wh_perspective_cursors</c> is rewritten on every batch a perspective applies, so its pages keep
/// 10% free for the new row versions (fillfactor 90), and an update that changes no indexed column stays on its page.
/// A table that already holds rows is queued for a rewrite, since a new fillfactor applies only to new pages.
/// </summary>
/// <docs>operations/infrastructure/storage-maintenance#fillfactor</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/223_PerspectiveCursorFillFactor.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class PerspectiveCursorFillFactorTests : EFCoreTestBase {
  private const string MIGRATION = "223_PerspectiveCursorFillFactor";
  private const string TABLE = "wh_perspective_cursors";

  [Test]
  public async Task TheCursorTable_KeepsTenPercentOfEachPageFreeAsync() {
    await Assert.That(await _scalarAsync($"SELECT array_to_string(reloptions, ',') FROM pg_class WHERE oid = to_regclass('{TABLE}')"))
      .Contains("fillfactor=90");
    await Assert.That(await _pendingRewritesAsync()).DoesNotContain(TABLE)
      .Because("a fresh database's cursor table is empty, so there is nothing to rewrite.");
  }

  [Test]
  public async Task ATableHoldingRows_IsQueuedForARewriteAsync() {
    await _execAsync($"""
      INSERT INTO {TABLE} (stream_id, perspective_name, status)
      VALUES ('01890000-0000-7000-8000-000000001592', 'fillfactor-probe', 0);
      ALTER TABLE {TABLE} RESET (fillfactor);
      """);

    await _execAsync(_migrationSql());

    await Assert.That(await _scalarAsync($"SELECT array_to_string(reloptions, ',') FROM pg_class WHERE oid = to_regclass('{TABLE}')"))
      .Contains("fillfactor=90");
    await Assert.That(await _pendingRewritesAsync()).Contains(TABLE)
      .Because("rows already stored sit in pages packed full until the table is rewritten.");
  }

  [Test]
  public async Task ApplyingTheMigrationAgain_ChangesNothing_AndQueuesNothingAsync() {
    await _execAsync($"""
      INSERT INTO {TABLE} (stream_id, perspective_name, status)
      VALUES ('01890000-0000-7000-8000-000000001593', 'fillfactor-probe', 0);
      UPDATE wh_settings SET setting_value = '' WHERE setting_key = 'pending_table_rewrites';
      """);
    var before = await _scalarAsync($"SELECT array_to_string(reloptions, ',') FROM pg_class WHERE oid = to_regclass('{TABLE}')");

    await _execAsync(_migrationSql());

    await Assert.That(await _scalarAsync($"SELECT array_to_string(reloptions, ',') FROM pg_class WHERE oid = to_regclass('{TABLE}')"))
      .IsEqualTo(before);
    await Assert.That(await _pendingRewritesAsync()).DoesNotContain(TABLE)
      .Because("the fillfactor is already 90, so a re-run finds nothing to change and has nothing to rewrite.");
  }

  private static string _migrationSql() =>
    new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, "public").GetMigration(MIGRATION)!.Sql;

  private Task<string> _pendingRewritesAsync() =>
    _scalarAsync("SELECT coalesce(setting_value, '') FROM wh_settings WHERE setting_key = 'pending_table_rewrites'");

  private async Task _execAsync(string sql) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<string> _scalarAsync(string sql) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    return (await command.ExecuteScalarAsync())?.ToString() ?? "";
  }
}
