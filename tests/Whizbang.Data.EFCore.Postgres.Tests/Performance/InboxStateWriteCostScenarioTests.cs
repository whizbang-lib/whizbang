using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What a claim costs to write, on the wide row it used to live on versus the narrow state table it
/// lives on now.
/// </summary>
/// <remarks>
/// <para>
/// This is the split's headline number and it had no committed measurement: the 69 percent came from
/// an ad-hoc lab run, and the state table has since gained five indexes to restore the priority
/// lanes, which is exactly the thing that number is sensitive to. A claim that cannot be re-measured
/// after every change to the index set is a claim nobody can trust, so it is measured here.
/// </para>
/// <para>
/// Both sides are built in the test rather than read from the live schema, because the point of
/// comparison no longer exists: after the cutover, wh_inbox has no mutable columns to stamp. So the
/// BEFORE side is reconstructed - a row of the same width carrying the same ten columns, with the
/// sixteen indexes the cutover removed - and the AFTER side is the real wh_inbox_state with whatever
/// index set it currently declares. Synthetic on identity, faithful on the two dimensions that drive
/// the cost: row width and index count.
/// </para>
/// <para>
/// The measure is blocks per row stamped, never wall clock. An update's cost is the heap page it
/// dirties plus one maintenance write per index whose column it touches, and PostgreSQL can only
/// take its cheap in-place path when an update touches NO indexed column and the page has room.
/// Claiming writes indexed columns by definition, so the cheap path is unreachable and the index
/// count IS the cost.
/// </para>
/// <para>
/// Autovacuum is off and nothing is vacuumed, for the reason in <see cref="WorkCostRecorder"/>: a
/// vacuumed table is all-visible and measures a shape a live queue never has.
/// </para>
/// </remarks>
[Category("Benchmark")]
[Category("Performance")]
public class InboxStateWriteCostScenarioTests : EFCoreTestBase {
  /// <summary>Rows seeded on each side. Large enough that index maintenance dominates.</summary>
  private const int ROWS = 20_000;
  /// <summary>Rows stamped per measured batch, as a claim stamps a batch.</summary>
  private const int BATCH = 500;

  [Test]
  [Timeout(1800000)]
  public async Task ClaimWriteCost_NarrowStateRowVersusWideInboxRow_ReportAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    var baseline = PerformanceBaseline.Load(PerformanceBaseline.DefaultPath);
    var report = new PerformanceBaseline.Report(baseline, "Claim write cost, narrow state row vs wide inbox row");

    await _seedWideAsync(conn);
    await _seedStateAsync(conn);

    var wide = await _stampAsync(conn, "wh_inbox_wide_before", cancellationToken);
    var narrow = await _stampAsync(conn, "wh_inbox_state", cancellationToken);

    report.Measure("claim_write.wide_row.blocks_per_row", wide.BlocksPerRow, "blocks/row");
    report.Measure("claim_write.state_row.blocks_per_row", narrow.BlocksPerRow, "blocks/row");
    report.Measure("claim_write.wide_row.indexes", wide.Indexes, "indexes");
    report.Measure("claim_write.state_row.indexes", narrow.Indexes, "indexes");
    var reduction = wide.BlocksPerRow <= 0 ? 0 : (1 - (narrow.BlocksPerRow / wide.BlocksPerRow)) * 100;
    report.Measure("claim_write.reduction_percent", reduction, "percent");

    var rendered = report.Render();
    Console.WriteLine(rendered);
    Console.WriteLine($"wide: {wide.Indexes} indexes, {wide.BlocksPerRow:F1} blocks/row");
    Console.WriteLine($"state: {narrow.Indexes} indexes, {narrow.BlocksPerRow:F1} blocks/row");
    Console.WriteLine("Baseline lines for this run:\n" + report.RenderBaselineLines());

    // The scenario has to have cost something on the wide side, or the ratio below compares two
    // cheap numbers and reports a win that was never available.
    await Assert.That(wide.BlocksPerRow).IsGreaterThan(1.0)
      .Because("stamping a claim on a wide row with every moved index in place has to cost more than "
        + "one block per row, or the seed has not reproduced the shape the split exists to leave");
    // The property, not a machine number: the narrow row must still be materially cheaper. Stated as
    // a ratio so it survives a faster disk, and loose enough that adding ONE lane index does not
    // flap it -- but tight enough that restoring all eleven orphans would fail here, which is the
    // regression this test is really guarding.
    await Assert.That(narrow.BlocksPerRow * 1.5).IsLessThan(wide.BlocksPerRow)
      .Because($"the split's whole justification is that a claim writes less: wide is "
        + $"{wide.BlocksPerRow:F1} blocks/row over {wide.Indexes} indexes, state is "
        + $"{narrow.BlocksPerRow:F1} over {narrow.Indexes}. If this fails, the state table's index "
        + "set has grown back toward the wide row's and the write win has been traded away");
    await Assert.That(report.Breaches).IsEmpty().Because($"a declared ceiling was passed. {rendered}");
  }

  private sealed record Stamp(double BlocksPerRow, int Indexes);

  /// <summary>
  /// Stamps a claim on <paramref name="table"/> and returns blocks dirtied per row. Reads the block
  /// counters either side of the update from pg_statio_user_tables, which counts the heap and index
  /// blocks the statement actually touched.
  /// </summary>
  private static async Task<Stamp> _stampAsync(
      NpgsqlConnection conn, string table, CancellationToken cancellationToken) {
    await using (var reset = conn.CreateCommand()) {
      reset.CommandText = "SELECT pg_stat_force_next_flush()";
      await reset.ExecuteNonQueryAsync(cancellationToken);
    }
    var before = await _blocksAsync(conn, table, cancellationToken);

    await using (var stamp = conn.CreateCommand()) {
      // A claim stamps ownership and a deadline. Both columns are indexed on either side, which is
      // what makes the cheap in-place path unreachable and the index count the cost.
      stamp.CommandText = $@"
        UPDATE {table}
           SET instance_id = gen_random_uuid(),
               lease_expiry = NOW() + INTERVAL '5 minutes',
               attempts = attempts + 1
         WHERE message_id IN (
           SELECT message_id FROM {table} WHERE instance_id IS NULL LIMIT @batch)";
      stamp.Parameters.AddWithValue("batch", BATCH);
      stamp.CommandTimeout = 600;
      await stamp.ExecuteNonQueryAsync(cancellationToken);
    }

    await using (var flush = conn.CreateCommand()) {
      flush.CommandText = "SELECT pg_stat_force_next_flush()";
      await flush.ExecuteNonQueryAsync(cancellationToken);
    }
    var after = await _blocksAsync(conn, table, cancellationToken);

    await using var idx = conn.CreateCommand();
    idx.CommandText = "SELECT count(*)::int FROM pg_indexes WHERE tablename = @t";
    idx.Parameters.AddWithValue("t", table);
    var indexes = (int)(await idx.ExecuteScalarAsync(cancellationToken))!;
    return new Stamp((double)(after - before) / BATCH, indexes);
  }

  private static async Task<long> _blocksAsync(
      NpgsqlConnection conn, string table, CancellationToken cancellationToken) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT coalesce(heap_blks_read,0) + coalesce(heap_blks_hit,0)
           + coalesce(idx_blks_read,0) + coalesce(idx_blks_hit,0)
      FROM pg_statio_user_tables WHERE relname = @t";
    cmd.Parameters.AddWithValue("t", table);
    var v = await cmd.ExecuteScalarAsync(cancellationToken);
    return v is null or DBNull ? 0L : Convert.ToInt64(v, CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// The pre-cutover shape, reconstructed: the ten moved columns on a row padded to the width the
  /// inbox row actually had, carrying the sixteen indexes the cutover removed.
  /// </summary>
  private static async Task _seedWideAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      DROP TABLE IF EXISTS wh_inbox_wide_before;
      CREATE TABLE wh_inbox_wide_before (
        message_id       UUID PRIMARY KEY,
        stream_id        UUID,
        received_at      TIMESTAMPTZ NOT NULL,
        priority         INTEGER NOT NULL DEFAULT 100,
        is_event         BOOLEAN NOT NULL DEFAULT FALSE,
        partition_number INTEGER,
        processed_at     TIMESTAMPTZ,
        instance_id      UUID,
        lease_expiry     TIMESTAMPTZ,
        attempts         INTEGER NOT NULL DEFAULT 0,
        scheduled_for    TIMESTAMPTZ,
        failure_reason   INTEGER,
        error            TEXT,
        status           INTEGER NOT NULL DEFAULT 0,
        chain_emitted_at TIMESTAMPTZ,
        -- the payload that made the row 2,070 bytes wide
        event_data       JSONB NOT NULL,
        metadata         JSONB NOT NULL,
        handler_name     VARCHAR(500) NOT NULL,
        message_type     VARCHAR(500) NOT NULL
      );
      ALTER TABLE wh_inbox_wide_before SET (autovacuum_enabled = false);

      INSERT INTO wh_inbox_wide_before
        (message_id, stream_id, received_at, priority, is_event, partition_number, attempts,
         event_data, metadata, handler_name, message_type)
      SELECT gen_random_uuid(),
             ('00000000-0000-0000-0000-' || lpad((s % 200)::text, 12, '0'))::uuid,
             NOW() - (s || ' seconds')::interval,
             CASE WHEN s % 3 = 0 THEN 50 WHEN s % 3 = 1 THEN 150 ELSE 250 END,
             TRUE, s % 10, 0,
             jsonb_build_object('pad', repeat('x', 1500)), '{}'::jsonb, 'TestHandler', 'TestEvent'
      FROM generate_series(1, @rows) s;

      -- The sixteen indexes the cutover removed, as they were.
      CREATE INDEX ON wh_inbox_wide_before (processed_at);
      CREATE INDEX ON wh_inbox_wide_before (received_at);
      CREATE INDEX ON wh_inbox_wide_before (lease_expiry) WHERE lease_expiry IS NOT NULL;
      CREATE INDEX ON wh_inbox_wide_before (status, lease_expiry);
      CREATE INDEX ON wh_inbox_wide_before (failure_reason);
      CREATE INDEX ON wh_inbox_wide_before (stream_id, scheduled_for, received_at) WHERE scheduled_for IS NOT NULL;
      CREATE INDEX ON wh_inbox_wide_before (partition_number, scheduled_for, received_at);
      CREATE INDEX ON wh_inbox_wide_before (instance_id, lease_expiry) WHERE instance_id IS NOT NULL;
      CREATE INDEX ON wh_inbox_wide_before (instance_id) WHERE instance_id IS NOT NULL;
      CREATE INDEX ON wh_inbox_wide_before (stream_id) WHERE (status & 2) != 2;
      CREATE INDEX ON wh_inbox_wide_before (attempts) WHERE processed_at IS NULL AND attempts > 5;
      CREATE INDEX ON wh_inbox_wide_before (instance_id, message_id) WHERE processed_at IS NULL;
      CREATE INDEX ON wh_inbox_wide_before (instance_id, lease_expiry, processed_at);
      CREATE INDEX ON wh_inbox_wide_before (partition_number, instance_id, lease_expiry) WHERE processed_at IS NULL;
      CREATE INDEX ON wh_inbox_wide_before (stream_id, received_at, message_id)
        INCLUDE (instance_id, lease_expiry, scheduled_for, partition_number) WHERE processed_at IS NULL;
      CREATE INDEX ON wh_inbox_wide_before (received_at, message_id)
        INCLUDE (stream_id, instance_id, lease_expiry, scheduled_for, partition_number)
        WHERE processed_at IS NULL AND is_event = TRUE;

      ANALYZE wh_inbox_wide_before;";
    cmd.Parameters.AddWithValue("rows", ROWS);
    cmd.CommandTimeout = 900;
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// The real state table, seeded to the same row count. Its index set is whatever the migration
  /// declares, which is the point: this measure moves when that set changes.
  /// </summary>
  private static async Task _seedStateAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      ALTER TABLE wh_inbox SET (autovacuum_enabled = false);
      ALTER TABLE wh_inbox_state SET (autovacuum_enabled = false);

      WITH m AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id,
           is_event, priority)
        SELECT gen_random_uuid(), 'TestHandler', 'TestEvent',
               jsonb_build_object('pad', repeat('x', 1500)), '{}'::jsonb,
               NOW() - (s || ' seconds')::interval,
               ('00000000-0000-0000-0000-' || lpad((s % 200)::text, 12, '0'))::uuid,
               TRUE,
               CASE WHEN s % 3 = 0 THEN 50 WHEN s % 3 = 1 THEN 150 ELSE 250 END
        FROM generate_series(1, @rows) s
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, partition_number, status, attempts)
      SELECT message_id, stream_id, received_at, priority, is_event, 0, 1, 0 FROM m;

      ANALYZE wh_inbox; ANALYZE wh_inbox_state;";
    cmd.Parameters.AddWithValue("rows", ROWS);
    cmd.CommandTimeout = 900;
    await cmd.ExecuteNonQueryAsync();
  }
}
