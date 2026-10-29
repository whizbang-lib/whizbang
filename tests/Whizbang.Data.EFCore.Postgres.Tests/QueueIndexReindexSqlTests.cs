// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Dispatch;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The SQL behind the queue-index rebuild (#1746), against real Postgres: the survey covers every B-tree index of the
/// queue tables that churn to empty and nothing else, its catalog estimate reads a fresh index as lean and a churned one
/// as bloated, <c>REINDEX INDEX CONCURRENTLY</c> shrinks a bloated index on its own connection, an invalid leftover of an
/// interrupted rebuild is dropped first, and nothing is dropped while a build is running.
/// </summary>
/// <docs>operations/infrastructure/storage-maintenance#queue-index-rebuild</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/QueueIndexReindexStep.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public class QueueIndexReindexSqlTests : EFCoreTestBase {
  private const string PKEY = "wh_active_streams_pkey";
  private const long MIB = 1024 * 1024;
  private static readonly string[] _queueTables =
    ["wh_outbox", "wh_inbox", "wh_inbox_state", "wh_perspective_events", "wh_active_streams", "wh_doorbell_queue"];

  private sealed class CapturingLogger<T> : ILogger<T> {
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Messages.Add(formatter(state, exception));
  }

  private async Task _execAsync(string sql, CancellationToken cancellationToken = default) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync(cancellationToken);
    await using var command = new NpgsqlCommand(sql, db) { CommandTimeout = 0 };
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  private async Task<T> _scalarAsync<T>(string sql, CancellationToken cancellationToken = default) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync(cancellationToken);
    await using var command = new NpgsqlCommand(sql, db);
    return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
  }

  private Task<long> _sizeAsync(string index, CancellationToken cancellationToken) =>
    _scalarAsync<long>($"SELECT pg_relation_size(to_regclass('{index}'))", cancellationToken);

  private Task<bool> _existsAsync(string index, CancellationToken cancellationToken) =>
    _scalarAsync<bool>($"SELECT to_regclass('{index}') IS NOT NULL", cancellationToken);

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(
      WorkCoordinationDbContext context, ILogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>>? logger = null) =>
    new(context, JsonContextRegistry.CreateCombinedOptions(), logger);

  /// <summary>
  /// Real index bloat on a queue table: fill <c>wh_active_streams</c>, empty it as a drained queue does, and vacuum it.
  /// The vacuum records that the indexes hold no rows, but a B-tree never gives its pages back to the file, so the
  /// primary key keeps the size it had at the peak until something rebuilds it.
  /// </summary>
  private async Task _churnToEmptyAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using (var fill = new NpgsqlCommand(
      "INSERT INTO wh_active_streams (stream_id, partition_number) SELECT gen_random_uuid(), g % 100 FROM generate_series(1, 100000) g",
      conn)) {
      await fill.ExecuteNonQueryAsync(cancellationToken);
    }
    await using (var drain = new NpgsqlCommand("DELETE FROM wh_active_streams", conn)) {
      await drain.ExecuteNonQueryAsync(cancellationToken);
    }
    // Deleted rows are indexed by a rebuild until no snapshot anywhere on the server could still see them.
    await TableBloat.WaitUntilTheDeletedRowsAreRemovableAsync(conn, cancellationToken);
    await using var vacuum = new NpgsqlCommand("VACUUM wh_active_streams", conn);
    await vacuum.ExecuteNonQueryAsync(cancellationToken);
  }

  // ── The survey ──────────────────────────────────────────────────────────

  /// <summary>
  /// The survey covers the B-tree indexes of exactly the queue tables that churn to empty, each of which exists in the
  /// migrated schema, and no other table's (the deduplication window and the settings table are not queues).
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-which-tables</docs>
  [Test]
  [Timeout(120000)]
  public async Task TheSurvey_CoversEveryQueueTable_AndNoOtherAsync(CancellationToken cancellationToken) {
    await using var context = CreateDbContext();

    var survey = await _coordinator(context).GetQueueIndexBloatAsync(cancellationToken);

    await Assert.That(survey.Select(s => s.TableName).Distinct().Order(StringComparer.Ordinal))
      .IsEquivalentTo(_queueTables.Order(StringComparer.Ordinal));
    await Assert.That(survey.Select(s => s.IndexName)).Contains(PKEY);
    await Assert.That(survey.Select(s => s.IndexName)).DoesNotContain("wh_settings_pkey");
    await Assert.That(survey.All(s => s.Valid && !s.BuildInProgress && s.Method == BloatMeasurementMethod.Estimate)).IsTrue();
  }

  /// <summary>An empty, vacuumed queue table's index is expected to need two pages: the metapage and one leaf.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-expected-size</docs>
  [Test]
  [Timeout(120000)]
  public async Task AnEmptyVacuumedQueueIndex_IsExpectedToNeedTwoPagesAsync(CancellationToken cancellationToken) {
    await _execAsync("VACUUM wh_active_streams", cancellationToken);
    await using var context = CreateDbContext();

    var survey = await _coordinator(context).GetQueueIndexBloatAsync(cancellationToken);

    await Assert.That(survey.Single(s => s.IndexName == PKEY).ExpectedBytes).IsEqualTo(2 * 8192);
  }

  /// <summary>
  /// A freshly built index over live rows reads as about 1, so it is never rebuilt again at any threshold the option
  /// accepts: the estimate is the leaf pages the rows need at the B-tree's fill factor, plus the metapage.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-expected-size</docs>
  [Test]
  [Timeout(120000)]
  public async Task AFreshlyBuiltIndex_ReadsAsAboutOneAsync(CancellationToken cancellationToken) {
    await _execAsync(
      "INSERT INTO wh_active_streams (stream_id, partition_number) SELECT gen_random_uuid(), g % 100 FROM generate_series(1, 50000) g;"
      + $"REINDEX INDEX {PKEY}; ANALYZE wh_active_streams;", cancellationToken);
    await using var context = CreateDbContext();

    var survey = await _coordinator(context).GetQueueIndexBloatAsync(cancellationToken);

    var ratio = survey.Single(s => s.IndexName == PKEY).Ratio;
    await Assert.That(ratio.HasValue).IsTrue();
    await Assert.That(ratio!.Value).IsBetween(0.8, 1.3);
  }

  /// <summary>An index that has never been vacuumed or analyzed has no row count, so its expected size is not known.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-expected-size</docs>
  [Test]
  [Timeout(120000)]
  public async Task AnIndexWithNoRowCount_HasNoExpectedSizeAsync(CancellationToken cancellationToken) {
    await _execAsync($"UPDATE pg_class SET reltuples = -1 WHERE oid = to_regclass('{PKEY}')", cancellationToken);
    await using var context = CreateDbContext();

    var survey = await _coordinator(context).GetQueueIndexBloatAsync(cancellationToken);

    await Assert.That(survey.Single(s => s.IndexName == PKEY).ExpectedBytes).IsNull();
  }

  // ── The rebuild ─────────────────────────────────────────────────────────

  /// <summary>
  /// The step, against real Postgres: the churned primary key reads as far over the threshold, and after one cycle it is
  /// rebuilt concurrently and back to a few pages.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#queue-index-rebuild</docs>
  [Test]
  [Timeout(180000)]
  public async Task TheStep_ShrinksABloatedQueueIndexAsync(CancellationToken cancellationToken) {
    await _churnToEmptyAsync(cancellationToken);
    await using var context = CreateDbContext();
    var coordinator = _coordinator(context);
    var bloat = (await coordinator.GetQueueIndexBloatAsync(cancellationToken)).Single(s => s.IndexName == PKEY);
    await Assert.That(bloat.Ratio!.Value).IsGreaterThan(3.0);
    await Assert.That(bloat.SizeBytes).IsGreaterThanOrEqualTo(MIB);
    var (chosen, before) = await _mostReclaimableAsync(coordinator, cancellationToken);

    await _runStepAsync(context, coordinator, cancellationToken);

    var after = await _sizeAsync(chosen, cancellationToken);
    await Assert.That(after).IsLessThan(before / 10);
    await Assert.That(await _scalarAsync<bool>($"SELECT indisvalid FROM pg_index WHERE indexrelid = to_regclass('{chosen}')", cancellationToken)).IsTrue();
  }

  /// <summary>
  /// An invalid leftover of an interrupted rebuild (here a concurrent build that failed, which Postgres leaves invalid,
  /// named as a rebuild names its copy) is dropped by the step, and the bloated index beside it is still rebuilt.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#queue-index-leftovers</docs>
  [Test]
  [Timeout(180000)]
  public async Task TheStep_DropsAnInvalidLeftover_AndStillRebuildsAsync(CancellationToken cancellationToken) {
    await _churnToEmptyAsync(cancellationToken);
    await _leaveAnInvalidLeftoverAsync(PKEY + "_ccnew", cancellationToken);
    await using var context = CreateDbContext();
    var coordinator = _coordinator(context);
    var leftover = (await coordinator.GetQueueIndexBloatAsync(cancellationToken)).Single(s => s.IndexName == PKEY + "_ccnew");
    await Assert.That(leftover.IsReindexLeftover).IsTrue();
    var (chosen, before) = await _mostReclaimableAsync(coordinator, cancellationToken);

    await _runStepAsync(context, coordinator, cancellationToken);

    await Assert.That(await _existsAsync(PKEY + "_ccnew", cancellationToken)).IsFalse();
    await Assert.That(await _sizeAsync(chosen, cancellationToken)).IsLessThan(before / 10);
  }

  /// <summary>
  /// The coordinator refuses to drop a valid index, an invalid one on a table that is not a queue, or a name the catalog
  /// does not know (a name only reaches the statement once the catalog has said it is an index on a queue table, and then
  /// quoted); everything is still there afterwards.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-leftover-names</docs>
  [Test]
  [Timeout(120000)]
  public async Task Drop_RefusesAValidIndex_AndOneOffTheQueueTablesAsync(CancellationToken cancellationToken) {
    await _execAsync(
      "INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES ('dup_a', 'x', 'string'), ('dup_b', 'x', 'string')",
      cancellationToken);
    await _leaveAnInvalidLeftoverAsync("wh_settings_value_ccnew", cancellationToken, "wh_settings", "setting_value");
    await using var context = CreateDbContext();
    var logger = new CapturingLogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>>();
    var coordinator = _coordinator(context, logger);

    await Assert.That(await coordinator.DropInvalidQueueIndexAsync(PKEY, cancellationToken)).IsFalse();
    await Assert.That(await coordinator.DropInvalidQueueIndexAsync("wh_settings_value_ccnew", cancellationToken)).IsFalse();
    await Assert.That(await coordinator.DropInvalidQueueIndexAsync("x; DROP TABLE wh_outbox", cancellationToken)).IsFalse();

    await Assert.That(await _existsAsync(PKEY, cancellationToken)).IsTrue();
    await Assert.That(await _existsAsync("wh_settings_value_ccnew", cancellationToken)).IsTrue();
    await Assert.That(await _existsAsync("wh_outbox", cancellationToken)).IsTrue();
    await Assert.That(logger.Messages).Contains($"Index {PKEY} was not dropped: it is valid; only an invalid leftover is dropped");
    await Assert.That(logger.Messages).Contains(
      "Index wh_settings_value_ccnew was not dropped: it is not an index on a queue table");
  }

  /// <summary>
  /// The coordinator refuses to rebuild an invalid index (it is dropped instead), one off the queue tables, or a name the
  /// catalog does not know, however it is spelled.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#queue-index-rebuild</docs>
  [Test]
  [Timeout(120000)]
  public async Task Reindex_RefusesAnInvalidIndex_AndOneOffTheQueueTablesAsync(CancellationToken cancellationToken) {
    await _leaveAnInvalidLeftoverAsync(PKEY + "_ccnew", cancellationToken);
    await using var context = CreateDbContext();
    var logger = new CapturingLogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>>();
    var coordinator = _coordinator(context, logger);

    await Assert.That(await coordinator.ReindexQueueIndexAsync(PKEY + "_ccnew", cancellationToken)).IsNull();
    await Assert.That(await coordinator.ReindexQueueIndexAsync("wh_settings_pkey", cancellationToken)).IsNull();
    await Assert.That(await coordinator.ReindexQueueIndexAsync("Bad-Name\"; DROP TABLE wh_outbox; --", cancellationToken)).IsNull();

    await Assert.That(logger.Messages).Contains($"Index {PKEY}_ccnew was not rebuilt: it is invalid; it is dropped, not rebuilt");
    await Assert.That(logger.Messages).Contains("Index wh_settings_pkey was not rebuilt: it is not an index on a queue table");
    await Assert.That(await _existsAsync("wh_outbox", cancellationToken)).IsTrue();
  }

  /// <summary>
  /// While a concurrent index build is running on a queue table, the survey says so for that table's indexes, so the
  /// step leaves the build's invalid work in progress alone. The build is held in its wait for older snapshots by an
  /// open transaction, then released.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-build-in-progress</docs>
  [Test]
  [Timeout(120000)]
  public async Task WhileABuildIsRunning_TheSurveySaysSoAsync(CancellationToken cancellationToken) {
    await using var holder = new NpgsqlConnection(ConnectionString);
    await holder.OpenAsync(cancellationToken);
    await using var hold = await holder.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
    await using (var snapshot = new NpgsqlCommand("SELECT count(*) FROM wh_settings", holder, hold)) {
      await snapshot.ExecuteScalarAsync(cancellationToken);
    }
    var build = _execAsync("CREATE INDEX CONCURRENTLY idx_held_build ON wh_active_streams (last_activity_at)", cancellationToken);
    await _untilTheBuildIsReportedAsync(cancellationToken);
    await using var context = CreateDbContext();

    var survey = await _coordinator(context).GetQueueIndexBloatAsync(cancellationToken);

    await hold.CommitAsync(cancellationToken);
    await build.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
    var activeStreams = survey.Where(s => s.TableName == "wh_active_streams").ToArray();
    await Assert.That(activeStreams).IsNotEmpty();
    await Assert.That(activeStreams.All(s => s.BuildInProgress)).IsTrue();
    await Assert.That(survey.Any(s => s.TableName == "wh_outbox" && s.BuildInProgress)).IsFalse();
  }

  /// <summary>
  /// Under a data-only application role: with the schema-initialization connection configured, its role (which owns the
  /// tables) drops and rebuilds; without it nothing is touched and the log names the table and the role.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#reindex-connection</docs>
  [Test]
  [Timeout(180000)]
  [Arguments(true)]
  [Arguments(false)]
  public async Task UnderADataOnlyApplicationRole_OnlyTheInitConnectionDropsAndRebuildsAsync(
      bool withInitConnection, CancellationToken cancellationToken) {
    await _leaveAnInvalidLeftoverAsync(PKEY + "_ccold", cancellationToken);
    var role = $"wh_app_{Guid.NewGuid():N}"[..30];
    await _execAsync($"""
      CREATE ROLE {role} LOGIN PASSWORD 'data-only';
      GRANT USAGE ON SCHEMA public TO {role};
      GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {role};
      GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO {role};
      """, cancellationToken);
    try {
      var applicationConnection = new NpgsqlConnectionStringBuilder(ConnectionString) {
        Username = role,
        Password = "data-only",
      }.ConnectionString;
      var services = new ServiceCollection();
      if (withInitConnection) {
        services.AddKeyedSingleton<SchemaInitConnection>(typeof(WorkCoordinationDbContext), new SchemaInitConnection(ConnectionString));
      }
      await using var provider = services.BuildServiceProvider();
      await using var applicationContext = new WorkCoordinationDbContext(
        new DbContextOptionsBuilder<WorkCoordinationDbContext>()
          .UseNpgsql(applicationConnection)
          .UseApplicationServiceProvider(provider)
          .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
          .Options);
      var logger = new CapturingLogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>>();
      var coordinator = _coordinator(applicationContext, logger);

      var dropped = await coordinator.DropInvalidQueueIndexAsync(PKEY + "_ccold", cancellationToken);
      var rebuilt = await coordinator.ReindexQueueIndexAsync(PKEY, cancellationToken);

      await Assert.That(dropped).IsEqualTo(withInitConnection);
      await Assert.That(rebuilt is not null).IsEqualTo(withInitConnection);
      await Assert.That(await _existsAsync(PKEY + "_ccold", cancellationToken)).IsEqualTo(!withInitConnection);
      if (!withInitConnection) {
        await Assert.That(logger.Messages).Contains(
          $"Index {PKEY} was not rebuilt: role '{role}' does not own wh_active_streams and no schema-initialization (-init) "
          + "connection is configured; configure one so the table owner runs it");
      }
    } finally {
      await _execAsync($"DROP OWNED BY {role}; DROP ROLE {role};", cancellationToken);
    }
  }

  /// <summary>
  /// A failed concurrent build leaves its index behind, invalid: a unique index over a column with duplicates, as a
  /// rebuild interrupted part way leaves its copy.
  /// </summary>
  private async Task _leaveAnInvalidLeftoverAsync(
      string name, CancellationToken cancellationToken, string table = "wh_active_streams", string column = "partition_number") {
    if (table == "wh_active_streams") {
      await _execAsync(
        "INSERT INTO wh_active_streams (stream_id, partition_number) VALUES (gen_random_uuid(), 7), (gen_random_uuid(), 7)",
        cancellationToken);
    }
    await Assert.That(async () => await _execAsync($"CREATE UNIQUE INDEX CONCURRENTLY {name} ON {table} ({column})", cancellationToken))
      .Throws<PostgresException>();
    await Assert.That(await _scalarAsync<bool>($"SELECT NOT indisvalid FROM pg_index WHERE indexrelid = to_regclass('{name}')", cancellationToken))
      .IsTrue();
  }

  /// <summary>
  /// Waits until the held build appears in <c>pg_stat_progress_create_index</c>: the exact transition the survey reads.
  /// A catalog view has nothing to subscribe to, so it is read until it says so, bounded by the test's timeout.
  /// </summary>
  private async Task _untilTheBuildIsReportedAsync(CancellationToken cancellationToken) {
    while (await _scalarAsync<long>(
      "SELECT count(*) FROM pg_stat_progress_create_index WHERE relid = to_regclass('wh_active_streams')", cancellationToken) == 0) {
      cancellationToken.ThrowIfCancellationRequested();
    }
  }

  /// <summary>
  /// The index the step should rebuild, by its documented rule (valid, over 3x, at least 1 MiB, the most bytes to give
  /// back), and its size now. Several indexes on the churned table bloat together, so the test names the one the rule
  /// picks rather than assuming it.
  /// </summary>
  private static async Task<(string Index, long SizeBytes)> _mostReclaimableAsync(
      IWorkCoordinator coordinator, CancellationToken cancellationToken) {
    var chosen = (await coordinator.GetQueueIndexBloatAsync(cancellationToken))
      .Where(s => s.Valid && s.SizeBytes >= MIB && s.Ratio > 3.0)
      .OrderByDescending(s => s.SizeBytes - s.ExpectedBytes!.Value)
      .ThenBy(s => s.IndexName, StringComparer.Ordinal)
      .First();
    return (chosen.IndexName, chosen.SizeBytes);
  }

  private static async Task _runStepAsync(
      WorkCoordinationDbContext context, IWorkCoordinator coordinator, CancellationToken cancellationToken) {
    var step = new QueueIndexReindexStep(
      Options.Create(new MaintenanceWorkerOptions()), NullLogger<QueueIndexReindexStep>.Instance);
    await using var services = new ServiceCollection()
      .AddSingleton(coordinator)
      .AddSingleton<IClaimedEmissionStore>(new EFCoreClaimedEmissionStore(context))
      .BuildServiceProvider();
    await step.RunAsync(services, cancellationToken);
  }
}
