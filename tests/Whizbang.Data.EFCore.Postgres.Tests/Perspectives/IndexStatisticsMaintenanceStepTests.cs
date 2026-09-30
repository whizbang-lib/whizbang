using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Dispatch;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.EFCore.Postgres.Tests.Migrations;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Issue #1004: the maintenance step analyzes perspective tables whose expression indexes have no
/// statistics, a bounded number per run and on one instance per claim window.
/// </summary>
/// <remarks>
/// Each table here was analyzed before its index existed and then had the index built directly, the
/// shape seen in practice: the table's last analyze predates the index, so the planner has no statistics
/// for the expression and estimates a predicate on it with a fixed default. Autovacuum is off for the
/// tables so only the step can analyze them.
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-indexes#statistics-for-a-new-index</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class IndexStatisticsMaintenanceStepTests {
  private const string OPTED_OUT = "wh_per_document_index_opted_out";
  private const string UNDECLARED = "wh_per_document_index_undeclared";

  private static readonly DateTimeOffset _now = new(2026, 9, 30, 12, 15, 0, TimeSpan.Zero);

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("idx_stats_step");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
    await using var context = _context();
    await context.EnsureWhizbangDatabaseInitializedAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  private DocumentIndexesDbContext _context() => new(
    new DbContextOptionsBuilder<DocumentIndexesDbContext>()
      .UseNpgsql(_connectionString)
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);

  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<string> _scalarAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    return (await command.ExecuteScalarAsync())?.ToString() ?? "<null>";
  }

  /// <summary>
  /// 20,000 documents of which 78 carry <paramref name="key"/>, analyzed, and only then an index over it.
  /// </summary>
  private async Task<string> _indexedAfterAnalyzeAsync(string table, string key) {
    var index = $"{table}_{key.ToLowerInvariant()}_late";
    await _execAsync($"""
      ALTER TABLE {table} SET (autovacuum_enabled = false);
      INSERT INTO {table} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version)
      SELECT gen_random_uuid(),
             CASE WHEN g <= 78 THEN jsonb_build_object('{key}', 'v-' || g) ELSE jsonb_build_object('Note', repeat('x', 200)) END,
             jsonb_build_object(), jsonb_build_object(), now(), now(), now(), now(), 1
      FROM generate_series(1, 20000) AS g;
      ANALYZE {table};
      CREATE INDEX {index} ON {table} ((data ->> '{key}'));
      """);
    return index;
  }

  private async Task<long> _statisticsAsync(string index) =>
    long.Parse(await _scalarAsync(
      $"SELECT count(*) FROM pg_stats WHERE schemaname = 'public' AND tablename = '{index}'"), CultureInfo.InvariantCulture);

  private async Task<string> _planAsync(string table, string key) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand($"EXPLAIN SELECT id FROM {table} WHERE (data ->> '{key}') IS NOT NULL", db);
    var lines = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      lines.Add(reader.GetString(0));
    }
    return string.Join('\n', lines);
  }

  private async Task _runAsync(IndexStatisticsMaintenanceStep step, bool withClaimStore = true) {
    await using var context = _context();
    var services = new ServiceCollection();
    services.AddSingleton(context);
    if (withClaimStore) {
      services.AddSingleton<IClaimedEmissionStore>(new EFCoreClaimedEmissionStore(context));
    }
    await using var provider = services.BuildServiceProvider();
    await step.RunAsync(provider, CancellationToken.None);
  }

  private static IndexStatisticsMaintenanceStep _step(ILogger<IndexStatisticsMaintenanceStep>? logger = null, int limit = IndexStatisticsMaintenanceStep.DEFAULT_MAX_TABLES_PER_RUN) =>
    new(typeof(DocumentIndexesDbContext), logger, new FakeTimeProvider(_now), limit);

  /// <summary>
  /// A table whose last analyze predates its expression index is analyzed, and the planner then uses the
  /// index for a selective predicate it would otherwise scan the whole table for.
  /// </summary>
  [Test]
  public async Task AnExpressionIndexWithoutStatistics_IsAnalyzedAsync() {
    var index = await _indexedAfterAnalyzeAsync(OPTED_OUT, "Lineage");
    await Assert.That(await _planAsync(OPTED_OUT, "Lineage")).DoesNotContain(index)
      .Because("without statistics for the expression, IS NOT NULL is estimated to match nearly every row");

    await _runAsync(_step());

    await Assert.That(await _statisticsAsync(index)).IsGreaterThan(0);
    await Assert.That(await _planAsync(OPTED_OUT, "Lineage")).Contains(index)
      .Because("with the expression's null fraction known, 78 rows of 20,000 are read through the index");
  }

  /// <summary>
  /// Only the instance that wins the window's claim runs; another run in the same window analyzes nothing,
  /// even when there is something to analyze.
  /// </summary>
  [Test]
  public async Task ASecondRunInTheSameWindow_AnalyzesNothingAsync() {
    await _runAsync(_step());
    var index = await _indexedAfterAnalyzeAsync(OPTED_OUT, "Lineage");

    await _runAsync(_step());

    await Assert.That(await _statisticsAsync(index)).IsEqualTo(0)
      .Because("the window's claim is taken, so this run belongs to an instance that stands down");
  }

  /// <summary>One run analyzes no more tables than its limit.</summary>
  [Test]
  public async Task ARun_AnalyzesNoMoreThanItsLimitAsync() {
    var first = await _indexedAfterAnalyzeAsync(OPTED_OUT, "Lineage");
    var second = await _indexedAfterAnalyzeAsync(UNDECLARED, "Lineage");

    await _runAsync(_step(limit: 1));

    var analyzed = (await _statisticsAsync(first) > 0 ? 1 : 0) + (await _statisticsAsync(second) > 0 ? 1 : 0);
    await Assert.That(analyzed).IsEqualTo(1);
  }

  /// <summary>Without a claim store there is no way to run on one instance, so the step does not run.</summary>
  [Test]
  public async Task WithoutAClaimStore_TheStepDoesNotRunAsync() {
    var index = await _indexedAfterAnalyzeAsync(OPTED_OUT, "Lineage");

    await _runAsync(_step(), withClaimStore: false);

    await Assert.That(await _statisticsAsync(index)).IsEqualTo(0);
  }

  /// <summary>A table that is queued but gone is skipped, and a table whose analyze fails is reported and skipped.</summary>
  [Test]
  public async Task AGoneOrLockedTable_IsSkippedAndTheRestAnalyzedAsync() {
    var index = await _indexedAfterAnalyzeAsync(UNDECLARED, "Lineage");
    await using var holder = new NpgsqlConnection(_connectionString);
    await holder.OpenAsync();
    await using var hold = await holder.BeginTransactionAsync();
    await using (var lockTable = new NpgsqlCommand($"LOCK TABLE {OPTED_OUT} IN ACCESS EXCLUSIVE MODE", holder, hold)) {
      await lockTable.ExecuteNonQueryAsync();
    }
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await using (var timeout = new NpgsqlCommand("SET lock_timeout = '200ms'", connection)) {
      await timeout.ExecuteNonQueryAsync();
    }
    var log = new SignalingListLogger();

    var analyzed = await IndexStatistics.AnalyzeAsync(
      connection, ["public.wh_per_gone", $"public.{OPTED_OUT}", $"public.{UNDECLARED}"], log, CancellationToken.None);

    await Assert.That(analyzed).IsEquivalentTo([$"public.{UNDECLARED}"]);
    await Assert.That(await _statisticsAsync(index)).IsGreaterThan(0);
    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains(OPTED_OUT, StringComparison.Ordinal)))
      .IsTrue()
      .Because("a table that could not be analyzed is named, and left for the next run");
  }

  /// <summary>The Postgres driver registers the step, so every host with the driver runs it.</summary>
  [Test]
  public async Task ThePostgresDriver_RegistersTheStepAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().ToList();

    await Assert.That(steps.OfType<IndexStatisticsMaintenanceStep>().Count()).IsEqualTo(1);
    await Assert.That(steps.OfType<IndexStatisticsMaintenanceStep>().Single().Name).IsEqualTo("index-statistics");
  }
}
