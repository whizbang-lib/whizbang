// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>
/// Issue #1747 (WS-4 decision 3) on the Dapper driver: the generated perspective schema resets a governed storage
/// parameter the model does not declare, keeps the one it declares, and leaves a parameter the attribute cannot set.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#removed-storage-settings</docs>
[NotInParallel("PostgreSQL")]
public class DapperTableStorageResetTests : PostgresTestBase {
  private const string TABLE = "wh_per_dapper_storage_reset";

  private static KeyValuePair<string, string>[] _entries() => [..
    Whizbang.Generated.PerspectiveSchemas.Entries.Where(e => e.Key is "DapperStorageResetPerspective")];

  private Task _startAsync() => new PostgresSchemaInitializer(ConnectionString, _entries()).InitializeSchemaAsync();

  [Test]
  public async Task AnUndeclaredSetting_IsReset_AndTheRestAreLeftAloneAsync() {
    await _startAsync();
    await _execAsync($"""
      ALTER TABLE {TABLE} SET (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.2, autovacuum_analyze_threshold = 77,
        toast.autovacuum_vacuum_threshold = 300);
      DELETE FROM wh_schema_migrations WHERE file_name = 'perspective:DapperStorageResetPerspective';
      """);
    await Assert.That(await _reloptionsAsync()).Contains("fillfactor=80")
      .Because("the control: the harness sees the parameter before the pass, so its absence after is the reset.");

    await _startAsync();

    var table = await _reloptionsAsync();
    await Assert.That(table).DoesNotContain("fillfactor").And.DoesNotContain("autovacuum_vacuum_scale_factor");
    await Assert.That(await _scalarAsync(
        $"SELECT coalesce(array_to_string(t.reloptions, ','), '') FROM pg_class c JOIN pg_class t ON t.oid = c.reltoastrelid WHERE c.oid = '{TABLE}'::regclass"))
      .DoesNotContain("autovacuum_vacuum_threshold");
    await Assert.That(table).Contains("autovacuum_vacuum_threshold=500")
      .Because("the declared setting is kept.");
    await Assert.That(table).Contains("autovacuum_analyze_threshold=77")
      .Because("a parameter the attribute cannot set is never reset.");
  }

  private Task<string> _reloptionsAsync() =>
    _scalarAsync($"SELECT coalesce(array_to_string(reloptions, ','), '') FROM pg_class WHERE oid = '{TABLE}'::regclass");

  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<string> _scalarAsync(string sql) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    return (await command.ExecuteScalarAsync())?.ToString() ?? "<null>";
  }
}
