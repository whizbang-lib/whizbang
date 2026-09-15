using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// That one instance rewrites a stored format, not every instance that starts.
/// </summary>
/// <remarks>
/// <para>
/// The rewrites run before the initializer's transaction opens, which is also outside the
/// transaction-scoped advisory lock that makes one instance do the schema work. Taken at session
/// scope here instead, over the connection that runs them.
/// </para>
/// <para>
/// The statements are idempotent, so concurrent instances would still reach the right answer. What
/// they would also do is scan the same tables many times over and risk deadlocking each other: two
/// full-table updates over the same rows take row locks in whatever order they meet them.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#statements-that-need-a-commit-between-them</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class CanonicalTemporalRewritePhaseTests : IAsyncDisposable {
  private const long LOCK_ID = 987654321;
  private const int TIMEOUT_SECONDS = 30;

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _databaseName = $"rewritephase_{Guid.NewGuid():N}";
    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await using var create = new NpgsqlCommand($"CREATE DATABASE {_databaseName}", admin);
      await create.ExecuteNonQueryAsync();
    }

    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
    }.ConnectionString;

    await _executeAsync("CREATE TABLE marker (note text NOT NULL)");
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

  private NpgsqlConnection _connect() => new(_connectionString);

  /// <summary>A rewrite that leaves a trace, so applying it is observable.</summary>
  private static (string Name, string Sql)[] _rewrites(params string[] notes) =>
    [.. notes.Select(n => (n, $"INSERT INTO marker (note) VALUES ('{n}');"))];

  /// <summary>Whether the schema lock is held by anyone.</summary>
  private async Task<string> _lockHoldersAsync() =>
    await _scalarAsync($"SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND objid = {LOCK_ID}");

  /// <summary>The holder applies every rewrite and gives the lock back.</summary>
  [Test]
  public async Task TheRewritesRunUnderTheSchemaLockAsync() {
    var ran = await CanonicalTemporalRewritePhase.ApplyAsync(
      _connect, LOCK_ID, _rewrites("first", "second"), TIMEOUT_SECONDS);

    await Assert.That(ran).IsTrue();
    await Assert.That(await _scalarAsync("SELECT count(*) FROM marker")).IsEqualTo("2");
    await Assert.That(await _lockHoldersAsync()).IsEqualTo("0")
      .Because("a lock still held after the phase would stall every other instance's rewrite");
  }

  /// <summary>
  /// An instance that cannot take the lock applies nothing.
  /// </summary>
  /// <remarks>
  /// The expected outcome for every instance but one. The holder commits the rewrite, and the
  /// losers' own index builds then read converted rows, so doing nothing is correct rather than a
  /// failure to report loudly.
  /// </remarks>
  [Test]
  public async Task AnInstanceThatCannotTakeTheLockAppliesNothingAsync() {
    await using var holder = _connect();
    await holder.OpenAsync();
    await using (var take = new NpgsqlCommand($"SELECT pg_advisory_lock({LOCK_ID})", holder)) {
      await take.ExecuteScalarAsync();
    }

    var ran = await CanonicalTemporalRewritePhase.ApplyAsync(
      _connect, LOCK_ID, _rewrites("should-not-run"), TIMEOUT_SECONDS);

    await Assert.That(ran).IsFalse();
    await Assert.That(await _scalarAsync("SELECT count(*) FROM marker")).IsEqualTo("0");

    await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({LOCK_ID})", holder);
    await release.ExecuteScalarAsync();
  }

  /// <summary>
  /// A rewrite that fails is reported, the rest still run, and the lock comes back.
  /// </summary>
  /// <remarks>
  /// A lock leaked on the failure path is worse than the failure: every later instance would skip
  /// its rewrite forever while believing another instance was doing it.
  /// </remarks>
  [Test]
  public async Task AFailedRewriteStillReleasesTheLockAsync() {
    var ran = await CanonicalTemporalRewritePhase.ApplyAsync(
      _connect,
      LOCK_ID,
      [("broken", "INSERT INTO table_that_does_not_exist (x) VALUES (1);"),
       ("good", "INSERT INTO marker (note) VALUES ('after-the-failure');")],
      TIMEOUT_SECONDS);

    await Assert.That(ran).IsTrue();
    await Assert.That(await _scalarAsync("SELECT count(*) FROM marker")).IsEqualTo("1")
      .Because("one rewrite failing must not stop the others, which convert different tables");
    await Assert.That(await _lockHoldersAsync()).IsEqualTo("0");
  }

  /// <summary>Nothing to rewrite takes no lock and opens no connection.</summary>
  [Test]
  public async Task NoRewritesTakesNoLockAsync() {
    var opened = 0;

    var ran = await CanonicalTemporalRewritePhase.ApplyAsync(
      () => { opened++; return _connect(); }, LOCK_ID, [], TIMEOUT_SECONDS);

    await Assert.That(ran).IsTrue();
    await Assert.That(opened).IsEqualTo(0)
      .Because("a database with nothing to convert should pay nothing for this phase");
  }

  /// <summary>A missing factory or rewrite list is a caller error.</summary>
  [Test]
  public async Task MissingArgumentsAreRefusedAsync() {
    await Assert.That(async () => await CanonicalTemporalRewritePhase.ApplyAsync(
      null!, LOCK_ID, _rewrites("x"), TIMEOUT_SECONDS)).Throws<ArgumentNullException>();

    await Assert.That(async () => await CanonicalTemporalRewritePhase.ApplyAsync(
      _connect, LOCK_ID, null!, TIMEOUT_SECONDS)).Throws<ArgumentNullException>();
  }
}
