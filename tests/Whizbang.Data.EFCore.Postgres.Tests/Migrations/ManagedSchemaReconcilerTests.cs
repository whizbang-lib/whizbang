// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The reconcile against a real database: the first run records what is there and drops nothing; a later run
/// drops what Whizbang created and no longer declares, and leaves foreign and pinned objects alone.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Schema/ManagedSchemaReconciler.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class ManagedSchemaReconcilerTests {
  private const string SCHEMA = "public";
  private const string TABLE = "wh_per_job";
  private static readonly long _lockId = SchemaInitializationLockKey.Compute(SCHEMA);

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("mgd_reconcile");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;

    await using var db = await _openAsync();
    foreach (var migration in new[] { "000_MigrationTracking.sql", "200_ManagedObjects.sql" }) {
      await _execAsync(db, (await File.ReadAllTextAsync(_migrationPath(migration)))
        .Replace("__SCHEMA__", "\"public\"", StringComparison.Ordinal));
    }
    await _execAsync(db, $"""
      CREATE TABLE {TABLE} (id uuid PRIMARY KEY, data jsonb NOT NULL, code text);
      CREATE INDEX idx_job_status ON {TABLE} ((data->>'Status'));
      CREATE INDEX idx_job_data_gin ON {TABLE} USING gin (data);
      CREATE INDEX customer_report_idx ON {TABLE} (code);
      ALTER TABLE {TABLE} ADD CONSTRAINT ck_{TABLE}_code_len CHECK (length(code) <= 50) NOT VALID;
      """);
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  private static ManagedSchemaObjectSet _declared() =>
    new ManagedSchemaObjectSet().Index(TABLE, "idx_job_status", "JobModel.Status [Indexed]");

  private static ManagedSchemaSettings _settings(ReconcileMode mode = ReconcileMode.Apply) => new(mode, [], [], DropAfterFleetConverged: false);

  private async Task<ManagedSchemaReport> _reconcileAsync(
      ManagedSchemaObjectSet? declared = null, ManagedSchemaSettings? settings = null, Guid? instanceId = null,
      ILogger? logger = null) {
    await using var db = await _openAsync();
    return await ManagedSchemaReconciler.RunAsync(
      db, SCHEMA, declared ?? _declared(), settings ?? _settings(), instanceId, logger, CancellationToken.None);
  }

  /// <summary>Every message logged, formatted.</summary>
  private sealed class ListLogger : ILogger {
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
  }

  private static ManagedSchemaSettings _gated => new(ReconcileMode.Apply, [], [], DropAfterFleetConverged: true);

  /// <summary>The instance registry's shape the fleet gate reads: an id and a heartbeat.</summary>
  private async Task _registryAsync(params (Guid Id, TimeSpan Ago)[] instances) {
    await using var db = await _openAsync();
    await _execAsync(db, "CREATE TABLE IF NOT EXISTS wh_service_instances (instance_id uuid PRIMARY KEY, last_heartbeat_at timestamptz NOT NULL)");
    foreach (var (id, ago) in instances) {
      await _execAsync(db, $"INSERT INTO wh_service_instances VALUES ('{id}', now() - interval '{(int)ago.TotalSeconds} seconds')");
    }
  }

  [Test]
  public async Task FirstRun_RecordsEverything_AndDropsNothingAsync() {
    var report = await _reconcileAsync();

    await Assert.That(report.Dropped).IsEmpty();
    await Assert.That(await _objectsAsync()).Contains("idx_job_data_gin");
    await Assert.That(await _ledgerAsync()).IsEquivalentTo([
      "ck_wh_per_job_code_len:whizbang:pending-retirement",
      "customer_report_idx:foreign:active",
      "idx_job_data_gin:whizbang:pending-retirement",
      "idx_job_status:whizbang:active",
    ]);
  }

  [Test]
  public async Task SecondRun_DropsWhatWhizbangCreatedAndNoLongerDeclares_AndKeepsForeignObjectsAsync() {
    await _reconcileAsync();

    var report = await _reconcileAsync();

    await Assert.That(report.Dropped.Select(d => d.Name)).IsEquivalentTo(["idx_job_data_gin", "ck_wh_per_job_code_len"]);
    var objects = await _objectsAsync();
    await Assert.That(objects).DoesNotContain("idx_job_data_gin");
    await Assert.That(objects).DoesNotContain("ck_wh_per_job_code_len");
    await Assert.That(objects).Contains("customer_report_idx");
    await Assert.That(objects).Contains("idx_job_status");
    await Assert.That(await _ledgerAsync()).Contains("idx_job_data_gin:whizbang:retired");
  }

  [Test]
  public async Task AnObjectARunningInstanceStillDeclares_IsKeptUntilItStopsAsync() {
    var self = Guid.CreateVersion7();
    var previous = Guid.CreateVersion7();
    await _registryAsync((self, TimeSpan.Zero), (previous, TimeSpan.FromSeconds(10)));
    // The previous release, still running, declares the index this one retired.
    await _reconcileAsync(_declared().Index(TABLE, "idx_job_data_gin", "JobModel [IndexAllFields]"), _gated, previous);

    var whileRunning = await _reconcileAsync(settings: _gated, instanceId: self);

    await Assert.That(whileRunning.Dropped.Select(d => d.Name)).IsEquivalentTo(["ck_wh_per_job_code_len"]);
    await Assert.That(whileRunning.Kept.Single(k => k.Name == "idx_job_data_gin").Reason).Contains("still declared by a running instance");

    await using (var db = await _openAsync()) {
      await _execAsync(db, $"UPDATE wh_service_instances SET last_heartbeat_at = now() - interval '1 hour' WHERE instance_id = '{previous}'");
    }
    var afterItStops = await _reconcileAsync(settings: _gated, instanceId: self);

    await Assert.That(afterItStops.Dropped.Select(d => d.Name)).IsEquivalentTo(["idx_job_data_gin"]);
    await Assert.That(await _declarationsAsync()).IsEquivalentTo([self])
      .Because("the declarations of an instance that is no longer running are forgotten");
  }

  [Test]
  public async Task WhileAnInstanceThatHasNotReportedIsRunning_NothingIsDroppedAsync() {
    var self = Guid.CreateVersion7();
    await _registryAsync((self, TimeSpan.Zero), (Guid.CreateVersion7(), TimeSpan.FromSeconds(10)));
    await _reconcileAsync(settings: _gated, instanceId: self);

    var report = await _reconcileAsync(settings: _gated, instanceId: self);

    await Assert.That(report.Dropped).IsEmpty()
      .Because("a running instance that never reported is on a release before the ledger, and what it uses is unknown");
  }

  [Test]
  public async Task WithNoInstanceRegistry_TheFleetIsThisInstanceAloneAsync() {
    await _reconcileAsync(settings: _gated);

    var report = await _reconcileAsync(settings: _gated);

    await Assert.That(report.Dropped.Select(d => d.Name)).IsEquivalentTo(["idx_job_data_gin", "ck_wh_per_job_code_len"]);
  }

  [Test]
  public async Task ADeclaredObjectDroppedByHand_NamesItsTableForRebuildingAsync() {
    await _reconcileAsync();
    await using var db = await _openAsync();
    await _execAsync(db, "DROP INDEX idx_job_status");

    var tables = await ManagedSchemaReconciler.TablesMissingDeclaredObjectsAsync(db, SCHEMA, _declared(), CancellationToken.None);

    await Assert.That(tables).IsEquivalentTo([TABLE]);
  }

  [Test]
  public async Task ADeclaredObjectThatWasNeverBuilt_IsNotRebuiltAtEveryStartAsync() {
    await _reconcileAsync();
    await using var db = await _openAsync();

    var tables = await ManagedSchemaReconciler.TablesMissingDeclaredObjectsAsync(
      db, SCHEMA, _declared().Index(TABLE, "idx_job_trgm", "JobModel.Code [Indexed(Substring)]"), CancellationToken.None);

    await Assert.That(tables).IsEmpty()
      .Because("an index the server never built (an extension it refused) is reported, not retried under the lock forever");
  }

  [Test]
  public async Task WithNoLedger_NothingIsNamedForRebuildingAsync() {
    await using var db = await _openAsync();
    await _execAsync(db, "DROP TABLE wh_managed_objects");

    await Assert.That(await ManagedSchemaReconciler.TablesMissingDeclaredObjectsAsync(
      db, SCHEMA, _declared(), CancellationToken.None)).IsEmpty();
  }

  [Test]
  public async Task EveryDropKeepAndMissingObject_IsLoggedAsync() {
    var logger = new ListLogger();
    var declared = _declared().Index(TABLE, "idx_job_owner", "JobModel.Owner [Indexed]");
    await _reconcileAsync(declared);
    await using (var db = await _openAsync()) {
      await _execAsync(db, "SELECT public.wh_pin_object('wh_per_job', 'idx_job_data_gin', 'keep it')");
    }

    await _reconcileAsync(declared, logger: logger);

    await Assert.That(logger.Messages).Contains(m => m.Contains("dropped Constraint ck_wh_per_job_code_len", StringComparison.Ordinal));
    await Assert.That(logger.Messages).Contains(m => m.Contains("kept Index idx_job_data_gin (pinned by sql: keep it)", StringComparison.Ordinal));
    await Assert.That(logger.Messages).Contains(m => m.Contains("Index idx_job_owner is missing", StringComparison.Ordinal));
  }

  [Test]
  public async Task ADropTheDatabaseRefuses_IsReportedAndStaysPendingAsync() {
    var logger = new ListLogger();
    await using (var db = await _openAsync()) {
      // A unique constraint another table's foreign key depends on cannot be dropped without CASCADE.
      await _execAsync(db, $"""
        ALTER TABLE {TABLE} ADD CONSTRAINT ck_{TABLE}_code_uq UNIQUE (code);
        CREATE TABLE job_reference (code text REFERENCES {TABLE} (code));
        """);
    }
    await _reconcileAsync();

    var report = await _reconcileAsync(logger: logger);

    await Assert.That(report.Failed.Select(f => f.Drop.Name)).IsEquivalentTo([$"ck_{TABLE}_code_uq"]);
    await Assert.That(logger.Messages).Contains(m => m.Contains($"could not drop Constraint ck_{TABLE}_code_uq", StringComparison.Ordinal));
    await Assert.That(await _ledgerAsync()).Contains($"ck_{TABLE}_code_uq:whizbang:pending-retirement");
  }

  [Test]
  public async Task WithTheReconcileOff_NothingIsReadOrWrittenAsync() {
    var report = await _reconcileAsync(settings: _settings(ReconcileMode.Off));

    await Assert.That(report.Skipped).IsTrue();
    await Assert.That(await _ledgerAsync()).IsEmpty();
  }

  [Test]
  public async Task WithoutTheLedgerTable_TheReconcileIsSkippedAsync() {
    await using (var db = await _openAsync()) {
      await _execAsync(db, "DROP TABLE wh_managed_objects");
    }

    await Assert.That((await _reconcileAsync()).Skipped).IsTrue();
  }

  [Test]
  public async Task WithNoPerspectiveTables_NothingIsRecordedAsync() {
    await using (var db = await _openAsync()) {
      await _execAsync(db, $"DROP TABLE {TABLE}");
    }

    var report = await _reconcileAsync();

    await Assert.That(report.Recorded).IsEqualTo(0);
    await Assert.That(report.Missing.Select(m => m.Name)).IsEquivalentTo(["idx_job_status"]);
  }

  [Test]
  public async Task APinSetWithSql_KeepsTheObjectAsync() {
    await _reconcileAsync();
    await using (var db = await _openAsync()) {
      await _execAsync(db, "SELECT public.wh_pin_object('wh_per_job', 'idx_job_data_gin', 'keep it')");
    }

    var report = await _reconcileAsync();

    await Assert.That(report.Dropped.Select(d => d.Name)).DoesNotContain("idx_job_data_gin");
    await Assert.That(await _objectsAsync()).Contains("idx_job_data_gin");
  }

  [Test]
  public async Task APinComment_KeepsTheObjectAsync() {
    await using (var db = await _openAsync()) {
      await _execAsync(db, "COMMENT ON INDEX idx_job_data_gin IS 'whizbang:pin the search page reads it'");
    }
    await _reconcileAsync();

    await _reconcileAsync();

    await Assert.That(await _objectsAsync()).Contains("idx_job_data_gin");
  }

  [Test]
  public async Task ReportOnly_RecordsButDropsNothingAsync() {
    await _reconcileAsync();

    var report = await _reconcileAsync(settings: _settings(ReconcileMode.ReportOnly));

    await Assert.That(report.Dropped).IsEmpty();
    await Assert.That(report.Kept.Select(k => k.Name)).Contains("idx_job_data_gin");
    await Assert.That(await _objectsAsync()).Contains("idx_job_data_gin");
  }

  [Test]
  public async Task WhileAnotherSessionHoldsTheSchemaLock_ItDoesNothingAsync() {
    await using var holder = await _openAsync();
    await _execAsync(holder, $"SELECT pg_advisory_lock({_lockId})");

    var report = await _reconcileAsync();

    await Assert.That(report.Skipped).IsTrue();
    await Assert.That(await _ledgerAsync()).IsEmpty();
  }

  [Test]
  public async Task ADeclaredObjectTheDatabaseLacks_IsReportedMissingAsync() {
    var declared = _declared().Index(TABLE, "idx_job_owner", "JobModel.Owner [Indexed]");

    var report = await _reconcileAsync(declared);

    await Assert.That(report.Missing.Select(m => m.Name)).IsEquivalentTo(["idx_job_owner"]);
  }

  private async Task<List<string>> _objectsAsync() {
    await using var db = await _openAsync();
    await using var cmd = new NpgsqlCommand($"""
      SELECT indexname FROM pg_indexes WHERE tablename = '{TABLE}'
      UNION ALL SELECT conname FROM pg_constraint WHERE conrelid = '{TABLE}'::regclass
      """, db);
    var names = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      names.Add(reader.GetString(0));
    }
    return names;
  }

  private async Task<List<string>> _ledgerAsync() {
    await using var db = await _openAsync();
    await using var cmd = new NpgsqlCommand(
      "SELECT object_name || ':' || coalesce(owner, '?') || ':' || status FROM public.wh_managed_objects ORDER BY object_name", db);
    var rows = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetString(0));
    }
    return rows;
  }

  private async Task<List<Guid>> _declarationsAsync() {
    await using var db = await _openAsync();
    await using var cmd = new NpgsqlCommand("SELECT instance_id FROM wh_managed_object_declarations", db);
    var ids = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
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
