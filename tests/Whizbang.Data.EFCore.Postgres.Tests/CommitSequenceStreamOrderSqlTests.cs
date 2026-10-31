// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// What <c>commit_sequence</c> promises once rows are numbered in insertion order (#1225, migration 197): within a
/// stream, the sequence follows the stream's versions; across streams, rows that become eligible together are numbered
/// in the order they were inserted; and a drain costs what it stamps (#1062).
/// </summary>
/// <remarks>
/// <para>
/// The stamper used to number every eligible row in transaction-id order. A transaction's id is assigned at its first
/// write, not when it takes a stream's lock, so a transaction that wrote something else first carries a LOWER id than
/// one that appended to the same stream before it and committed first. Numbered by id, the later version of the stream
/// received the lower sequence, and every reader that walks a stream in <c>commit_sequence</c> order (the perspective
/// fetch, collective replay, the outbox publish order) saw the stream out of order.
/// </para>
/// <para>
/// Each transaction id is pinned with <c>pg_current_xact_id()</c> before the append, which is exactly what an earlier
/// write in the same transaction does.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/197_CommitSequenceInsertionOrder.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/248_StampReadsOnlyTheBatchItStamps.sql</code-under-test>
[Category("Shard1")]
public class CommitSequenceStreamOrderSqlTests : EFCoreTestBase {

  [Test]
  public async Task Stamp_ALowerIdTransactionAppendsAfterAHigherOne_TheStreamKeepsItsVersionOrderAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var early = await dataSource.OpenConnectionAsync();
    await using var late = await dataSource.OpenConnectionAsync();
    var stream = Guid.CreateVersion7();
    var version1 = Guid.CreateVersion7();
    var version2 = Guid.CreateVersion7();

    // The early transaction takes its id first (as any earlier write would), then waits.
    await _execAsync(early, "BEGIN");
    var earlyXid = await _xidAsync(early);
    // The late transaction takes a higher id, appends version 1 and commits.
    await _execAsync(late, "BEGIN");
    var lateXid = await _xidAsync(late);
    await _appendAsync(late, version1, stream, version: 1);
    await _execAsync(late, "COMMIT");
    // Only now does the early transaction append version 2, after version 1 is committed.
    await _appendAsync(early, version2, stream, version: 2);
    await _execAsync(early, "COMMIT");
    await Assert.That(earlyXid).IsLessThan(lateXid);

    var stamped = await _stampAsync(early, 1000);

    await Assert.That(stamped).IsEqualTo(2);
    await Assert.That(await _stampedAsync(early, version1)).IsLessThan(await _stampedAsync(early, version2))
      .Because("both rows were stamped in one batch, and version 2 was appended after version 1 committed. Numbered "
        + "by transaction id, version 2 carried the lower id and received the lower sequence, so the stream read "
        + "backwards; numbered in insertion order it cannot");
  }

  [Test]
  public async Task Stamp_TheEarlierVersionIsNotYetEligible_TheLaterOneWaitsForItAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var early = await dataSource.OpenConnectionAsync();
    await using var bystander = await dataSource.OpenConnectionAsync();
    await using var late = await dataSource.OpenConnectionAsync();
    await using var stamper = await dataSource.OpenConnectionAsync();
    var stream = Guid.CreateVersion7();
    var version1 = Guid.CreateVersion7();
    var version2 = Guid.CreateVersion7();

    // Ids in this order: early < bystander < late. The bystander stays open, so the stamper's horizon sits between
    // the early transaction's id and the late one's.
    await _execAsync(early, "BEGIN");
    _ = await _xidAsync(early);
    await _execAsync(bystander, "BEGIN");
    _ = await _xidAsync(bystander);
    await _execAsync(late, "BEGIN");
    _ = await _xidAsync(late);
    await _appendAsync(late, version1, stream, version: 1);
    await _execAsync(late, "COMMIT");
    await _appendAsync(early, version2, stream, version: 2);
    await _execAsync(early, "COMMIT");

    var whileOpen = await _stampAsync(stamper, 1000);

    await Assert.That(whileOpen).IsEqualTo(0)
      .Because("version 2's transaction is below the horizon and version 1's is not. Stamping version 2 now would give "
        + "it a lower sequence than version 1 receives later: the same inversion, split across two calls");
    await Assert.That(await _sequenceAsync(stamper, version2)).IsNull();

    await _execAsync(bystander, "ROLLBACK");
    var afterwards = await _stampAsync(stamper, 1000);

    await Assert.That(afterwards).IsEqualTo(2);
    await Assert.That(await _stampedAsync(stamper, version1)).IsLessThan(await _stampedAsync(stamper, version2));
  }

  [Test]
  public async Task Stamp_OneTransactionInsertsAStreamOutOfVersionOrder_TheSequenceStillFollowsTheVersionsAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var stream = Guid.CreateVersion7();
    var ids = Enumerable.Range(0, 4).Select(_ => Guid.CreateVersion7()).ToArray();

    await _execAsync(conn, "BEGIN");
    await _appendAsync(conn, ids[3], stream, version: 4);
    await _appendAsync(conn, ids[1], stream, version: 2);
    await _appendAsync(conn, ids[0], stream, version: 1);
    await _appendAsync(conn, ids[2], stream, version: 3);
    await _execAsync(conn, "COMMIT");

    _ = await _stampAsync(conn, 1000);

    var sequences = new List<long>();
    foreach (var id in ids) {
      sequences.Add((await _sequenceAsync(conn, id))!.Value);
    }
    await Assert.That(sequences).IsInOrder()
      .Because("within a stream the sequence follows the versions, whatever order one transaction inserted them in");
  }

  [Test]
  public async Task Stamp_ABatchSmallerThanAStream_StampsTheStreamFromItsFirstVersionAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var stream = Guid.CreateVersion7();
    var ids = Enumerable.Range(0, 3).Select(_ => Guid.CreateVersion7()).ToArray();

    // Inserted last-version first, so the insertion-order walk reaches version 3 before anything else.
    await _execAsync(conn, "BEGIN");
    await _appendAsync(conn, ids[2], stream, version: 3);
    await _appendAsync(conn, ids[1], stream, version: 2);
    await _appendAsync(conn, ids[0], stream, version: 1);
    await _execAsync(conn, "COMMIT");

    var calls = new List<int>();
    for (var i = 0; i < 3; i++) {
      calls.Add(await _stampAsync(conn, 1));
    }

    await Assert.That(calls).IsEquivalentTo(_oneEachCall)
      .Because("a batch of one must still make progress: the walk chooses the stream, and the stream is stamped from "
        + "its lowest unstamped version");
    await Assert.That(await _stampedAsync(conn, ids[0])).IsLessThan(await _stampedAsync(conn, ids[1]));
    await Assert.That(await _stampedAsync(conn, ids[1])).IsLessThan(await _stampedAsync(conn, ids[2]));
  }

  [Test]
  public async Task Stamp_StreamsEligibleTogether_AreNumberedInInsertionOrderAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var first = await dataSource.OpenConnectionAsync();
    await using var second = await dataSource.OpenConnectionAsync();
    var a = Guid.CreateVersion7();
    var b = Guid.CreateVersion7();

    // The second transaction has the lower id but inserts after the first.
    await _execAsync(second, "BEGIN");
    _ = await _xidAsync(second);
    await _execAsync(first, "BEGIN");
    await _appendAsync(first, a, Guid.CreateVersion7(), version: 1);
    await _execAsync(first, "COMMIT");
    await _appendAsync(second, b, Guid.CreateVersion7(), version: 1);
    await _execAsync(second, "COMMIT");

    _ = await _stampAsync(first, 1000);

    await Assert.That(await _stampedAsync(first, a)).IsLessThan(await _stampedAsync(first, b))
      .Because("rows that become eligible together are numbered in the order they were inserted (#1225's decision)");
  }

  [Test]
  public async Task Stamp_StreamsInDifferentBatches_FollowTheOrderTheirTransactionsBeganInAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var first = await dataSource.OpenConnectionAsync();
    await using var second = await dataSource.OpenConnectionAsync();
    var a = Guid.CreateVersion7();
    var b = Guid.CreateVersion7();

    // The same shape as above: the second transaction began first, and inserts after the first one commits.
    await _execAsync(second, "BEGIN");
    _ = await _xidAsync(second);
    await _execAsync(first, "BEGIN");
    await _appendAsync(first, a, Guid.CreateVersion7(), version: 1);
    await _execAsync(first, "COMMIT");
    await _appendAsync(second, b, Guid.CreateVersion7(), version: 1);
    await _execAsync(second, "COMMIT");

    // A batch of one, so the two streams cannot be numbered together.
    _ = await _stampAsync(first, 1);
    _ = await _stampAsync(first, 1);

    await Assert.That(await _stampedAsync(first, b)).IsLessThan(await _stampedAsync(first, a))
      .Because("across streams that are not numbered together, the batch takes the rows whose transactions began "
        + "first (205, #1217): the walk reads only rows past the horizon, and it can only find those through the "
        + "transaction that wrote them. Insertion order is kept within a batch, and a stream's versions keep their "
        + "order whatever the batch");
  }

  /// <summary>
  /// #1062: the drain reads its batch in insertion order through an index and stops at the batch, so a call costs
  /// what it stamps. The candidate query used to sort every unstamped row by transaction id before its LIMIT, so the
  /// same batch cost more as the backlog behind it grew.
  /// </summary>
  /// <remarks>
  /// Measured as the same batch at two backlog depths, in index entries read inside the call's own transaction. The
  /// plan's shared-buffer count was used before and does not follow the walk: a call that read 1,000 entries visited
  /// 910 buffers and one that read 20,000 visited 530, so it could not have caught the walk reading the backlog.
  /// </remarks>
  [Test]
  public async Task Stamp_ABacklog_CostsWhatTheBatchStampsNotWhatTheBacklogHoldsAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    await _execAsync(conn, "ALTER TABLE wh_event_store SET (autovacuum_enabled = false)");

    await _fillBacklogAsync(conn, SHALLOW_STREAMS);
    var shallow = await _entriesReadAsync(conn);
    await _fillBacklogAsync(conn, DEEP_STREAMS - SHALLOW_STREAMS);
    var deep = await _entriesReadAsync(conn);
    Console.WriteLine($"stamp of {BATCH}: read {shallow.Entries} index entries behind {SHALLOW_STREAMS * BACKLOG_VERSIONS} rows, {deep.Entries} behind {DEEP_STREAMS * BACKLOG_VERSIONS} ({deep.ByIndex})");

    await Assert.That(deep.Stamped).IsEqualTo(BATCH);
    await Assert.That(deep.Entries).IsLessThanOrEqualTo(shallow.Entries + (shallow.Entries / 4))
      .Because($"stamping {BATCH} rows read {shallow.Entries} index entries behind a backlog of "
        + $"{SHALLOW_STREAMS * BACKLOG_VERSIONS} rows and {deep.Entries} behind {DEEP_STREAMS * BACKLOG_VERSIONS} ({deep.ByIndex}). "
        + "A call that reads the backlog to choose its batch makes a drain cost the backlog squared over the batch");
  }

  /// <summary>
  /// #1217: rows behind an open transaction are not eligible, and a call that finds none must not have read them all
  /// to find that out. An import's oldest transaction holds the horizon while the rest of it commits behind, and the
  /// stamper retries every fenced interval while rows are pending, under the role fence.
  /// </summary>
  /// <remarks>
  /// Measured as index entries read inside the call's own transaction, which do not depend on the machine, so the
  /// ceiling is absolute: a call may read what a batch could stamp, never the rows it cannot.
  /// </remarks>
  [Test]
  public async Task Stamp_RowsBehindAnOpenTransaction_AreNotReadToFindNothingEligibleAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var import = await dataSource.OpenConnectionAsync();
    await using var conn = await dataSource.OpenConnectionAsync();
    await _execAsync(conn, "ALTER TABLE wh_event_store SET (autovacuum_enabled = false)");

    // The import's oldest transaction, open with a write, holds the horizon below everything committed after it.
    await _execAsync(import, "BEGIN");
    await _appendAsync(import, Guid.CreateVersion7(), Guid.CreateVersion7(), version: 1);

    await _fillBacklogAsync(conn, HELD_BACK_SHALLOW_STREAMS);
    var shallow = await _entriesReadAsync(conn);
    await _fillBacklogAsync(conn, HELD_BACK_DEEP_STREAMS - HELD_BACK_SHALLOW_STREAMS);
    var deep = await _entriesReadAsync(conn);
    await _execAsync(import, "ROLLBACK");
    Console.WriteLine($"held-back stamp of {BATCH}: read {shallow.Entries} index entries behind {HELD_BACK_SHALLOW_STREAMS * BACKLOG_VERSIONS} rows, {deep.Entries} behind {HELD_BACK_DEEP_STREAMS * BACKLOG_VERSIONS} ({deep.ByIndex})");

    await Assert.That(deep.Stamped).IsEqualTo(0)
      .Because("every row was committed after the open transaction began, so none is past the horizon");
    await Assert.That(deep.Entries).IsLessThanOrEqualTo(BATCH)
      .Because($"finding nothing to stamp read {shallow.Entries} index entries behind {HELD_BACK_SHALLOW_STREAMS * BACKLOG_VERSIONS} "
        + $"rows and {deep.Entries} behind {HELD_BACK_DEEP_STREAMS * BACKLOG_VERSIONS} ({deep.ByIndex}). A walk that reads every row behind the "
        + "horizon to learn none is eligible repeats that read on every fenced retry of an import");
  }

  /// <summary>
  /// #1786: a backlog the statistics do not know about (an upgrade from below 046 writes every unstamped row's
  /// commit-order columns in the same pass that creates them), every row sharing one insert_xid as 197 and 205 leave
  /// it. The call reads its batch through idx_event_store_unstamped_insert_xid, under a Limit and without a sort, and
  /// no scan of the event store returns more rows than the batch.
  /// </summary>
  /// <remarks>
  /// Read from the plans the call's own statements ran (auto_explain, nested statements, actual row counts), which do
  /// not depend on the machine.
  /// </remarks>
  [Test]
  public async Task Stamp_ABacklogTheStatisticsDoNotKnow_IsReadOnceThroughTheInsertXidIndexAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    await _fillBacklogTheStatisticsDoNotKnowAsync(conn);

    var call = await _plansOfOneStampAsync(conn);

    var walks = call.Nodes.Where(n => n.Index == "idx_event_store_unstamped_insert_xid").ToList();
    await Assert.That(walks).Count().IsEqualTo(1)
      .Because("the walk is the one read of the insert_xid index in a call");
    await Assert.That(walks[0].ParentType).IsEqualTo("Limit")
      .Because("the walk stops at the batch only when the Limit sits directly on the ordered index scan; a sort "
        + "between them reads every unstamped row before the first one is returned");
    await Assert.That(walks[0].RowsReturned).IsEqualTo(LARGE_BATCH);
    var widest = call.Nodes.Where(n => n.Relation == "wh_event_store").MaxBy(n => n.RowsReturned)!;
    await Assert.That(widest.RowsReturned).IsLessThanOrEqualTo(LARGE_BATCH)
      .Because($"a call of {LARGE_BATCH} rows behind a backlog of {LARGE_BACKLOG_STREAMS * BACKLOG_VERSIONS} read "
        + $"{widest.RowsReturned} rows in one {widest.Type} ({widest.Index ?? "no index"}); a call that reads the "
        + "backlog to stamp a batch makes a drain cost the backlog squared over the batch");
  }

  /// <summary>
  /// #1786: no step of a call compares the rows of its batch with each other. 205 joined the batch's own sets in one
  /// statement, and on estimates as small as an unknown backlog gives (and on the generic plan a long-lived connection
  /// settles on), the planner joined them by nested loop: a call of 1,000 rows removed about a million rows by join
  /// filter, twice, so its cost grew with the square of the batch.
  /// </summary>
  [Test]
  public async Task Stamp_ABacklogTheStatisticsDoNotKnow_NeverComparesTheBatchWithItselfAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    await _fillBacklogTheStatisticsDoNotKnowAsync(conn);
    // The plan the stamper's own connection uses once it has called the function more than five times.
    await _execAsync(conn, "SET plan_cache_mode = force_generic_plan");

    var call = await _plansOfOneStampAsync(conn);

    var removed = call.Nodes.Sum(n => n.RemovedByJoinFilter);
    await Assert.That(call.Stamped).IsEqualTo(LARGE_BATCH);
    await Assert.That(removed).IsLessThanOrEqualTo(LARGE_BATCH)
      .Because($"stamping {LARGE_BATCH} rows removed {removed} rows by join filter. A join of the batch with "
        + "itself compares every row with every other, so halving the batch only quarters a cost that should halve");
  }

  /// <summary>
  /// #1786: the same unknown backlog drains completely, in insertion order and in version order within each stream,
  /// while a row committed behind an open transaction waits for it and is then numbered after every row before it:
  /// the visibility fence and the commit order the rewrite must keep.
  /// </summary>
  [Test]
  public async Task Stamp_ABacklogTheStatisticsDoNotKnow_DrainsInInsertionOrderBehindTheFenceAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    await using var bystander = await dataSource.OpenConnectionAsync();
    await using var late = await dataSource.OpenConnectionAsync();
    await _fillBacklogTheStatisticsDoNotKnowAsync(conn);
    await _execAsync(conn, "SET plan_cache_mode = force_generic_plan");
    // The bystander holds the horizon below the late transaction, which commits a row behind it.
    await _execAsync(bystander, "BEGIN");
    _ = await _xidAsync(bystander);
    var lateRow = Guid.CreateVersion7();
    await _execAsync(late, "BEGIN");
    await _appendAsync(late, lateRow, Guid.CreateVersion7(), version: 1);
    await _execAsync(late, "COMMIT");

    var calls = await _drainAsync(conn);

    await Assert.That(calls).IsEqualTo((LARGE_BACKLOG_STREAMS * BACKLOG_VERSIONS / LARGE_BATCH) + 1)
      .Because("each call stamps a full batch until the backlog is drained, and the last one finds nothing eligible");
    await Assert.That(await _sequenceAsync(conn, lateRow)).IsNull()
      .Because("the late row committed behind the bystander's open transaction, so it is not yet past the horizon");
    await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_event_store WHERE commit_sequence IS NULL")).IsEqualTo(1);
    await Assert.That(await _countAsync(conn, """
      SELECT count(*) FROM (
        SELECT row_number() OVER (ORDER BY commit_sequence) AS by_sequence,
               row_number() OVER (ORDER BY insert_order) AS by_insertion
        FROM wh_event_store WHERE commit_sequence IS NOT NULL) o
      WHERE o.by_sequence <> o.by_insertion
      """)).IsEqualTo(0)
      .Because("every batch is numbered in insertion order and each batch after the one before; each stream was "
        + "inserted in version order, so this is also version order within every stream");

    await _execAsync(bystander, "ROLLBACK");
    var afterwards = await _stampAsync(conn, LARGE_BATCH);

    await Assert.That(afterwards).IsEqualTo(1);
    await Assert.That(await _stampedAsync(conn, lateRow))
      .IsGreaterThan(await _countAsync(conn, "SELECT max(commit_sequence) FROM wh_event_store WHERE event_id <> '" + lateRow + "'"))
      .Because("a row stamped later always has a higher value than every row stamped before it");
  }

  private const int LARGE_BACKLOG_STREAMS = 3_000;
  private const int LARGE_BATCH = 1_000;
  private const int STREAMS_PER_BLOCK = 100;
  private const int MOST_CALLS = 100;

  /// <summary>
  /// The backlog an upgrade from below 046 leaves: statistics taken while the table was empty, then
  /// <see cref="LARGE_BACKLOG_STREAMS"/> streams of <see cref="BACKLOG_VERSIONS"/> rows in one statement, rewritten by
  /// 205's own backfill so every row carries the insert_xid it gives them. Streams are inserted in blocks of
  /// <see cref="STREAMS_PER_BLOCK"/>, version by version within a block, so a batch takes several versions of each of
  /// its streams, interleaved.
  /// </summary>
  private static async Task _fillBacklogTheStatisticsDoNotKnowAsync(NpgsqlConnection conn) {
    await _execAsync(conn, "ALTER TABLE wh_event_store SET (autovacuum_enabled = false)");
    await _execAsync(conn, "ANALYZE wh_event_store");
    await _execAsync(conn, $"""
      WITH s AS (SELECT n, gen_random_uuid() AS stream_id FROM generate_series(0, {LARGE_BACKLOG_STREAMS - 1}) n)
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at)
      SELECT gen_random_uuid(), s.stream_id, s.stream_id, 'TestAggregate', v, 'TestEvent', NULL, NOW()
      FROM s CROSS JOIN generate_series(1, {BACKLOG_VERSIONS}) v
      ORDER BY s.n / {STREAMS_PER_BLOCK}, v, s.n
      """);
    await _execAsync(conn, "UPDATE wh_event_store SET insert_xid = xmin::text::xid8 WHERE commit_sequence IS NULL");
  }

  /// <summary>Stamps <see cref="LARGE_BATCH"/> at a time until a call stamps nothing, and returns the number of calls.</summary>
  private static async Task<int> _drainAsync(NpgsqlConnection conn) {
    for (var call = 1; call <= MOST_CALLS; call++) {
      if (await _stampAsync(conn, LARGE_BATCH) == 0) {
        return call;
      }
    }
    throw new InvalidOperationException($"the backlog did not drain in {MOST_CALLS} calls of {LARGE_BATCH}");
  }

  /// <summary>
  /// One stamp of <see cref="LARGE_BATCH"/>, rolled back, with the plan of every statement it ran: auto_explain logs
  /// each nested statement's plan, with actual row counts, as a notice on this connection.
  /// </summary>
  private static async Task<StampPlans> _plansOfOneStampAsync(NpgsqlConnection conn) {
    var notices = new List<string>();
    void Collect(object? _, NpgsqlNoticeEventArgs e) => notices.Add(e.Notice.MessageText);
    await _execAsync(conn, """
      LOAD 'auto_explain';
      SET auto_explain.log_min_duration = 0;
      SET auto_explain.log_analyze = on;
      SET auto_explain.log_timing = off;
      SET auto_explain.log_nested_statements = on;
      SET auto_explain.log_format = 'json';
      SET auto_explain.log_level = 'notice';
      """);
    conn.Notice += Collect;
    await _execAsync(conn, "BEGIN");
    var stamped = await _stampAsync(conn, LARGE_BATCH);
    await _execAsync(conn, "ROLLBACK");
    conn.Notice -= Collect;
    await _execAsync(conn, "SET auto_explain.log_min_duration = -1");
    var nodes = new List<PlanNode>();
    foreach (var notice in notices.Where(n => n.Contains("\"Plan\"", StringComparison.Ordinal))) {
      using var document = JsonDocument.Parse(notice[notice.IndexOf('{', StringComparison.Ordinal)..]);
      _flatten(document.RootElement.GetProperty("Plan"), parentType: null, nodes);
    }
    return new StampPlans(stamped, nodes);
  }

  /// <summary>What one call stamped, and every node of every statement it ran, each with its parent's type.</summary>
  private sealed record StampPlans(int Stamped, IReadOnlyList<PlanNode> Nodes);

  private static void _flatten(JsonElement node, string? parentType, List<PlanNode> nodes) {
    var type = node.GetProperty("Node Type").GetString()!;
    nodes.Add(new PlanNode(
      type,
      parentType,
      node.TryGetProperty("Relation Name", out var relation) ? relation.GetString() : null,
      node.TryGetProperty("Index Name", out var index) ? index.GetString() : null,
      (long)(node.GetProperty("Actual Rows").GetDouble() * node.GetProperty("Actual Loops").GetDouble()),
      node.TryGetProperty("Rows Removed by Join Filter", out var removed) ? (long)removed.GetDouble() : 0));
    if (node.TryGetProperty("Plans", out var children)) {
      foreach (var child in children.EnumerateArray()) {
        _flatten(child, type, nodes);
      }
    }
  }

  private sealed record PlanNode(string Type, string? ParentType, string? Relation, string? Index, long RowsReturned, long RemovedByJoinFilter);

  private static async Task<long> _countAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private const int HELD_BACK_SHALLOW_STREAMS = 100;
  private const int HELD_BACK_DEEP_STREAMS = 2_000;
  private const int SHALLOW_STREAMS = 1_000;
  private const int DEEP_STREAMS = 16_000;
  private const int BACKLOG_VERSIONS = 10;
  private const int BATCH = 100;

  private static readonly int[] _oneEachCall = [1, 1, 1];

  private static Task _fillBacklogAsync(NpgsqlConnection conn, int streams) => _execAsync(conn, $"""
    INSERT INTO wh_event_store
      (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at)
    SELECT gen_random_uuid(), s.stream_id, s.stream_id, 'TestAggregate', v, 'TestEvent', NULL, NOW()
    FROM (SELECT gen_random_uuid() AS stream_id FROM generate_series(1, {streams})) s
    CROSS JOIN generate_series(1, {BACKLOG_VERSIONS}) v;
    ANALYZE wh_event_store;
    """);

  /// <summary>
  /// One stamp of <see cref="BATCH"/>, rolled back, with the index entries it read on the event store's indexes: counted
  /// inside the call's own transaction, so the number is the same on any machine and names the index that did the reading.
  /// </summary>
  private static async Task<StampRead> _entriesReadAsync(NpgsqlConnection conn) {
    const string READ = """
      SELECT i.indexrelid::regclass::text AS name, pg_stat_get_xact_tuples_returned(i.indexrelid) AS entries
      FROM pg_index i WHERE i.indrelid = 'wh_event_store'::regclass
      """;
    await _execAsync(conn, "BEGIN");
    var before = await _readByIndexAsync(conn, READ);
    var stamped = await _stampAsync(conn, BATCH);
    var after = await _readByIndexAsync(conn, READ);
    await _execAsync(conn, "ROLLBACK");
    var delta = after.ToDictionary(kv => kv.Key, kv => kv.Value - before.GetValueOrDefault(kv.Key));
    var byIndex = string.Join(", ", delta.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key} {kv.Value}"));
    return new StampRead(stamped, delta.Values.Sum(), byIndex.Length == 0 ? "no index read" : byIndex);
  }

  private sealed record StampRead(int Stamped, long Entries, string ByIndex);

  private static async Task<Dictionary<string, long>> _readByIndexAsync(NpgsqlConnection conn, string sql) {
    var read = new Dictionary<string, long>(StringComparer.Ordinal);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      read[reader.GetString(0)] = reader.GetInt64(1);
    }
    return read;
  }

  // ------------------------------------------------------------------

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>Assigns the transaction its id now, as an earlier write in it would, and returns it.</summary>
  private static async Task<long> _xidAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT pg_current_xact_id()::text::bigint";
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task _appendAsync(NpgsqlConnection conn, Guid eventId, Guid streamId, int version) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at)
      VALUES (@eid, @sid, @sid, 'TestAggregate', @ver, 'TestEvent', NULL, NOW())";
    cmd.Parameters.AddWithValue("eid", eventId);
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("ver", version);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<int> _stampAsync(NpgsqlConnection conn, int batchSize) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT stamp_pending_commit_sequences(@bs)";
    cmd.Parameters.AddWithValue("bs", batchSize);
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task<long> _stampedAsync(NpgsqlConnection conn, Guid eventId) =>
    await _sequenceAsync(conn, eventId) ?? throw new InvalidOperationException($"event {eventId} was not stamped");

  private static async Task<long?> _sequenceAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT commit_sequence FROM wh_event_store WHERE event_id = @id";
    cmd.Parameters.AddWithValue("id", eventId);
    var value = await cmd.ExecuteScalarAsync();
    return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
  }
}
