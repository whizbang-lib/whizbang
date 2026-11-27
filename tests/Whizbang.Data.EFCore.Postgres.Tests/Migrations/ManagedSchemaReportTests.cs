// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// #2608: the report every start makes, apart from the reconcile. It records what this instance declares, as a reconcile
/// by it would record it (row retention's <c>updated_at</c> index and each declared index standing under another name
/// included), and changes nothing else: no ledger row, no drop, no index built. It records nothing with the reconcile
/// off, with no instance id or without the declarations table, and a report that fails is said at Warning and never
/// thrown.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects#rolling-deploys</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Schema/ManagedSchemaReconciler.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Schema/ManagedSchemaFleet.cs</code-under-test>
[Category("Integration")]
[Category("Shard1")]
public class ManagedSchemaReportTests {
  private const string SCHEMA = "public";
  private const string TABLE = "wh_per_job";

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("mgd_report");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;

    await using var db = await _openAsync();
    foreach (var migration in new[] { "000_MigrationTracking.sql", "200_ManagedObjects.sql" }) {
      await _execAsync(db, (await File.ReadAllTextAsync(_migrationPath(migration)))
        .Replace("__SCHEMA__", "\"public\"", StringComparison.Ordinal));
    }
    await _execAsync(db, $"""
      CREATE TABLE {TABLE} (id uuid PRIMARY KEY, data jsonb NOT NULL, updated_at timestamptz NOT NULL DEFAULT now());
      CREATE INDEX idx_job_status ON {TABLE} ((data->>'Status'));
      CREATE INDEX idx_job_data_gin ON {TABLE} USING gin (data);
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

  private static ManagedSchemaSettings _settings(ReconcileMode mode = ReconcileMode.Apply) =>
    new(mode, [], [], DropAfterFleetConverged: true);

  private async Task<bool> _reportAsync(
      Guid? instanceId, ManagedSchemaObjectSet? declared = null, ManagedSchemaSettings? settings = null, ILogger? logger = null) {
    await using var db = await _openAsync();
    return await ManagedSchemaReconciler.ReportAsync(
      db, SCHEMA, declared ?? _declared(), settings ?? _settings(), instanceId, logger, CancellationToken.None);
  }

  [Test]
  public async Task AReport_RecordsWhatThisInstanceDeclares_AndChangesNothingElseAsync() {
    var self = Guid.CreateVersion7();

    var reported = await _reportAsync(self);

    await Assert.That(reported).IsTrue();
    await Assert.That(await _declaredByAsync(self)).IsEquivalentTo([$"{TABLE}:idx_job_status"]);
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_managed_objects")).IsEqualTo(0L)
      .Because("the report writes no ledger row: that is the reconcile's");
    await Assert.That(await _scalarAsync<bool>("SELECT to_regclass('public.idx_job_data_gin') IS NOT NULL")).IsTrue()
      .Because("the report drops nothing");
  }

  [Test]
  public async Task ALaterReport_ReplacesTheInstancesEarlierRecordAsync() {
    var self = Guid.CreateVersion7();
    await _reportAsync(self, _declared().Index(TABLE, "idx_job_data_gin", "JobModel [IndexAllFields]"));

    await _reportAsync(self);

    await Assert.That(await _declaredByAsync(self)).IsEquivalentTo([$"{TABLE}:idx_job_status"]);
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_managed_object_declarations")).IsEqualTo(1L);
  }

  [Test]
  public async Task AReport_NamesADeclaredIndexByTheNameItStandsUnder_AndRetentionsIndex_WithoutBuildingItAsync() {
    var self = Guid.CreateVersion7();
    await using (var db = await _openAsync()) {
      await _execAsync(db, $"""
        CREATE TABLE wh_index_equivalents (table_name text NOT NULL, declared_name text NOT NULL, existing_name text NOT NULL);
        INSERT INTO wh_index_equivalents VALUES ('{TABLE}', 'idx_job_status_v2', 'idx_job_status');
        CREATE TABLE wh_perspective_registry (table_name text NOT NULL, row_retention_enrolled boolean NOT NULL);
        INSERT INTO wh_perspective_registry VALUES ('{TABLE}', TRUE);
        """);
    }

    await _reportAsync(self, new ManagedSchemaObjectSet().Index(TABLE, "idx_job_status_v2", "JobModel.Status [Indexed]"));

    await Assert.That(await _declaredByAsync(self)).IsEquivalentTo([
      $"{TABLE}:idx_job_status", $"{TABLE}:{PerspectiveRetentionIndexes.IndexName(TABLE)}",
    ]);
    await Assert.That(await _scalarAsync<bool>($"SELECT to_regclass('public.{PerspectiveRetentionIndexes.IndexName(TABLE)}') IS NOT NULL"))
      .IsFalse().Because("the report reads what retention asks for; building it is the reconcile's");
  }

  [Test]
  public async Task WithTheReconcileOff_NothingIsRecordedAsync() {
    var self = Guid.CreateVersion7();

    var reported = await _reportAsync(self, settings: _settings(ReconcileMode.Off));

    await Assert.That(reported).IsFalse();
    await Assert.That(await _declaredByAsync(self)).IsNull();
  }

  [Test]
  public async Task WithNoInstanceId_NothingIsRecordedAsync() {
    var reported = await _reportAsync(null);

    await Assert.That(reported).IsFalse();
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_managed_object_declarations")).IsEqualTo(0L);
  }

  [Test]
  public async Task WithoutTheDeclarationsTable_NothingIsRecordedAsync() {
    await using (var db = await _openAsync()) {
      await _execAsync(db, "DROP TABLE wh_managed_object_declarations");
    }

    var reported = await _reportAsync(Guid.CreateVersion7());

    await Assert.That(reported).IsFalse();
  }

  [Test]
  public async Task AReportThatFails_IsSaidAtWarning_AndNotThrownAsync() {
    var log = new ListLogger();
    await using var closed = new NpgsqlConnection(_connectionString);

    var reported = await ManagedSchemaReconciler.ReportAsync(
      closed, SCHEMA, _declared(), _settings(), Guid.CreateVersion7(), log, CancellationToken.None);

    await Assert.That(reported).IsFalse();
    await Assert.That(log.Entries).Contains(e => e.Level == LogLevel.Warning
      && e.Message.Contains("could not record what this instance declares", StringComparison.Ordinal)
      && e.Exception is not null);
  }

  [Test]
  public async Task AReportOnACanceledToken_IsCanceledAsync() {
    await using var db = await _openAsync();
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();

    await Assert.That(async () => await ManagedSchemaReconciler.ReportAsync(
        db, SCHEMA, _declared(), _settings(), Guid.CreateVersion7(), null, canceled.Token))
      .Throws<OperationCanceledException>();
  }

  /// <summary>Every entry logged, with its level and exception.</summary>
  private sealed class ListLogger : ILogger {
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
  }

  private async Task<string[]?> _declaredByAsync(Guid instance) {
    await using var db = await _openAsync();
    await using var command = new NpgsqlCommand("SELECT objects FROM wh_managed_object_declarations WHERE instance_id = $1", db);
    command.Parameters.AddWithValue(instance);
    return await command.ExecuteScalarAsync() as string[];
  }

  private async Task<T> _scalarAsync<T>(string sql) {
    await using var db = await _openAsync();
    await using var command = new NpgsqlCommand(sql, db);
    return (T)(await command.ExecuteScalarAsync())!;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    return db;
  }

  private static async Task _execAsync(NpgsqlConnection db, string sql) {
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private static string _migrationPath(string fileName) => Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
    "src", "Whizbang.Data.Postgres", "Migrations", fileName);
}
