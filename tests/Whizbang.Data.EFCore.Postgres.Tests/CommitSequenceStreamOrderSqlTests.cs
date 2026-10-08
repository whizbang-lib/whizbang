// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
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

  /// <summary>
  /// #1062: the drain reads its batch in insertion order through an index and stops at the batch, so a call costs
  /// what it stamps. The candidate query used to sort every unstamped row by transaction id before its LIMIT, so the
  /// same batch cost more as the backlog behind it grew.
  /// </summary>
  /// <remarks>
  /// Measured as the same batch at two backlog depths rather than against a fixed ceiling: the update itself touches
  /// every index of the event store for each row it stamps, which is the work the batch buys and the same at any
  /// depth. What must not change between the depths is everything else.
  /// </remarks>
  [Test]
  public async Task Stamp_ABacklog_CostsWhatTheBatchStampsNotWhatTheBacklogHoldsAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    await _execAsync(conn, "ALTER TABLE wh_event_store SET (autovacuum_enabled = false)");

    await _fillBacklogAsync(conn, SHALLOW_STREAMS);
    var shallow = await _stampCostAsync(conn);
    await _fillBacklogAsync(conn, DEEP_STREAMS - SHALLOW_STREAMS);
    var deep = await _stampCostAsync(conn);
    Console.WriteLine($"stamp of {BATCH}: {shallow} buffers behind {SHALLOW_STREAMS * BACKLOG_VERSIONS} rows, {deep} behind {DEEP_STREAMS * BACKLOG_VERSIONS}");

    await Assert.That(deep).IsLessThanOrEqualTo(shallow + (shallow / 4))
      .Because($"stamping {BATCH} rows visited {shallow} shared buffers behind a backlog of "
        + $"{SHALLOW_STREAMS * BACKLOG_VERSIONS} rows and {deep} behind {DEEP_STREAMS * BACKLOG_VERSIONS}. A call that "
        + "reads the backlog to choose its batch makes a drain cost the backlog squared over the batch");
  }

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

  /// <summary>One stamp of <see cref="BATCH"/> rows, rolled back, so both depths stamp the same kind of batch.</summary>
  private static async Task<long> _stampCostAsync(NpgsqlConnection conn) {
    await _execAsync(conn, "BEGIN");
    var plan = await QueryPlan.CaptureAsync(conn, $"SELECT stamp_pending_commit_sequences({BATCH})");
    await _execAsync(conn, "ROLLBACK");
    return plan.SharedBuffersVisited;
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
