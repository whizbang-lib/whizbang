// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// <c>wh_ensure_index</c> records which existing index stands for a declared one it did not duplicate, so the
/// managed-object reconcile keeps that index, and the migration that adds the record makes every perspective's
/// schema SQL run once more, so a twin an earlier release skipped is recorded too.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/201_IndexEquivalents.sql</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class IndexEquivalentsTests {
  private const string TABLE = "wh_per_equiv_probe";

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("index_equiv");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;

    await using var db = await _openAsync();
    await _execAsync(db, $"CREATE TABLE {TABLE} (id uuid PRIMARY KEY, scope jsonb NOT NULL)");
    await _applyAsync(db, "000_MigrationTracking.sql", "174_EnsureIndex.sql", "178_IndexStatistics.sql");
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  [Test]
  public async Task AnIndexNotBuiltBecauseATwinExists_IsRecordedAsStandingForItAsync() {
    await using var db = await _openAsync();
    await _applyAsync(db, "201_IndexEquivalents.sql");
    await _execAsync(db, $"CREATE INDEX idx_equiv_probe_scope_t ON {TABLE} ((scope->>'t'))");

    var outcome = await _ensureAsync(db, $"CREATE INDEX IF NOT EXISTS idx_equiv_probe_tenant ON public.{TABLE} ((scope->>'t'));");

    await Assert.That(outcome).IsEqualTo("equivalent:idx_equiv_probe_scope_t");
    await Assert.That(await _pairsAsync(db)).IsEquivalentTo([$"{TABLE}:idx_equiv_probe_tenant:idx_equiv_probe_scope_t"]);
  }

  [Test]
  public async Task OnceTheDeclaredNameExists_ThePairIsForgottenAsync() {
    await using var db = await _openAsync();
    await _applyAsync(db, "201_IndexEquivalents.sql");
    await _execAsync(db, $"CREATE INDEX idx_equiv_probe_scope_t ON {TABLE} ((scope->>'t'))");
    await _ensureAsync(db, $"CREATE INDEX IF NOT EXISTS idx_equiv_probe_tenant ON public.{TABLE} ((scope->>'t'));");
    await _execAsync(db, "ALTER INDEX idx_equiv_probe_scope_t RENAME TO idx_equiv_probe_tenant");

    var outcome = await _ensureAsync(db, $"CREATE INDEX IF NOT EXISTS idx_equiv_probe_tenant ON public.{TABLE} ((scope->>'t'));");

    await Assert.That(outcome).IsEqualTo("exists");
    await Assert.That(await _pairsAsync(db)).IsEmpty();
  }

  [Test]
  public async Task AnIndexBuilt_RecordsNothingAsync() {
    await using var db = await _openAsync();
    await _applyAsync(db, "201_IndexEquivalents.sql");

    var outcome = await _ensureAsync(db, $"CREATE INDEX IF NOT EXISTS idx_equiv_probe_tenant ON public.{TABLE} ((scope->>'t'));");

    await Assert.That(outcome).IsEqualTo("created");
    await Assert.That(await _pairsAsync(db)).IsEmpty();
  }

  [Test]
  public async Task TheMigration_MakesEveryPerspectivesSchemaSqlRunOnceMoreAsync() {
    await using var db = await _openAsync();
    await _execAsync(db, """
      INSERT INTO public.wh_schema_versions (library_version) VALUES ('test') ON CONFLICT DO NOTHING;
      INSERT INTO public.wh_schema_migrations (file_name, content_hash, version_id)
      SELECT name, 'h', (SELECT min(id) FROM public.wh_schema_versions)
      FROM (VALUES ('perspective:wh_per_equiv_probe'), ('perspective:wh_per_other'), ('174_EnsureIndex.sql')) AS v(name);
      """);

    await _applyAsync(db, "201_IndexEquivalents.sql");

    await using var command = new NpgsqlCommand("SELECT file_name FROM public.wh_schema_migrations ORDER BY 1", db);
    var remaining = new List<string>();
    await using (var reader = await command.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        remaining.Add(reader.GetString(0));
      }
    }
    await Assert.That(remaining).IsEquivalentTo(["174_EnsureIndex.sql"]);
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    return db;
  }

  private static async Task _applyAsync(NpgsqlConnection db, params string[] migrations) {
    foreach (var migration in migrations) {
      await _execAsync(db, (await File.ReadAllTextAsync(_migrationPath(migration)))
        .Replace("__SCHEMA__", "\"public\"", StringComparison.Ordinal));
    }
  }

  private static async Task<string> _ensureAsync(NpgsqlConnection db, string ddl) {
    await using var command = new NpgsqlCommand("SELECT public.wh_ensure_index($1)", db);
    command.Parameters.AddWithValue(ddl);
    return (string)(await command.ExecuteScalarAsync())!;
  }

  private static async Task<List<string>> _pairsAsync(NpgsqlConnection db) {
    await using var command = new NpgsqlCommand(
      "SELECT table_name || ':' || declared_name || ':' || existing_name FROM public.wh_index_equivalents", db);
    var pairs = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      pairs.Add(reader.GetString(0));
    }
    return pairs;
  }

  private static async Task _execAsync(NpgsqlConnection db, string sql) {
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private static string _migrationPath(string fileName) => Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
    "src", "Whizbang.Data.Postgres", "Migrations", fileName);
}
