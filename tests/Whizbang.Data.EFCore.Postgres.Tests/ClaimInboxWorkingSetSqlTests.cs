// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A claim told not to acquire inbox work (<c>p_acquire_inbox = FALSE</c>) leases no new inbox row, and every other
/// acquisition goes on (#1853).
/// </summary>
/// <remarks>
/// The claim worker passes it while the inbox rows it holds in memory are at their byte budget (#1793). Before, every
/// poll leased new rows that the drain fetched, turned away and released, so a full working set cost a claim, a fetch
/// and a release per poll for nothing. Perspective acquisition shares the claim's row bound but not the inbox's byte
/// budget, so it must not stop with it.
/// </remarks>
/// <docs>fundamentals/work-coordinator/per-stream-drain#working-set-budget</docs>
[Category("Shard1")]
public class ClaimInboxWorkingSetSqlTests : EFCoreTestBase {
  private const int BACKLOG = 5;

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, Guid? instance = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    if (instance is { } id) {
      cmd.Parameters.AddWithValue("inst", id);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  private static Task _registerInstanceAsync(NpgsqlConnection conn, Guid instance) => _execAsync(conn, """
    INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
    VALUES (@inst, 'test', 'test-host', 1, NOW(), NOW())
    ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()
    """, instance);

  private static Task _seedUnclaimedInboxAsync(NpgsqlConnection conn) => _execAsync(conn, $"""
    WITH m AS (
      INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id)
      SELECT gen_random_uuid(), 'TestHandler', 'TestEvent', '{"{}"}', '{"{}"}', NOW() - (g || ' seconds')::INTERVAL, gen_random_uuid()
      FROM generate_series(1, {BACKLOG}) AS g
      RETURNING message_id, stream_id, received_at, priority, is_event
    )
    INSERT INTO wh_inbox_state
      (message_id, stream_id, received_at, priority, is_event, status, attempts,
       partition_number, instance_id, lease_expiry, error, failure_reason)
    SELECT message_id, stream_id, received_at, priority, is_event, 1, 0, 0, NULL, NULL, NULL, 99
    FROM m
    """);

  private static Task _seedUnclaimedPerspectiveEventAsync(NpgsqlConnection conn) => _execAsync(conn, """
    INSERT INTO wh_perspective_events
      (stream_id, perspective_name, event_id, instance_id, lease_expiry, partition_number, status, attempts, priority)
    VALUES (gen_random_uuid(), 'WorkingSetPerspective', gen_random_uuid(), NULL, NULL, 0, 1, 0, 0)
    """);

  private static async Task _claimAsync(NpgsqlConnection conn, Guid instance, bool? acquireInbox) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM claim_work(@inst, 'test', 'test-host', 1, 100, 1, 300)";
    if (acquireInbox is { } acquire) {
      // Passed by name, as an older caller that does not know the parameter leaves it out entirely.
      cmd.CommandText = "SELECT * FROM claim_work(@inst, 'test', 'test-host', 1, 100, 1, 300, p_acquire_inbox => @acquire)";
      cmd.Parameters.AddWithValue("acquire", acquire);
    }
    cmd.Parameters.AddWithValue("inst", instance);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) { /* drain */ }
  }

  private static async Task<long> _countAsync(NpgsqlConnection conn, string sql, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("inst", instance);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static Task<long> _leasedInboxAsync(NpgsqlConnection conn, Guid instance) => _countAsync(conn,
    "SELECT count(*) FROM wh_inbox_state WHERE instance_id = @inst AND processed_at IS NULL", instance);

  [Test]
  public async Task ClaimWork_WithoutInboxAcquisition_LeasesNoNewInboxRow_AndStillReOffersWhatItHoldsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    await _seedUnclaimedInboxAsync(conn);

    await _claimAsync(conn, instance, acquireInbox: false);

    await Assert.That(await _leasedInboxAsync(conn, instance)).IsEqualTo(0L)
      .Because("while the working set is at its byte budget, every row leased is a row the drain hands straight back");

    // The positive control: the same backlog, the default claim, and every row is leased.
    await _claimAsync(conn, instance, acquireInbox: null);

    await Assert.That(await _leasedInboxAsync(conn, instance)).IsEqualTo((long)BACKLOG)
      .Because("an older caller passes no p_acquire_inbox and claims as before");
  }

  [Test]
  public async Task ClaimWork_WithoutInboxAcquisition_StillAcquiresPerspectiveWorkAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    await _seedUnclaimedInboxAsync(conn);
    await _seedUnclaimedPerspectiveEventAsync(conn);

    await _claimAsync(conn, instance, acquireInbox: false);

    await Assert.That(await _countAsync(conn,
        "SELECT count(*) FROM wh_perspective_events WHERE instance_id = @inst AND perspective_name = 'WorkingSetPerspective'", instance))
      .IsEqualTo(1L)
      .Because("the inbox's byte budget says nothing about perspective work, which shares only the claim's row bound");
    await Assert.That(await _leasedInboxAsync(conn, instance)).IsEqualTo(0L);
  }

  [Test]
  public async Task ClaimWork_WithInboxAcquisitionAskedForExplicitly_LeasesTheBacklogAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    await _seedUnclaimedInboxAsync(conn);

    await _claimAsync(conn, instance, acquireInbox: true);

    await Assert.That(await _leasedInboxAsync(conn, instance)).IsEqualTo((long)BACKLOG);
  }
}
