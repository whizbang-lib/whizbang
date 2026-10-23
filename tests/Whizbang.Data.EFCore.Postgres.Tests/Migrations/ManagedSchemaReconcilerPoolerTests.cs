// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
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
/// The reconcile behind a transaction-pooling pooler, where consecutive statements outside a transaction can run on
/// different backends: it must not leave the schema initialization lock held on a backend it can no longer reach,
/// because every later schema initialization on any instance would find that lock held.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Schema/ManagedSchemaReconciler.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class ManagedSchemaReconcilerPoolerTests {
  private const string SCHEMA = "public";
  private const string TABLE = "wh_per_job";
  private static readonly long _lockId = SchemaInitializationLockKey.Compute(SCHEMA);

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("mgd_pooler");
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
      ALTER TABLE {TABLE} ADD CONSTRAINT ck_{TABLE}_code_len CHECK (length(code) <= 50) NOT VALID;
      """);
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  [Test]
  public async Task ThroughATransactionPooler_NoLockIsLeftOnTheSchemaKey_AndTheNextInitializationTakesItAsync() {
    await using var pooler = new TransactionPoolerSimulation(_connectionString, claimsDirect: false);

    await _reconcileAsync(pooler);
    var report = await _reconcileAsync(pooler);

    await Assert.That(report.Skipped).IsFalse();
    await Assert.That(pooler.Backends).IsGreaterThan(2)
      .Because("the simulation only proves something when the reconcile's statements reached more than one backend");
    await using var next = await _openAsync();
    await Assert.That(await AdvisoryLockProbe.IsHeldElsewhereAsync(next, _lockId)).IsFalse()
      .Because("a lock left on a backend the reconcile can no longer reach makes every later schema initialization wait or skip");
    await Assert.That(await _tryTheSchemaLockAsync(next)).IsTrue()
      .Because("the next schema initialization takes the same key with pg_try_advisory_xact_lock");
  }

  [Test]
  public async Task ThroughATransactionPooler_IndexDropsWaitForADirectConnection_AndSayWhyAsync() {
    await using var pooler = new TransactionPoolerSimulation(_connectionString, claimsDirect: false);
    var logger = new ListLogger();
    await _reconcileAsync(pooler);

    var report = await _reconcileAsync(pooler, logger);

    await Assert.That(report.Dropped.Select(d => d.Name)).IsEquivalentTo([$"ck_{TABLE}_code_len"])
      .Because("a constraint drop runs in a transaction, under a lock the transaction releases");
    await Assert.That(await _objectsAsync()).Contains("idx_job_data_gin")
      .Because("a concurrent index drop cannot run in a transaction, and a session lock around it can leak through a pooler");
    await Assert.That(await _ledgerAsync()).Contains("idx_job_data_gin:whizbang:pending-retirement");
    await Assert.That(logger.Warnings).Contains(m =>
      m.Contains("idx_job_data_gin", StringComparison.Ordinal) && m.Contains("direct", StringComparison.Ordinal));
  }

  [Test]
  public async Task ThroughAPoolerTheDirectCheckCannotSee_AReleaseThatFindsNothingIsLoggedAsync() {
    await using var pooler = new TransactionPoolerSimulation(_connectionString, claimsDirect: true);
    var logger = new ListLogger();
    await _reconcileAsync(pooler);

    await _reconcileAsync(pooler, logger);

    await Assert.That(logger.Warnings).Contains(m =>
      m.Contains("schema initialization", StringComparison.Ordinal)
      && m.Contains(_lockId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
      && m.Contains("not held", StringComparison.Ordinal))
      .Because("an unlock that returns false means the lock is still held on some other backend, and that must be said");
  }

  [Test]
  public async Task WhenAnotherSessionTakesTheSchemaLockAfterThePlan_EveryDropStaysPendingAsync() {
    await using var holder = await _openAsync();
    NpgsqlTransaction? held = null;
    await using var pooler = new TransactionPoolerSimulation(_connectionString, claimsDirect: true) {
      // Another instance's schema initialization, holding the key in its transaction.
      AfterFirstTransaction = async () => {
        held = await holder.BeginTransactionAsync();
        await using var take = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({_lockId})", holder, held);
        await take.ExecuteNonQueryAsync();
      },
    };
    var logger = new ListLogger();
    await using (var first = new TransactionPoolerSimulation(_connectionString, claimsDirect: false)) {
      await _reconcileAsync(first);
    }

    var report = await _reconcileAsync(pooler, logger);

    await Assert.That(held).IsNotNull();
    await Assert.That(report.Skipped).IsFalse();
    await Assert.That(report.Dropped).IsEmpty()
      .Because("a drop runs only while this session holds the schema lock, and another session took it after the plan");
    await Assert.That(report.Failed).IsEmpty();
    var objects = await _objectsAsync();
    await Assert.That(objects).Contains("idx_job_data_gin");
    await Assert.That(objects).Contains($"ck_{TABLE}_code_len");
    await Assert.That(logger.Messages).Contains(m =>
      m.Contains("pending", StringComparison.Ordinal) && m.Contains("idx_job_data_gin", StringComparison.Ordinal)
      && m.Contains($"ck_{TABLE}_code_len", StringComparison.Ordinal));
    await held!.RollbackAsync();
  }

  private static async Task<ManagedSchemaReport> _reconcileAsync(IReconcileBackend backend, ILogger? logger = null) =>
    await ManagedSchemaReconciler.RunAsync(
      backend, SCHEMA, new ManagedSchemaObjectSet().Index(TABLE, "idx_job_status", "JobModel.Status [Indexed]"),
      new ManagedSchemaSettings(ReconcileMode.Apply, [], [], DropAfterFleetConverged: false), null, logger, CancellationToken.None);

  private static async Task<bool> _tryTheSchemaLockAsync(NpgsqlConnection connection) {
    await using var transaction = await connection.BeginTransactionAsync();
    await using var command = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@id)", connection, transaction);
    command.Parameters.AddWithValue("id", _lockId);
    var won = await command.ExecuteScalarAsync() is true;
    await transaction.RollbackAsync();
    return won;
  }

  /// <summary>Every message logged, formatted, and the warnings among them.</summary>
  private sealed class ListLogger : ILogger {
    public List<string> Messages { get; } = [];
    public List<string> Warnings { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      var message = formatter(state, exception);
      Messages.Add(message);
      if (logLevel == LogLevel.Warning) {
        Warnings.Add(message);
      }
    }
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
