using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The declared-length constraint, executed rather than inspected.
/// </summary>
/// <remarks>
/// <para>
/// The generators are covered by assertions over the text they produce, which can say that a
/// statement was written and cannot say that PostgreSQL accepts it or that it does what the text
/// implies. Both are the point here: the statement is applied to a real table, applied a second
/// time because it runs on every start, and then asked to accept and refuse actual rows.
/// </para>
/// <para>
/// NOT VALID is the property the whole approach rests on. It has to leave rows already present
/// alone, including rows longer than the limit, while applying to everything written afterwards.
/// A constraint that rejected the existing rows would turn a start-up script into an outage.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
[Category("Integration")]
[Category("Shard4")]
public class LengthConstraintDdlTests : IAsyncDisposable {
  private const string TABLE = "ck_probe";

  private string _databaseName = null!;
  private string _connectionString = null!;

  /// <summary>The statement shape both generators emit for a declared length.</summary>
  private static string _constraintSql(string table, string column, int maxLength) => $"""
    DO $$ BEGIN
      IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_{table}_{column}_len'
        AND conrelid = '{table}'::regclass) THEN
        ALTER TABLE {table} ADD CONSTRAINT ck_{table}_{column}_len
          CHECK (length({column}) <= {maxLength}) NOT VALID;
      END IF;
    END $$;
    """;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _databaseName = $"ck_probe_{Guid.NewGuid():N}";
    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await _execAsync(admin, $"CREATE DATABASE {_databaseName}");
    }

    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
    }.ConnectionString;
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    try {
      await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
      await admin.OpenAsync();
      await _execAsync(admin, $"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)");
    } catch (NpgsqlException) {
      // The container is shared and the database is per-test; a failed drop is not a test failure.
    }

    GC.SuppressFinalize(this);
  }

  /// <summary>
  /// The statement applies, applies again without complaint, and then holds.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task TheConstraintApplies_Twice_AndThenHoldsAsync(CancellationToken cancellationToken) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync(cancellationToken);

    await _execAsync(db, $"CREATE TABLE {TABLE} (id int PRIMARY KEY, label TEXT)");

    // A row longer than the limit, written before the constraint exists. NOT VALID has to leave it.
    await _execAsync(db, $"INSERT INTO {TABLE} VALUES (1, repeat('x', 100))");

    await _execAsync(db, _constraintSql(TABLE, "label", 64));
    await _execAsync(db, _constraintSql(TABLE, "label", 64));

    var constraints = await _scalarAsync(db,
      $"SELECT count(*) FROM pg_constraint WHERE conname = 'ck_{TABLE}_label_len'", cancellationToken);
    await Assert.That(constraints).IsEqualTo(1L)
      .Because("the script runs on every start, so applying it again has to be silent rather than an error");

    var survivors = await _scalarAsync(db, $"SELECT count(*) FROM {TABLE}", cancellationToken);
    await Assert.That(survivors).IsEqualTo(1L)
      .Because("NOT VALID leaves what is already there alone, which is what keeps this additive");

    await _execAsync(db, $"INSERT INTO {TABLE} VALUES (2, repeat('y', 64))");
    await _execAsync(db, $"INSERT INTO {TABLE} VALUES (3, NULL)");

    var refused = await Assert.That(async () =>
      await _execAsync(db, $"INSERT INTO {TABLE} VALUES (4, repeat('z', 65))"))
      .Throws<PostgresException>();

    await Assert.That(refused!.SqlState).IsEqualTo("23514")
      .Because("one over the limit is a check violation, which is the enforcement the length asked for");
  }

  private static async Task _execAsync(NpgsqlConnection db, string sql) {
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection db, string sql, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(sql, db);
    return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
  }
}
