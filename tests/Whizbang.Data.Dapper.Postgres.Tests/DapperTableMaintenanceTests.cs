// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Table maintenance on the Dapper driver, with the contract the EF Core driver keeps when no schema-initialization
/// connection is configured: the rewrite queue and its measurements, the estimate and approximate measurement, the
/// offline, external and online rewrites, the work-table vacuum, and the queue-index survey, drop and rebuild, each
/// refused for a name outside the framework's tables and reported for a table the coordinator's role does not own (#2051).
/// </summary>
/// <remarks>
/// <para>
/// This coordinator inherited the interface's inert defaults for every one of them, so on this driver the maintenance
/// cycle found nothing to rewrite, vacuum or rebuild whatever the tables held. Each member now runs the statements in
/// <c>TableMaintenanceSql</c>, which the EF Core coordinator runs too.
/// </para>
/// <para>
/// A rewrite is observed through the table's file node, which <c>VACUUM FULL</c> and <c>REINDEX</c> always replace,
/// rather than through a size that depends on the server's cleanup horizon. Twins on the EF Core driver:
/// <c>TableRewriteRequestSqlTests</c>, <c>TableBloatMeasurementSqlTests</c>, <c>WorkTableVacuumTests</c>,
/// <c>QueueIndexReindexSqlTests</c>, <c>OnlineTableRewriteSqlTests</c>, <c>ExternalTableRewriteTests</c>. Design choice
/// documented at data/drivers#dapper-coordinator-parity.
/// </para>
/// </remarks>
/// <docs>data/drivers#dapper-coordinator-parity</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.TableMaintenance.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/TableMaintenanceSql.cs</code-under-test>
[Category("Integration")]
[NotInParallel("DapperTableMaintenance")]
public class DapperTableMaintenanceTests : PostgresTestBase {
  private const string PKEY = "wh_active_streams_pkey";
  private static readonly string[] _workTables = ["wh_outbox", "wh_inbox", "wh_perspective_events", "wh_message_deduplication"];
  private static readonly string[] _queueTables =
    ["wh_outbox", "wh_inbox", "wh_inbox_state", "wh_perspective_events", "wh_active_streams", "wh_doorbell_queue"];

  private sealed class CapturingLogger : ILogger<DapperWorkCoordinator> {
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Messages.Add(formatter(state, exception));
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Deliberate: the interface is what the maintenance steps call through, so an unimplemented member reaches its default here as it does there.")]
  private IWorkCoordinator _coordinator(ILogger<DapperWorkCoordinator>? logger = null, string? connectionString = null) =>
    new DapperWorkCoordinator(connectionString ?? ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), logger);

  [Test]
  public async Task RewriteRequests_AreQueuedOnce_ListedWithTheirMeasurement_AndClearedAsync() {
    var coordinator = _coordinator();
    await _executeAsync("UPDATE wh_settings SET setting_value = '0' WHERE setting_key = 'table_rewrite_bloat_threshold'");
    await _executeAsync("UPDATE wh_settings SET setting_value = '1' WHERE setting_key = 'table_rewrite_min_rows'");
    await _executeAsync("ANALYZE wh_settings");
    await _executeAsync("ANALYZE wh_outbox");

    await coordinator.RequestTableRewriteAsync("wh_outbox");
    await coordinator.RequestTableRewriteAsync("wh_outbox");
    await coordinator.RequestMeasuredTableRewriteAsync("wh_settings", new BloatMeasurement(999.5, BloatMeasurementMethod.Approx), 4.0);
    var queue = await coordinator.GetTablesNeedingRewriteAsync();

    await Assert.That((await _pendingAsync()).Split(',').Count(t => t == "wh_outbox")).IsEqualTo(1)
      .Because("re-recording a request does not duplicate it");
    await Assert.That(queue[0].TableName).IsEqualTo("wh_settings")
      .Because("the queue is taken largest measured ratio first, as on EF Core");
    await Assert.That(queue[0].BloatRatio).IsEqualTo(999.5);
    await Assert.That(queue[0].Method).IsEqualTo(BloatMeasurementMethod.Approx);
    await Assert.That(queue[0].Requested).IsTrue();
    await Assert.That(queue[0].SizeBytes).IsEqualTo(await _scalarAsync<long>("SELECT pg_relation_size('wh_settings')"));
    await Assert.That(queue[0].RewriteThreshold).IsEqualTo(0.0);

    await coordinator.ClearTableRewriteRequestAsync("wh_outbox");
    await coordinator.ClearTableRewriteRequestAsync("wh_settings");

    await Assert.That((await _pendingAsync()).Split(',').Contains("wh_outbox")).IsFalse();
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_table_rewrite_measurements")).IsEqualTo(0L)
      .Because("a cleared request takes its measurement with it");
  }

  [Test]
  public async Task MeasureTableBloat_EstimateMatchesTheScan_ApproxReadsTheTable_AndExactIsRefusedAsync() {
    var coordinator = _coordinator();
    await _executeAsync("CREATE EXTENSION IF NOT EXISTS pgstattuple");
    await _executeAsync("UPDATE wh_settings SET setting_value = '0' WHERE setting_key = 'table_rewrite_bloat_threshold'");
    await _executeAsync("UPDATE wh_settings SET setting_value = '1' WHERE setting_key = 'table_rewrite_min_rows'");
    await _executeAsync("ANALYZE wh_settings");

    var candidate = (await coordinator.GetTablesNeedingRewriteAsync()).Single(c => c.TableName == "wh_settings");
    var estimate = await coordinator.MeasureTableBloatAsync("wh_settings", BloatMeasurementMethod.Estimate);
    var approx = await coordinator.MeasureTableBloatAsync("wh_settings", BloatMeasurementMethod.Approx);
    var missing = await coordinator.MeasureTableBloatAsync("wh_no_such_table", BloatMeasurementMethod.Approx);

    await Assert.That(estimate).IsEqualTo(candidate.BloatRatio)
      .Because("the estimate measured on its own is the ratio the candidate scan reports");
    await Assert.That(approx.HasValue).IsTrue();
    await Assert.That(missing.HasValue).IsFalse();
    await Assert.That(async () => await coordinator.MeasureTableBloatAsync("wh_settings", BloatMeasurementMethod.Exact))
      .Throws<NotSupportedException>();
    await Assert.That(async () => await coordinator.MeasureTableBloatAsync("users", BloatMeasurementMethod.Approx))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task RewriteTable_RewritesTheHeap_AndRefusesANameOutsideTheFrameworksTablesAsync() {
    var coordinator = _coordinator();
    var before = await _fileNodeAsync("wh_outbox");

    var ratio = await coordinator.RewriteTableAsync("wh_outbox");

    await Assert.That(await _fileNodeAsync("wh_outbox")).IsNotEqualTo(before)
      .Because("VACUUM FULL writes the table into a new file, which is the rewrite having run");
    await Assert.That(ratio is null || ratio > 0).IsTrue();
    foreach (var bad in new[] { "users", "wh_outbox; DROP TABLE wh_outbox", "wh_Outbox", "wh" }) {
      await Assert.That(async () => await coordinator.RewriteTableAsync(bad)).Throws<ArgumentException>()
        .Because($"'{bad}' must never reach interpolated DDL");
    }
  }

  [Test]
  public async Task RewriteTableWith_HandsTheProgramThisCoordinatorsConnection_AndMeasuresOnlyWhenItRewroteAsync() {
    var coordinator = _coordinator();
    TableRewriteTarget? seen = null;

    var notRewritten = await coordinator.RewriteTableWithAsync("wh_settings", (_, _) => Task.FromResult(false));
    var ratio = await coordinator.RewriteTableWithAsync("wh_settings", (target, _) => {
      seen = target;
      return Task.FromResult(true);
    });

    var expected = new NpgsqlConnectionStringBuilder(ConnectionString);
    await Assert.That(notRewritten).IsNull().Because("a program that did not rewrite leaves the request queued");
    await Assert.That(seen).IsNotNull();
    await Assert.That(seen!.QualifiedTable).IsEqualTo("\"public\".wh_settings");
    await Assert.That(seen.ClientEnvironment["PGUSER"]).IsEqualTo(expected.Username);
    await Assert.That(seen.ClientEnvironment["PGPASSWORD"]).IsEqualTo(expected.Password);
    await Assert.That(seen.ClientEnvironment["PGDATABASE"]).IsEqualTo(expected.Database);
    await Assert.That(ratio is null || ratio > 0).IsTrue();
    await Assert.That(async () => await coordinator.RewriteTableWithAsync("orders", (_, _) => Task.FromResult(true)))
      .Throws<ArgumentException>();
    await Assert.That(async () => await coordinator.RewriteTableWithAsync("wh_settings", null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Squeeze_WithoutPgSqueeze_IsReportedAndNotAttemptedAsync() {
    var logger = new CapturingLogger();

    var after = await _coordinator(logger).SqueezeTableAsync("wh_settings");

    await Assert.That(after).IsNull().Because("nothing was rewritten, so the request stays queued");
    await Assert.That(logger.Messages).Contains(
      "Table wh_settings was not rewritten: pg_squeeze is not installed in this database (no squeeze.squeeze_table function)");
    await Assert.That(async () => await _coordinator().SqueezeTableAsync("orders; DROP TABLE wh_settings")).Throws<ArgumentException>();
  }

  [Test]
  public async Task Squeeze_WithThePrerequisitesMet_CallsPgSqueezeForTheTableAsync() {
    await _executeAsync("""
      CREATE SCHEMA squeeze;
      CREATE TABLE squeeze.calls (tabschema name, tabname name);
      CREATE FUNCTION squeeze.squeeze_table(
          tabschema name, tabname name, clustering_index name DEFAULT NULL, rel_tablespace name DEFAULT NULL,
          ind_tablespaces name[] DEFAULT NULL)
        RETURNS void LANGUAGE sql
        AS $$ INSERT INTO squeeze.calls VALUES (tabschema, tabname) $$;
      """);
    var coordinator = new DapperWorkCoordinator(ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions()) {
      SqueezePrerequisitesUnmet = (_, _) => null,
    };

    var after = await coordinator.SqueezeTableAsync("wh_settings");

    await Assert.That(await _scalarAsync<string>("SELECT string_agg(tabschema || '.' || tabname, ',') FROM squeeze.calls"))
      .IsEqualTo("public.wh_settings").Because("pg_squeeze is called once, for the table in its own schema");
    await Assert.That(after is null || after > 0).IsTrue();
  }

  [Test]
  public async Task VacuumWorkTables_VacuumsEveryWorkTable_AndReportsOneThatIsMissingAsync() {
    await _executeAsync("DROP TABLE wh_message_deduplication CASCADE");

    var outcomes = await _coordinator().VacuumWorkTablesAsync();

    await Assert.That(outcomes.Select(o => o.TableName)).IsEquivalentTo(_workTables);
    await Assert.That(outcomes.Count(o => o.Vacuumed && o.SkippedReason is null)).IsEqualTo(3);
    await Assert.That(outcomes.Single(o => o.TableName == "wh_message_deduplication").SkippedReason).Contains("does not exist");
  }

  [Test]
  public async Task UnderARoleThatOwnsNoTable_NothingIsVacuumedRewrittenOrRebuilt_AndEachIsReportedAsync() {
    var role = $"wh_app_{Guid.NewGuid():N}"[..30];
    await _executeAsync($"""
      CREATE ROLE {role} LOGIN PASSWORD 'data-only';
      GRANT USAGE ON SCHEMA public TO {role};
      GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {role};
      GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO {role};
      """);
    try {
      var logger = new CapturingLogger();
      var coordinator = _coordinator(logger, new NpgsqlConnectionStringBuilder(ConnectionString) { Username = role, Password = "data-only" }.ConnectionString);
      var called = false;

      var outcomes = await coordinator.VacuumWorkTablesAsync();
      var rewritten = await coordinator.RewriteTableAsync("wh_outbox");
      var handedOver = await coordinator.RewriteTableWithAsync("wh_outbox", (_, _) => {
        called = true;
        return Task.FromResult(true);
      });
      var rebuilt = await coordinator.ReindexQueueIndexAsync(PKEY);

      foreach (var outcome in outcomes) {
        await Assert.That(outcome.Vacuumed).IsFalse();
        await Assert.That(outcome.SkippedReason).IsEqualTo(
          $"role '{role}' does not own {outcome.TableName} and {DapperWorkCoordinator.OWNER_REMEDY}");
      }
      await Assert.That(rewritten).IsNull();
      await Assert.That(handedOver).IsNull();
      await Assert.That(called).IsFalse();
      await Assert.That(rebuilt).IsNull();
      await Assert.That(logger.Messages).Contains($"Table wh_outbox was not rewritten: role '{role}' does not own wh_outbox and {DapperWorkCoordinator.OWNER_REMEDY}");
      await Assert.That(logger.Messages).Contains($"Index {PKEY} was not rebuilt: role '{role}' does not own wh_active_streams and {DapperWorkCoordinator.OWNER_REMEDY}");
    } finally {
      await _executeAsync($"DROP OWNED BY {role}; DROP ROLE {role};");
    }
  }

  [Test]
  public async Task QueueIndexes_SurveyCoversTheQueueTables_ReindexRebuilds_AndOnlyAnInvalidLeftoverIsDroppedAsync() {
    var logger = new CapturingLogger();
    var coordinator = _coordinator(logger);
    await _executeAsync("INSERT INTO wh_active_streams (stream_id, partition_number) VALUES (gen_random_uuid(), 7), (gen_random_uuid(), 7)");
    await Assert.That(async () => await _executeAsync($"CREATE UNIQUE INDEX CONCURRENTLY {PKEY}_ccnew ON wh_active_streams (partition_number)"))
      .Throws<PostgresException>();

    var survey = await coordinator.GetQueueIndexBloatAsync();
    var before = await _fileNodeAsync(PKEY);
    var size = await coordinator.ReindexQueueIndexAsync(PKEY);
    var refusedValid = await coordinator.DropInvalidQueueIndexAsync(PKEY);
    var refusedRebuild = await coordinator.ReindexQueueIndexAsync(PKEY + "_ccnew");
    var refusedOther = await coordinator.ReindexQueueIndexAsync("wh_settings_pkey");
    var dropped = await coordinator.DropInvalidQueueIndexAsync(PKEY + "_ccnew");

    await Assert.That(survey.Select(s => s.TableName).Distinct().Order(StringComparer.Ordinal))
      .IsEquivalentTo(_queueTables.Order(StringComparer.Ordinal));
    await Assert.That(survey.Single(s => s.IndexName == PKEY + "_ccnew").Valid).IsFalse();
    await Assert.That(size.HasValue).IsTrue();
    await Assert.That(await _fileNodeAsync(PKEY)).IsNotEqualTo(before).Because("REINDEX builds the index into a new file");
    await Assert.That(refusedValid).IsFalse();
    await Assert.That(refusedRebuild).IsNull();
    await Assert.That(refusedOther).IsNull();
    await Assert.That(dropped).IsTrue();
    await Assert.That(await _scalarAsync<bool>($"SELECT to_regclass('{PKEY}_ccnew') IS NULL")).IsTrue();
    await Assert.That(logger.Messages).Contains($"Index {PKEY} was not dropped: it is valid; only an invalid leftover is dropped");
    await Assert.That(logger.Messages).Contains($"Index {PKEY}_ccnew was not rebuilt: it is invalid; it is dropped, not rebuilt");
    await Assert.That(logger.Messages).Contains("Index wh_settings_pkey was not rebuilt: it is not an index on a queue table");
  }

  private Task<string> _pendingAsync() =>
    _scalarAsync<string>("SELECT COALESCE((SELECT setting_value FROM wh_settings WHERE setting_key = 'pending_table_rewrites'), '')");

  private Task<long> _fileNodeAsync(string relation) =>
    _scalarAsync<long>($"SELECT pg_relation_filenode(to_regclass('{relation}'))::BIGINT");

  private async Task _executeAsync(string sql) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.ExecuteAsync(sql);
  }

  private async Task<T> _scalarAsync<T>(string sql) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    return (await connection.ExecuteScalarAsync<T>(sql))!;
  }
}
