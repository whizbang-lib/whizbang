// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// #2092: the paced backfill's index on <c>wh_event_store</c> is built concurrently after the migrations, never inside a
/// migration's transaction, so writes to the event store go on while it builds. The build runs once and is idempotent;
/// it steps aside while another session builds; an invalid index an interrupted build left is dropped and rebuilt; and a
/// failed build leaves nothing invalid behind. The maintenance step runs it.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/ConcurrentIndexBuild.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/FrameworkIndexBuildMaintenanceStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/334_PacedSubscriptionBackfill.sql</code-under-test>
[Category("Shard1")]
[Category("Integration")]
public partial class ConcurrentIndexBuildTests : EFCoreTestBase {
  private const string INDEX = "idx_event_store_type_commit_sequence";

  [Test]
  public async Task NoMigration_BuildsTheBackfillIndex_InsideItsTransactionAsync() {
    var builders = new PostgresMigrationProvider().GetMigrations()
      .Where(m => _createsTheIndex().IsMatch(_stripLineComments(m.Sql)))
      .Select(m => m.Name)
      .ToList();

    await Assert.That(builders).IsEmpty()
      .Because("a CREATE INDEX inside a migration's transaction blocks every write to the event store for the whole build");
  }

  [Test]
  public async Task AfterTheMigrations_TheIndexIsNotThereYet_AndTheMaintenanceStepBuildsItAsync() {
    await using var conn = await _openAsync();
    var before = await _validityAsync(conn, INDEX);

    await _runStepAsync();

    await Assert.That(before).IsNull().Because("the migrations leave the build to the step");
    await Assert.That(await _validityAsync(conn, INDEX)).IsEqualTo(true);
    await Assert.That(ConcurrentIndexBuild.FrameworkIndexes.Select(i => i.Name)).Contains(INDEX);
  }

  [Test]
  public async Task Ensure_BuildsOnce_ThenFindsItValidAsync() {
    await using var conn = await _openAsync();
    var index = _definition();

    var first = await ConcurrentIndexBuild.EnsureAsync(conn, "public", index, 120);
    var second = await ConcurrentIndexBuild.EnsureAsync(conn, "public", index, 120);

    await Assert.That(first).IsEqualTo(ConcurrentIndexOutcome.Built);
    await Assert.That(second).IsEqualTo(ConcurrentIndexOutcome.AlreadyValid).Because("a restart finds the index and builds nothing");
    await Assert.That(await _validityAsync(conn, INDEX)).IsEqualTo(true);
    await Assert.That(await _scalarAsync(conn, $"SELECT obj_description('public.{INDEX}'::regclass, 'pg_class')"))
      .IsEqualTo(index.Comment);
  }

  [Test]
  public async Task Ensure_AnInvalidLeftoverOfAnInterruptedBuild_IsDroppedAndRebuiltAsync() {
    await using var conn = await _openAsync();
    var index = _definition();
    await _executeAsync(conn, $"CREATE INDEX {INDEX} ON wh_event_store (event_type, commit_sequence)");
    await _executeAsync(conn, $"UPDATE pg_index SET indisvalid = false WHERE indexrelid = 'public.{INDEX}'::regclass");

    var outcome = await ConcurrentIndexBuild.EnsureAsync(conn, "public", index, 120);

    await Assert.That(outcome).IsEqualTo(ConcurrentIndexOutcome.Built)
      .Because("an invalid index under the name would pass for the index forever and serve no read");
    await Assert.That(await _validityAsync(conn, INDEX)).IsEqualTo(true);
    await Assert.That(await _scalarAsync(conn, $"SELECT pg_get_indexdef('public.{INDEX}'::regclass)") as string)
      .Contains("WHERE ((commit_sequence IS NOT NULL) AND (event_type IS NOT NULL))");
  }

  [Test]
  [Timeout(120_000)]
  public async Task Ensure_WhileAnotherBuildIsInProgressOnTheTable_StepsAside_AndLeavesItsIndexAloneAsync(CancellationToken testToken) {
    await using var conn = await _openAsync();
    await using var blocker = await _openAsync();
    await using var builder = await _openAsync();
    // An open snapshot holds a concurrent build in its wait phase, with its index already in the catalog, invalid.
    await using var snapshot = await blocker.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, testToken);
    await using (var read = new NpgsqlCommand("SELECT count(*) FROM wh_event_store", blocker, snapshot)) {
      await read.ExecuteScalarAsync(testToken);
    }
    await using var build = new NpgsqlCommand($"CREATE INDEX CONCURRENTLY {INDEX} ON wh_event_store (event_type, commit_sequence)", builder) {
      CommandTimeout = 0,
    };
    var building = build.ExecuteNonQueryAsync(testToken);
    // Waits on the other build's index reaching the catalog, invalid: the state this test is about. Its progress row
    // appears earlier, before the build commits that catalog entry, so waiting on it read the index back as absent.
    while (await _validityAsync(conn, INDEX) is not false) {
      testToken.ThrowIfCancellationRequested();
      await Task.Yield();
    }

    var outcome = await ConcurrentIndexBuild.EnsureAsync(conn, "public", _definition(), 120, cancellationToken: testToken);
    var during = await _validityAsync(conn, INDEX);
    await snapshot.CommitAsync(testToken);
    await building;

    await Assert.That(outcome).IsEqualTo(ConcurrentIndexOutcome.Busy);
    await Assert.That(during).IsEqualTo(false)
      .Because("an invalid index during another build is its work in progress, never a leftover to drop");
    await Assert.That(await _validityAsync(conn, INDEX)).IsEqualTo(true).Because("the other build finished undisturbed");
  }

  /// <summary>
  /// A build in progress in another database does not make this one step aside, even on a table with the same
  /// relation id.
  /// </summary>
  /// <remarks>
  /// <c>pg_stat_progress_create_index</c> lists every database's builds, and a relation id is unique only within its
  /// database: two databases created from one template (every copy of the test template, or databases cloned for
  /// tenants) give the same table the same id. Matched on the id alone, a sibling database's build read as this one's,
  /// and the index here was never built while the sibling's ran.
  /// </remarks>
  [Test]
  [Timeout(120_000)]
  public async Task Ensure_ABuildInAnotherDatabaseOnTheSameRelationId_DoesNotStepAsideAsync(CancellationToken testToken) {
    var sibling = await EFCoreSchemaTemplate.CreateDatabaseAsync("idxsibling", testToken);
    try {
      await using var conn = await _openAsync();
      await using var blocker = new NpgsqlConnection(sibling.ConnectionString);
      await blocker.OpenAsync(testToken);
      await using var builder = new NpgsqlConnection(sibling.ConnectionString);
      await builder.OpenAsync(testToken);
      // Watches the sibling's catalog from outside the held snapshot, which would never see the index arrive.
      await using var watcher = new NpgsqlConnection(sibling.ConnectionString);
      await watcher.OpenAsync(testToken);
      var oidHere = await _scalarAsync(conn, "SELECT 'wh_event_store'::regclass::oid");
      await Assert.That(await _scalarAsync(blocker, "SELECT 'wh_event_store'::regclass::oid")).IsEqualTo(oidHere)
        .Because("the sibling is a copy of the same template, so its table has the same relation id");

      // The sibling's build held in its wait phase by an open snapshot in the sibling, as in the in-progress test.
      await using var snapshot = await blocker.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, testToken);
      await using (var read = new NpgsqlCommand("SELECT count(*) FROM wh_event_store", blocker, snapshot)) {
        await read.ExecuteScalarAsync(testToken);
      }
      await using var build = new NpgsqlCommand(
        $"CREATE INDEX CONCURRENTLY {INDEX} ON wh_event_store (event_type, commit_sequence)", builder) { CommandTimeout = 0 };
      var building = build.ExecuteNonQueryAsync(testToken);
      while (await _validityAsync(watcher, INDEX) is not false) {
        testToken.ThrowIfCancellationRequested();
        await Task.Yield();
      }

      var outcome = await ConcurrentIndexBuild.EnsureAsync(conn, "public", _definition(), 120, cancellationToken: testToken);
      await snapshot.CommitAsync(testToken);
      await building;

      await Assert.That(outcome).IsEqualTo(ConcurrentIndexOutcome.Built)
        .Because("a build in another database is not work in progress on this database's table");
      await Assert.That(await _validityAsync(conn, INDEX)).IsEqualTo(true);
    } finally {
      await PerTestDatabaseFactory.DropAsync(sibling.Name, cancellationToken: CancellationToken.None);
    }
  }

  [Test]
  public async Task Ensure_ABuildThatFails_LeavesNoInvalidIndexBehindAsync() {
    await using var conn = await _openAsync();
    await _executeAsync(conn, $"""
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, commit_sequence)
      VALUES ('{TrackedGuid.New().Value}', '{TrackedGuid.New().Value}', '{TrackedGuid.New().Value}', 'T', 'Contracts.Failing', 1, 1)
      """);
    // Dividing by zero for every row: the concurrent build creates the index, then fails reading the table.
    var failing = new ConcurrentIndexDefinition("wh_event_store", "idx_event_store_failing_build", "(event_type)", "1 / (version - version) = 0");

    var outcome = await ConcurrentIndexBuild.EnsureAsync(conn, "public", failing, 120);

    await Assert.That(outcome).IsEqualTo(ConcurrentIndexOutcome.Failed);
    await Assert.That(await _validityAsync(conn, failing.Name)).IsNull()
      .Because("a failed concurrent build leaves an invalid index that a later IF NOT EXISTS would take for the index");
  }

  [Test]
  public async Task Ensure_AnIndexWithNoPredicateOrComment_IsBuiltPlainAsync() {
    await using var conn = await _openAsync();
    var plain = new ConcurrentIndexDefinition("wh_event_store", "idx_event_store_plain_build", "(stream_id)", null);

    var outcome = await ConcurrentIndexBuild.EnsureAsync(conn, "public", plain, 120);

    await Assert.That(outcome).IsEqualTo(ConcurrentIndexOutcome.Built);
    await Assert.That(await _scalarAsync(conn, $"SELECT pg_get_indexdef('public.{plain.Name}'::regclass)") as string)
      .DoesNotContain("WHERE");
  }

  [Test]
  public async Task Ensure_ABuildRefusedOutright_IsFailed_AndLeavesNothingAsync() {
    await using var conn = await _openAsync();
    var refused = new ConcurrentIndexDefinition("wh_event_store", "idx_event_store_refused_build", "(no_such_column)", null);

    var outcome = await ConcurrentIndexBuild.EnsureAsync(conn, "public", refused, 120);

    await Assert.That(outcome).IsEqualTo(ConcurrentIndexOutcome.Failed);
    await Assert.That(await _validityAsync(conn, refused.Name)).IsNull();
  }

  [Test]
  public async Task Ensure_WithoutTheTable_BuildsNothingAsync() {
    await using var conn = await _openAsync();

    var outcome = await ConcurrentIndexBuild.EnsureAsync(
      conn, "public", new ConcurrentIndexDefinition("wh_no_such_table", "idx_no_such_table", "(id)", null), 120);

    await Assert.That(outcome).IsEqualTo(ConcurrentIndexOutcome.TableAbsent);
  }

  [Test]
  public async Task ThePostgresDriver_RegistersTheStepAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<FrameworkIndexBuildMaintenanceStep>().ToList();

    await Assert.That(steps).Count().IsEqualTo(1);
    await Assert.That(steps[0].Name).IsEqualTo("framework-index-build");
  }

  [Test]
  public async Task TheStep_RefusesWhatItCannotRunAsync() {
    await Assert.That(() => new FrameworkIndexBuildMaintenanceStep(null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await new FrameworkIndexBuildMaintenanceStep(typeof(WorkCoordinationDbContext))
      .RunAsync(null!, CancellationToken.None)).Throws<ArgumentNullException>();
    await using var conn = await _openAsync();
    await Assert.That(async () => await ConcurrentIndexBuild.EnsureAsync(null!, "public", _definition(), 1)).Throws<ArgumentNullException>();
    await Assert.That(async () => await ConcurrentIndexBuild.EnsureAsync(conn, "public", null!, 1)).Throws<ArgumentNullException>();
    await Assert.That(async () => await ConcurrentIndexBuild.EnsureAsync(conn, " ", _definition(), 1)).Throws<ArgumentException>();
  }

  private static ConcurrentIndexDefinition _definition() => ConcurrentIndexBuild.FrameworkIndexes.Single(i => i.Name == INDEX);

  private async Task _runStepAsync() {
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    await new FrameworkIndexBuildMaintenanceStep(typeof(WorkCoordinationDbContext)).RunAsync(scope.ServiceProvider, CancellationToken.None);
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task<bool?> _validityAsync(NpgsqlConnection conn, string name) =>
    await _scalarAsync(conn, $"SELECT i.indisvalid FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid WHERE c.relname = '{name}'") as bool?;

  private static async Task<object?> _scalarAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = new NpgsqlCommand(sql, conn);
    var value = await cmd.ExecuteScalarAsync();
    return value is DBNull ? null : value;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = new NpgsqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync();
  }

  private static string _stripLineComments(string sql) => _lineComment().Replace(sql, string.Empty);

  [GeneratedRegex(@"CREATE\s+(UNIQUE\s+)?INDEX[^;]*\bidx_event_store_type_commit_sequence\b", RegexOptions.IgnoreCase)]
  private static partial Regex _createsTheIndex();

  [GeneratedRegex(@"--[^\n]*")]
  private static partial Regex _lineComment();
}
