using System.Collections.Immutable;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Shared.Models;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// That the schema a perspective declares actually builds against a real database.
/// </summary>
/// <remarks>
/// <para>
/// The generator tests assert which statements are emitted. They cannot assert that a statement is
/// valid, because a string containing the word CREATE looks exactly like a working one. Every index
/// this surface can ask for is therefore executed here and then read back out of the catalog, which
/// is the only way to know an index exists and is the one that was meant.
/// </para>
/// <para>
/// The ordering case is the reason this file exists rather than being folded into the usage tests.
/// A perspective's schema pass rewrites rows into the canonical temporal form and then builds the
/// index over the result, and that order is load-bearing rather than tidy: PostgreSQL evaluates an
/// index expression for every row, so a half-converted column refuses the index outright. Both
/// directions are asserted, because a test that only proves the working order does not show that the
/// other one fails.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class PerspectiveIndexSetupTests : IAsyncDisposable {
  private const string TABLE = "wh_per_index_setup";

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _databaseName = $"index_setup_{Guid.NewGuid():N}";
    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await using var create = new NpgsqlCommand($"CREATE DATABASE {_databaseName}", admin);
      await create.ExecuteNonQueryAsync();
    }

    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
    }.ConnectionString;

    await _executeAsync($"""
      CREATE TABLE {TABLE} (
        id uuid PRIMARY KEY,
        data jsonb NOT NULL,
        tenant_id uuid,
        embedding real[]
      );
      """);
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    if (_databaseName is not null) {
      try {
        await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand(
          $"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
      } catch (NpgsqlException) {
        // The container outlives the run; a leftover database costs nothing.
      }
    }

    GC.SuppressFinalize(this);
  }

  private async Task _executeAsync(string sql) {
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

  /// <summary>Runs exactly the statements the generator emits for these declarations.</summary>
  /// <remarks>
  /// Takes more than one because a field compared both ways carries a declaration each, and what
  /// matters there is that running both leaves both indexes rather than one.
  /// </remarks>
  private async Task _createDeclaredAsync(params JsonIndexInfo[] indexes) {
    foreach (var index in indexes) {
      foreach (var statement in JsonIndexSql.CreateStatements(index, TABLE, "setup")) {
        await _executeAsync(statement);
      }
    }
  }

  /// <summary>Every index on the table, as PostgreSQL describes it back.</summary>
  private async Task<string> _definitionsAsync() =>
    await _scalarAsync(
      "SELECT coalesce(string_agg(indexdef, ' | ' ORDER BY indexname), '') "
      + $"FROM pg_indexes WHERE tablename = '{TABLE}'");

  /// <summary>
  /// Every cast this surface can declare builds an index, and builds the one that was asked for.
  /// </summary>
  /// <remarks>
  /// The cast is the whole point of the declaration: an index over a different expression than the
  /// query produces is a different index, which the planner ignores while every row is still read.
  /// So the definition is read back rather than the statement merely being run without error.
  /// </remarks>
  [Test]
  [Arguments(JsonIndexCast.None, "Text", "->> 'Text'")]
  [Arguments(JsonIndexCast.Int2, "Small", "::smallint")]
  [Arguments(JsonIndexCast.Int4, "Count", "::integer")]
  [Arguments(JsonIndexCast.Int8, "Big", "::bigint")]
  [Arguments(JsonIndexCast.Numeric, "Money", "::numeric")]
  [Arguments(JsonIndexCast.Float4, "Single", "::real")]
  [Arguments(JsonIndexCast.Float8, "Double", "::double precision")]
  [Arguments(JsonIndexCast.Bool, "Flag", "::boolean")]
  [Arguments(JsonIndexCast.Uuid, "Owner", "::uuid")]
  public async Task ADeclaredIndexBuildsWithTheCastItAskedForAsync(
    JsonIndexCast cast, string key, string expected) {
    await _createDeclaredAsync(new JsonIndexInfo(key, key, cast, Ordered: true, Substring: false, CaseInsensitive: false));

    var definitions = await _definitionsAsync();

    await Assert.That(definitions).Contains(expected, StringComparison.Ordinal)
      .Because($"a query extracting '{key}' produces this cast, and an index over any other "
        + "expression is a different index the planner will not use");
  }

  /// <summary>
  /// The date family builds too, which is the property the canonical stored form was bought for.
  /// </summary>
  /// <remarks>
  /// These are the casts PostgreSQL refused while a date was stored as a rendering, because the cast
  /// from text to a timestamp is stable and an index expression has to be immutable. Asserted here
  /// against a real database rather than inferred from the cast table.
  /// </remarks>
  [Test]
  [Arguments("OccurredAt", JsonIndexCast.Int8)]
  [Arguments("Day", JsonIndexCast.Int4)]
  [Arguments("Clock", JsonIndexCast.Int8)]
  [Arguments("Elapsed", JsonIndexCast.Int8)]
  public async Task ATemporalFieldBuildsItsIndexAsync(string key, JsonIndexCast cast) {
    await _createDeclaredAsync(new JsonIndexInfo(key, key, cast, Ordered: true, Substring: false, CaseInsensitive: false));

    await Assert.That(await _definitionsAsync()).Contains($"'{key}'", StringComparison.Ordinal)
      .Because("stored as a number the extraction casts through an immutable expression, which is "
        + "what PostgreSQL refused when the same value was stored as a rendering");
  }

  /// <summary>
  /// A trigram declaration builds a different kind of index, and creates the extension it needs.
  /// </summary>
  [Test]
  public async Task ATrigramDeclarationBuildsAGinIndexAsync() {
    await _createDeclaredAsync(
      new JsonIndexInfo("Title", "Title", JsonIndexCast.None, Ordered: false, Substring: true, CaseInsensitive: false));

    var definitions = await _definitionsAsync();

    await Assert.That(definitions).Contains("USING gin", StringComparison.Ordinal);
    await Assert.That(definitions).Contains("gin_trgm_ops", StringComparison.Ordinal)
      .Because("substring matching is answered by the trigram operator class and by nothing else");

    var extension = await _scalarAsync("SELECT count(*) FROM pg_extension WHERE extname = 'pg_trgm'");
    await Assert.That(extension).IsEqualTo("1")
      .Because("the operator class lives in the extension, so a declaration that did not create it "
        + "would fail at a customer's startup rather than here");
  }

  /// <summary>
  /// Asking for both kinds builds both, which is the case the flag enumeration exists for.
  /// </summary>
  [Test]
  public async Task BothKindsBuildBothIndexesAsync() {
    await _createDeclaredAsync(
      new JsonIndexInfo("Title", "Title", JsonIndexCast.None, Ordered: true, Substring: true, CaseInsensitive: false));

    var count = await _scalarAsync(
      $"SELECT count(*) FROM pg_indexes WHERE tablename = '{TABLE}' AND indexname LIKE 'idx_setup%'");

    await Assert.That(count).IsEqualTo("2")
      .Because("a field filtered by range and by substring wants one index for each, and neither "
        + "answers the other's query");
  }

  /// <summary>
  /// Running the schema pass again changes nothing, which is what lets it run at every startup.
  /// </summary>
  /// <remarks>
  /// The schema is created on the way up rather than by a separate migration step, so it runs on
  /// every boot of every instance. If a second run failed, or duplicated an index, the second pod to
  /// start would be the one that found out.
  /// </remarks>
  [Test]
  public async Task RunningTheSchemaPassTwiceChangesNothingAsync() {
    var index = new JsonIndexInfo("Count", "Count", JsonIndexCast.Int4, Ordered: true, Substring: false, CaseInsensitive: false);

    await _createDeclaredAsync(index);
    var first = await _definitionsAsync();

    await _createDeclaredAsync(index);
    var second = await _definitionsAsync();

    await Assert.That(second).IsEqualTo(first);
  }

  /// <summary>
  /// The rewrite runs before the index, and the other order fails rather than misleading.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is the assertion that makes the emitted ordering more than a convention. PostgreSQL
  /// evaluates an index expression for every row, so a column still holding a rendering on any row
  /// refuses a numeric index outright. Converting first and indexing second works; indexing first
  /// does not.
  /// </para>
  /// <para>
  /// Failing is the good outcome here. A backfill that did not finish stops the schema pass at the
  /// next statement instead of leaving an index built over a column about to change underneath it,
  /// which nothing would have surfaced.
  /// </para>
  /// </remarks>
  [Test]
  public async Task TheRewriteHasToComeBeforeTheIndexAsync() {
    // One row in each form, which is the state a half-finished rewrite leaves behind.
    await _executeAsync(
      $"INSERT INTO {TABLE} (id, data) VALUES "
      + "(gen_random_uuid(), '{\"OccurredAt\": \"2026-03-04T05:06:07Z\"}'::jsonb), "
      + "(gen_random_uuid(), '{\"OccurredAt\": 1772600767000000}'::jsonb);");

    var properties = new[] {
      new CanonicalTemporalProperty("OccurredAt", CanonicalTemporalKind.Instant, false),
    }.ToImmutableArray();

    var index = new JsonIndexInfo(
      "OccurredAt", "OccurredAt", JsonIndexCast.Int8, Ordered: true, Substring: false, CaseInsensitive: false);

    // Indexing a column that still holds a rendering on any row is refused, which is the reason the
    // generator emits the rewrite first.
    await Assert.That(async () => await _createDeclaredAsync(index)).Throws<PostgresException>()
      .Because("the index expression is evaluated for every row, so one unconverted row is enough "
        + "to refuse it, and that refusal is what a half-finished backfill has to run into");

    foreach (var statement in CanonicalTemporalBackfillSql.Statements(properties, TABLE)) {
      await _executeAsync(statement);
    }

    await Assert.That(async () => await _createDeclaredAsync(index)).ThrowsNothing()
      .Because("every row is a number once the rewrite has run, which is the state the index needs "
        + "and the reason the two are emitted in this order");
  }

  /// <summary>
  /// A promoted column takes an ordinary index, which is what asking for one on a promoted field
  /// means.
  /// </summary>
  /// <remarks>
  /// The same attribute asks on either side of the promotion, so this is the other half of what
  /// <c>[Indexed]</c> builds. Nothing here goes through the document at all.
  /// </remarks>
  [Test]
  public async Task APromotedColumnTakesAnOrdinaryIndexAsync() {
    await _executeAsync($"CREATE INDEX idx_setup_tenant_id ON {TABLE} (tenant_id);");

    var definitions = await _definitionsAsync();

    await Assert.That(definitions).Contains("(tenant_id)", StringComparison.Ordinal);
    await Assert.That(definitions).DoesNotContain("data ->> 'TenantId'", StringComparison.Ordinal)
      .Because("a promoted field is not in the document, so an index over an extraction of it would "
        + "be built over a key that is never present");
  }

  /// <summary>
  /// A declaration is what creates an index, so a table nobody declared anything for has only its
  /// key.
  /// </summary>
  /// <remarks>
  /// The baseline the rest of this file is measured against, and the one that pins the opt-in: a
  /// field gains an index because someone asked, never because the framework guessed.
  /// </remarks>
  [Test]
  public async Task NothingIsIndexedWithoutADeclarationAsync() {
    var count = await _scalarAsync(
      $"SELECT count(*) FROM pg_indexes WHERE tablename = '{TABLE}'");

    await Assert.That(count).IsEqualTo("1")
      .Because("only the primary key, because every index is write amplification and this surface "
        + "is opt-in");
  }

  /// <summary>
  /// A case-insensitive declaration builds over the folded value, which is the only expression a
  /// folded comparison can use.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The reason this needs a real database rather than a string assertion: an index over
  /// <c>(data -&gt;&gt; 'X')</c> and one over <c>lower((data -&gt;&gt; 'X'))</c> are both valid DDL and
  /// both build without complaint. The difference only shows in what the planner will use them for,
  /// so the catalog is read back and the folded expression is what is asserted.
  /// </para>
  /// <para>
  /// Both kinds fold, because the fold decides the expression and not the structure.
  /// </para>
  /// </remarks>
  [Test]
  [Arguments(true, false, "a folded ordered index")]
  [Arguments(false, true, "a folded substring index")]
  public async Task ACaseInsensitiveDeclarationBuildsOverTheFoldedValueAsync(
      bool ordered, bool substring, string what) {
    await _createDeclaredAsync(new JsonIndexInfo(
      "Label", "Label", JsonIndexCast.None, Ordered: ordered, Substring: substring,
      CaseInsensitive: true));

    var definitions = await _definitionsAsync();

    await Assert.That(definitions).Contains("lower(", StringComparison.Ordinal)
      .Because($"{what} is only a candidate for a predicate over lower(...), so the index has to be "
        + "built over that expression rather than over the bare extraction");
    await Assert.That(definitions).Contains("_ci_", StringComparison.Ordinal)
      .Because("the folded index needs a name of its own, or a field compared both ways would have "
        + "its second CREATE INDEX IF NOT EXISTS quietly do nothing");
  }

  /// <summary>
  /// A field compared both ways carries both indexes, and neither name collides with the other.
  /// </summary>
  /// <remarks>
  /// This is the case the naming exists for. Both declarations are idempotent statements over the
  /// same table and key, so a shared name would mean the second one silently did nothing and the
  /// query it was declared for kept scanning.
  /// </remarks>
  [Test]
  public async Task AFieldComparedBothWaysGetsBothIndexesAsync() {
    await _createDeclaredAsync(
      new JsonIndexInfo("Label", "Label", JsonIndexCast.None, Ordered: true, Substring: false,
        CaseInsensitive: false),
      new JsonIndexInfo("Label", "Label", JsonIndexCast.None, Ordered: true, Substring: false,
        CaseInsensitive: true));

    var definitions = await _definitionsAsync();

    await Assert.That(definitions).Contains("lower(", StringComparison.Ordinal);
    await Assert.That(definitions.Split("CREATE INDEX").Length - 1).IsGreaterThanOrEqualTo(2)
      .Because("the folded and unfolded comparisons need one index each, and the planner uses "
        + "neither for the other");
  }

}
