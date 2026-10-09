// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// <c>wh_managed_objects</c> is the ledger of the database objects Whizbang manages for perspectives,
/// and <c>wh_pin_object</c> / <c>wh_unpin_object</c> are how a DBA pins and unpins one directly in the
/// database. A pinned object is never dropped; each case runs the real migration against a real database.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/200_ManagedObjects.sql</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class ManagedObjectsLedgerTests {
  private const string SCHEMA = "\"public\"";

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("managed_objects");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;

    await using var db = await _openAsync();
    // The real migration text: a copy here would pass whether or not the shipped one works.
    foreach (var migration in new[] { "000_MigrationTracking.sql", "200_ManagedObjects.sql" }) {
      await _execAsync(db, (await File.ReadAllTextAsync(_migrationPath(migration)))
        .Replace("__SCHEMA__", SCHEMA, StringComparison.Ordinal));
    }
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  [Test]
  public async Task Pin_RecordsThePinWithItsReasonAndWhoSetItAsync() {
    await using var db = await _openAsync();

    var result = await _scalarAsync(db, "SELECT public.wh_pin_object('wh_per_job', 'idx_job_legacy', 'reporting reads it')");

    await Assert.That(result).IsEqualTo("pinned");
    await Assert.That(await _scalarAsync(db, """
      SELECT db_pinned::text || '|' || db_pin_source || '|' || db_pin_reason || '|' || (db_pinned_by IS NOT NULL)::text || '|' || (db_pinned_at IS NOT NULL)::text
      FROM public.wh_managed_objects WHERE table_name = 'wh_per_job' AND object_name = 'idx_job_legacy'
      """)).IsEqualTo("true|sql|reporting reads it|true|true");
  }

  [Test]
  public async Task Pin_AnObjectTheLedgerAlreadyHas_KeepsItsClassificationAsync() {
    await using var db = await _openAsync();
    await _execAsync(db, """
      INSERT INTO public.wh_managed_objects (table_name, object_name, object_kind, owner, status)
      VALUES ('wh_per_job', 'idx_job_status', 'index', 'whizbang', 'pending-retirement')
      """);

    await _scalarAsync(db, "SELECT public.wh_pin_object('wh_per_job', 'idx_job_status', NULL)");

    await Assert.That(await _scalarAsync(db, """
      SELECT owner || '|' || status || '|' || db_pinned::text FROM public.wh_managed_objects
      WHERE table_name = 'wh_per_job' AND object_name = 'idx_job_status'
      """)).IsEqualTo("whizbang|pending-retirement|true");
  }

  [Test]
  public async Task Unpin_ReleasesAPinSetInTheDatabaseAsync() {
    await using var db = await _openAsync();
    await _scalarAsync(db, "SELECT public.wh_pin_object('wh_per_job', 'idx_job_legacy', 'temporary')");

    var result = await _scalarAsync(db, "SELECT public.wh_unpin_object('wh_per_job', 'idx_job_legacy')");

    await Assert.That(result).IsEqualTo("unpinned");
    await Assert.That(await _scalarAsync(db, """
      SELECT db_pinned::text || '|' || coalesce(db_pin_source, 'none') FROM public.wh_managed_objects
      WHERE table_name = 'wh_per_job' AND object_name = 'idx_job_legacy'
      """)).IsEqualTo("false|none");
  }

  [Test]
  [Arguments("code")]
  [Arguments("config")]
  public async Task Unpin_ReleasesOnlyTheDatabasePin_AndSaysCSharpStillPinsItAsync(string source) {
    await using var db = await _openAsync();
    await _execAsync(db, $"""
      INSERT INTO public.wh_managed_objects (table_name, object_name, object_kind, owner, status, code_pinned, code_pin_source, code_pin_reason)
      VALUES ('wh_per_job', 'idx_job_code', 'index', 'whizbang', 'active', true, '{source}', 'JobModel [KeepSchemaObject]')
      """);
    await _scalarAsync(db, "SELECT public.wh_pin_object('wh_per_job', 'idx_job_code', 'dba too')");

    var result = await _scalarAsync(db, "SELECT public.wh_unpin_object('wh_per_job', 'idx_job_code')");

    await Assert.That(result).IsEqualTo($"unpinned; still pinned by {source}: JobModel [KeepSchemaObject]");
    await Assert.That(await _scalarAsync(db, """
      SELECT code_pinned::text || '|' || db_pinned::text FROM public.wh_managed_objects
      WHERE table_name = 'wh_per_job' AND object_name = 'idx_job_code'
      """)).IsEqualTo("true|false");
  }

  [Test]
  public async Task Pin_InTheDatabase_LeavesTheCodePinAloneAsync() {
    await using var db = await _openAsync();
    await _execAsync(db, """
      INSERT INTO public.wh_managed_objects (table_name, object_name, object_kind, owner, status, code_pinned, code_pin_source)
      VALUES ('wh_per_job', 'idx_job_code', 'index', 'whizbang', 'active', true, 'code')
      """);

    await _scalarAsync(db, "SELECT public.wh_pin_object('wh_per_job', 'idx_job_code', 'dba')");

    await Assert.That(await _scalarAsync(db, """
      SELECT code_pinned::text || '|' || code_pin_source || '|' || db_pinned::text || '|' || db_pin_source FROM public.wh_managed_objects
      WHERE table_name = 'wh_per_job' AND object_name = 'idx_job_code'
      """)).IsEqualTo("true|code|true|sql");
  }

  [Test]
  public async Task Unpin_AnObjectNotInTheLedger_IsAbsentAsync() {
    await using var db = await _openAsync();

    await Assert.That(await _scalarAsync(db, "SELECT public.wh_unpin_object('wh_per_job', 'idx_missing')")).IsEqualTo("absent");
  }

  [Test]
  public async Task Migration_RunsTwice_WithoutLosingRowsAsync() {
    await using var db = await _openAsync();
    await _scalarAsync(db, "SELECT public.wh_pin_object('wh_per_job', 'idx_job_legacy', 'kept')");

    await _execAsync(db, (await File.ReadAllTextAsync(_migrationPath("200_ManagedObjects.sql")))
      .Replace("__SCHEMA__", SCHEMA, StringComparison.Ordinal));

    await Assert.That(await _scalarAsync(db, "SELECT count(*)::text FROM public.wh_managed_objects WHERE db_pinned")).IsEqualTo("1");
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    return db;
  }

  private static async Task _execAsync(NpgsqlConnection db, string sql) {
    await using var cmd = new NpgsqlCommand(sql, db);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<string?> _scalarAsync(NpgsqlConnection db, string sql) {
    await using var cmd = new NpgsqlCommand(sql, db);
    return (await cmd.ExecuteScalarAsync())?.ToString();
  }

  private static string _migrationPath(string fileName) => Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
    "src", "Whizbang.Data.Postgres", "Migrations", fileName);
}
