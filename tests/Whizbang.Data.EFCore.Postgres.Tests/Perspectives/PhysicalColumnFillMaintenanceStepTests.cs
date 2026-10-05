// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Dispatch;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Issue #1009: a row an instance on the previous release writes after the promoting start, with the value
/// only in the document, has its column filled by the maintenance step.
/// </summary>
/// <remarks>
/// Each test promotes the fields the way a release does (see <see cref="PhysicalFieldPromotionTests"/>),
/// then writes rows the way the previous release still writes them: the document carries the value and the
/// column, which that release does not know, stays null.
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#rows-written-during-a-rolling-deploy</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class PhysicalColumnFillMaintenanceStepTests {
  private const string TABLE = PhysicalFieldPromotionTests.TABLE;

  private static readonly DateTimeOffset _now = new(2026, 9, 30, 12, 15, 0, TimeSpan.Zero);

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("physical_fill");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
    await PhysicalFieldPromotionTests.InitializeAsync(_connectionString);
    await PhysicalFieldPromotionTests.PromoteAsync(_connectionString);
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  /// <summary>Rows written by an instance that does not know the columns: the values only in the document.</summary>
  private Task _writeAsThePreviousReleaseAsync(int count, string prefix) =>
    PhysicalFieldPromotionTests.ExecAsync(_connectionString, $"""
      INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version)
      SELECT gen_random_uuid(), jsonb_build_object('Name', '{prefix}-' || g, 'Code', 'LATE-' || g, 'Rank', 100000 + g),
             jsonb_build_object(), jsonb_build_object(), now(), now(), now(), now(), 1
      FROM generate_series(1, {count}) AS g;
      """);

  private Task<string> _scalarAsync(string sql) => PhysicalFieldPromotionTests.ScalarAsync(_connectionString, sql);

  private Task<string> _unfilledAsync() => _scalarAsync(
    $"SELECT count(*) FROM {TABLE} WHERE (name IS NULL AND data ? 'Name') OR (code IS NULL AND data ? 'Code') OR (rank IS NULL AND data ? 'Rank')");

  private async Task _runAsync(PhysicalColumnFillMaintenanceStep step, bool withClaimStore = true) {
    await using var context = PhysicalFieldPromotionTests.Context(_connectionString);
    var services = new ServiceCollection();
    services.AddSingleton(context);
    if (withClaimStore) {
      services.AddSingleton<IClaimedEmissionStore>(new EFCoreClaimedEmissionStore(context));
    }
    await using var provider = services.BuildServiceProvider();
    await step.RunAsync(provider, CancellationToken.None);
  }

  private static PhysicalColumnFillMaintenanceStep _step(DateTimeOffset? at = null, TimeSpan? settle = null) =>
    new(typeof(PhysicalPromotionDbContext), null, new FakeTimeProvider(at ?? _now), settle: settle);

  private async Task<long> _fillAsync(int batchSize, int maxRounds, TimeSpan settle) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    return await PhysicalColumnFill.RunAsync(db, "\"public\"", batchSize, maxRounds, settle, null, CancellationToken.None);
  }

  /// <summary>
  /// The promoting start armed each column it added, and a row written afterwards with only the document
  /// value is filled by the step and then found by a filter on the column.
  /// </summary>
  [Test]
  public async Task ClaimWindow_IsTheSharedFillWindowAsync() {
    // The EF step and the Dapper step claim the same key, so they must agree on the window or a fleet running
    // both drivers would run the fill twice per window.
    await Assert.That(PhysicalColumnFillMaintenanceStep.ClaimWindow).IsEqualTo(Whizbang.Data.Postgres.PhysicalColumnFill.ClaimWindow);
  }

  [Test]
  public async Task ARowWrittenWithOnlyTheDocumentValue_IsFilledAndFoundByAColumnFilterAsync() {
    await Assert.That(await _scalarAsync("SELECT string_agg(column_name, ',' ORDER BY column_name) FROM wh_physical_column_fills WHERE direction = 'to_column'"))
      .IsEqualTo("code,name,rank");
    await _writeAsThePreviousReleaseAsync(3, "late");
    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {TABLE} WHERE name = 'late-2'")).IsEqualTo("0")
      .Because("the column is null, so a filter on it misses the row");

    await _runAsync(_step());

    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {TABLE} WHERE name = 'late-2'")).IsEqualTo("1");
    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {TABLE} WHERE rank = 100002 AND code = 'LATE-2'")).IsEqualTo("1");
    await Assert.That(await _unfilledAsync()).IsEqualTo("0");
  }

  /// <summary>A second run finds nothing to fill and changes nothing, including a column a writer set.</summary>
  [Test]
  public async Task ASecondRun_ChangesNothingAsync() {
    await _writeAsThePreviousReleaseAsync(3, "late");
    await _runAsync(_step());
    await PhysicalFieldPromotionTests.ExecAsync(_connectionString, $"UPDATE {TABLE} SET name = 'written' WHERE data ->> 'Name' = 'late-1'");

    var filled = await _fillAsync(PhysicalColumnFill.DEFAULT_BATCH_SIZE, PhysicalColumnFill.DEFAULT_MAX_ROUNDS, TimeSpan.FromDays(1));

    await Assert.That(filled).IsEqualTo(0);
    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {TABLE} WHERE name = 'written'")).IsEqualTo("1")
      .Because("a column is only ever set where it is null");
  }

  /// <summary>
  /// A column stays armed while the settle window lasts, even once nothing is left, so rows the last
  /// instance on the previous release writes are still caught; after it, a run with nothing left disarms it.
  /// </summary>
  [Test]
  public async Task AColumnStaysArmedUntilTheSettleWindowHasPassedAsync() {
    await _fillAsync(PhysicalColumnFill.DEFAULT_BATCH_SIZE, 1, TimeSpan.FromDays(1));
    await Assert.That(await _scalarAsync("SELECT count(*) FROM wh_physical_column_fills")).IsEqualTo("3");

    await _writeAsThePreviousReleaseAsync(2, "later");
    await _fillAsync(PhysicalColumnFill.DEFAULT_BATCH_SIZE, 1, TimeSpan.FromDays(1));
    await Assert.That(await _unfilledAsync()).IsEqualTo("0");

    await _fillAsync(PhysicalColumnFill.DEFAULT_BATCH_SIZE, 1, TimeSpan.Zero);
    await Assert.That(await _scalarAsync("SELECT count(*) FROM wh_physical_column_fills WHERE settled_at IS NULL")).IsEqualTo("0");
    await Assert.That(await _scalarAsync("SELECT count(*) FROM wh_physical_column_fills WHERE direction = 'recorded'")).IsEqualTo("3")
      .Because("a disarmed column is kept as the record that the framework created it for its field (#1022)");
  }

  /// <summary>One batch fills no more rows per column than its size; the rest wait for the next round.</summary>
  [Test]
  public async Task ABatchFillsNoMoreThanItsSizeAsync() {
    await _writeAsThePreviousReleaseAsync(5, "late");

    var filled = await _fillAsync(2, 1, TimeSpan.Zero);

    await Assert.That(filled).IsEqualTo(6).Because("two rows in each of three columns");
    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {TABLE} WHERE name IS NULL")).IsEqualTo("3");
    await Assert.That(await _scalarAsync("SELECT count(*) FROM wh_physical_column_fills")).IsEqualTo("3")
      .Because("a full batch means more may be left, so no column is disarmed");

    await _fillAsync(2, PhysicalColumnFill.DEFAULT_MAX_ROUNDS, TimeSpan.Zero);
    await Assert.That(await _unfilledAsync()).IsEqualTo("0");
  }

  /// <summary>Only the instance that wins the window's claim runs; another in the same window fills nothing.</summary>
  [Test]
  public async Task ASecondRunInTheSameWindow_FillsNothingAsync() {
    await _runAsync(_step());
    await _writeAsThePreviousReleaseAsync(1, "late");

    await _runAsync(_step());

    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {TABLE} WHERE name = 'late-1'")).IsEqualTo("0");
  }

  /// <summary>Without a claim store there is no way to run on one instance, so the step does not run.</summary>
  [Test]
  public async Task WithoutAClaimStore_TheStepDoesNotRunAsync() {
    await _writeAsThePreviousReleaseAsync(1, "late");

    await _runAsync(_step(), withClaimStore: false);

    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {TABLE} WHERE name = 'late-1'")).IsEqualTo("0");
  }

  /// <summary>With nothing armed the step does not claim, so a service that promotes nothing writes nothing.</summary>
  [Test]
  public async Task WithNothingArmed_TheStepDoesNotClaimAsync() {
    await PhysicalFieldPromotionTests.ExecAsync(_connectionString, "DELETE FROM wh_physical_column_fills");

    await _runAsync(_step());

    await Assert.That(await _scalarAsync(
      "SELECT count(*) FROM wh_unique_emission_claims WHERE claim_key LIKE 'whizbang:physical-column-fill:%'")).IsEqualTo("0");
  }

  /// <summary>A column whose table is gone is disarmed rather than retried forever.</summary>
  [Test]
  public async Task AColumnWhoseTableIsGone_IsDisarmedAsync() {
    await PhysicalFieldPromotionTests.ExecAsync(_connectionString,
      "INSERT INTO wh_physical_column_fills (table_name, column_name, json_key, extraction) VALUES ('public.wh_per_gone', 'x', 'X', '(data ->> ''X'')')");

    await _fillAsync(PhysicalColumnFill.DEFAULT_BATCH_SIZE, 1, TimeSpan.FromDays(1));

    await Assert.That(await _scalarAsync("SELECT count(*) FROM wh_physical_column_fills WHERE table_name = 'public.wh_per_gone'")).IsEqualTo("0");
  }

  /// <summary>
  /// A document value the column's type cannot take (a rank written as text) fails that column's batch only:
  /// it is reported, the column stays armed for an operator, and the other columns are still filled.
  /// </summary>
  [Test]
  public async Task AValueTheColumnCannotTake_FailsOnlyThatColumn_WhichStaysArmedAsync() {
    await PhysicalFieldPromotionTests.ExecAsync(_connectionString, $"""
      INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version)
      VALUES (gen_random_uuid(), jsonb_build_object('Name', 'odd', 'Code', 'ODD', 'Rank', 'not-a-number'),
              jsonb_build_object(), jsonb_build_object(), now(), now(), now(), now(), 1);
      """);

    var filled = await _fillAsync(PhysicalColumnFill.DEFAULT_BATCH_SIZE, 1, TimeSpan.Zero);

    await Assert.That(filled).IsEqualTo(2).Because("the name and code columns are filled; the rank batch failed");
    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {TABLE} WHERE name = 'odd' AND code = 'ODD' AND rank IS NULL")).IsEqualTo("1");
    await Assert.That(await _scalarAsync("SELECT string_agg(column_name, ',') FROM wh_physical_column_fills WHERE settled_at IS NULL")).IsEqualTo("rank")
      .Because("a failed column is not disarmed, so the failure keeps being reported until it is dealt with");
  }

  /// <summary>The Postgres driver registers the step, so every host with the driver runs it.</summary>
  [Test]
  public async Task ThePostgresDriver_RegistersTheStepAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<PhysicalColumnFillMaintenanceStep>().ToList();

    await Assert.That(steps.Count).IsEqualTo(1);
    await Assert.That(steps.Single().Name).IsEqualTo("physical-column-fill");
  }
}
