using System.Collections.Immutable;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Generators.Shared.Models;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// How a script is divided, for the cases that need no database to decide.
/// </summary>
/// <remarks>
/// Separate from the integration tests below because these are decisions about text. Standing up a
/// database to assert that a script with no marker yields itself would make the cheap half of this
/// type's behavior cost as much as the expensive half.
/// </remarks>
/// <docs>operations/infrastructure/migrations#statements-that-need-a-commit-between-them</docs>
[Category("Shard1")]
public class SchemaCommandBoundarySegmentTests {

  /// <summary>A script with no marker yields itself, so a caller applies both cases alike.</summary>
  [Test]
  public async Task AScriptWithNoMarkerIsOneSegmentAsync() {
    var segments = SchemaCommandBoundary.Segments("SELECT 1;");

    await Assert.That(segments.Length).IsEqualTo(1);
    await Assert.That(segments[0]).IsEqualTo("SELECT 1;");
  }

  /// <summary>Nothing to apply yields nothing, rather than one empty command.</summary>
  [Test]
  [Arguments("")]
  [Arguments("   \n  ")]
  public async Task AnEmptyScriptYieldsNoSegmentAsync(string sql) =>
    await Assert.That(SchemaCommandBoundary.Segments(sql).Length).IsEqualTo(0);

  /// <summary>
  /// A marker is recognized whatever it is indented by, and contributes no segment of its own.
  /// </summary>
  /// <remarks>
  /// The generator writes the schema for a perspective entry indented, so a comparison that did not
  /// trim would match in the bulk script and silently not match in the per-perspective one, which is
  /// the path that actually runs.
  /// </remarks>
  [Test]
  public async Task AnIndentedMarkerStillSeparatesAsync() {
    var segments = SchemaCommandBoundary.Segments(
      $"SELECT 1;\n      {SchemaCommandBoundary.MARKER}   \nSELECT 2;\nSELECT 3;");

    await Assert.That(segments.Length).IsEqualTo(2);
    await Assert.That(segments[0].Trim()).IsEqualTo("SELECT 1;");
    await Assert.That(segments[1]).Contains("SELECT 3;", StringComparison.Ordinal);
    await Assert.That(segments[1]).DoesNotContain("@whizbang", StringComparison.Ordinal);
  }

  /// <summary>
  /// A marker with nothing after it yields no trailing empty segment.
  /// </summary>
  /// <remarks>
  /// An empty command is not merely wasteful: it is a round trip that opens a connection to send
  /// nothing, and Npgsql rejects it.
  /// </remarks>
  [Test]
  public async Task ATrailingMarkerAddsNoSegmentAsync() =>
    await Assert.That(
      SchemaCommandBoundary.Segments($"SELECT 1;\n{SchemaCommandBoundary.MARKER}\n").Length)
      .IsEqualTo(1);

  /// <summary>Several markers divide into several pieces.</summary>
  [Test]
  public async Task SeveralMarkersDivideIntoSeveralPiecesAsync() {
    var m = SchemaCommandBoundary.MARKER;

    await Assert.That(
      SchemaCommandBoundary.Segments($"SELECT 1;\n{m}\nSELECT 2;\n{m}\nSELECT 3;").Length)
      .IsEqualTo(3);
  }

  /// <summary>A missing script is a caller error rather than a script with nothing in it.</summary>
  [Test]
  public async Task ANullScriptIsRefusedAsync() =>
    await Assert.That(() => SchemaCommandBoundary.Segments(null!))
      .Throws<ArgumentNullException>();

  /// <summary>
  /// Applying without somewhere to apply it is a caller error, said at the call rather than as a
  /// connection failure later.
  /// </summary>
  [Test]
  [Arguments("")]
  [Arguments("   ")]
  public async Task ApplyingWithNoConnectionStringIsRefusedAsync(string connectionString) =>
    await Assert.That(async () =>
        await SchemaCommandBoundary.ApplyAsync(connectionString, "SELECT 1;", 30))
      .Throws<ArgumentException>();

  /// <summary>A missing script is refused by the apply as well.</summary>
  [Test]
  public async Task ApplyingANullScriptIsRefusedAsync() =>
    await Assert.That(async () =>
        await SchemaCommandBoundary.ApplyAsync("Host=nowhere", null!, 30))
      .Throws<ArgumentNullException>();
}

/// <summary>
/// That a rewrite of a stored value and an index built over the result are applied with a commit
/// between them, and what goes wrong when they are not.
/// </summary>
/// <remarks>
/// <para>
/// A perspective holding a date written by an earlier release carries a rendering where the mapping
/// now writes a number, so its schema pass rewrites the column and then indexes it. Both statements
/// are correct and the generator emits them in that order. Applied in one transaction they still
/// fail, because an index over an expression is built by evaluating that expression on every heap
/// tuple that is not yet dead, and the row version an uncommitted rewrite superseded is still live.
/// </para>
/// <para>
/// That failure is self-perpetuating rather than merely noisy, which is why it is pinned here: the
/// rollback undoes the rewrite as well as the index, so every later attempt starts from the state
/// that failed and fails identically. The first test is the characterization that justifies the
/// boundary existing; the second is the behavior that has to hold.
/// </para>
/// <para>
/// Both build their SQL from the generator's own emitters rather than retyping it, so a change to
/// either the rewrite or the index expression cannot pass by being mirrored wrongly here.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#statements-that-need-a-commit-between-them</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class SchemaCommandBoundaryTests : IAsyncDisposable {
  private const string TABLE = "wh_per_boundary";
  private const string INDEX = "idx_boundary_occurredat_json";
  private const string OLD_RENDERING = "2026-04-21T22:38:17.357886+00:00";
  private const int SEEDED_ROWS = 200;

  /// <summary>The one property, in the shape that carries the dependency.</summary>
  private static readonly ImmutableArray<CanonicalTemporalProperty> _properties = [
    new("OccurredAt", CanonicalTemporalKind.Instant, false),
  ];

  /// <summary>The declaration that puts an index over the rewritten key.</summary>
  private static readonly JsonIndexInfo _index =
    new("OccurredAt", "OccurredAt", JsonIndexCast.Int8, true, false, false);

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _databaseName = $"boundary_{Guid.NewGuid():N}";
    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await using var create = new NpgsqlCommand($"CREATE DATABASE {_databaseName}", admin);
      await create.ExecuteNonQueryAsync();
    }

    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
    }.ConnectionString;

    // fillfactor leaves no free space in a page, so a rewrite cannot place the new row version
    // beside the old one. That matters: an update that fits in its own page is done in place, the
    // superseded version never becomes separately visible, and an index built in the same
    // transaction then sees only the rewritten value. A perspective row carries a whole document
    // and pages fill, so in-place is the case that does not occur in practice, and a test seeded
    // with one narrow row would pass while the real table failed.
    await _executeAsync($"""
      CREATE TABLE {TABLE} (
        id uuid PRIMARY KEY,
        data jsonb NOT NULL
      ) WITH (fillfactor = 100);
      """);

    // Rows in the shape an earlier release left behind, which is what both the rewrite and the
    // index have to cope with. Padded to a realistic document size so several fill a page.
    await _executeAsync($"""
      INSERT INTO {TABLE} (id, data)
      SELECT gen_random_uuid(),
             jsonb_build_object('OccurredAt', '{OLD_RENDERING}', 'Padding', repeat('x', 1000))
      FROM generate_series(1, {SEEDED_ROWS});
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
        // The container is torn down with the run; a database left behind costs nothing.
      }
    }

    GC.SuppressFinalize(this);
  }

  /// <summary>
  /// The script the generator emits for a perspective whose date is indexed: the rewrite, the
  /// boundary, then the index over the result.
  /// </summary>
  private static string _perspectiveSchema() {
    var script = new System.Text.StringBuilder();

    foreach (var statement in CanonicalTemporalBackfillSql.Statements(_properties, TABLE)) {
      script.AppendLine(statement);
    }

    script.AppendLine(SchemaCommandBoundary.MARKER);

    foreach (var statement in JsonIndexSql.CreateStatements(_index, TABLE, "boundary")) {
      script.AppendLine(statement);
    }

    return script.ToString();
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
    var value = await command.ExecuteScalarAsync();
    return value?.ToString() ?? "<null>";
  }

  /// <summary>The one stored type across every row, or the count of distinct types if they differ.</summary>
  private async Task<string> _storedTypeAsync() =>
    await _scalarAsync(
      $"SELECT string_agg(DISTINCT jsonb_typeof(data -> 'OccurredAt'), ',') FROM {TABLE}");

  private async Task<string> _indexCountAsync() =>
    await _scalarAsync($"SELECT count(*) FROM pg_indexes WHERE indexname = '{INDEX}'");

  /// <summary>
  /// Applied in one transaction, the index refuses to build and the rewrite is undone with it.
  /// </summary>
  /// <remarks>
  /// The characterization the boundary exists for. Both halves are asserted, because the undone
  /// rewrite is what makes the failure permanent: an attempt that left the rewrite behind would
  /// succeed on the next start, and this one cannot.
  /// </remarks>
  [Test]
  public async Task OneTransactionCannotBuildAnIndexOverAValueItJustRewroteAsync() {
    var script = _perspectiveSchema();

    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var transaction = await db.BeginTransactionAsync();

    var failure = await Assert.ThrowsAsync<PostgresException>(async () => {
      foreach (var segment in SchemaCommandBoundary.Segments(script)) {
        await using var command = new NpgsqlCommand(segment, db, transaction);
        await command.ExecuteNonQueryAsync();
      }
    });

    // ThrowsAsync fails the test when nothing is thrown, so there is an exception to read here.
    // 22P02 is invalid_text_representation: the cast in the index expression met the rendering the
    // rewrite had already replaced, on the row version the rewrite superseded.
    await Assert.That(failure!.SqlState).IsEqualTo("22P02");
    await Assert.That(failure.MessageText).Contains(OLD_RENDERING);

    await transaction.RollbackAsync();

    await Assert.That(await _storedTypeAsync()).IsEqualTo("string");
    await Assert.That(await _indexCountAsync()).IsEqualTo("0");
  }

  /// <summary>
  /// Applied through the boundary, the rewrite commits and the index is built over its result.
  /// </summary>
  [Test]
  public async Task ApplyingAcrossTheBoundaryRewritesAndThenIndexesAsync() {
    await SchemaCommandBoundary.ApplyAsync(_connectionString, _perspectiveSchema(), 600);

    await Assert.That(await _storedTypeAsync()).IsEqualTo("number");
    await Assert.That(await _indexCountAsync()).IsEqualTo("1");
  }

  /// <summary>
  /// The same script without its boundary fails, so the marker is load-bearing rather than a note.
  /// </summary>
  /// <remarks>
  /// Without this the boundary could be dropped from the generator, every other test here would
  /// still pass, and the failure would come back on the next upgrade of a database that has rows.
  /// A whole script with no marker is one command, which PostgreSQL runs in one transaction, so it
  /// fails the same way an explicit transaction does.
  /// </remarks>
  [Test]
  public async Task WithoutTheBoundaryTheSameScriptStillFailsAsync() {
    var unseparated = _perspectiveSchema()
      .Replace(SchemaCommandBoundary.MARKER, string.Empty, StringComparison.Ordinal);

    var failure = await Assert.ThrowsAsync<PostgresException>(
      async () => await SchemaCommandBoundary.ApplyAsync(_connectionString, unseparated, 600));

    await Assert.That(failure!.SqlState).IsEqualTo("22P02");
    await Assert.That(await _storedTypeAsync()).IsEqualTo("string");
    await Assert.That(await _indexCountAsync()).IsEqualTo("0");
  }

  /// <summary>
  /// A second apply changes nothing, so the schema pass can run it on every start.
  /// </summary>
  [Test]
  public async Task ApplyingTwiceIsANoOpTheSecondTimeAsync() {
    var script = _perspectiveSchema();

    await SchemaCommandBoundary.ApplyAsync(_connectionString, script, 600);
    var afterFirst = await _scalarAsync(
      $"SELECT string_agg(DISTINCT data ->> 'OccurredAt', ',') FROM {TABLE}");

    await SchemaCommandBoundary.ApplyAsync(_connectionString, script, 600);

    await Assert.That(await _scalarAsync(
      $"SELECT string_agg(DISTINCT data ->> 'OccurredAt', ',') FROM {TABLE}")).IsEqualTo(afterFirst);
    await Assert.That(await _indexCountAsync()).IsEqualTo("1");
  }

  /// <summary>
  /// The marker the generator writes is the marker the runtime reads.
  /// </summary>
  /// <remarks>
  /// The two are separate declarations because the generator's copy ships only to generators, so
  /// nothing but this stops them drifting. Drifting would be silent: the runtime would apply the
  /// script whole, which is the behavior the boundary exists to prevent.
  /// </remarks>
  [Test]
  public async Task TheGeneratorAndTheRuntimeAgreeOnTheMarkerAsync() {
    // Asserted through the behavior rather than by comparing the two constants, so what is pinned
    // is the thing that matters: a script written with the generator's marker gets split.
    var script = $"SELECT 1;\n{CanonicalTemporalBackfillSql.COMMIT_BOUNDARY}\nSELECT 2;";

    await Assert.That(SchemaCommandBoundary.Segments(script).Length).IsEqualTo(2);
  }

  /// <summary>
  /// A script carrying no boundary is applied whole, so nothing else changes shape.
  /// </summary>
  [Test]
  public async Task AScriptWithoutABoundaryIsAppliedWholeAsync() {
    await SchemaCommandBoundary.ApplyAsync(
      _connectionString, $"CREATE TABLE wh_per_plain (id uuid PRIMARY KEY);", 600);

    await Assert.That(await _scalarAsync(
      "SELECT count(*) FROM pg_tables WHERE tablename = 'wh_per_plain'")).IsEqualTo("1");
  }
}
