using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Issue #1009: promoting an indexed document field to a physical column moves its indexes to the column.
/// </summary>
/// <remarks>
/// Each test first puts the table in the shape an earlier release left it: the fields only in the
/// document, with the indexes the schema built over their extractions, under the names it gave them. The
/// perspective's schema hash is then forgotten so the next start applies the promoting DDL, which is what
/// a release that promotes the field does.
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#promoting-an-existing-field</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class PhysicalFieldPromotionTests {
  internal const string TABLE = "wh_per_promoted_item";
  private const string OPERATOR_INDEX = "operator_name_lookup";
  private static readonly string[] _promotedKeys = ["Name", "Code", "Rank"];

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("phys_promote");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
    await InitializeAsync(_connectionString);
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  internal static PhysicalPromotionDbContext Context(string connectionString) => new(
    new DbContextOptionsBuilder<PhysicalPromotionDbContext>()
      .UseNpgsql(connectionString)
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);

  internal static async Task InitializeAsync(string connectionString) {
    await using var context = Context(connectionString);
    await context.EnsureWhizbangDatabaseInitializedAsync();
  }

  internal static async Task ExecAsync(string connectionString, string sql) {
    await using var db = new NpgsqlConnection(connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  internal static async Task<string> ScalarAsync(string connectionString, string sql) {
    await using var db = new NpgsqlConnection(connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    return (await command.ExecuteScalarAsync())?.ToString() ?? "<null>";
  }

  /// <summary>
  /// The table as the release before the promotion left it: 5,000 rows with the fields only in the
  /// document, the schema's indexes over their extractions, and an operator's index over one of them.
  /// Then the promoting start.
  /// </summary>
  internal static async Task PromoteAsync(string connectionString) {
    await ExecAsync(connectionString, $"""
      ALTER TABLE {TABLE} DROP COLUMN name, DROP COLUMN code, DROP COLUMN rank;
      INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version)
      SELECT gen_random_uuid(), jsonb_build_object('Name', 'item-' || g, 'Code', 'CODE-' || g, 'Rank', g),
             jsonb_build_object(), jsonb_build_object(), now(), now(), now(), now(), 1
      FROM generate_series(1, 5000) AS g;
      CREATE INDEX idx_promoted_item_name_json ON {TABLE} ((data ->> 'Name'));
      CREATE INDEX idx_promoted_item_name_fold_trgm ON {TABLE} USING gin (public.wh_fold(data ->> 'Name') gin_trgm_ops);
      CREATE INDEX idx_promoted_item_code_ci_trgm ON {TABLE} USING gin ((lower(data ->> 'Code')) gin_trgm_ops);
      CREATE INDEX idx_promoted_item_rank_json ON {TABLE} (((data ->> 'Rank')::integer));
      CREATE INDEX {OPERATOR_INDEX} ON {TABLE} ((data ->> 'Name'));
      DELETE FROM wh_schema_migrations WHERE file_name = 'perspective:{TABLE}';
      """);
    await InitializeAsync(connectionString);
  }

  private async Task<Dictionary<string, string>> _indexesAsync() {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(
      $"SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = '{TABLE}'", db);
    var indexes = new Dictionary<string, string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      indexes[reader.GetString(0)] = reader.GetString(1);
    }
    return indexes;
  }

  private async Task<string> _planAsync(string predicate) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand($"EXPLAIN SELECT id FROM {TABLE} WHERE {predicate}", db);
    var lines = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      lines.Add(reader.GetString(0));
    }
    return string.Join('\n', lines);
  }

  /// <summary>
  /// Each index the schema built over a promoted field's extraction is gone, the column carries an index
  /// of the same kind, and a filter on the column is answered through it.
  /// </summary>
  [Test]
  public async Task APromotedField_LosesItsDocumentIndexesAndGainsColumnIndexesAsync() {
    await PromoteAsync(_connectionString);

    var indexes = await _indexesAsync();

    await Assert.That(indexes.ContainsKey("idx_promoted_item_name_json")).IsFalse();
    await Assert.That(indexes.ContainsKey("idx_promoted_item_rank_json")).IsFalse();
    await Assert.That(indexes["idx_promoted_item_name"]).Contains("btree (name)");
    await Assert.That(indexes["idx_promoted_item_rank"]).Contains("btree (rank)");
    await Assert.That(indexes["idx_promoted_item_name_fold_trgm"]).Contains("wh_fold(name) gin_trgm_ops")
      .Because("the search index took the same name as the document one, which was dropped first");
    await Assert.That(indexes["idx_promoted_item_code_ci_trgm"]).Contains("lower(code) gin_trgm_ops")
      .Because("a case-folded substring index over the document becomes one over the column");
    var overTheDocument = indexes
      .Where(i => i.Key != OPERATOR_INDEX)
      .Where(i => _promotedKeys.Any(key => i.Value.Contains($"(data ->> '{key}'::text)", StringComparison.Ordinal)))
      .Select(i => i.Key)
      .ToList();
    await Assert.That(overTheDocument).IsEmpty()
      .Because("no index the schema built over a promoted field's extraction remains");

    await Assert.That(await _planAsync("name = 'item-42'")).Contains("idx_promoted_item_name");
    await Assert.That(await _planAsync("rank = 42")).Contains("idx_promoted_item_rank");
    await Assert.That(await ScalarAsync(_connectionString, $"SELECT count(*) FROM {TABLE} WHERE name = 'item-42'"))
      .IsEqualTo("1").Because("the promoting start filled the column from the document");
  }

  /// <summary>An index an operator built over the same extraction, under a name of their own, is kept.</summary>
  [Test]
  public async Task AnIndexTheSchemaDidNotBuild_IsKeptAsync() {
    await PromoteAsync(_connectionString);

    var indexes = await _indexesAsync();

    await Assert.That(indexes.ContainsKey(OPERATOR_INDEX)).IsTrue();
    await Assert.That(indexes[OPERATOR_INDEX]).Contains("(data ->> 'Name'::text)");
  }

  /// <summary>
  /// Applying the promoting DDL again changes nothing: the indexes are the same, and the column indexes
  /// that share a document index's name are not dropped for it.
  /// </summary>
  [Test]
  public async Task ASecondPass_ChangesNothingAsync() {
    await PromoteAsync(_connectionString);
    var first = await _indexesAsync();
    var armed = await ScalarAsync(_connectionString,
      "SELECT string_agg(column_name || '@' || armed_at::text, ',' ORDER BY column_name) FROM wh_physical_column_fills");

    await ExecAsync(_connectionString, $"DELETE FROM wh_schema_migrations WHERE file_name = 'perspective:{TABLE}'");
    await InitializeAsync(_connectionString);

    var second = await _indexesAsync();
    await Assert.That(second).IsEquivalentTo(first);
    await Assert.That(await ScalarAsync(_connectionString,
      "SELECT string_agg(column_name || '@' || armed_at::text, ',' ORDER BY column_name) FROM wh_physical_column_fills"))
      .IsEqualTo(armed).Because("the columns already exist, so the pass does not arm them again");
  }

  /// <summary>A table created with its fields already promoted has the same indexes and arms nothing.</summary>
  [Test]
  public async Task ANewTable_HasTheSameColumnIndexesAndArmsNothingAsync() {
    var indexes = await _indexesAsync();

    await Assert.That(indexes["idx_promoted_item_name"]).Contains("btree (name)");
    await Assert.That(indexes["idx_promoted_item_name_fold_trgm"]).Contains("wh_fold(name) gin_trgm_ops");
    await Assert.That(indexes["idx_promoted_item_code_ci_trgm"]).Contains("lower(code) gin_trgm_ops");
    await Assert.That(await ScalarAsync(_connectionString, "SELECT count(*) FROM wh_physical_column_fills")).IsEqualTo("0");
  }
}
