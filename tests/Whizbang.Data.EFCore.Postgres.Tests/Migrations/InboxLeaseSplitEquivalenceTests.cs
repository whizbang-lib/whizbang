using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The two properties the inbox lease split has to preserve: which rows the per-stream ordering gate
/// admits, and that every message has exactly one lease row.
/// </summary>
/// <remarks>
/// <para>
/// Moving <c>instance_id</c>, <c>lease_expiry</c>, <c>attempts</c> and <c>processed_at</c> out of
/// <c>wh_inbox</c> is worth doing because those columns appear in fifteen of the table's indexes, so
/// a claim can never be a heap-only update and pays full index maintenance on a wide row. It is only
/// worth doing if the claim still takes the same rows in the same order.
/// </para>
/// <para>
/// The gate is a correlated <c>NOT EXISTS</c>: a row is claimable only when no earlier unprocessed
/// event of the same stream is itself claimable. It reads nine columns, and the split is safe
/// precisely because all nine end up on the lease table, so the gate stays a single-table query
/// rather than becoming a cross-table join. That is the claim this fixture checks, by running both
/// forms over identical data and comparing the sets rather than the counts: two queries can admit the
/// same NUMBER of rows and disagree about which.
/// </para>
/// <para>
/// The fixture builds both representations itself rather than running the migration, so it tests the
/// equivalence of the two queries directly and keeps working once the old columns are gone.
/// </para>
/// </remarks>
[Category("Integration")]
public class InboxLeaseSplitEquivalenceTests : EFCoreTestBase {
  private const int STREAMS = 40;
  private const int PER_STREAM = 25;

  [Test]
  [Timeout(600000)]
  public async Task TheOrderingGateAdmitsTheSameRowsOnBothRepresentationsAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    await _buildBothRepresentationsAsync(conn, cancellationToken);

    // Rows admitted by one form and not the other, in both directions. Counting alone would let a
    // swap pass: the same number of rows, a different set.
    long wide, split, onlyWide, onlySplit;
    // Scoped, because the connection stays busy until the reader is disposed and there is another
    // command below.
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = _gateComparison();
      cmd.CommandTimeout = 300;
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
      _ = await reader.ReadAsync(cancellationToken);
      wide = reader.GetInt64(0);
      split = reader.GetInt64(1);
      onlyWide = reader.GetInt64(2);
      onlySplit = reader.GetInt64(3);
    }

    await Assert.That(wide).IsGreaterThan(0)
      .Because("the gate has to admit something, or a fixture that admits nothing on both sides "
        + "would report perfect agreement while proving nothing");
    await Assert.That(onlyWide).IsEqualTo(0L)
      .Because($"the split representation refused {onlyWide} rows the current one admits, which "
        + "would stall those streams: a message nothing will claim is a message that never arrives");
    await Assert.That(onlySplit).IsEqualTo(0L)
      .Because($"the split representation admitted {onlySplit} rows the current one refuses, which "
        + "is the dangerous direction: an admitted row whose predecessor is still in flight breaks "
        + "per-stream ordering, and every saga and projection in the system depends on it");
    await Assert.That(split).IsEqualTo(wide);

    // The set comparison above is necessary and not sufficient, and it is worth being honest about
    // why: both sides run the same query text over the same data, so agreement is close to
    // guaranteed. What it actually rules out is a transcription error in the rewrite.
    //
    // The structural property is the one below, and it is the one the split stands on: EVERY column
    // the gate reads has to be on the lease table. If even one stayed behind on the message table,
    // the gate would become a cross-table join executed per candidate row, which is the single
    // change that would make this migration slower rather than faster. This asks the catalog rather
    // than trusting the CREATE TABLE above.
    var gateColumns = new[] {
      "stream_id", "processed_at", "is_event", "received_at", "message_id",
      "instance_id", "lease_expiry", "scheduled_for", "partition_number",
    };
    await using var columnsCmd = conn.CreateCommand();
    columnsCmd.CommandText =
      "SELECT count(*) FROM information_schema.columns "
      + "WHERE table_name = 'split_lease' AND column_name = ANY(@cols)";
    columnsCmd.Parameters.AddWithValue("cols", gateColumns);
    var present = (long?)await columnsCmd.ExecuteScalarAsync(cancellationToken) ?? 0L;

    await Assert.That(present).IsEqualTo((long)gateColumns.Length)
      .Because("the per-stream ordering gate reads nine columns, and the split is only worth making "
        + "if all nine are on the lease table. One left behind turns the gate into a join against "
        + "the wide message row, executed once per candidate, which costs more than the index "
        + "maintenance the split exists to avoid");
  }

  /// <summary>
  /// Every message has exactly one lease row, and a deleted message takes its lease with it.
  /// </summary>
  /// <remarks>
  /// A message with no lease row can never be claimed and says nothing about it: the claim simply
  /// never returns it, the backlog grows, and nothing in the logs names the cause. That silence is
  /// why this is asserted as an invariant rather than trusted to the insert path.
  /// </remarks>
  [Test]
  [Timeout(600000)]
  public async Task EveryMessageHasExactlyOneLeaseRowAndDeletesCascadeAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    await _buildBothRepresentationsAsync(conn, cancellationToken);

    await Assert.That(await _scalarAsync(conn,
        "SELECT count(*) FROM split_inbox i LEFT JOIN split_lease l ON l.message_id = i.message_id "
        + "WHERE l.message_id IS NULL", cancellationToken))
      .IsEqualTo(0L)
      .Because("a message with no lease row can never be claimed, and it fails silently: the claim "
        + "just never returns it and nothing names the cause");
    await Assert.That(await _scalarAsync(conn,
        "SELECT count(*) FROM split_lease l LEFT JOIN split_inbox i ON i.message_id = l.message_id "
        + "WHERE i.message_id IS NULL", cancellationToken))
      .IsEqualTo(0L)
      .Because("a lease with no message is a row the reclaim paths will keep considering forever");

    // The cascade, exercised rather than assumed: reaping deletes messages, and a lease left behind
    // would be claimed against a message that no longer exists.
    var before = await _scalarAsync(conn, "SELECT count(*) FROM split_lease", cancellationToken);
    await using (var delete = conn.CreateCommand()) {
      delete.CommandText =
        "DELETE FROM split_inbox WHERE message_id IN "
        + "(SELECT message_id FROM split_inbox ORDER BY message_id LIMIT 10)";
      _ = await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM split_lease", cancellationToken))
      .IsEqualTo(before - 10)
      .Because("deleting a message must take its lease with it, or the reclaim paths keep finding a "
        + "lease for a message that is gone");
  }

  /// <summary>
  /// The same data in both shapes: the wide table as it is today, and the narrow lease table beside
  /// a message table with the claim-state columns removed.
  /// </summary>
  /// <remarks>
  /// The state is deliberately mixed, because a gate only disagrees where the data is interesting:
  /// some rows unowned, some leased and live, some leased and expired, some scheduled for later,
  /// some already processed, spread across streams so the per-stream predicate has work to do.
  /// </remarks>
  private static async Task _buildBothRepresentationsAsync(
      NpgsqlConnection conn, CancellationToken cancellationToken) {
    await using var seed = conn.CreateCommand();
    seed.CommandText = @"
      DROP TABLE IF EXISTS split_lease;
      DROP TABLE IF EXISTS split_inbox;
      DROP TABLE IF EXISTS wide_inbox;

      CREATE TABLE wide_inbox (
        message_id UUID PRIMARY KEY, stream_id UUID, received_at TIMESTAMPTZ NOT NULL,
        partition_number INTEGER, priority INTEGER NOT NULL DEFAULT 100,
        is_event BOOLEAN NOT NULL DEFAULT FALSE, scheduled_for TIMESTAMPTZ,
        processed_at TIMESTAMPTZ, instance_id UUID, lease_expiry TIMESTAMPTZ,
        attempts INTEGER NOT NULL DEFAULT 0, body JSONB);

      INSERT INTO wide_inbox
        (message_id, stream_id, received_at, partition_number, priority, is_event,
         scheduled_for, processed_at, instance_id, lease_expiry, attempts, body)
      SELECT
        ('00000000-0000-7000-8000-' || lpad((st.s * 1000 + r)::text, 12, '0'))::uuid,
        ('00000000-0000-7000-8000-' || lpad(st.s::text, 12, '0'))::uuid,
        NOW() - ((st.s * 1000 + r) * INTERVAL '1 millisecond'),
        st.s % 97,
        CASE (st.s + r) % 3 WHEN 0 THEN 50 WHEN 1 THEN 150 ELSE 250 END,
        (r % 4) <> 0,
        CASE WHEN (st.s + r) % 11 = 0 THEN NOW() + INTERVAL '1 hour' END,
        CASE WHEN (st.s + r) % 7 = 0 THEN NOW() - INTERVAL '1 minute' END,
        CASE WHEN (st.s + r) % 5 = 0
             THEN ('00000000-0000-7000-8000-' || lpad((900000 + st.s)::text, 12, '0'))::uuid END,
        CASE WHEN (st.s + r) % 5 = 0
             THEN CASE WHEN (st.s + r) % 10 = 0
                       THEN NOW() - INTERVAL '2 minutes' ELSE NOW() + INTERVAL '5 minutes' END END,
        (st.s + r) % 3,
        jsonb_build_object('pad', repeat('x', 200))
      FROM generate_series(1, @streams) AS st(s) CROSS JOIN generate_series(1, @per_stream) AS r;

      -- The split shape: the message row without claim state, and the lease row beside it.
      CREATE TABLE split_inbox AS
        SELECT message_id, stream_id, received_at, partition_number, priority, is_event, body
        FROM wide_inbox;
      ALTER TABLE split_inbox ADD PRIMARY KEY (message_id);

      CREATE TABLE split_lease (
        message_id UUID PRIMARY KEY REFERENCES split_inbox (message_id) ON DELETE CASCADE,
        stream_id UUID, received_at TIMESTAMPTZ NOT NULL, partition_number INTEGER,
        priority INTEGER NOT NULL DEFAULT 100, is_event BOOLEAN NOT NULL DEFAULT FALSE,
        scheduled_for TIMESTAMPTZ, processed_at TIMESTAMPTZ, instance_id UUID,
        lease_expiry TIMESTAMPTZ, attempts INTEGER NOT NULL DEFAULT 0);
      INSERT INTO split_lease
        SELECT message_id, stream_id, received_at, partition_number, priority, is_event,
               scheduled_for, processed_at, instance_id, lease_expiry, attempts
        FROM wide_inbox;

      ANALYZE wide_inbox; ANALYZE split_inbox; ANALYZE split_lease;";
    seed.Parameters.AddWithValue("streams", STREAMS);
    seed.Parameters.AddWithValue("per_stream", PER_STREAM);
    seed.CommandTimeout = 600;
    await seed.ExecuteNonQueryAsync(cancellationToken);
  }

  /// <summary>
  /// The gate in both forms, and the two directions of disagreement between them.
  /// </summary>
  private static string _gateComparison() => @"
    WITH wide AS (
      SELECT i.message_id FROM wide_inbox i
      WHERE i.processed_at IS NULL
        AND (i.instance_id IS NULL OR i.lease_expiry < NOW())
        AND (i.scheduled_for IS NULL OR i.scheduled_for <= NOW())
        AND NOT EXISTS (
          SELECT 1 FROM wide_inbox j
          WHERE j.stream_id = i.stream_id AND j.processed_at IS NULL AND j.is_event = TRUE
            AND (j.received_at, j.message_id) < (i.received_at, i.message_id)
            AND (j.instance_id IS NULL OR j.lease_expiry < NOW())
            AND (j.scheduled_for IS NULL OR j.scheduled_for <= NOW()))
    ),
    split AS (
      SELECT l.message_id FROM split_lease l
      WHERE l.processed_at IS NULL
        AND (l.instance_id IS NULL OR l.lease_expiry < NOW())
        AND (l.scheduled_for IS NULL OR l.scheduled_for <= NOW())
        AND NOT EXISTS (
          SELECT 1 FROM split_lease j
          WHERE j.stream_id = l.stream_id AND j.processed_at IS NULL AND j.is_event = TRUE
            AND (j.received_at, j.message_id) < (l.received_at, l.message_id)
            AND (j.instance_id IS NULL OR j.lease_expiry < NOW())
            AND (j.scheduled_for IS NULL OR j.scheduled_for <= NOW()))
    )
    SELECT (SELECT count(*) FROM wide),
           (SELECT count(*) FROM split),
           (SELECT count(*) FROM (SELECT message_id FROM wide
                                  EXCEPT SELECT message_id FROM split) a),
           (SELECT count(*) FROM (SELECT message_id FROM split
                                  EXCEPT SELECT message_id FROM wide) b)";

  private static async Task<long> _scalarAsync(
      NpgsqlConnection conn, string sql, CancellationToken cancellationToken) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 300;
    return (long?)await cmd.ExecuteScalarAsync(cancellationToken) ?? 0L;
  }
}
