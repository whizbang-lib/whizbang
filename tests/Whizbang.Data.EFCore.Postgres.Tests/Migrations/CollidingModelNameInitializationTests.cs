using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Two perspective models that share a type name are two tables, and a schema that holds both
/// reads as current on its second start.
/// </summary>
/// <remarks>
/// <para>
/// A service that nests its models (<c>Order.Model</c>, <c>Invoice.Model</c>) has many models with
/// one simple name. The per-perspective hash rows were keyed by that simple name, so the models
/// shared one row: whichever wrote last owned it, the first one compared against it read as changed
/// on every start, the slow path ran, and the perspective pass re-applied its DDL under the schema
/// lock on every start of every instance. A start under load then deadlocked against the running
/// work on the very tables it was needlessly re-creating.
/// </para>
/// <para>
/// The rows are keyed by table name now, which is unique within a schema by construction. This
/// test holds the whole path: two colliding models initialize once, and the second start takes the
/// fast path, which is the only start that issues no statement and takes no lock.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class CollidingModelNameInitializationTests {
  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("colliding");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is null) {
      return;
    }
    await PerTestDatabaseFactory.DropAsync(_databaseName);
  }

  private CollidingNamesDbContext _context() =>
    new(new DbContextOptionsBuilder<CollidingNamesDbContext>()
      .UseNpgsql(_connectionString)
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);

  private async Task<T?> _scalarAsync<T>(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    var result = await command.ExecuteScalarAsync();
    return result is null or DBNull ? default : (T)result;
  }

  private const string PERSPECTIVE_ROWS =
    "SELECT count(*) FROM wh_schema_migrations WHERE owner = 'perspective'";
  private const string LAST_PERSPECTIVE_WRITE =
    "SELECT max(updated_at) FROM wh_schema_migrations WHERE owner = 'perspective'";

  /// <summary>Each model gets its own hash row, and the second start is hash-clean.</summary>
  [Test]
  [Timeout(180000)]
  public async Task ASecondStartWithCollidingModelNamesTakesTheFastPathAsync(CancellationToken cancellationToken) {
    var first = new ListLogger();
    await using (var context = _context()) {
      await context.EnsureWhizbangDatabaseInitializedAsync(first, cancellationToken: cancellationToken);
    }

    await Assert.That(await _scalarAsync<bool>("SELECT to_regclass('public.wh_per_colliding_first') IS NOT NULL")).IsTrue();
    await Assert.That(await _scalarAsync<bool>("SELECT to_regclass('public.wh_per_colliding_second') IS NOT NULL")).IsTrue();
    await Assert.That(await _scalarAsync<long>(PERSPECTIVE_ROWS)).IsEqualTo(2L)
      .Because("two tables are two rows; two models sharing a type name must not share one");
    var writtenAt = await _scalarAsync<DateTime>(LAST_PERSPECTIVE_WRITE);

    var second = new ListLogger();
    await using (var context = _context()) {
      await context.EnsureWhizbangDatabaseInitializedAsync(second, cancellationToken: cancellationToken);
    }

    await Assert.That(second.Entries.Any(e => e.Message.Contains("Schema up to date", StringComparison.Ordinal))).IsTrue()
      .Because("with every hash current the second start must decide before taking the lock");
    await Assert.That(second.Entries.Any(e => e.Message.Contains("Starting database initialization", StringComparison.Ordinal))).IsFalse()
      .Because("the slow path is where the perspective pass re-applies DDL under the lock");
    await Assert.That(second.Entries.Any(e => e.Message.Contains("Acquiring advisory lock", StringComparison.Ordinal))).IsFalse()
      .Because("a current schema takes no lock");
    await Assert.That(await _scalarAsync<DateTime>(LAST_PERSPECTIVE_WRITE)).IsEqualTo(writtenAt)
      .Because("a hash-clean start touches no perspective row");
    await Assert.That(await _scalarAsync<long>(
        "SELECT count(*) FROM wh_schema_migrations WHERE owner = 'perspective' AND status = 2")).IsEqualTo(0L)
      .Because("nothing was updated, because nothing had changed");
  }

  /// <summary>Keeps every entry, so a test can ask what was said.</summary>
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
