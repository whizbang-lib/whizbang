using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Generators.Shared.Models;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// A schema script whose indexes need an extension the server refuses still completes: the block
/// that needs the extension is skipped with one warning, and everything else is applied.
/// </summary>
/// <remarks>
/// <para>
/// A managed server that does not allow-list an extension answers <c>CREATE EXTENSION</c> with
/// <c>0A000</c>; a role without the privilege with <c>42501</c>; a build without the extension's
/// files with <c>58P01</c>. Applied as ordinary DDL inside the initializer's transaction, any of
/// them failed the whole perspective pass, so a service with one substring index paid a failed
/// startup attempt on every start and never got the index either way.
/// </para>
/// <para>
/// The block is guarded by a savepoint when a transaction is open, so the refusal does not poison
/// the transaction the rest of the pass runs in.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class OptionalExtensionBlocksTests : IAsyncDisposable {
  private const string MISSING = "wh_no_such_extension";
  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = $"optext_{Guid.NewGuid():N}";
    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await using var create = new NpgsqlCommand($"CREATE DATABASE {_databaseName}", admin);
      await create.ExecuteNonQueryAsync();
    }
    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
    }.ConnectionString;
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    if (_databaseName is not null) {
      try {
        await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
      } catch (NpgsqlException) {
        // The container goes with the run; a database left behind costs nothing.
      }
    }
    GC.SuppressFinalize(this);
  }

  private async Task<string> _scalarAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    return (await command.ExecuteScalarAsync())?.ToString() ?? "<null>";
  }

  private static string _script(string extension) =>
    "CREATE TABLE IF NOT EXISTS optional_thing (a text, b text);\n"
    + "CREATE INDEX IF NOT EXISTS idx_optional_thing_a ON optional_thing (a);\n"
    + OptionalExtensionBlocks.BEGIN_MARKER + extension + "\n"
    + $"CREATE EXTENSION IF NOT EXISTS {extension};\n"
    + "CREATE INDEX IF NOT EXISTS idx_optional_thing_a_trgm ON optional_thing USING gin (a gin_trgm_ops);\n"
    + "CREATE INDEX IF NOT EXISTS idx_optional_thing_b_trgm ON optional_thing USING gin (b gin_trgm_ops);\n"
    + OptionalExtensionBlocks.END_MARKER + "\n"
    + "CREATE INDEX IF NOT EXISTS idx_optional_thing_b ON optional_thing (b);\n";

  private Task<string> _indexesAsync() =>
    _scalarAsync("SELECT coalesce(string_agg(indexname, ',' ORDER BY indexname), '') FROM pg_indexes WHERE tablename = 'optional_thing'");

  /// <summary>
  /// The reader reads a block out of the script the generator actually writes, so the two sides
  /// cannot drift on the markers.
  /// </summary>
  /// <remarks>
  /// The generator and the reader hold the marker text separately, because the generator's project
  /// is referenced by analyzers rather than by the runtime. Nothing but this makes them the same
  /// text: a marker changed on one side alone would leave the block unrecognized, and an
  /// unrecognized block is applied as ordinary DDL, which is the failure the block prevents.
  /// </remarks>
  [Test]
  public async Task TheReaderReadsTheGeneratorsOwnScriptAsync() {
    var script = JsonIndexSql.Script(
      [new JsonIndexInfo("Title", "Title", JsonIndexCast.None, Ordered: false, Substring: true, CaseInsensitive: false)],
      "optional_thing",
      "optional_thing");

    await Assert.That(OptionalExtensionBlocks.HasBlocks(script)).IsTrue()
      .Because("the generator writes the block with its own copy of the markers the reader looks for");
    var pieces = OptionalExtensionBlocks.Split(script);
    var blocks = pieces.Where(p => p.Extension is not null).ToList();
    await Assert.That(blocks).Count().IsEqualTo(1);
    await Assert.That(blocks[0].Extension).IsEqualTo(JsonIndexSql.TRIGRAM_EXTENSION);
    await Assert.That(blocks[0].Sql).Contains("gin_trgm_ops", StringComparison.Ordinal);
    await Assert.That(blocks[0].Sql).DoesNotContain("CREATE EXTENSION", StringComparison.Ordinal);
    await Assert.That(pieces.Any(p => p.Extension is null && p.Sql.Trim().Length > 0)).IsFalse()
      .Because("this script is nothing but the block, so no statement may land outside it");
  }

  /// <summary>A script without a block is one plain piece; a block is read with its extension.</summary>
  [Test]
  public async Task SplitReadsTheBlocksAsync() {
    await Assert.That(OptionalExtensionBlocks.HasBlocks("CREATE TABLE t (a int);")).IsFalse();
    await Assert.That(OptionalExtensionBlocks.HasBlocks(null!)).IsFalse()
      .Because("the question is asked of a script before anything has validated it, and no script "
        + "is no block rather than a throw on the path that decides how to apply one");
    var plain = OptionalExtensionBlocks.Split("CREATE TABLE t (a int);");
    await Assert.That(plain).Count().IsEqualTo(1);
    await Assert.That(plain[0].Extension).IsNull();

    var pieces = OptionalExtensionBlocks.Split(_script("pg_trgm"));
    await Assert.That(pieces).Count().IsEqualTo(3);
    await Assert.That(pieces[0].Extension).IsNull();
    await Assert.That(pieces[0].Sql).Contains("idx_optional_thing_a ON", StringComparison.Ordinal);
    await Assert.That(pieces[1].Extension).IsEqualTo("pg_trgm");
    await Assert.That(pieces[1].Sql).DoesNotContain("CREATE EXTENSION", StringComparison.Ordinal)
      .Because("the reader issues the extension statement itself, guarded, and keeps the block to what depends on it");
    await Assert.That(pieces[1].Sql).Contains("idx_optional_thing_b_trgm", StringComparison.Ordinal);
    await Assert.That(pieces[2].Extension).IsNull();
    await Assert.That(pieces[2].Sql).Contains("idx_optional_thing_b ON", StringComparison.Ordinal);
  }

  /// <summary>A block's index names are read for the warning.</summary>
  [Test]
  public async Task TheIndexNamesOfABlockAreReadAsync() {
    var names = OptionalExtensionBlocks.IndexNames(OptionalExtensionBlocks.Split(_script("pg_trgm"))[1].Sql);

    await Assert.That(names).IsEquivalentTo(["idx_optional_thing_a_trgm", "idx_optional_thing_b_trgm"]);
  }

  /// <summary>The three ways a server refuses an extension are the three that skip.</summary>
  [Test]
  public async Task TheRefusalStatesAreRecognizedAsync() {
    await Assert.That(OptionalExtensionBlocks.IsUnavailable("0A000")).IsTrue();
    await Assert.That(OptionalExtensionBlocks.IsUnavailable("42501")).IsTrue();
    await Assert.That(OptionalExtensionBlocks.IsUnavailable("58P01")).IsTrue();
    await Assert.That(OptionalExtensionBlocks.IsUnavailable("42601")).IsFalse()
      .Because("a syntax error is a defect, not an unavailable extension");
    await Assert.That(OptionalExtensionBlocks.IsUnavailable(null)).IsFalse()
      .Because("a failure that carries no SQL state is not a refusal either, and must propagate");
  }

  /// <summary>
  /// Inside a transaction, a refused extension skips its block, warns once, and the rest applies
  /// and commits.
  /// </summary>
  [Test]
  public async Task ARefusedExtensionSkipsItsBlockInsideATransactionAsync() {
    var log = new ListLogger();
    await using (var connection = new NpgsqlConnection(_connectionString)) {
      await connection.OpenAsync();
      await using var transaction = await connection.BeginTransactionAsync();

      var skipped = await OptionalExtensionBlocks.ApplyAsync(connection, transaction, _script(MISSING), 30, log);

      await Assert.That(skipped).IsEquivalentTo([MISSING]);
      await transaction.CommitAsync();
    }

    await Assert.That(await _indexesAsync()).IsEqualTo("idx_optional_thing_a,idx_optional_thing_b")
      .Because("the plain indexes before and after the block are built; the trigram ones are not");
    var warnings = log.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
    await Assert.That(warnings).Count().IsEqualTo(1);
    await Assert.That(warnings[0].Message).Contains(MISSING, StringComparison.Ordinal);
    await Assert.That(warnings[0].Message).Contains("idx_optional_thing_a_trgm", StringComparison.Ordinal);
    await Assert.That(warnings[0].Message).Contains("idx_optional_thing_b_trgm", StringComparison.Ordinal)
      .Because("an operator reads which indexes the service is running without from one line");
  }

  /// <summary>Without a transaction the outcome is the same.</summary>
  [Test]
  public async Task ARefusedExtensionSkipsItsBlockWithoutATransactionAsync() {
    var log = new ListLogger();
    await using (var connection = new NpgsqlConnection(_connectionString)) {
      await connection.OpenAsync();
      await OptionalExtensionBlocks.ApplyAsync(connection, null, _script(MISSING), 30, log);
    }

    await Assert.That(await _indexesAsync()).IsEqualTo("idx_optional_thing_a,idx_optional_thing_b");
    await Assert.That(log.Entries.Count(e => e.Level == LogLevel.Warning)).IsEqualTo(1);
  }

  /// <summary>An available extension is created and its block applied, with nothing to warn about.</summary>
  [Test]
  public async Task AnAvailableExtensionAppliesItsBlockAsync() {
    var log = new ListLogger();
    await using (var connection = new NpgsqlConnection(_connectionString)) {
      await connection.OpenAsync();
      await using var transaction = await connection.BeginTransactionAsync();
      var skipped = await OptionalExtensionBlocks.ApplyAsync(connection, transaction, _script("pg_trgm"), 30, log);
      await Assert.That(skipped).IsEmpty();
      await transaction.CommitAsync();
    }

    await Assert.That(await _indexesAsync())
      .IsEqualTo("idx_optional_thing_a,idx_optional_thing_a_trgm,idx_optional_thing_b,idx_optional_thing_b_trgm");
    await Assert.That(await _scalarAsync("SELECT count(*) FROM pg_extension WHERE extname = 'pg_trgm'")).IsEqualTo("1");
    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Warning)).IsFalse();
  }

  /// <summary>A closed connection is opened for the apply and closed after it.</summary>
  [Test]
  public async Task AClosedConnectionIsOpenedAndClosedAsync() {
    await using var connection = new NpgsqlConnection(_connectionString);

    await OptionalExtensionBlocks.ApplyAsync(connection, null, _script(MISSING), 30);

    await Assert.That(connection.State).IsEqualTo(System.Data.ConnectionState.Closed);
    await Assert.That(await _indexesAsync()).IsEqualTo("idx_optional_thing_a,idx_optional_thing_b");
  }

  /// <summary>A defect inside a block is not an unavailable extension, and is not swallowed.</summary>
  [Test]
  public async Task ADefectInsideABlockStillFailsAsync() {
    const string broken = OptionalExtensionBlocks.BEGIN_MARKER + "pg_trgm\n"
      + "CREATE EXTENSION IF NOT EXISTS pg_trgm;\n"
      + "CREATE INDEX IF NOT EXISTS idx_broken ON table_that_does_not_exist USING gin (a gin_trgm_ops);\n"
      + OptionalExtensionBlocks.END_MARKER + "\n";
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    await Assert.That(async () => await OptionalExtensionBlocks.ApplyAsync(connection, null, broken, 30))
      .Throws<PostgresException>();
  }

  /// <summary>Missing arguments are caller errors.</summary>
  [Test]
  public async Task MissingArgumentsAreRefusedAsync() {
    await Assert.That(async () => await OptionalExtensionBlocks.ApplyAsync(null!, null, "SELECT 1;", 30)).Throws<ArgumentNullException>();
    await using var connection = new NpgsqlConnection(_connectionString);
    await Assert.That(async () => await OptionalExtensionBlocks.ApplyAsync(connection, null, null!, 30)).Throws<ArgumentNullException>();
    await Assert.That(() => OptionalExtensionBlocks.Split(null!)).Throws<ArgumentNullException>();
  }

  /// <summary>Keeps every entry, so a test can ask what was said and at what level.</summary>
  private sealed class ListLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (Entries) {
        Entries.Add((logLevel, formatter(state, exception)));
      }
    }
  }
}
