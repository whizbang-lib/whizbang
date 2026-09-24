using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Data.EFCore.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The catalog reads behind the measured advisory, executed rather than faked.
/// </summary>
/// <remarks>
/// The advisory's own decisions are covered with fakes, which says what it does with the numbers
/// and nothing about whether the numbers can be obtained. These statements are the part that can be
/// wrong in ways no fake would show: a column that is not there, a view that cannot be read, a
/// pattern that matches nothing.
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
[Category("Integration")]
[Category("Shard4")]
public class TableStatisticsProviderTests : IAsyncDisposable {
  private string _databaseName = null!;
  private NpgsqlDataSource _dataSource = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = $"stats_{Guid.NewGuid():N}";

    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await using var create = new NpgsqlCommand($"CREATE DATABASE {_databaseName}", admin);
      await create.ExecuteNonQueryAsync();
    }

    var connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
    }.ConnectionString;

    _dataSource = NpgsqlDataSource.Create(connectionString);
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    await _dataSource.DisposeAsync();

    try {
      await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
      await admin.OpenAsync();
      await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)", admin);
      await drop.ExecuteNonQueryAsync();
    } catch (NpgsqlException) {
      // The container is shared and the database is per-test; a failed drop is not a test failure.
    }

    GC.SuppressFinalize(this);
  }

  /// <summary>
  /// A table that has been read through reports the reading.
  /// </summary>
  /// <remarks>
  /// The counters are what the whole advisory rests on, so what is asserted is that they arrive at
  /// all and that a table scanned is distinguishable from one that was not. The exact numbers are
  /// the database's business and are deliberately not pinned: PostgreSQL updates these
  /// asynchronously, and a test that demanded an exact count would fail for reasons that say
  /// nothing about this code.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task ScanCountersAreReadForATableThatHasBeenScannedAsync(CancellationToken cancellationToken) {
    await using (var db = await _dataSource.OpenConnectionAsync(cancellationToken)) {
      await using var create = new NpgsqlCommand(
        "CREATE TABLE wh_per_stats_probe (id int, label text)", db);
      await create.ExecuteNonQueryAsync(cancellationToken);

      await using var seed = new NpgsqlCommand(
        "INSERT INTO wh_per_stats_probe SELECT g, 'row-' || g FROM generate_series(1, 500) g", db);
      await seed.ExecuteNonQueryAsync(cancellationToken);

      // No index exists, so this can only be answered by reading the table.
      for (var i = 0; i < 5; i++) {
        await using var scan = new NpgsqlCommand(
          "SELECT count(*) FROM wh_per_stats_probe WHERE label = 'row-7'", db);
        await scan.ExecuteScalarAsync(cancellationToken);
      }

      // A backend accumulates its statistics and flushes them on its own schedule, so without this
      // the counters are whatever happened to have been written by the time they were read, which
      // is nothing at all when the reads just happened. Forcing the flush is what makes this a test
      // rather than a race. It has to run on the connection that did the reading, because what is
      // pending is that backend's.
      await using var flush = new NpgsqlCommand("SELECT pg_stat_force_next_flush()", db);
      await flush.ExecuteScalarAsync(cancellationToken);
    }

    var provider = new PostgresTableStatisticsProvider(_dataSource);
    var statistics = await provider.GetTableScanStatisticsAsync(cancellationToken);

    await Assert.That(statistics.ContainsKey("wh_per_stats_probe")).IsTrue()
      .Because("a table the advisory may have to advise on has to appear in the counters at all");

    var probe = statistics["wh_per_stats_probe"];
    await Assert.That(probe.SequentialScans).IsGreaterThan(0L)
      .Because("the reads above had no index to use, so they were sequential scans");
    await Assert.That(probe.SequentialRowsRead).IsGreaterThan(0L)
      .Because("rows read is the measure the advisory ranks on, so an empty one would rank nothing");
  }

  /// <summary>
  /// Statement statistics are read where they exist, and their absence is not a failure.
  /// </summary>
  /// <remarks>
  /// The extension has to be installed and also loaded at startup, and a test container has done
  /// neither. That is the path worth executing here, because it is the one most deployments take:
  /// the read is refused and the provider answers empty, so the advisory still names the table.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task StatementStatisticsAreEmptyRatherThanFatalWhenNotCollectedAsync(
      CancellationToken cancellationToken) {
    var provider = new PostgresTableStatisticsProvider(_dataSource);

    var predicates = await provider.GetExpensivePredicatesAsync(cancellationToken);

    await Assert.That(predicates).IsNotNull()
      .Because("naming the filter sharpens the advice and was never a condition of giving it, so "
             + "an engine that records nothing has to answer nothing rather than throw");
  }

  /// <summary>
  /// What a recorded statement is understood to say, without needing the extension that records it.
  /// </summary>
  /// <remarks>
  /// A recorded statement has had its literals replaced by placeholders, which is why the numbers
  /// in these are gone and the document keys are not: a key is part of the expression rather than a
  /// literal of the query. That is the property the whole feature depends on, so it is stated here
  /// rather than assumed.
  /// </remarks>
  [Test]
  public async Task ARecordedStatementNamesTheFiltersItReadsAsync() {
    var collected = new Dictionary<string, List<ExpensivePredicate>>(StringComparer.Ordinal);

    PostgresTableStatisticsProvider.Collect(collected,
      "SELECT w.id FROM wh_per_documents AS w WHERE (w.data ->> 'TenantId') = $1 "
      + "AND (w.data ->> 'EntityType') = $2", calls: 1158, meanMilliseconds: 218.0);

    await Assert.That(collected.ContainsKey("wh_per_documents")).IsTrue();

    var found = collected["wh_per_documents"];
    await Assert.That(found.Select(p => p.Field)).IsEquivalentTo(["TenantId", "EntityType"])
      .Because("both filters in one statement are both things that might want promoting");
    await Assert.That(found[0].MeanMilliseconds).IsEqualTo(218.0);
    await Assert.That(found[0].Document).IsEqualTo("data");
  }

  [Test]
  public async Task TheSameFilterSeenTwice_IsOneThingToPromoteAsync() {
    var collected = new Dictionary<string, List<ExpensivePredicate>>(StringComparer.Ordinal);

    // Statements arrive dearest first, so the dearest sighting is the one that should survive.
    PostgresTableStatisticsProvider.Collect(collected,
      "SELECT 1 FROM wh_per_documents WHERE (data ->> 'TenantId') = $1", 900, 218.0);
    PostgresTableStatisticsProvider.Collect(collected,
      "SELECT 1 FROM wh_per_documents WHERE (data ->> 'TenantId') = $1 AND id = $2", 40, 3.0);

    await Assert.That(collected["wh_per_documents"]).Count().IsEqualTo(1)
      .Because("one field is one column to promote, however many statements read it");
    await Assert.That(collected["wh_per_documents"][0].MeanMilliseconds).IsEqualTo(218.0)
      .Because("the dearest sighting is the one that argues for promoting it");
  }

  [Test]
  public async Task AStatementTouchingNoPerspective_IsIgnoredAsync() {
    var collected = new Dictionary<string, List<ExpensivePredicate>>(StringComparer.Ordinal);

    PostgresTableStatisticsProvider.Collect(collected,
      "SELECT 1 FROM wh_outbox WHERE (metadata ->> 'Kind') = $1", 5000, 90.0);

    await Assert.That(collected).IsEmpty()
      .Because("the framework's own tables are not a consumer's to promote a column on");
  }

  [Test]
  public async Task TheScopeAndMetadataDocuments_AreReadTooAsync() {
    var collected = new Dictionary<string, List<ExpensivePredicate>>(StringComparer.Ordinal);

    PostgresTableStatisticsProvider.Collect(collected,
      "SELECT 1 FROM wh_per_documents WHERE (scope ->> 't') = $1 AND (metadata ->> 'EventType') = $2",
      100, 12.0);

    await Assert.That(collected["wh_per_documents"].Select(p => p.Document))
      .IsEquivalentTo(["scope", "metadata"])
      .Because("a tenant filter reads the scope document, and it is the highest-traffic filter there is");
  }

  /// <summary>The sizes read alongside the counters, which the finding reports as context.</summary>
  [Test]
  [Timeout(120000)]
  public async Task TableSizesAreReadAsync(CancellationToken cancellationToken) {
    await using (var db = await _dataSource.OpenConnectionAsync(cancellationToken)) {
      await using var create = new NpgsqlCommand("CREATE TABLE wh_inbox (id int)", db);
      await create.ExecuteNonQueryAsync(cancellationToken);
    }

    var provider = new PostgresTableStatisticsProvider(_dataSource);
    var sizes = await provider.GetEstimatedTableSizesAsync(cancellationToken);

    await Assert.That(sizes.ContainsKey("wh_inbox")).IsTrue();
  }
}
