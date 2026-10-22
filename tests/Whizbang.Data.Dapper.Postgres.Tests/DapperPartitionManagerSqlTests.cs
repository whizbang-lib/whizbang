// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Issue #1611 on a database the Dapper driver migrated: <c>wh_log</c> converts to weekly partitions, its writers keep
/// inserting, the manager creates partitions ahead, and a partition past the window is dropped. The EF Core twins
/// (<c>PartitionMaintainSqlTests</c>, <c>LogPartitionConversionTests</c>) cover each path.
/// </summary>
/// <docs>operations/infrastructure/partitioned-ledgers</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/226_PartitionManager.sql</code-under-test>
[NotInParallel("PostgreSQL")]
[Category("Integration")]
public class DapperPartitionManagerSqlTests : PostgresTestBase {

  [Test]
  public async Task TheLog_Converts_KeepsItsWriters_AndIsMaintainedByPartitionAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync(
      "INSERT INTO wh_log (logged_at, log_level, source, error_message) VALUES (NOW() - INTERVAL '20 days', 2, 'test', 'old')");

    var outcome = await connection.ExecuteScalarAsync<string>(
      "SELECT wh_partition_convert('wh_log', 'logged_at', '7 days')");
    await connection.ExecuteAsync("SELECT log_event(3, 'test', 'after-conversion')");
    var created = (await connection.QueryAsync<string>(
      "SELECT partition_name FROM wh_partition_maintain('wh_log', '7 days', 3, '14 days') WHERE action = 'created'")).ToList();
    var dropped = (await connection.QueryAsync<string>(
      "SELECT partition_name FROM wh_partition_maintain('wh_log', '7 days', 3, '14 days', NOW() + INTERVAL '30 days') " +
      "WHERE action = 'dropped'")).ToList();

    await Assert.That(outcome).IsEqualTo("converted");
    await Assert.That(created.Count).IsEqualTo(3)
      .Because("the legacy partition covers the current week, so the three weeks after it are created");
    await Assert.That(dropped).Contains("wh_log_legacy");
    await Assert.That(await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_log WHERE source = 'test'"))
      .IsEqualTo(0L);
  }
}
