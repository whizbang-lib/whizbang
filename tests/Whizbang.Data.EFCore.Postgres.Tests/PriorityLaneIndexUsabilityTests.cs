using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Priority;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Each priority lane's partial index must be usable by the lane query that exists to read it. A partial index is
/// only considered when Postgres can prove the query's predicate implies the index's, and that proof is textual, not
/// arithmetic: it does not know an integer greater than 199 is an integer at least 200. So an index declared one way
/// and queried the other is silently dead, the planner falls back to a broader index, and the lane pays a filter and
/// a sort over every pending row instead of reading exactly its own band in order. That is invisible in any
/// correctness test, because the rows returned are identical either way.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#the-claim</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/150_BucketAwareClaim.sql</code-under-test>
[Category("Shard1")]
public class PriorityLaneIndexUsabilityTests : EFCoreTestBase {
  // Deliberately not re-declared here. WorkPriority is the one place the bands are defined; a test that wrote its
  // own 99 and 199 would be a fourth copy of the very literals these cases exist to keep in agreement.
  private const int _interactiveBandEnd = WorkPriority.INTERACTIVE_BAND_END;
  private const int _standardBandEnd = WorkPriority.STANDARD_BAND_END;
  private const int _backgroundBandEnd = WorkPriority.BACKGROUND_BAND_END;

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  /// <summary>Seeds pending rows spread across all four bands, so every lane index has rows to offer.</summary>
  private static async Task _seedEveryBandAsync(NpgsqlConnection conn) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = """
      WITH m AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, received_at,
           stream_id, is_event, priority)
        SELECT gen_random_uuid(), 'TestHandler', 'TestEvent', '{"p": {}}', '{}',
               NOW() - (g * INTERVAL '1 second'), gen_random_uuid(), TRUE,
               CASE g % 4 WHEN 0 THEN 50 WHEN 1 THEN 150 WHEN 2 THEN 250 ELSE 450 END
        FROM generate_series(1, 600) g
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts,
         partition_number, instance_id, lease_expiry, error, failure_reason)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 0, 0, NULL, NULL, NULL, 99
      FROM m
      """;
    await ins.ExecuteNonQueryAsync();
    await using var analyze = conn.CreateCommand();
    analyze.CommandText = "ANALYZE wh_inbox, wh_inbox_state";
    await analyze.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// The plan for one lane's predicate, with sequential scans disabled so the answer is which index the planner
  /// considers usable rather than which access path is cheapest on a small table.
  /// </summary>
  private static async Task<string> _planForAsync(NpgsqlConnection conn, string bandPredicate, string orderBy) {
    // Session scope, not SET LOCAL: each command here runs in its own implicit transaction, and SET LOCAL outside a
    // transaction block is silently discarded. With sequential scans merely expensive rather than forbidden, a
    // 600-row table plans as a scan whatever the indexes say, and the test proves nothing.
    await using var off = conn.CreateCommand();
    off.CommandText = "SET enable_seqscan = off";
    await off.ExecuteNonQueryAsync();

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      EXPLAIN SELECT i.stream_id, i.received_at, i.message_id
      FROM wh_inbox_state i
      WHERE i.processed_at IS NULL
        AND (i.instance_id IS NULL OR i.lease_expiry < NOW())
        AND (i.scheduled_for IS NULL OR i.scheduled_for <= NOW())
        AND {bandPredicate}
      ORDER BY {orderBy} LIMIT 100
      """;
    var plan = new System.Text.StringBuilder();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      plan.AppendLine(reader.GetString(0));
    }
    return plan.ToString();
  }

  /// <summary>
  /// The shape the claim actually issues, not a lane predicate written for the test.
  /// </summary>
  /// <remarks>
  /// Every other case here builds its own <c>priority &gt; 199</c> style predicate and proves the index is usable BY
  /// THAT QUERY. That is a property of the index, not of the claim, and the two came apart: the claim selects a band
  /// by comparing a CASE expression to a bucket column joined in from a VALUES list, and Postgres cannot prove
  /// <c>CASE ... END = b.bucket</c> implies <c>priority &lt;= 99</c> when the bucket is a join column rather than a
  /// constant. The partial indexes are then unusable, the lanes fall back to scanning the whole pending set once per
  /// bucket, and the indexes stay on the table costing every claim write and earning nothing. Measured on 300k rows:
  /// 11,102 buffers and 5,249ms against 5 buffers and under 20ms -- measured with the held-lane index present, the
  /// only fair comparison, since the old shape reached THAT index and read the whole unowned set per bucket rather
  /// than reaching no index at all. The deployed fleet showed the three band indexes at zero scans across a whole
  /// bulk import while the table carried them.
  /// </remarks>
  [Test]
  public async Task TheClaimsLaneSelection_IsWrittenSoAnIndexCanMatchIt_NotAsAComputedBucketAsync() {
    // Read from the shipped migration, never copied: a copy of the claim's shape is a second shape, and it
    // passes happily once the real one moves. Identity substitution leaves the corpus verbatim. The bounds
    // are compared against WorkPriority, so the claim and the framework cannot drift apart.
    // The LAST migration that defines this CTE, not a numbered one. A corpus has last-word semantics: the shape
    // that runs is the one defined latest, so a rule pinned to "162_" keeps passing against a definition the
    // database no longer has the moment a later migration redefines it. 167 did exactly that.
    var claim = new Whizbang.Data.Postgres.PostgresMigrationProvider(
        typeof(Whizbang.Data.Postgres.PostgresMigrationProvider).Assembly, "__SCHEMA__")
      .GetMigrations()
      .Where(m => m.Sql.Contains("claimable AS MATERIALIZED", StringComparison.Ordinal))
      .OrderBy(m => int.Parse(m.Name.Split('_')[0], System.Globalization.CultureInfo.InvariantCulture))
      .Last().Sql;

    var start = claim.IndexOf("claimable AS MATERIALIZED", StringComparison.Ordinal);
    await Assert.That(start).IsGreaterThan(-1)
      .Because("the CTE this rule is about has to be findable, or the rule asserts nothing.");
    var cte = claim[start..(start + 4000)];

    await Assert.That(cte).DoesNotContain("= b.bucket")
      .Because("selecting a band by comparing a CASE expression to a bucket column joined in from a VALUES "
        + "list is not provable against any partial index predicate, because the bucket is a join column and "
        + "not a constant. Every band index is then unusable and each lane reads the whole pending set.");

    await Assert.That(cte).Contains($"i.priority <= {_interactiveBandEnd}")
      .Because("the interactive lane has to name its bound as a literal the planner can match to the index.");
    await Assert.That(cte).Contains($"i.priority <= {_standardBandEnd}");
    await Assert.That(cte).Contains($"i.priority > {_standardBandEnd}");
    await Assert.That(cte).Contains($"i.priority <= {_backgroundBandEnd}")
      .Because("the background lane names its upper bound as a literal too, or it selects idle rows as well and "
        + "stops matching its own index.");
    await Assert.That(cte).Contains($"i.priority > {_backgroundBandEnd}")
      .Because("the idle lane names its bound as a literal the planner can match to the idle index.");
  }


  /// <summary>
  /// The background lane. Its query says "greater than the standard band end and no greater than the background
  /// band end" and its index must say the same, or the index it exists for is never consulted and the lane reads
  /// the whole pending set through a broader index.
  /// </summary>
  /// <remarks>
  /// The upper bound arrived with the idle band (167). Without it the background lane's predicate also selects
  /// idle rows, so a band that exists to be withheld while the service is busy would be drained by the lane above
  /// it -- and, because a predicate of <c>priority &gt; 199</c> cannot be proved to imply an index declared
  /// <c>priority &gt; 199 AND priority &lt;= 399</c>, the lane would additionally stop reaching its own index.
  /// </remarks>
  [Test]
  public async Task BackgroundLane_ReadsItsOwnIndex_NotTheWholePendingSetAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _seedEveryBandAsync(conn);

    var plan = await _planForAsync(conn,
      $"i.is_event = TRUE AND i.priority > {_standardBandEnd} AND i.priority <= {_backgroundBandEnd}",
      "i.received_at, i.message_id");

    await Assert.That(plan).Contains("idx_inbox_state_pending_arrival_background")
      .Because($"the background lane is where a bulk load's rows sit; an index it cannot use leaves the claim filtering and sorting every pending row. Plan was:\n{plan}");
  }

  [Test]
  public async Task StandardLane_ReadsItsOwnIndexAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _seedEveryBandAsync(conn);

    var plan = await _planForAsync(conn,
      $"i.is_event = TRUE AND i.priority BETWEEN {_interactiveBandEnd + 1} AND {_standardBandEnd}",
      "i.received_at, i.message_id");

    await Assert.That(plan).Contains("idx_inbox_state_pending_arrival_standard").Because($"plan was:\n{plan}");
  }

  [Test]
  public async Task InteractiveLane_ReadsItsOwnIndexAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _seedEveryBandAsync(conn);

    var plan = await _planForAsync(conn,
      $"i.priority <= {_interactiveBandEnd}",
      "i.stream_id, i.received_at, i.message_id");

    await Assert.That(plan).Contains("idx_inbox_state_pending_interactive").Because($"plan was:\n{plan}");
  }

  /// <summary>
  /// The idle lane (167). It is the band the claim withholds while the service is busy, which is precisely why it
  /// needs its own index: the rows sit pending for as long as the service stays busy, and a lane that cannot reach
  /// an index pays for that backlog on every poll that goes looking for it.
  /// </summary>
  [Test]
  public async Task IdleLane_ReadsItsOwnIndexAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _seedEveryBandAsync(conn);

    var plan = await _planForAsync(conn,
      $"i.is_event = TRUE AND i.priority > {_backgroundBandEnd}",
      "i.received_at, i.message_id");

    await Assert.That(plan).Contains("idx_inbox_state_pending_arrival_idle").Because($"plan was:\n{plan}");
  }

  /// <summary>
  /// The rule the three cases above enforce, stated directly against the catalog: every lane index's predicate is
  /// written with the same operator and constant the claim uses, so a later edit to either side that breaks the
  /// pairing fails here rather than silently costing a scan per claim.
  /// </summary>
  [Test]
  public async Task EveryLaneIndexPredicate_IsWrittenTheWayTheClaimQueriesItAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT indexname, substring(indexdef from position('WHERE' in indexdef))
      FROM pg_indexes WHERE tablename = 'wh_inbox_state' AND indexname LIKE 'idx_inbox_state_pending_%'
      """;
    var predicates = new Dictionary<string, string>(StringComparer.Ordinal);
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        predicates[reader.GetString(0)] = reader.GetString(1);
      }
    }

    await Assert.That(predicates["idx_inbox_state_pending_arrival_background"]).Contains($"priority > {_standardBandEnd}")
      .Because("claim_orphaned_inbox's background lane filters with 'priority > c_standard_band_end'; an index declaring the same set as 'priority >= 200' cannot be matched to it");
    await Assert.That(predicates["idx_inbox_state_pending_interactive"]).Contains($"priority <= {_interactiveBandEnd}");
    await Assert.That(predicates["idx_inbox_state_pending_arrival_standard"]).Contains($"priority >= {_interactiveBandEnd + 1}");
    await Assert.That(predicates["idx_inbox_state_pending_arrival_standard"]).Contains($"priority <= {_standardBandEnd}");
    await Assert.That(predicates["idx_inbox_state_pending_arrival_background"]).Contains($"priority <= {_backgroundBandEnd}")
      .Because("167 bounded the background lane at the top so the idle band is not drained by the lane above it; "
        + "an index left open-ended no longer describes the set the lane asks for");
    await Assert.That(predicates["idx_inbox_state_pending_arrival_idle"]).Contains($"priority > {_backgroundBandEnd}")
      .Because("the idle lane filters with 'priority > c_background_band_end' and its index must say the same");
  }

  /// <summary>
  /// The third place the bands are written. Index DDL cannot reference a function's constants and neither can
  /// reference the C# ones, so the boundaries exist as literals in three independent places: WorkPriority, the
  /// claim function's CONSTANT declarations, and the partial index predicates. There is no textual way to share
  /// them, which leaves this: the deployed function is read back and pinned to the C# definition, so a change to
  /// one that is not made in the others fails here instead of quietly costing a scan on every claim.
  /// </summary>
  [Test]
  public async Task TheClaimFunctionsBandConstants_MatchTheFrameworkDefinitionAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT prosrc FROM pg_proc WHERE proname = 'claim_orphaned_inbox' LIMIT 1";
    var source = (string?)await cmd.ExecuteScalarAsync() ?? string.Empty;

    await Assert.That(source).Contains($"c_interactive_band_end CONSTANT INTEGER := {_interactiveBandEnd};")
      .Because("the claim's band constants and WorkPriority describe one set of bands; if they disagree the lanes and the buckets the rest of the framework reports stop meaning the same thing");
    await Assert.That(source).Contains($"c_standard_band_end CONSTANT INTEGER := {_standardBandEnd};");
  }
}
