// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Issue #1666: the applied-event ledger takes a row for every completed apply and loses it an hour later, so it is a
/// sliding window over the streams. Its vacuum waits on a fixed count of dead rows and of inserted rows rather than on
/// a fraction of a table that only ever holds the last hour.
/// </summary>
/// <docs>operations/infrastructure/storage-maintenance#autovacuum</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/234_PerspectiveAppliedAutovacuum.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class PerspectiveAppliedAutovacuumTests : EFCoreTestBase {
  private const string TABLE = "wh_perspective_applied";

  private async Task<string> _reloptionsAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT COALESCE(array_to_string(reloptions, ','), '') FROM pg_class WHERE oid = to_regclass(@r)";
    command.Parameters.AddWithValue("r", TABLE);
    return (string)(await command.ExecuteScalarAsync())!;
  }

  [Test]
  public async Task TheLedger_VacuumsAfterAFixedNumberOfDeadRowsAsync() {
    var options = await _reloptionsAsync();

    await Assert.That(options).Contains("autovacuum_vacuum_scale_factor=0,")
      .Because("the prune deletes rows continuously, and a fraction of the table is the wrong trigger for a window");
    await Assert.That(options).Contains("autovacuum_vacuum_threshold=10000");
  }

  [Test]
  public async Task TheLedger_VacuumsAfterAFixedNumberOfInsertsAsync() {
    var options = await _reloptionsAsync();

    await Assert.That(options).Contains("autovacuum_vacuum_insert_scale_factor=0,")
      .Because("every completed apply inserts a row, and the visibility map has to keep up for the status reads");
    await Assert.That(options).Contains("autovacuum_vacuum_insert_threshold=10000");
  }

  [Test]
  public async Task ApplyingTheSettingsAgain_ChangesNothingAsync() {
    var before = await _reloptionsAsync();
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = $"""
      ALTER TABLE {TABLE} SET (
        autovacuum_vacuum_scale_factor = 0,
        autovacuum_vacuum_threshold = 10000,
        autovacuum_vacuum_insert_scale_factor = 0,
        autovacuum_vacuum_insert_threshold = 10000)
      """;
    await command.ExecuteNonQueryAsync();

    await Assert.That(await _reloptionsAsync()).IsEqualTo(before)
      .Because("the settings are plain storage parameters, so re-applying the migration leaves them as they are");
  }
}
