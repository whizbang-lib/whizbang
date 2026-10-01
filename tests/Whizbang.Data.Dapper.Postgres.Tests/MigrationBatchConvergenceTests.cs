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
