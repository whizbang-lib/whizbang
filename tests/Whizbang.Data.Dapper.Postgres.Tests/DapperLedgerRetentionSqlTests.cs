// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Issue #1572 on a database the Dapper driver migrated: the maintenance sweep bounds <c>wh_log</c> and
/// <c>wh_schedule_runs</c>, keeping each schedule's newest run. The EF Core twin covers every ledger and window.
/// </summary>
/// <docs>fundamentals/workers/maintenance-steps#ledger-retention</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/221_LedgerRetention.sql</code-under-test>
[NotInParallel("PostgreSQL")]
[Category("Integration")]
public class DapperLedgerRetentionSqlTests : PostgresTestBase {

  [Test]
  public async Task Maintenance_BoundsTheLogAndScheduleRuns_KeepingEachSchedulesNewestRunAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    var schedule = Guid.CreateVersion7();
    await connection.ExecuteAsync(
      "INSERT INTO wh_log (logged_at, log_level, source, error_message) VALUES " +
      "(NOW() - INTERVAL '20 days', 2, 'test', 'old'), (NOW(), 2, 'test', 'young')");
    await connection.ExecuteAsync(
      "INSERT INTO wh_schedule_runs (schedule_id, occurrence_id, fired_at, status) VALUES " +
      "(@schedule, @a, NOW() - INTERVAL '60 days', 0), (@schedule, @b, NOW() - INTERVAL '45 days', 0)",
      new { schedule, a = Guid.CreateVersion7(), b = Guid.CreateVersion7() });

    var tasks = (await connection.QueryAsync<(string Task, long Rows)>(
      "SELECT task_name, rows_affected FROM perform_maintenance()")).ToDictionary(t => t.Task, t => t.Rows);

    await Assert.That(await connection.QueryAsync<string>("SELECT error_message FROM wh_log WHERE source = 'test'"))
      .IsEquivalentTo(["young"]);
    await Assert.That(await connection.ExecuteScalarAsync<long>(
      "SELECT count(*) FROM wh_schedule_runs WHERE schedule_id = @schedule", new { schedule })).IsEqualTo(1L)
      .Because("the older run past the window goes, and the schedule's newest run stays whatever its age");
    await Assert.That(tasks["purge_old_schedule_runs"]).IsEqualTo(1L);
    await Assert.That(tasks).ContainsKey("trim_integrity_ledger");
  }
}
