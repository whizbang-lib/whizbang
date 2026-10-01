using System;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// That a batched backfill moves a bounded slice per call and stops, against a real database.
/// </summary>
/// <remarks>
/// <para>
/// The unit cover proves a migration is split into the right pieces. It cannot prove the piece
/// terminates: that depends on the statement excluding the rows it already handled, which is a
/// property of the SQL rather than of the splitting. A region that does not exclude them reports the
/// same rows forever, and the failure shows up as a service that never finishes starting rather than
/// as an error — the exact shape this whole mechanism exists to prevent.
/// </para>
/// <para>
/// The bound matters as much as the termination. A call that ignores its limit and does the whole
/// table would pass a convergence check while reintroducing the single long statement.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/008_CreateMessageAssociationRegistry.sql</code-under-test>
public class MigrationBatchConvergenceTests : PostgresTestBase {

  private const int ROWS = 7;
  private const int LIMIT = 2;

  private static async Task _seedUnnormalizedAsync(NpgsqlConnection connection) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO public.wh_message_associations
        (message_type, association_type, target_name, service_name, normalized_message_type,
         created_at, updated_at)
      SELECT
        'Some.Namespace.Type' || g::TEXT || ', Some.Assembly',
        'perspective',
        'Target' || g::TEXT,
        'a-service',
        NULL,
        NOW(), NOW()
      FROM generate_series(1, @rows) AS g;";
    cmd.Parameters.AddWithValue("rows", ROWS);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _execAsync(NpgsqlConnection connection, string sql) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection connection, string sql) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    var value = await cmd.ExecuteScalarAsync();
    return value is null or DBNull ? 0L : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
  }

  [Test]
  public async Task TheBackfillMovesOneBoundedSliceAtATimeAndThenStopsAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // The migration pass already ran against an empty table, so anything here is what we seeded.
    await _execAsync(connection, "DELETE FROM public.wh_message_associations;");
    await _seedUnnormalizedAsync(connection);

    var remaining = await _scalarAsync(connection,
      "SELECT count(*) FROM public.wh_message_associations WHERE normalized_message_type IS NULL;");
    await Assert.That(remaining).IsEqualTo(ROWS);

    var passes = 0;
    long moved;
    do {
      moved = await _scalarAsync(connection,
        $"SELECT public.wh_backfill_normalized_message_type_batch({LIMIT});");

      // Never more than it was asked for: the bound is the whole point.
      await Assert.That(moved).IsLessThanOrEqualTo(LIMIT);

      if (moved > 0) {
        passes++;
      }

      await Assert.That(passes).IsLessThanOrEqualTo(ROWS + 1);
    } while (moved > 0);

    // 7 rows at 2 per call is 4 calls: three full and one of one.
    await Assert.That(passes).IsEqualTo(4);

    var left = await _scalarAsync(connection,
      "SELECT count(*) FROM public.wh_message_associations WHERE normalized_message_type IS NULL;");
    await Assert.That(left).IsEqualTo(0L);
  }

  [Test]
  public async Task TheBackfillReportsNothingWhenThereIsNothingToDoAsync() {
    // The runner reads zero as "stop", so a backfill with no work must report zero on the first
    // call rather than, say, the row count of the table.
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    await _execAsync(connection, "DELETE FROM public.wh_message_associations;");

    var moved = await _scalarAsync(connection,
      $"SELECT public.wh_backfill_normalized_message_type_batch({LIMIT});");

    await Assert.That(moved).IsEqualTo(0L);
  }


  private static async Task<string> _textAsync(NpgsqlConnection connection, string sql) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    var sb = new System.Text.StringBuilder();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      sb.AppendLine(reader.GetValue(0)?.ToString());
    }
    return sb.ToString();
  }

  /// <summary>
  /// Puts the store back into its pre-split shape and rebuilds the split's scratch tables.
  /// </summary>
  /// <remarks>
  /// The fixture applies every migration, so by the time a test runs 078 has dropped the inline
  /// body columns and 077 has dropped its worklist — which means the backfill could only ever be
  /// exercised on its replay path, where it returns zero without doing anything. That is why a
  /// quadratic slice shipped: nothing ran it against rows. Restoring the shape here is what lets
  /// the real shipped function be driven with data.
  /// </remarks>
  /// <summary>
  /// Fails loudly if the database is not running the migration under test.
  /// </summary>
  /// <remarks>
  /// Measuring the wrong function is the easiest way to draw a confident wrong conclusion: three
  /// separate implementations once produced block counts identical to six figures, because none of
  /// them was the code actually installed. Asserting the shape up front makes that impossible.
  /// </remarks>
  private static async Task _assertWorklistFunctionInstalledAsync(NpgsqlConnection connection) {
    var marker = await _scalarAsync(connection, @"
      SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
      WHERE p.proname = 'wh_backfill_event_bodies_batch' AND n.nspname = 'public'
        AND p.prosrc LIKE '%wh_body_split_todo%';");
    await Assert.That(marker).IsEqualTo(1L)
      .Because("the installed backfill must be the worklist version, or the measurement is of other code");
  }

  private static async Task _restorePreSplitShapeAsync(NpgsqlConnection connection) {
    await _assertWorklistFunctionInstalledAsync(connection);
    await _execAsync(connection, @"
      ALTER TABLE public.wh_event_store ADD COLUMN IF NOT EXISTS event_data JSONB;
      ALTER TABLE public.wh_event_store ADD COLUMN IF NOT EXISTS metadata JSONB;
      DROP TABLE IF EXISTS public.wh_body_split_todo;
      DROP TABLE IF EXISTS public.wh_body_split_cursor;
      CREATE TABLE public.wh_body_split_todo (
        seq      BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
        event_id UUID NOT NULL
      );
      CREATE TABLE public.wh_body_split_cursor (
        only_row BOOLEAN PRIMARY KEY DEFAULT TRUE CHECK (only_row),
        last_seq BIGINT NOT NULL DEFAULT 0
      );
      INSERT INTO public.wh_body_split_cursor (only_row, last_seq) VALUES (TRUE, 0);
      DELETE FROM public.wh_event_body;
      DELETE FROM public.wh_event_store;");
  }

  private static async Task _dropPreSplitShapeAsync(NpgsqlConnection connection) {
    await _execAsync(connection, @"
      DELETE FROM public.wh_event_body;
      DELETE FROM public.wh_event_store;
      DROP TABLE IF EXISTS public.wh_body_split_todo;
      DROP TABLE IF EXISTS public.wh_body_split_cursor;
      ALTER TABLE public.wh_event_store DROP COLUMN IF EXISTS event_data;
      ALTER TABLE public.wh_event_store DROP COLUMN IF EXISTS metadata;");
  }

  private static async Task _seedBodiesAsync(NpgsqlConnection connection, int rows) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO public.wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, version,
         event_data, metadata, created_at)
      SELECT
        ('01900000-0000-7000-8000-' || lpad(g::text, 12, '0'))::uuid,
        gen_random_uuid(),
        gen_random_uuid(),
        'Some.Aggregate, Some.Assembly',
        'Some.Event, Some.Assembly',
        g,
        ('{""n"":' || g || '}')::jsonb,
        '{}'::jsonb,
        NOW()
      FROM generate_series(1, @rows) AS g;
      INSERT INTO public.wh_body_split_todo (event_id)
      SELECT event_id FROM public.wh_event_store WHERE event_data IS NOT NULL ORDER BY event_id;";
    cmd.Parameters.AddWithValue("rows", rows);
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task TheBackfillClaimsEachWorklistEntryExactlyOnceAndThenStopsAsync() {
    const int rows = 50;
    const int limit = 10;

    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    try {
      await _restorePreSplitShapeAsync(connection);
      await _seedBodiesAsync(connection, rows);

      var cursors = new System.Collections.Generic.List<long>();
      var calls = 0;
      long moved;
      do {
        moved = await _scalarAsync(connection,
          $"SELECT public.wh_backfill_event_bodies_batch({limit});");
        await Assert.That(moved).IsLessThanOrEqualTo(limit);
        calls++;
        cursors.Add(await _scalarAsync(connection,
          "SELECT last_seq FROM public.wh_body_split_cursor WHERE only_row;"));
        await Assert.That(calls).IsLessThanOrEqualTo(rows);
      } while (moved > 0);

      // One call per slice, plus the call that finds the worklist exhausted. More than that means
      // slices overlap or the cursor is not advancing — the shape that made this quadratic.
      await Assert.That(calls).IsEqualTo(rows / limit + 1);

      // The cursor only ever moves forward, so no entry is claimed twice.
      for (var i = 1; i < cursors.Count; i++) {
        await Assert.That(cursors[i] >= cursors[i - 1]).IsTrue();
      }

      var left = await _scalarAsync(connection,
        "SELECT count(*) FROM public.wh_event_store WHERE event_data IS NOT NULL;");
      await Assert.That(left).IsEqualTo(0L);
      var bodies = await _scalarAsync(connection, "SELECT count(*) FROM public.wh_event_body;");
      await Assert.That(bodies).IsEqualTo((long)rows);
    } finally {
      await _dropPreSplitShapeAsync(connection);
    }
  }

  [Test]
  public async Task TheSliceIsAnIndexRangeOverTheWorklistNotAScanOfTheStoreAsync() {
    // The regression guard. Convergence cannot see the defect that shipped: over fifty rows a
    // sequential scan is free, so a slice costing the whole table still passes. What distinguishes
    // the two is the plan — the slice must be an index range on the worklist, never a scan.
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    try {
      await _restorePreSplitShapeAsync(connection);
      await _seedBodiesAsync(connection, 200);
      await _execAsync(connection,
        "ANALYZE public.wh_body_split_todo; ANALYZE public.wh_event_store;");

      var plan = await _textAsync(connection, @"
        EXPLAIN (COSTS OFF)
        SELECT seq FROM public.wh_body_split_todo WHERE seq > 0 ORDER BY seq LIMIT 10;");

      await Assert.That(plan).DoesNotContain("Seq Scan");
      await Assert.That(plan).Contains("wh_body_split_todo_pkey");
    } finally {
      await _dropPreSplitShapeAsync(connection);
    }
  }

  [Test]
  public async Task AClaimedSliceThatMovesNothingStillAdvancesThePassAsync() {
    // A slice can legitimately move no rows — a replay where the bodies are already copied. If the
    // function reported that slice's row count the runner would stop with work outstanding, so it
    // reports at least one until the worklist is exhausted.
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    try {
      await _restorePreSplitShapeAsync(connection);
      await _seedBodiesAsync(connection, 20);

      // Pre-copy every body and clear the inline columns, so each slice finds nothing to do.
      await _execAsync(connection, @"
        INSERT INTO public.wh_event_body (event_id, event_data, metadata)
        SELECT event_id, event_data, metadata FROM public.wh_event_store
        ON CONFLICT DO NOTHING;
        UPDATE public.wh_event_store SET event_data = NULL, metadata = NULL;");

      var calls = 0;
      long moved;
      do {
        moved = await _scalarAsync(connection, "SELECT public.wh_backfill_event_bodies_batch(5);");
        calls++;
        await Assert.That(calls).IsLessThanOrEqualTo(10);
      } while (moved > 0);

      // 20 entries at 5 per slice is four slices, then the call that finds the worklist exhausted.
      await Assert.That(calls).IsEqualTo(5);
    } finally {
      await _dropPreSplitShapeAsync(connection);
    }
  }

  // Rows read sequentially, which is the property itself: a pass that never re-reads the store
  // reads almost nothing this way, and one that re-evaluates a predicate per slice reads the table
  // once per slice. Buffer counts and scan counts both fail to separate the two -- buffers pick up
  // unrelated activity, and scan counts are dominated by one-row bookkeeping tables.
  private const string SEQ_TUP_SQL = @"
    SELECT coalesce(sum(seq_tup_read),0)::bigint FROM pg_stat_user_tables
    WHERE relname IN ('wh_event_store','wh_event_body','wh_body_split_todo','wh_body_split_cursor');";

  [Test]
  public async Task TheWholePassCostsBlocksInProportionToTheRowsNotTheirSquareAsync() {
    // This is the assertion the shipped defect fails. It bounded each statement but not the work:
    // the slice came from a predicate re-evaluated against the store, so by the end of the pass each
    // call was reading most of the table again. Convergence cannot see it -- over fifty rows a scan
    // is free -- and neither can per-slice sampling, because pg_statio does not flush reliably
    // inside a short window and a nulled row is rewritten smaller rather than moved out of the way.
    // Totalling the blocks over the whole pass avoids both problems: the figure is large, it is
    // flushed by the time the pass ends, and it separates linear from quadratic by more than an
    // order of magnitude.
    const int rows = 200000;
    const int limit = 1000;

    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    try {
      await _restorePreSplitShapeAsync(connection);
      await _seedBodiesAsync(connection, rows);
      await _execAsync(connection, "ANALYZE public.wh_event_store; ANALYZE public.wh_body_split_todo;");

      await Task.Delay(1500);
      await _execAsync(connection, "SELECT pg_stat_clear_snapshot();");
      var before = await _scalarAsync(connection, SEQ_TUP_SQL);

      var calls = 0;
      long moved;
      do {
        moved = await _scalarAsync(connection,
          $"SELECT public.wh_backfill_event_bodies_batch({limit});");
        calls++;
        await Assert.That(calls).IsLessThanOrEqualTo(rows / limit + 5);
      } while (moved > 0);

      await Task.Delay(1500);
      await _execAsync(connection, "SELECT pg_stat_clear_snapshot();");
      var seqRows = await _scalarAsync(connection, SEQ_TUP_SQL) - before;
      Console.WriteLine($"[pass cost] rows={rows} limit={limit} calls={calls} seq_tup_read={seqRows}");

      // Measured, by mutating this file back to the design it replaces: the worklist reads ~400
      // rows sequentially across the whole pass, the predicate version ~763,000. The bound sits
      // fifty times above the former and nearly forty below the latter, so it fails a partial
      // return to that shape and not merely a complete one.
      await Assert.That(seqRows).IsLessThan((long)rows / 10);

      var left = await _scalarAsync(connection,
        "SELECT count(*) FROM public.wh_event_store WHERE event_data IS NOT NULL;");
      await Assert.That(left).IsEqualTo(0L);
      var bodies = await _scalarAsync(connection, "SELECT count(*) FROM public.wh_event_body;");
      await Assert.That(bodies).IsEqualTo((long)rows);
    } finally {
      await _dropPreSplitShapeAsync(connection);
    }
  }

  [Test]
  public async Task TheGuardedBackfillsReportZeroRatherThanFailingAsync() {
    // 063 and 077 guard on state that a replay may have already changed. Reporting zero is what
    // lets the runner stop; throwing would fail the migration on every replay.
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    var aggregate = await _scalarAsync(connection,
      "SELECT public.wh_normalize_aggregate_type_batch(10);");
    var bodies = await _scalarAsync(connection,
      "SELECT public.wh_backfill_event_bodies_batch(10);");
    var inboxState = await _scalarAsync(connection,
      "SELECT public.wh_seed_inbox_state_batch(10);");
    var inboxColumns = await _scalarAsync(connection,
      "SELECT public.wh_backfill_inbox_source_columns_batch(10);");

    await Assert.That(aggregate).IsEqualTo(0L);
    await Assert.That(bodies).IsEqualTo(0L);
    await Assert.That(inboxState).IsEqualTo(0L);
    await Assert.That(inboxColumns).IsEqualTo(0L);
  }
}
