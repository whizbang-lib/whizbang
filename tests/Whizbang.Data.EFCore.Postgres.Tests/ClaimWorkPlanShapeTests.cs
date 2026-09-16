using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// That the claim poll's cost is bounded by the batch it returns, not by the backlog it polls over.
/// </summary>
/// <remarks>
/// <para>
/// Every instance calls <c>claim_work</c> several times a second. Measured under a bulk load, one
/// call read tens of thousands of blocks and the queue tables were scanned whole thousands of
/// times an hour, so the poll alone took most of the database's cores and the backlog it was
/// polling over grew because of it.
/// </para>
/// <para>
/// The queue tables are empty between loads. A session that polled while they were empty carries
/// plans made for empty tables, and once the tables fill those plans scan them whole. The test
/// reproduces exactly that: poll on empty tables, fill them with wide rows leased elsewhere, poll
/// again on the same session, and read how many tuples the tables gave up for that one call.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
[Category("Shard2")]
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class ClaimWorkPlanShapeTests : EFCoreTestBase {
  private const int ROWS_PER_TABLE = 20_000;
  private const int BATCH = 100;
  /// <summary>Tuples a bounded poll may read from one table: a few batches, never the backlog.</summary>
  private const long READ_BUDGET_PER_TABLE = 4L * BATCH * 4;
  private const string OUTBOX = "wh_outbox";
  private const string INBOX = "wh_inbox";
  private const string PERSPECTIVE_EVENTS = "wh_perspective_events";

  private static async Task _heartbeatAsync(NpgsqlConnection connection, Guid instanceId) {
    await using var hb = connection.CreateCommand();
    hb.CommandText = @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)";
    hb.Parameters.AddWithValue("id", instanceId);
    await hb.ExecuteNonQueryAsync();
  }

  private static async Task<int> _claimAsync(NpgsqlConnection connection, Guid instanceId) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      SELECT count(*) FROM claim_work(
        p_instance_id => @id,
        p_service_name => 'test',
        p_host_name => 'test-host',
        p_process_id => 1,
        p_max_streams => @batch,
        p_partition_count => 10000,
        p_lease_seconds => 300
      )";
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue("batch", BATCH);
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// Wide rows. With a holder, all leased by that live instance, so the poller finds nothing to
  /// take; without one, all unowned, so the poller acquires from them.
  /// </summary>
  private static async Task _fillAsync(NpgsqlConnection connection, Guid? holder) {
    await using var fill = connection.CreateCommand();
    var lease = holder is null ? "NULL::timestamptz" : "NOW() + INTERVAL '5 minutes'";
    fill.CommandText = $@"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at,
         stream_id, partition_number, instance_id, lease_expiry)
      SELECT gen_random_uuid(), 'topic', 'TestEvent', jsonb_build_object('pad', repeat('x', 1500)), '{{}}',
             0, 0, NOW(), gen_random_uuid(), 0, @holder, {lease}
      FROM generate_series(1, @n);
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
         stream_id, partition_number, instance_id, lease_expiry)
      SELECT gen_random_uuid(), 'TestHandler', 'TestEvent', jsonb_build_object('pad', repeat('x', 1500)), '{{}}',
             0, 0, NOW(), gen_random_uuid(), 0, @holder, {lease}
      FROM generate_series(1, @n);
      INSERT INTO wh_perspective_events
        (stream_id, perspective_name, event_id, status, attempts, created_at, instance_id, lease_expiry)
      SELECT gen_random_uuid(), 'TestPerspective', gen_random_uuid(), 0, 0, NOW(), @holder, {lease}
      FROM generate_series(1, @n);";
    fill.Parameters.AddWithValue(nameof(holder), (object?)holder ?? DBNull.Value);
    fill.Parameters[nameof(holder)].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid;
    fill.Parameters.AddWithValue("n", ROWS_PER_TABLE);
    fill.CommandTimeout = 300;
    await fill.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// A poll that acquires from an unowned backlog reads a few batches of it, not all of it.
  /// </summary>
  /// <remarks>
  /// This is the shape a bulk load produces: the producer's outbox and the consumers' perspective
  /// events fill with unowned rows faster than the fleet takes them. Measured under one, each poll
  /// read tens of thousands of blocks, the queue tables were scanned whole several times per poll,
  /// and adding instances made it worse, because every instance paid the backlog on every poll.
  /// </remarks>
  [Test]
  [Timeout(600000)]
  public async Task ClaimWork_AcquiringFromAnUnownedBacklog_ReadsAFewBatchesNotTheBacklogAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync(cancellationToken);
    }
    var poller = Guid.NewGuid();
    await _heartbeatAsync(connection, poller);
    for (var i = 0; i < 6; i++) {
      await _claimAsync(connection, poller);
    }

    await _fillAsync(connection, holder: null);
    var before = await _sequentialTuplesReadAsync(connection);

    var claimed = await _claimAsync(connection, poller);

    var after = await _sequentialTuplesReadAsync(connection);
    await Assert.That(claimed).IsGreaterThan(0).Because("there is unowned work and the poller is alive");
    await Assert.That(claimed).IsLessThanOrEqualTo(3 * BATCH)
      .Because("a poll returns at most a batch per category");
    foreach (var table in new[] { OUTBOX, INBOX, PERSPECTIVE_EVENTS }) {
      var read = after[table] - before[table];
      await Assert.That(read).IsLessThan(READ_BUDGET_PER_TABLE)
        .Because($"one poll read {read} tuples of {table} sequentially while acquiring from {ROWS_PER_TABLE} unowned rows; "
          + "acquisition bounded by its batch walks an index to the first eligible rows, and one bounded by the "
          + "backlog ranks every pending row on every poll of every instance");
    }
  }

  /// <summary>Tuples read by sequential scans per table, after the reporting backend flushed its counters.</summary>
  private async Task<Dictionary<string, long>> _sequentialTuplesReadAsync(NpgsqlConnection reporter) {
    await using (var flush = reporter.CreateCommand()) {
      flush.CommandText = "SELECT pg_stat_force_next_flush()";
      await flush.ExecuteNonQueryAsync();
    }
    await using var observer = new NpgsqlConnection(ConnectionString);
    await observer.OpenAsync();
    await using var cmd = observer.CreateCommand();
    cmd.CommandText = @"
      SELECT relname, seq_tup_read FROM pg_stat_user_tables
      WHERE relname IN ('wh_outbox', 'wh_inbox', 'wh_perspective_events')";
    var counts = new Dictionary<string, long>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      counts[reader.GetString(0)] = reader.GetInt64(1);
    }
    return counts;
  }

  /// <summary>
  /// A session that polled empty tables must not scan them whole once they fill.
  /// </summary>
  [Test]
  [Timeout(600000)]
  public async Task ClaimWork_AfterTheTablesFill_ReadsAFewBatchesNotTheBacklogAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync(cancellationToken);
    }
    var poller = Guid.NewGuid();
    var holder = Guid.NewGuid();
    await _heartbeatAsync(connection, poller);
    await _heartbeatAsync(connection, holder);

    // Enough polls on empty tables for the session to settle on the plans it will keep.
    for (var i = 0; i < 6; i++) {
      await _claimAsync(connection, poller);
    }

    await _fillAsync(connection, holder: holder);
    var before = await _sequentialTuplesReadAsync(connection);

    var claimed = await _claimAsync(connection, poller);

    var after = await _sequentialTuplesReadAsync(connection);
    await Assert.That(claimed).IsEqualTo(0)
      .Because("every row is leased by a live peer, so the poll has nothing to take and its cost is pure overhead");
    foreach (var table in new[] { OUTBOX, INBOX, PERSPECTIVE_EVENTS }) {
      var read = after[table] - before[table];
      await Assert.That(read).IsLessThan(READ_BUDGET_PER_TABLE)
        .Because($"one poll read {read} tuples of {table} sequentially with {ROWS_PER_TABLE} rows queued; "
          + "a poll bounded by its batch reads a few hundred, and one bounded by the backlog reads them all "
          + "several times a second on every instance");
    }
  }
}
