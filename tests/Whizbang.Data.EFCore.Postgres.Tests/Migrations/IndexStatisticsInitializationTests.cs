using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Issue #1004: an index the schema pass creates is followed by an <c>ANALYZE</c> of its table, so the
/// planner has statistics for the index's expression at once rather than after autovacuum next analyzes
/// the table, which it does only once about a tenth of the rows have changed.
/// </summary>
/// <remarks>
/// <para>
/// Without statistics for an expression, PostgreSQL estimates a predicate on it with a fixed default.
/// For <c>IS NOT NULL</c> that default says almost every row matches, so a selective predicate over a new
/// index is planned as a scan of the whole table, and the index goes unused.
/// </para>
/// <para>
/// A release that adds an index to a populated table is simulated by dropping the index, loading rows, and
/// removing the perspective pass's hash rows, which is what a changed perspective looks like to the pass.
/// Autovacuum is switched off for the table so nothing but the pass can analyze it.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-indexes#statistics-for-a-new-index</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class IndexStatisticsInitializationTests {
  private const string TABLE = "wh_per_document_index_opted_out";
  private const string INDEX = "idx_document_index_opted_out_status_json";

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("index_stats");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  private async Task _initializeAsync(SignalingListLogger? logger, CancellationToken cancellationToken) {
    await using var context = new DocumentIndexesDbContext(
      new DbContextOptionsBuilder<DocumentIndexesDbContext>()
        .UseNpgsql(_connectionString)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
        .Options);
    await context.EnsureWhizbangDatabaseInitializedAsync(logger, cancellationToken: cancellationToken);
  }

  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<object?> _scalarAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    return await command.ExecuteScalarAsync();
  }

  /// <summary>
  /// A populated table without the status index and never analyzed: 20,000 documents, of which 78 carry a
  /// status, so <c>data -&gt;&gt; 'Status' IS NOT NULL</c> is highly selective.
  /// </summary>
  private async Task _populateWithoutTheIndexAsync() {
    await _execAsync($"""
      ALTER TABLE {TABLE} SET (autovacuum_enabled = false);
      DROP INDEX {INDEX};
      INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version)
      SELECT gen_random_uuid(),
             CASE WHEN g <= 78 THEN jsonb_build_object('Id', gen_random_uuid(), 'Status', 'open-' || g)
                  ELSE jsonb_build_object('Id', gen_random_uuid(), 'Note', repeat('x', 200)) END,
             jsonb_build_object(), jsonb_build_object(), now(), now(), now(), now(), 1
      FROM generate_series(1, 20000) AS g;
      DELETE FROM wh_schema_migrations WHERE owner = 'perspective';
      """);
  }

  private Task<object?> _expressionStatisticsAsync() =>
    _scalarAsync($"SELECT count(*) FROM pg_stats WHERE schemaname = 'public' AND tablename = '{INDEX}'");

  private async Task<string> _planAsync() {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(
      $"EXPLAIN SELECT id FROM {TABLE} WHERE (data ->> 'Status') IS NOT NULL", db);
    var lines = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      lines.Add(reader.GetString(0));
    }
    return string.Join('\n', lines);
  }

  /// <summary>
  /// The pass that creates an expression index analyzes its table, and the planner then uses the index
  /// for a selective predicate on the expression.
  /// </summary>
  [Test]
  [Timeout(180000)]
  public async Task AnIndexThePassCreates_HasStatisticsAndIsChosenForASelectivePredicateAsync(CancellationToken cancellationToken) {
    await _initializeAsync(null, cancellationToken);
    await _populateWithoutTheIndexAsync();

    await _initializeAsync(null, cancellationToken);

    await Assert.That(Convert.ToInt64(await _expressionStatisticsAsync(), System.Globalization.CultureInfo.InvariantCulture))
      .IsGreaterThan(0)
      .Because("the table is analyzed once the index exists, which gathers statistics for its expression");
    await Assert.That(await _planAsync()).Contains(INDEX)
      .Because("with the expression's null fraction known, 78 matching rows of 20,000 are read through the index");
  }

  /// <summary>
  /// A start that creates indexes analyzes their tables once; a start that creates nothing analyzes nothing.
  /// </summary>
  [Test]
  [Timeout(180000)]
  public async Task ASecondStart_AnalyzesNothingAsync(CancellationToken cancellationToken) {
    var first = new SignalingListLogger();
    await _initializeAsync(first, cancellationToken);
    await Assert.That(first.Entries.Count(e => e.Message.StartsWith("Analyzed ", StringComparison.Ordinal) && e.Message.Contains(TABLE, StringComparison.Ordinal)))
      .IsEqualTo(1)
      .Because("the first start creates the table's indexes, so it analyzes the table, once");

    var second = new SignalingListLogger();
    await _initializeAsync(second, cancellationToken);

    await Assert.That(second.Entries.Where(e => e.Message.StartsWith("Analyzed ", StringComparison.Ordinal))).IsEmpty()
      .Because("a start that creates no index has nothing to analyze");
    await Assert.That(Convert.ToInt64(await _scalarAsync("SELECT count(*) FROM wh_index_statistics_pending"), System.Globalization.CultureInfo.InvariantCulture))
      .IsEqualTo(0)
      .Because("each table the pass indexed is analyzed and then forgotten");
  }
}
