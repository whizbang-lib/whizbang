using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A stream deeper than the claim's row bound is leased across several polls, in order, with no
/// event skipped and no hole left behind.
/// </summary>
/// <remarks>
/// <para>
/// The perspective acquisition used to select a batch of streams and then lease every pending event
/// of each one, so a stream always arrived in a single lease. Bounding it by rows as well as by
/// streams is what stops a poll costing whatever a consumer's streams happen to hold, and it means a
/// deep stream is now captured over consecutive polls instead.
/// </para>
/// <para>
/// That is only safe if the order survives, and per-stream order is the one thing a perspective
/// cannot tolerate losing: a fold applied out of order produces a document that no replay would
/// reproduce. So this asserts the property directly rather than trusting the argument for it. The
/// events of one stream are leased a slice at a time, each slice is checked to be the next
/// contiguous run, and the concatenation of every slice is compared against the stream's events in
/// <c>event_id</c> order.
/// </para>
/// <para>
/// <c>event_id</c> is the right order to compare against because it is a version 7 identifier, so
/// it sorts chronologically, and it is the order the acquisition itself walks and the drain applies.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
[Category("Shard2")]
[Category("Integration")]
public class PerspectivePartialLeaseOrderingTests : EFCoreTestBase {
  /// <summary>Events on the one stream: several times the row bound, so it takes several polls.</summary>
  private const int STREAM_DEPTH = 24;
  /// <summary>The claim's batch and row bound, so each poll can take only a slice.</summary>
  private const int BATCH = 4;

  [Test]
  [Timeout(600000)]
  public async Task PerspectiveAcquisition_StreamDeeperThanTheRowBound_IsLeasedInOrderAcrossPollsAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }

    var streamId = Guid.NewGuid();
    var poller = await _seedOneDeepStreamAsync(conn, streamId);
    var expected = await _eventIdsInOrderAsync(conn, streamId);
    await Assert.That(expected.Count).IsEqualTo(STREAM_DEPTH)
      .Because("the fixture has to hold the depth the rest of the test reasons about");

    // Poll, take what was leased, complete it, and poll again: the loop a drain actually runs.
    var leasedInOrder = new List<Guid>();
    for (var poll = 0; poll < STREAM_DEPTH && leasedInOrder.Count < STREAM_DEPTH; poll++) {
      await _claimAsync(conn, poller, cancellationToken);
      var slice = await _leasedEventIdsInOrderAsync(conn, streamId, poller);
      if (slice.Count == 0) {
        continue;
      }

      // The slice must begin exactly where the last one ended: no gap, no overlap, no reordering.
      var expectedSlice = expected.Skip(leasedInOrder.Count).Take(slice.Count).ToList();
      await Assert.That(slice).IsEquivalentTo(expectedSlice)
        .Because($"poll {poll} leased {slice.Count} events of a {STREAM_DEPTH}-event stream after "
          + $"{leasedInOrder.Count} were already done; a partial lease has to take the next contiguous "
          + "run from the stream's head, because a perspective that folds events out of order produces a "
          + "document no replay would reproduce");

      leasedInOrder.AddRange(slice);
      await _completeAsync(conn, slice);
    }

    await Assert.That(leasedInOrder.Count).IsEqualTo(STREAM_DEPTH)
      .Because("every event of the stream has to be leased eventually; a bound that strands the tail of a "
        + "deep stream would be worse than the cost it was added to remove");
    await Assert.That(leasedInOrder).IsEquivalentTo(expected)
      .Because("the concatenation of the slices has to equal the stream in event_id order -- that is what "
        + "makes leasing a deep stream over several polls indistinguishable, to the fold, from leasing it "
        + "in one");
  }

  [Test]
  [Timeout(600000)]
  public async Task PerspectiveAcquisition_StreamDeeperThanTheRowBound_LeasesNoMoreThanTheBoundAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }

    var streamId = Guid.NewGuid();
    var poller = await _seedOneDeepStreamAsync(conn, streamId);

    await _claimAsync(conn, poller, cancellationToken);
    var leased = await _leasedEventIdsInOrderAsync(conn, streamId, poller);

    await Assert.That(leased.Count).IsLessThanOrEqualTo(BATCH)
      .Because($"one poll of a {STREAM_DEPTH}-event stream with a row bound of {BATCH} must lease at most "
        + $"{BATCH} events; it used to lease all {STREAM_DEPTH}, which is what made a poll cost whatever a "
        + "consumer's streams happened to hold");
    await Assert.That(leased.Count).IsGreaterThan(0)
      .Because("the bound has to admit work, not refuse it: a poll that leases nothing would satisfy the "
        + "ceiling above and starve the drain");
  }

  /// <summary>One stream carrying more events than the row bound, all unowned and due.</summary>
  private static async Task<Guid> _seedOneDeepStreamAsync(NpgsqlConnection conn, Guid streamId) {
    var poller = Guid.NewGuid();
    await using var seed = conn.CreateCommand();
    seed.CommandText = @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@poller, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb);

      INSERT INTO wh_perspective_events
        (stream_id, perspective_name, event_id, status, attempts, created_at, partition_number)
      -- Ids built from the counter so event_id order IS insertion order, which is the property the
      -- assertions compare against. A v7 id would do the same in production; this makes the fixture
      -- independent of the server having uuidv7().
      SELECT @sid, 'TestPerspective',
             ('00000000-0000-7000-8000-' || lpad(r::text, 12, '0'))::uuid,
             0, 0, NOW() + (r * INTERVAL '1 millisecond'), compute_partition(@sid)
      FROM generate_series(1, @depth) r;";
    seed.Parameters.AddWithValue("poller", poller);
    seed.Parameters.AddWithValue("sid", streamId);
    seed.Parameters.AddWithValue("depth", STREAM_DEPTH);
    seed.CommandTimeout = 300;
    await seed.ExecuteNonQueryAsync();
    return poller;
  }

  private static async Task<List<Guid>> _eventIdsInOrderAsync(NpgsqlConnection conn, Guid streamId) =>
    await _readEventIdsAsync(conn,
      "SELECT event_id FROM wh_perspective_events WHERE stream_id = @sid ORDER BY event_id",
      cmd => cmd.Parameters.AddWithValue("sid", streamId));

  private static async Task<List<Guid>> _leasedEventIdsInOrderAsync(
      NpgsqlConnection conn, Guid streamId, Guid poller) =>
    await _readEventIdsAsync(conn, @"
        SELECT event_id FROM wh_perspective_events
        WHERE stream_id = @sid AND instance_id = @poller
          AND lease_expiry > NOW() AND processed_at IS NULL
        ORDER BY event_id",
      cmd => {
        cmd.Parameters.AddWithValue("sid", streamId);
        cmd.Parameters.AddWithValue("poller", poller);
      });

  private static async Task<List<Guid>> _readEventIdsAsync(
      NpgsqlConnection conn, string sql, Action<NpgsqlCommand> bind) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    bind(cmd);
    var ids = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  /// <summary>
  /// Marks a leased slice done the way completion does: stamped AND no longer held.
  /// </summary>
  /// <remarks>
  /// The lease has to be released, not just the row stamped. complete_perspective_events (037)
  /// clears instance_id and lease_expiry in debug mode and deletes the row outright in production,
  /// so a completed event never holds a lease. It matters here because the acquisition's per-stream
  /// gate (140) refuses an event while an EARLIER event of the same stream is still leased -- a
  /// fixture that stamps processed_at and leaves the lease behind blocks the rest of its own stream
  /// and looks exactly like a row bound that strands a deep stream. The stamp is kept rather than
  /// deleting the row so the assertions can still read the stream's full order.
  /// </remarks>
  private static async Task _completeAsync(NpgsqlConnection conn, List<Guid> eventIds) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      UPDATE wh_perspective_events
      SET processed_at = NOW(), instance_id = NULL, lease_expiry = NULL
      WHERE event_id = ANY(@ids)";
    cmd.Parameters.AddWithValue("ids", eventIds.ToArray());
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _claimAsync(
      NpgsqlConnection conn, Guid poller, CancellationToken cancellationToken) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT count(*) FROM claim_work(
        p_instance_id => @id, p_service_name => 'test', p_host_name => 'test-host', p_process_id => 1,
        p_max_streams => @batch, p_partition_count => 10000, p_lease_seconds => 300,
        p_max_rows => @batch)";
    cmd.Parameters.AddWithValue("id", poller);
    cmd.Parameters.AddWithValue("batch", BATCH);
    cmd.CommandTimeout = 300;
    _ = await cmd.ExecuteScalarAsync(cancellationToken);
  }
}
