using System.Collections.Immutable;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Shared.Models;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// That the rewrite of a stored format can run, and commit, before the table it rewrites is known
/// to exist.
/// </summary>
/// <remarks>
/// <para>
/// An index over an extraction of a rewritten key has to be built after the rewrite has committed,
/// and the initializer builds its indexes inside one advisory-locked transaction. Committing the
/// rewrite from inside that transaction is impossible, and applying it on a second connection while
/// the transaction is open deadlocks: the second connection blocks on catalog rows the transaction
/// has not committed, and the transaction cannot advance because it is waiting for that connection
/// to return. PostgreSQL cannot break it, because one side is waiting on a client rather than a
/// lock. <see cref="ASideConnectionBlocksOnUncommittedSchemaDdlAsync"/> pins that.
/// </para>
/// <para>
/// So the rewrite runs before the transaction opens, which means it must tolerate a table that does
/// not exist yet. A database created by this release has nothing to rewrite and its tables are
/// created later in the same pass, so the guard is the ordinary case rather than an edge.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#statements-that-need-a-commit-between-them</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class GuardedTemporalRewriteTests : IAsyncDisposable {
  private const string TABLE = "wh_per_guarded";
  private const string INDEX = "idx_guarded_occurredat_json";
  private const string OLD_RENDERING = "2026-04-21T22:38:17.357886+00:00";
  private const int SEEDED_ROWS = 200;

  private static readonly ImmutableArray<CanonicalTemporalProperty> _properties = [
    new("OccurredAt", CanonicalTemporalKind.Instant, false),
  ];

  private static readonly JsonIndexInfo _index =
    new("OccurredAt", "OccurredAt", JsonIndexCast.Int8, true, false, false);

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _databaseName = $"guarded_{Guid.NewGuid():N}";
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
        await using var drop = new NpgsqlCommand(
          $"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
      } catch (NpgsqlException) {
        // The container is torn down with the run; a database left behind costs nothing.
      }
    }

    GC.SuppressFinalize(this);
  }

  /// <summary>Creates the table and seeds rows in the shape an earlier release left behind.</summary>
  /// <remarks>
  /// fillfactor leaves no free space in a page, so a rewrite cannot place the new row version beside
  /// the old one. An update that fits in its own page is done in place and the superseded version
  /// never becomes separately visible, which would let an index built too early pass.
  /// </remarks>
  private async Task _seedOldFormatAsync() {
    await _executeAsync($"""
      CREATE TABLE {TABLE} (
        id uuid PRIMARY KEY,
        data jsonb NOT NULL
      ) WITH (fillfactor = 100);
      """);
    await _executeAsync($"""
      INSERT INTO {TABLE} (id, data)
      SELECT gen_random_uuid(),
             jsonb_build_object('OccurredAt', '{OLD_RENDERING}', 'Padding', repeat('x', 1000))
      FROM generate_series(1, {SEEDED_ROWS});
      """);
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

  /// <summary>Applies every guarded rewrite, each committed on its own connection.</summary>
  private async Task _applyGuardedAsync() {
    foreach (var statement in CanonicalTemporalBackfillSql.GuardedStatements(_properties, TABLE)) {
      await _executeAsync(statement);
    }
  }

  /// <summary>A model with nothing to rewrite produces nothing to run.</summary>
  [Test]
  public async Task AModelWithNoTemporalPropertyIsNotGuardedAsync() =>
    await Assert.That(CanonicalTemporalBackfillSql.GuardedStatements([], TABLE).Any()).IsFalse();

  /// <summary>Each property gets its own guarded rewrite, carrying its own conversion.</summary>
  [Test]
  public async Task EachTemporalPropertyGetsItsOwnGuardedRewriteAsync() {
    var statements = CanonicalTemporalBackfillSql.GuardedStatements(
      [new("OccurredAt", CanonicalTemporalKind.Instant, false),
       new("Elapsed", CanonicalTemporalKind.Duration, false)],
      TABLE).ToList();

    await Assert.That(statements.Count).IsEqualTo(2);
    await Assert.That(statements[0]).Contains("OccurredAt", StringComparison.Ordinal);
    await Assert.That(statements[1]).Contains("Elapsed", StringComparison.Ordinal);
    await Assert.That(statements[0]).Contains(TABLE, StringComparison.Ordinal);
  }

  /// <summary>
  /// A rewrite whose table does not exist yet does nothing, rather than failing.
  /// </summary>
  /// <remarks>
  /// This runs before the tables are created, so on a database created by this release every rewrite
  /// meets a missing table. Failing there would make a first start impossible.
  /// </remarks>
  [Test]
  public async Task AGuardedRewriteAgainstAMissingTableDoesNothingAsync() {
    await _applyGuardedAsync();

    await Assert.That(await _scalarAsync(
      $"SELECT count(*) FROM pg_tables WHERE tablename = '{TABLE}'")).IsEqualTo("0");
  }

  /// <summary>The rewrite converts the rows and commits, on its own connection.</summary>
  [Test]
  public async Task AGuardedRewriteConvertsAndCommitsAsync() {
    await _seedOldFormatAsync();

    await _applyGuardedAsync();

    await Assert.That(await _scalarAsync(
      $"SELECT string_agg(DISTINCT jsonb_typeof(data -> 'OccurredAt'), ',') FROM {TABLE}"))
      .IsEqualTo("number");
  }

  /// <summary>Running it twice changes nothing, so the pass can run on every start.</summary>
  [Test]
  public async Task AGuardedRewriteIsANoOpTheSecondTimeAsync() {
    await _seedOldFormatAsync();
    await _applyGuardedAsync();
    var afterFirst = await _scalarAsync(
      $"SELECT string_agg(DISTINCT data ->> 'OccurredAt', ',') FROM {TABLE}");

    await _applyGuardedAsync();

    await Assert.That(await _scalarAsync(
      $"SELECT string_agg(DISTINCT data ->> 'OccurredAt', ',') FROM {TABLE}")).IsEqualTo(afterFirst);
  }

  /// <summary>
  /// Once the rewrite has committed, the index builds inside the initializer's single transaction.
  /// </summary>
  /// <remarks>
  /// The behaviour the whole arrangement exists for. The index is built exactly where it always was,
  /// in one transaction with the table DDL, and succeeds because the rows it reads were converted
  /// and committed before that transaction opened.
  /// </remarks>
  [Test]
  public async Task AnIndexBuildsInOneTransactionOnceTheRewriteIsCommittedAsync() {
    await _seedOldFormatAsync();
    await _applyGuardedAsync();

    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var transaction = await db.BeginTransactionAsync();
    foreach (var statement in JsonIndexSql.CreateStatements(_index, TABLE, "guarded")) {
      await using var command = new NpgsqlCommand(statement, db, transaction);
      await command.ExecuteNonQueryAsync();
    }

    await transaction.CommitAsync();

    await Assert.That(await _scalarAsync(
      $"SELECT count(*) FROM pg_indexes WHERE indexname = '{INDEX}'")).IsEqualTo("1");
  }

  /// <summary>
  /// A second connection blocks on schema DDL an open transaction has not committed.
  /// </summary>
  /// <remarks>
  /// The characterization that rules out the alternative. Applying part of a perspective's schema on
  /// a connection of its own while the initializer's transaction is open looks like it would work,
  /// and instead hangs: the second connection waits on catalog rows the transaction holds, while the
  /// transaction waits for that connection to return. Neither side can move and PostgreSQL cannot
  /// detect it, because one of them is blocked on a client rather than on a lock. Asserted with a
  /// statement timeout because the real thing does not end on its own.
  /// </remarks>
  [Test]
  public async Task ASideConnectionBlocksOnUncommittedSchemaDdlAsync() {
    await using var holder = new NpgsqlConnection(_connectionString);
    await holder.OpenAsync();
    await using var holding = await holder.BeginTransactionAsync();
    await using (var ddl = new NpgsqlCommand(
        $"CREATE TABLE IF NOT EXISTS {TABLE} (id uuid PRIMARY KEY, data jsonb NOT NULL)",
        holder, holding)) {
      await ddl.ExecuteNonQueryAsync();
    }

    await using var side = new NpgsqlConnection(_connectionString);
    await side.OpenAsync();
    await using var blocked = new NpgsqlCommand(
      $"SET statement_timeout='3s'; CREATE TABLE IF NOT EXISTS {TABLE} (id uuid PRIMARY KEY, data jsonb NOT NULL)",
      side);

    var failure = await Assert.ThrowsAsync<PostgresException>(
      async () => await blocked.ExecuteNonQueryAsync());

    // 57014 is query_canceled: it never completed, it was cut off.
    await Assert.That(failure!.SqlState).IsEqualTo("57014");

    await holding.RollbackAsync();
  }
}
