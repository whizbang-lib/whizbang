// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Testing.Containers;

namespace Whizbang.Testing.Tests;

/// <summary>
/// <see cref="QueryPlan.CaptureAsync"/> against a real planner. The parsing and the rules are covered without a
/// database in <see cref="QueryPlanTests"/>; what only Postgres can confirm is that the captured JSON is the shape
/// the parser expects, that a bound parameter reaches the planner, and that the rules fire on a plan nobody wrote
/// by hand.
/// </summary>
/// <remarks>
/// The table is deliberately large enough that the planner prefers an index for the selective lookup and a scan
/// for the unindexed one. At a few dozen rows it would choose a sequential scan for both, and the test would pass
/// for the wrong reason.
/// </remarks>
public class QueryPlanCaptureTests {

  private static async Task<NpgsqlConnection> _openSeededAsync() {
    await SharedPostgresContainer.InitializeOrSkipAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("queryplan");
    var connection = new NpgsqlConnection(database.ConnectionString);
    await connection.OpenAsync();

    await using var seed = connection.CreateCommand();
    seed.CommandText = """
      CREATE TABLE plan_probe (id bigint PRIMARY KEY, lane int NOT NULL, note text NOT NULL);
      INSERT INTO plan_probe (id, lane, note)
        SELECT g, g % 7, 'n' || g FROM generate_series(1, 50000) AS g;
      CREATE INDEX idx_plan_probe_lane ON plan_probe (lane);
      ANALYZE plan_probe;
      """;
    await seed.ExecuteNonQueryAsync();
    return connection;
  }

  [Test]
  public async Task CaptureAsync_AnIndexedLookup_ReadsTheIndexAndStaysProportionateAsync() {
    await using var connection = await _openSeededAsync();

    var plan = await QueryPlan.CaptureAsync(
      connection,
      "SELECT id, note FROM plan_probe WHERE id = @probe_id",
      new Dictionary<string, object?> { ["probe_id"] = 4242L });

    plan.MustNotSequentiallyScan("plan_probe")
        .MustNotAmplifyBeyond(500);

    await Assert.That(plan.ActualRows).IsEqualTo(1L);
    await Assert.That(plan.Json).Contains("Plan")
      .Because("the raw plan travels with the result so a failure can quote what the planner chose, rather than "
        + "sending the reader back to the database to reproduce it.");
  }

  [Test]
  public async Task CaptureAsync_AnUnindexedPredicate_IsCaughtAsASequentialScanAsync() {
    await using var connection = await _openSeededAsync();

    var plan = await QueryPlan.CaptureAsync(connection, "SELECT count(*) FROM plan_probe WHERE note = 'n4242'");

    await Assert.That(() => plan.MustNotSequentiallyScan("plan_probe"))
      .Throws<QueryPlanAssertionException>()
      .Because("note carries no index, so this is the case the harness exists to fail on - and it must fail "
        + "against a plan Postgres produced, not only against a fixture.");
  }

  [Test]
  public async Task CaptureAsync_MeasuresBuffersSoACeilingCanBeStatedAsync() {
    await using var connection = await _openSeededAsync();

    var scan = await QueryPlan.CaptureAsync(connection, "SELECT count(*) FROM plan_probe WHERE note LIKE 'n1%'");
    var lookup = await QueryPlan.CaptureAsync(
      connection, "SELECT note FROM plan_probe WHERE id = @probe_id",
      new Dictionary<string, object?> { ["probe_id"] = 99L });

    await Assert.That(scan.SharedBuffersVisited).IsGreaterThan(lookup.SharedBuffersVisited)
      .Because("BUFFERS has to actually arrive for a ceiling to mean anything; a scan of 50,000 rows must measure "
        + "as more work than a primary-key lookup, or the capture is not requesting it.");
    lookup.MustVisitAtMostSharedBuffers(scan.SharedBuffersVisited);
  }
}
