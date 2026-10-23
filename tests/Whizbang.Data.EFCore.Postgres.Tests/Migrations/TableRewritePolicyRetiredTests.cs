// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Issue #1667: the <c>table_rewrite_policy</c> setting was written by migration 089 and read by nothing. Rewrites are
/// governed by the <c>AllowTableRewrite</c> option alone, so the row only misled an operator who changed it. It is
/// removed, and the settings the rewrite detector does read stay where they are.
/// </summary>
/// <docs>operations/infrastructure/migrations#table-rewrites-run-post-ready-under-the-maintainer-duty</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/233_RetireTableRewritePolicySetting.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class TableRewritePolicyRetiredTests : EFCoreTestBase {
  private async Task<long> _settingCountAsync(string key) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT count(*) FROM wh_settings WHERE setting_key = @k";
    command.Parameters.AddWithValue("k", key);
    return (long)(await command.ExecuteScalarAsync())!;
  }

  [Test]
  public async Task TheUnreadPolicyKey_IsGoneAfterMigrationAsync() {
    await Assert.That(await _settingCountAsync("table_rewrite_policy")).IsEqualTo(0L)
      .Because("a setting nothing reads tells an operator that changing it does something");
  }

  [Test]
  [Arguments("table_rewrite_bloat_threshold")]
  [Arguments("table_rewrite_min_rows")]
  [Arguments("pending_table_rewrites")]
  public async Task TheSettingsTheDetectorReads_StayAsync(string key) {
    await Assert.That(await _settingCountAsync(key)).IsEqualTo(1L)
      .Because("the rewrite detector and the request queue read these, and only the unread key is retired");
  }

  [Test]
  public async Task TheDetector_StillAnswersWithoutTheKeyAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT count(*) FROM wh_tables_needing_rewrite()";

    var offered = (long)(await command.ExecuteScalarAsync())!;

    await Assert.That(offered).IsEqualTo(0L)
      .Because("a fresh database owes no rewrite, and the detector never consulted the retired key");
  }
}
