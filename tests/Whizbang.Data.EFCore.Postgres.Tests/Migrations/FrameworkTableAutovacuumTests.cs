// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Issue #1582: the large framework tables vacuum after a fixed number of changes, not after a fraction of the table,
/// so a table of millions of rows is not left holding hundreds of thousands of dead ones.
/// </summary>
/// <docs>operations/infrastructure/storage-maintenance#autovacuum</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/222_FrameworkTableAutovacuum.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class FrameworkTableAutovacuumTests : EFCoreTestBase {

  private async Task<string> _reloptionsAsync(string relation, bool toast = false) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = toast
      ? "SELECT COALESCE(array_to_string(t.reloptions, ','), '') FROM pg_class c JOIN pg_class t ON t.oid = c.reltoastrelid WHERE c.oid = to_regclass(@r)"
      : "SELECT COALESCE(array_to_string(reloptions, ','), '') FROM pg_class WHERE oid = to_regclass(@r)";
    command.Parameters.AddWithValue("r", relation);
    return (string)(await command.ExecuteScalarAsync())!;
  }

  [Test]
  [Arguments("wh_perspective_cursors")]
  [Arguments("wh_lifecycle_completions")]
  [Arguments("wh_stream_digests")]
  [Arguments("wh_message_deduplication")]
  public async Task UpdateAndDeleteTables_VacuumAfterAFixedCountAsync(string table) {
    var options = await _reloptionsAsync(table);

    await Assert.That(options).Contains("autovacuum_vacuum_scale_factor=0,")
      .Because("a fraction of a large table is too many dead rows; the threshold alone decides");
    await Assert.That(options).Contains("autovacuum_vacuum_threshold=10000");
  }

  [Test]
  public async Task TheEventStore_VacuumsAfterAFixedNumberOfInsertsAsync() {
    var options = await _reloptionsAsync("wh_event_store");

    await Assert.That(options).Contains("autovacuum_vacuum_insert_threshold=10000")
      .Because("an append-heavy table needs vacuum to keep its visibility map current for index-only scans");
    await Assert.That(options).Contains("autovacuum_vacuum_insert_scale_factor=0");
  }

  [Test]
  public async Task TheEventBodyToastTable_VacuumsAfterAFixedCountAsync() {
    var options = await _reloptionsAsync("wh_event_body", toast: true);

    await Assert.That(options).Contains("autovacuum_vacuum_scale_factor=0,")
      .Because("reaped ephemeral bodies leave their dead chunks in the TOAST table, not the heap");
    await Assert.That(options).Contains("autovacuum_vacuum_threshold=10000");
  }

  [Test]
  public async Task ApplyingTheSettingsAgain_ChangesNothingAsync() {
    var before = await _reloptionsAsync("wh_stream_digests");
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "ALTER TABLE wh_stream_digests SET (autovacuum_vacuum_scale_factor = 0, autovacuum_vacuum_threshold = 10000)";
    await command.ExecuteNonQueryAsync();

    await Assert.That(await _reloptionsAsync("wh_stream_digests")).IsEqualTo(before)
      .Because("the settings are plain storage parameters, so re-applying the migration leaves them as they are");
  }
}
