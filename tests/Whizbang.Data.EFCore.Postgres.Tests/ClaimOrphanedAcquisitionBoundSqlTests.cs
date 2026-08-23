using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Acquisition must be bounded. <c>claim_orphaned_inbox</c> and <c>claim_orphaned_outbox</c> take a
/// lease on every eligible row in one statement and charge an attempt to each, so an instance
/// carrying a large backlog claims all of it at once no matter what the caller asked for.
/// </summary>
/// <remarks>
/// <para>
/// The limit the claim loop computes — from the adaptive window and the outstanding budget — reaches
/// <c>claim_orphaned_perspective_events</c> but not the inbox or outbox equivalents. In
/// <c>claim_work</c>, <c>p_max_streams</c> bounds only the re-emission of work the instance already
/// holds, because <c>eligible_inbox</c> filters on <c>instance_id = p_instance_id</c>. Acquisition
/// runs upstream of that and unthrottled, which is why narrowing the claim window changed the rate
/// of the failure without ever converging.
/// </para>
/// <para>
/// The consequence is not merely holding too much. Rows claimed beyond what can be dispatched inside
/// the lease expire un-dispatched; the next claim re-acquires them and charges another attempt; and
/// they eventually dead-letter as <c>MaxAttemptsExceeded</c> having never reached a receptor.
/// <c>claim_orphaned_inbox</c> stamps those rows itself — "ended without a reported outcome" with
/// <c>failure_reason = 6</c> (LeaseExpired) — so the marker and the cause are the same statement.
/// </para>
/// </remarks>
/// <docs>operations/workers/claim-backpressure</docs>
public class ClaimOrphanedAcquisitionBoundSqlTests : EFCoreTestBase {

  private const int BACKLOG = 200;
  private const int LIMIT = 20;

  private static async Task<NpgsqlConnection> _openAsync(Microsoft.EntityFrameworkCore.DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  private static async Task _seedUnclaimedInboxAsync(NpgsqlConnection conn, int count) {
    await using var ins = conn.CreateCommand();
    // Distinct stream ids: same-stream rows would collapse onto one active-streams owner and could
    // mask an unbounded claim behind per-stream ordering rather than the row bound under test.
    ins.CommandText = @"
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
         stream_id, partition_number, instance_id, lease_expiry, error, failure_reason)
      SELECT gen_random_uuid(), 'TestHandler', 'TestEvent', '{}', '{}', 1, 0, NOW() - (g || ' seconds')::INTERVAL,
             gen_random_uuid(), 0, NULL, NULL, NULL, 99
      FROM generate_series(1, @n) AS g";
    ins.Parameters.AddWithValue("n", count);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
      VALUES (@inst, 'test', 'test-host', 1, NOW(), NOW())
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<int> _leasedCountAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT count(*) FROM wh_inbox
      WHERE instance_id = @inst AND processed_at IS NULL AND lease_expiry > NOW()";
    cmd.Parameters.AddWithValue("inst", instanceId);
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  [Test]
  public async Task ClaimOrphanedInbox_HonorsTheRowLimitItIsGivenAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();

    await _registerInstanceAsync(conn, instance);
    await _seedUnclaimedInboxAsync(conn, BACKLOG);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT * FROM claim_orphaned_inbox(
        @inst, 0, 1, NOW() + INTERVAL '5 minutes', NOW(), 1, NOW() - INTERVAL '10 minutes', @lim)";
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.AddWithValue("lim", LIMIT);
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) { }
    }

    var leased = await _leasedCountAsync(conn, instance);

    await Assert.That(leased).IsLessThanOrEqualTo(LIMIT)
      .Because($"acquisition must stop at the limit the caller computed; taking all {BACKLOG} rows "
             + "charges an attempt to every one of them, and the ones that cannot be dispatched "
             + "inside the lease spend that attempt without ever reaching a receptor");
  }

  [Test]
  public async Task ClaimWork_DoesNotAcquireMoreThanItsCallerAskedForAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();

    await _registerInstanceAsync(conn, instance);
    await _seedUnclaimedInboxAsync(conn, BACKLOG);

    // The whole path, as the worker drives it. p_max_streams is the value the adaptive window and
    // the outstanding budget produce, so it has to bound what the instance ends up holding.
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM claim_work(@inst, 'test', 'test-host', 1, @lim, 1, 300)";
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.AddWithValue("lim", LIMIT);
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) { }
    }

    var leased = await _leasedCountAsync(conn, instance);

    await Assert.That(leased).IsLessThanOrEqualTo(LIMIT)
      .Because("the claim limit must bound work ACQUIRED, not merely work re-emitted — bounding "
             + "re-emission alone throttles a valve downstream of the flood, which is why a "
             + "shrinking claim window changed the rate of lease saturation but never stopped it");
  }

  [Test]
  public async Task ClaimOrphanedInbox_ChargesAnAttemptOnlyToRowsItActuallyClaimsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();

    await _registerInstanceAsync(conn, instance);
    await _seedUnclaimedInboxAsync(conn, BACKLOG);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT * FROM claim_orphaned_inbox(
        @inst, 0, 1, NOW() + INTERVAL '5 minutes', NOW(), 1, NOW() - INTERVAL '10 minutes', @lim)";
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.AddWithValue("lim", LIMIT);
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) { }
    }

    await using var count = conn.CreateCommand();
    count.CommandText = "SELECT count(*) FROM wh_inbox WHERE attempts > 0";
    var charged = Convert.ToInt32(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);

    await Assert.That(charged).IsLessThanOrEqualTo(LIMIT)
      .Because("an attempt is a retry budget, not a bookkeeping detail — spending one on a row the "
             + "instance was never going to dispatch is what dead-letters healthy messages");
  }
}
