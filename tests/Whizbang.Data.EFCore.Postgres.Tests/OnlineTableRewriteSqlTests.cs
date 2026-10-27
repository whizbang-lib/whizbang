// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The SQL behind the online rewrite (#1743), against the shared test server. That server has no pg_squeeze (the
/// image does not ship it), so these tests prove what can be proved without it: each candidate carries its heap size
/// for the per-cycle budget, the prerequisite query reads the facts the decision needs from the real catalog, and a
/// squeeze on a server without pg_squeeze is reported and not attempted, leaving the heap as it was. A squeeze that
/// actually reclaims space needs an image with pg_squeeze, which the test infrastructure does not have yet.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/PgSqueezePrerequisites.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public class OnlineTableRewriteSqlTests : EFCoreTestBase {

  private sealed class CapturingLogger<T> : ILogger<T> {
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Messages.Add(formatter(state, exception));
  }

  private async Task<T> _scalarAsync<T>(string sql) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    return (T)(await command.ExecuteScalarAsync())!;
  }

  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<PgSqueezePrerequisiteFacts> _factsAsync(string table) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync();
    return await PgSqueezePrerequisites.ReadAsync(db, table, CancellationToken.None);
  }

  [Test]
  [Timeout(120000)]
  public async Task EachCandidate_CarriesItsHeapSize_ForTheBudgetAsync(CancellationToken cancellationToken) {
    await TableBloat.ManufactureAsync(ConnectionString, cancellationToken);
    await using var context = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions());

    var candidate = (await coordinator.GetTablesNeedingRewriteAsync(cancellationToken)).Single(c => c.TableName == "wh_settings");

    var heap = await _scalarAsync<long>("SELECT pg_relation_size('wh_settings')");
    await Assert.That(candidate.SizeBytes).IsEqualTo(heap);
    await Assert.That(heap).IsGreaterThan(0L);
  }

  [Test]
  public async Task ThePrerequisiteQuery_ReadsTheServersFactsAsync() {
    var facts = await _factsAsync("wh_settings");

    await Assert.That(facts.TableExists).IsTrue();
    await Assert.That(facts.SqueezeInstalled).IsFalse().Because("the test image does not ship pg_squeeze");
    await Assert.That(facts.CanExecuteSqueeze).IsFalse();
    await Assert.That(facts.WalLevel).IsEqualTo(await _scalarAsync<string>("SELECT current_setting('wal_level')"));
    await Assert.That(facts.ServerVersionNum).IsEqualTo(await _scalarAsync<int>("SELECT current_setting('server_version_num')::int"));
    await Assert.That(facts.MaxReplicationSlots).IsEqualTo(await _scalarAsync<int>("SELECT current_setting('max_replication_slots')::int"));
    await Assert.That(facts.SlotsInUse).IsEqualTo(await _scalarAsync<int>("SELECT count(*)::int FROM pg_replication_slots"));
    await Assert.That(facts.HasIdentityIndex).IsTrue().Because("wh_settings has a primary key");
    await Assert.That(facts.CanReplicate).IsTrue().Because("the test role is a superuser");
    await Assert.That(facts.OwnsTable).IsTrue();
    await Assert.That(facts.Role).IsEqualTo(await _scalarAsync<string>("SELECT current_user::text"));
  }

  [Test]
  public async Task ThePrerequisiteQuery_SeesATableWithoutAnIdentityIndex_AndAMissingTableAsync() {
    await using (var db = new NpgsqlConnection(ConnectionString)) {
      await db.OpenAsync();
      await using var create = new NpgsqlCommand("CREATE TABLE wh_no_key (v int)", db);
      await create.ExecuteNonQueryAsync();
    }

    var noKey = await _factsAsync("wh_no_key");
    var missing = await _factsAsync("wh_not_there");

    await Assert.That(noKey.HasIdentityIndex).IsFalse();
    await Assert.That(noKey.TableExists).IsTrue();
    await Assert.That(missing.TableExists).IsFalse();
    await Assert.That(missing.OwnsTable).IsFalse();
  }

  [Test]
  [Timeout(120000)]
  public async Task Squeeze_OnAServerWithoutPgSqueeze_IsReportedAndNotAttemptedAsync(CancellationToken cancellationToken) {
    await TableBloat.ManufactureAsync(ConnectionString, cancellationToken);
    var logger = new CapturingLogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>>();
    await using var context = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions(), logger);
    var before = await _scalarAsync<long>("SELECT pg_relation_size('wh_settings')");

    var after = await coordinator.SqueezeTableAsync("wh_settings", cancellationToken);

    await Assert.That(after).IsNull().Because("nothing was rewritten, so the request stays queued");
    await Assert.That(await _scalarAsync<long>("SELECT pg_relation_size('wh_settings')")).IsEqualTo(before);
    await Assert.That(logger.Messages).Contains(
      "Table wh_settings was not rewritten: pg_squeeze is not installed in this database (no squeeze.squeeze_table function)");
  }

  [Test]
  public async Task Squeeze_RefusesATableThatIsNotTheFrameworksAsync() {
    await using var context = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions());

    await Assert.That(async () => await coordinator.SqueezeTableAsync("orders; DROP TABLE wh_settings"))
      .Throws<ArgumentException>();
  }

  /// <summary>
  /// The call itself, with the prerequisite decision replaced: the test image has no logical WAL, so the real check can
  /// never pass here. A stand-in <c>squeeze.squeeze_table</c> with pg_squeeze's signature records what it was called
  /// with and rewrites nothing, so the re-measure reads the ratio the table had. Run through the schema-initialization
  /// connection and through the application's own.
  /// </summary>
  [Test]
  [Timeout(120000)]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Squeeze_WithThePrerequisitesMet_CallsPgSqueezeForTheTable_ThenReMeasuresAsync(
      bool withInitConnection, CancellationToken cancellationToken) {
    await TableBloat.ManufactureAsync(ConnectionString, cancellationToken);
    await _execAsync("""
      CREATE SCHEMA squeeze;
      CREATE TABLE squeeze.calls (tabschema name, tabname name, clustering_index name, rel_tablespace name, ind_tablespaces name[]);
      CREATE FUNCTION squeeze.squeeze_table(
          tabschema name, tabname name, clustering_index name DEFAULT NULL, rel_tablespace name DEFAULT NULL,
          ind_tablespaces name[] DEFAULT NULL)
        RETURNS void LANGUAGE sql
        AS $$ INSERT INTO squeeze.calls VALUES (tabschema, tabname, clustering_index, rel_tablespace, ind_tablespaces) $$;
      """);
    var services = new ServiceCollection();
    if (withInitConnection) {
      services.AddKeyedSingleton<SchemaInitConnection>(typeof(WorkCoordinationDbContext), new SchemaInitConnection(ConnectionString));
    }
    await using var provider = services.BuildServiceProvider();
    await using var context = new WorkCoordinationDbContext(
      new DbContextOptionsBuilder<WorkCoordinationDbContext>()
        .UseNpgsql(ConnectionString)
        .UseApplicationServiceProvider(provider)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
        .Options);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions()) {
      SqueezePrerequisitesUnmet = (_, _) => null,
    };
    var candidate = (await coordinator.GetTablesNeedingRewriteAsync(cancellationToken)).Single(c => c.TableName == "wh_settings");

    var after = await coordinator.SqueezeTableAsync("wh_settings", cancellationToken);

    await Assert.That(await _scalarAsync<string>(
        "SELECT string_agg(tabschema || '.' || tabname || ':' || (clustering_index IS NULL AND rel_tablespace IS NULL AND ind_tablespaces IS NULL)::text, ',') FROM squeeze.calls"))
      .IsEqualTo("public.wh_settings:true")
      .Because("pg_squeeze is called once, for the table in its own schema, with no clustering index or tablespace moves");
    await Assert.That(after).IsEqualTo(candidate.BloatRatio)
      .Because("the stand-in rewrote nothing, and the re-measure after ANALYZE reports the ratio as it is");
  }
}
