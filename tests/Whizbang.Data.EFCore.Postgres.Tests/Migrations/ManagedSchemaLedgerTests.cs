// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// What <c>whizbang schema status|plan|pin|unpin</c> do against a database: list the ledger, list what the next
/// start retires, and set or release the database pin, recorded as set from the CLI.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects#dba-runbook</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class ManagedSchemaLedgerTests {
  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("mgd_ledger");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;

    await using var db = await _openAsync();
    foreach (var migration in new[] { "000_MigrationTracking.sql", "200_ManagedObjects.sql" }) {
      await _execAsync(db, (await File.ReadAllTextAsync(_migrationPath(migration)))
        .Replace("__SCHEMA__", "\"public\"", StringComparison.Ordinal));
    }
    await _execAsync(db, """
      INSERT INTO public.wh_managed_objects (table_name, object_name, object_kind, owner, status, declared_by)
      VALUES ('wh_per_job', 'idx_job_status', 'index', 'whizbang', 'active', 'JobModel.Status [Indexed]'),
             ('wh_per_job', 'idx_job_data_gin', 'index', 'whizbang', 'pending-retirement', NULL),
             ('wh_per_job', 'idx_job_legacy', 'index', 'whizbang', 'pending-retirement', NULL),
             ('wh_per_job', 'customer_report_idx', 'index', 'foreign', 'active', NULL),
             ('wh_per_job', 'idx_job_old', 'index', 'whizbang', 'retired', NULL);
      UPDATE public.wh_managed_objects SET code_pinned = TRUE, code_pin_source = 'code', code_pin_reason = 'kept by the model'
      WHERE object_name = 'idx_job_legacy';
      """);
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  [Test]
  public async Task Status_ListsEveryObjectWithItsOwnerStatusAndPinsAsync() {
    await using var db = await _openAsync();

    var rows = await ManagedSchemaLedger.StatusAsync(db, "public");

    await Assert.That(rows.Select(r => $"{r.Name}:{r.Owner}:{r.Status}")).IsEquivalentTo([
      "customer_report_idx:foreign:active", "idx_job_data_gin:whizbang:pending-retirement",
      "idx_job_legacy:whizbang:pending-retirement", "idx_job_old:whizbang:retired", "idx_job_status:whizbang:active",
    ]);
    var formatted = ManagedSchemaLedger.Format(rows);
    await Assert.That(formatted).Contains("idx_job_legacy").And.Contains("code: kept by the model");
  }

  [Test]
  public async Task Plan_ListsWhatTheNextStartRetires_LeavingOutPinnedAndForeignObjectsAsync() {
    await using var db = await _openAsync();

    var rows = await ManagedSchemaLedger.PendingRetirementAsync(db, "public");

    await Assert.That(rows.Select(r => r.Name)).IsEquivalentTo(["idx_job_data_gin"]);
  }

  [Test]
  public async Task Pin_FromTheCli_IsRecordedAsTheClisAsync() {
    await using var db = await _openAsync();

    var result = await ManagedSchemaLedger.PinAsync(db, "public", "wh_per_job", "idx_job_data_gin", "reporting reads it");

    await Assert.That(result).IsEqualTo("pinned");
    var row = (await ManagedSchemaLedger.StatusAsync(db, "public")).Single(r => r.Name == "idx_job_data_gin");
    await Assert.That($"{row.DbPinned}|{row.DbPinSource}|{row.DbPinReason}").IsEqualTo($"True|{PinSources.CLI}|reporting reads it");
    await Assert.That(await ManagedSchemaLedger.PendingRetirementAsync(db, "public")).IsEmpty();
  }

  [Test]
  public async Task Unpin_SaysWhenCSharpStillPinsTheObjectAsync() {
    await using var db = await _openAsync();
    await ManagedSchemaLedger.PinAsync(db, "public", "wh_per_job", "idx_job_legacy", "also by a DBA");

    var result = await ManagedSchemaLedger.UnpinAsync(db, "public", "wh_per_job", "idx_job_legacy");

    await Assert.That(result).IsEqualTo("unpinned; still pinned by code: kept by the model");
  }

  [Test]
  public async Task Status_InASchemaWithoutTheLedger_IsEmptyAsync() {
    await using var db = await _openAsync();
    await _execAsync(db, "CREATE SCHEMA IF NOT EXISTS other");

    await Assert.That(await ManagedSchemaLedger.StatusAsync(db, "other")).IsEmpty();
  }

  [Test]
  public async Task Format_OfNothing_SaysSoAsync() {
    await Assert.That(ManagedSchemaLedger.Format([])).Contains("No managed objects");
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

  private static string _migrationPath(string fileName) => Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
    "src", "Whizbang.Data.Postgres", "Migrations", fileName);
}
