using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Slice 26 step 2 — RED-first locks for <c>stamp_pending_commit_sequences</c>.
///
/// <para>The stamper is the only writer of <c>wh_event_store.commit_sequence</c>. It
/// allocates from <c>wh_commit_seq</c> and stamps rows whose inserting transaction is
/// provably committed AND no older concurrent transaction could still commit. The
/// invariant: stamped values are monotonic with the order rows became
/// <c>pg_snapshot_xmin</c>-stable.</para>
///
/// <para><strong>Locked invariants:</strong></para>
/// <list type="bullet">
/// <item><description>Only rows whose inserting xmin is below
/// <c>pg_snapshot_xmin(pg_current_snapshot())</c> get stamped. While a concurrent
/// transaction is in-flight, ALL rows with xmin &gt;= that tx's xmin are deferred.</description></item>
/// <item><description>Stamping is in xmin order. Within a single batch, lower xmin
/// gets lower <c>commit_sequence</c>.</description></item>
/// <item><description>Already-stamped rows are skipped (<c>commit_sequence IS NULL</c>
/// filter).</description></item>
/// <item><description><c>p_batch_size</c> caps work per call (default 1000).</description></item>
/// <item><description>Concurrent callers (multiple stampers) cannot stamp the same row
/// twice — <c>FOR UPDATE SKIP LOCKED</c> partitions the work; the singleton-stamper
/// pattern is enforced at the C# layer via <c>pg_try_advisory_lock</c>, but the SQL is
/// safe under concurrent invocation regardless.</description></item>
/// </list>
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
public class StampPendingCommitSequencesSqlTests : EFCoreTestBase {

  [Test]
  public async Task Stamp_NoUnstampedRows_ReturnsZeroAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var count = await _stampAsync(conn, batchSize: 1000);

    await Assert.That(count).IsEqualTo(0)
      .Because("empty event store → zero stamped rows");
  }

  [Test]
  public async Task Stamp_SingleCommittedRow_StampsItWithSequenceOneAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var eventId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _insertEventStoreRowAsync(conn, eventId, streamId, version: 1);

    var stamped = await _stampAsync(conn, batchSize: 1000);

    await Assert.That(stamped).IsEqualTo(1);
    var seq = await _readCommitSequenceAsync(conn, eventId);
    await Assert.That(seq).IsEqualTo(1L)
      .Because("first stamp draws nextval from a fresh sequence");
  }

  [Test]
  public async Task Stamp_IgnoresAlreadyStampedRowsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var eventId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _insertEventStoreRowAsync(conn, eventId, streamId, version: 1);

    var first = await _stampAsync(conn, batchSize: 1000);
    var firstSeq = await _readCommitSequenceAsync(conn, eventId);

    // Run again — already-stamped row must be untouched.
    var second = await _stampAsync(conn, batchSize: 1000);
    var secondSeq = await _readCommitSequenceAsync(conn, eventId);

    await Assert.That(first).IsEqualTo(1);
    await Assert.That(second).IsEqualTo(0)
      .Because("commit_sequence IS NULL filter must exclude already-stamped rows");
    await Assert.That(secondSeq).IsEqualTo(firstSeq)
      .Because("idempotent re-call must not re-stamp");
  }

  [Test]
  public async Task Stamp_MultipleRowsInsertedSequentially_StampsInXminOrderAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var event1 = (Guid)TrackedGuid.NewMedo();
    var event2 = (Guid)TrackedGuid.NewMedo();
    var event3 = (Guid)TrackedGuid.NewMedo();

    // Sequential inserts → sequential xmins → must stamp in same order.
    await _insertEventStoreRowAsync(conn, event1, streamId, version: 1);
    await _insertEventStoreRowAsync(conn, event2, streamId, version: 2);
    await _insertEventStoreRowAsync(conn, event3, streamId, version: 3);

    var stamped = await _stampAsync(conn, batchSize: 1000);
    await Assert.That(stamped).IsEqualTo(3);

    var seq1 = await _readCommitSequenceAsync(conn, event1);
    var seq2 = await _readCommitSequenceAsync(conn, event2);
    var seq3 = await _readCommitSequenceAsync(conn, event3);

    await Assert.That(seq1!.Value).IsLessThan(seq2!.Value);
    await Assert.That(seq2!.Value).IsLessThan(seq3!.Value);
  }

  [Test]
  public async Task Stamp_BatchSizeLimitsPerCallAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var streamId = (Guid)TrackedGuid.NewMedo();
    for (var i = 1; i <= 5; i++) {
      await _insertEventStoreRowAsync(conn, (Guid)TrackedGuid.NewMedo(), streamId, version: i);
    }

    var first = await _stampAsync(conn, batchSize: 2);
    var second = await _stampAsync(conn, batchSize: 2);
    var third = await _stampAsync(conn, batchSize: 2);

    await Assert.That(first).IsEqualTo(2);
    await Assert.That(second).IsEqualTo(2);
    await Assert.That(third).IsEqualTo(1)
      .Because("5 rows / 2-per-batch → 2, 2, 1");
  }

  [Test]
  public async Task Stamp_DefersWhileOlderTxStillInFlightAsync() {
    // The smoking-gun test: simulates a production run's commit-order race.
    // T1 begins, inserts row r1 (xmin = X1, uncommitted), holds tx open.
    // T2 begins on a separate connection, inserts r2 (xmin = X2 > X1), commits fast.
    // Stamper runs from a third connection.
    //   * BEFORE T1 commits: snapshot_xmin <= X1, so neither r1 nor r2 are < snapshot_xmin
    //     → ZERO stamps. The stamper defers r2 until T1 resolves.
    //   * AFTER T1 commits: snapshot_xmin > X2, both rows visible & past barrier
    //     → BOTH stamped in xmin order (r1 first, r2 second).
    // This is the invariant that fixes the 2-second commit-order delta from that production run.
    var dataSource = NpgsqlDataSource.Create(ConnectionString);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var r1 = (Guid)TrackedGuid.NewMedo();
    var r2 = (Guid)TrackedGuid.NewMedo();

    await using var connT1 = await dataSource.OpenConnectionAsync();
    await using var connT2 = await dataSource.OpenConnectionAsync();
    await using var connStamper = await dataSource.OpenConnectionAsync();

    // T1: BEGIN + INSERT (no commit yet).
    await using (var begin = connT1.CreateCommand()) {
      begin.CommandText = "BEGIN";
      await begin.ExecuteNonQueryAsync();
    }
    await _insertEventStoreRowAsync(connT1, r1, streamId, version: 1);

    // T2: INSERT (auto-commit). Has higher xmin than T1.
    await _insertEventStoreRowAsync(connT2, r2, streamId, version: 2);

    // Stamper while T1 still open. r1 isn't visible to stamper (uncommitted),
    // r2's xmin > snapshot_xmin (which is held back by T1) → no stamps.
    var stampedDuringT1 = await _stampAsync(connStamper, batchSize: 1000);
    await Assert.That(stampedDuringT1).IsEqualTo(0)
      .Because("while T1 is in flight, pg_snapshot_xmin can't advance past T1's xmin — r2 is also held back even though it's committed");

    // T1 commits. Now both rows are stampable.
    await using (var commit = connT1.CreateCommand()) {
      commit.CommandText = "COMMIT";
      await commit.ExecuteNonQueryAsync();
    }

    var stampedAfterT1 = await _stampAsync(connStamper, batchSize: 1000);
    await Assert.That(stampedAfterT1).IsEqualTo(2)
      .Because("once snapshot_xmin advances past both xmins, the stamper picks up both in one pass");

    var seq1 = await _readCommitSequenceAsync(connStamper, r1);
    var seq2 = await _readCommitSequenceAsync(connStamper, r2);
    await Assert.That(seq1!.Value).IsLessThan(seq2!.Value)
      .Because("r1's xmin is lower → it must get the lower commit_sequence (xmin order = stamping order)");
  }

  [Test]
  public async Task Stamp_ConcurrentCallers_NoRowStampedTwiceAsync() {
    // FOR UPDATE SKIP LOCKED partitions the work between concurrent stampers.
    // Each row gets exactly one commit_sequence. Total stamps = total rows.
    var dataSource = NpgsqlDataSource.Create(ConnectionString);

    var streamId = (Guid)TrackedGuid.NewMedo();
    const int N = 20;
    for (var i = 1; i <= N; i++) {
      await using var conn = await dataSource.OpenConnectionAsync();
      await _insertEventStoreRowAsync(conn, (Guid)TrackedGuid.NewMedo(), streamId, version: i);
    }

    // Run three stampers concurrently. SKIP LOCKED guarantees they don't double-stamp.
    var task1 = _stampOnNewConnectionAsync(dataSource, batchSize: 100);
    var task2 = _stampOnNewConnectionAsync(dataSource, batchSize: 100);
    var task3 = _stampOnNewConnectionAsync(dataSource, batchSize: 100);

    var counts = await Task.WhenAll(task1, task2, task3);
    var totalStamped = counts.Sum();

    await Assert.That(totalStamped).IsEqualTo(N)
      .Because("each row must be stamped exactly once across all concurrent callers");

    // Verify monotonicity: every row has a non-NULL commit_sequence and the assigned
    // values are distinct (uniqueness via nextval).
    await using var verifyConn = await dataSource.OpenConnectionAsync();
    await using var verify = verifyConn.CreateCommand();
    verify.CommandText = "SELECT count(*), count(DISTINCT commit_sequence) FROM wh_event_store WHERE commit_sequence IS NOT NULL";
    await using var reader = await verify.ExecuteReaderAsync();
    await reader.ReadAsync();
    var stampedCount = reader.GetInt64(0);
    var distinctSequences = reader.GetInt64(1);
    await Assert.That(stampedCount).IsEqualTo((long)N);
    await Assert.That(distinctSequences).IsEqualTo((long)N)
      .Because("nextval guarantees uniqueness across concurrent callers");
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task<int> _stampAsync(NpgsqlConnection conn, int batchSize) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT stamp_pending_commit_sequences(@bs)";
    cmd.Parameters.AddWithValue("bs", batchSize);
    var result = await cmd.ExecuteScalarAsync();
    return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task<int> _stampOnNewConnectionAsync(NpgsqlDataSource dataSource, int batchSize) {
    await using var conn = await dataSource.OpenConnectionAsync();
    return await _stampAsync(conn, batchSize);
  }

  private static async Task<long?> _readCommitSequenceAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT commit_sequence FROM wh_event_store WHERE event_id = @id";
    cmd.Parameters.AddWithValue("id", eventId);
    var result = await cmd.ExecuteScalarAsync();
    return result switch {
      null or DBNull => null,
      _ => Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture)
    };
  }

  private static async Task _insertEventStoreRowAsync(NpgsqlConnection conn, Guid eventId, Guid streamId, int version) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, version, event_type,
         scope, created_at)
      VALUES
        (@eid, @sid, @sid, 'TestAggregate', @ver, 'TestEvent',
         NULL, NOW())";
    ins.Parameters.AddWithValue("eid", eventId);
    ins.Parameters.AddWithValue("sid", streamId);
    ins.Parameters.AddWithValue("ver", version);
    await ins.ExecuteNonQueryAsync();
  }
}
