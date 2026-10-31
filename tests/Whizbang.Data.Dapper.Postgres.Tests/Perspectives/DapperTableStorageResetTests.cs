// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>
/// Issue #1747 (WS-4 decision 3) on the Dapper driver: each start resets a governed storage parameter the model does
/// not declare, keeps the one it declares, and leaves a parameter the attribute cannot set; a table listed in
/// <c>perspective_storage_manual_tables</c> is left alone until it is taken off the list.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#removed-storage-settings</docs>
[NotInParallel("PostgreSQL")]
public class DapperTableStorageResetTests : PostgresTestBase {
  private const string TABLE = "wh_per_dapper_storage_reset";

  private static KeyValuePair<string, string>[] _entries() => [..
    Whizbang.Generated.PerspectiveSchemas.Entries.Where(e => e.Key is "DapperStorageResetPerspective")];

  private Task _startAsync(ILogger? logger = null) =>
    new PostgresSchemaInitializer(ConnectionString, _entries()) { Logger = logger }.InitializeSchemaAsync();

  [Test]
  public async Task AnUndeclaredSetting_IsReset_AndTheRestAreLeftAloneAsync() {
    await _startAsync();
    await _execAsync($"""
      ALTER TABLE {TABLE} SET (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.2, autovacuum_analyze_threshold = 77,
        toast.autovacuum_vacuum_threshold = 300);
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

  /// <summary>
  /// A table on the override list keeps what an operator set, declared or not, and the start warns of the declared
  /// value it ignores; taken off the list, the next start applies the model to it.
  /// </summary>
  /// <docs>fundamentals/perspectives/physical-fields#manually-managed-tables</docs>
  [Test]
  public async Task AManuallyManagedTable_IsLeftAlone_UntilItIsTakenOffTheListAsync() {
    await _startAsync();
    await _execAsync($"""
      ALTER TABLE {TABLE} SET (autovacuum_vacuum_threshold = 9, fillfactor = 80);
      UPDATE wh_settings SET setting_value = '{TABLE}' WHERE setting_key = 'perspective_storage_manual_tables';
      """);
    var log = new ListLogger();

    await _startAsync(log);

    await Assert.That(await _reloptionsAsync()).Contains("autovacuum_vacuum_threshold=9").And.Contains("fillfactor=80");
    await Assert.That(log.Entries.Single(e => e.Level == LogLevel.Information && e.Message.Contains("perspective_storage_manual_tables", StringComparison.Ordinal)).Message)
      .Contains(TABLE);
    await Assert.That(log.Entries.Single(e => e.Level == LogLevel.Warning && e.Message.Contains(TABLE, StringComparison.Ordinal)).Message)
      .Contains("autovacuum_vacuum_threshold=500").And.Contains("perspective_storage_manual_tables");

    await _execAsync("UPDATE wh_settings SET setting_value = '' WHERE setting_key = 'perspective_storage_manual_tables'");
    await _startAsync();

    var applied = await _reloptionsAsync();
    await Assert.That(applied).Contains("autovacuum_vacuum_threshold=500").And.DoesNotContain("fillfactor")
      .Because("the control: off the list, the same table is brought back to its model at the next start.");
  }

  private sealed class ListLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
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
