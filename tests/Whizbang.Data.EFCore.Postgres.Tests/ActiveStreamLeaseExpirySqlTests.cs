using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 148 (#731): acquiring a stream's rows leases the stream as well as the rows. No acquisition
/// function had ever set <c>wh_active_streams.lease_expiry</c>, so the stream-level guards of migration 145
/// (a live sibling's owned streams are never stolen; an owner's streams are preferred and renewed) compared
/// against NULL and were inert: every claim fell to the pinning path and a stream could be interleaved across
/// two instances the moment its owner had no row leased.
/// </summary>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/148_ActiveStreamLeases.sql</code-under-test>
[Category("Shard1")]
public class ActiveStreamLeaseExpirySqlTests : EFCoreTestBase {

  private static async Task<NpgsqlConnection> _openAsync(Microsoft.EntityFrameworkCore.DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
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

  /// <summary>Seeds <paramref name="rows"/> pending, unassigned inbox rows on one stream, one second apart.</summary>
  private static async Task<Guid> _seedInboxStreamAsync(NpgsqlConnection conn, int rows) {
    var streamId = Guid.CreateVersion7();
    await using var ins = conn.CreateCommand();
    ins.CommandText = """
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
         stream_id, partition_number, is_event, instance_id, lease_expiry, error, failure_reason)
      SELECT gen_random_uuid(), 'TestHandler', 'TestEvent', '{"p": {}}', '{}', 1, 0,
             NOW() - INTERVAL '10 minutes' + (r.seq * INTERVAL '1 second'),
             @sid, 0, TRUE, NULL, NULL, NULL, 99
      FROM generate_series(1, @rows) AS r(seq)
      """;
    ins.Parameters.AddWithValue("sid", streamId);
    ins.Parameters.AddWithValue(nameof(rows), rows);
    await ins.ExecuteNonQueryAsync();
    return streamId;
  }

  private static async Task<Guid> _seedOutboxRowAsync(NpgsqlConnection conn) {
    var streamId = Guid.CreateVersion7();
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number, instance_id, lease_expiry)
      VALUES (gen_random_uuid(), 'topic', 'TestEvent', 'TestEnvelope', '{}', '{}', 1, 0,
              NOW() - INTERVAL '10 minutes', @sid, 0, NULL, NULL)";
    ins.Parameters.AddWithValue("sid", streamId);
    await ins.ExecuteNonQueryAsync();
    return streamId;
  }

  private static async Task<Guid> _seedPerspectiveEventAsync(NpgsqlConnection conn) {
    var streamId = Guid.CreateVersion7();
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry,
         partition_number, status, attempts, created_at)
      VALUES (gen_random_uuid(), @sid, 'TestPerspective', gen_random_uuid(), NULL, NULL, 0, 0, 0, NOW() - INTERVAL '10 minutes')";
    ins.Parameters.AddWithValue("sid", streamId);
    await ins.ExecuteNonQueryAsync();
    return streamId;
  }

  private static async Task<List<Guid>> _claimInboxAsync(
      NpgsqlConnection conn, Guid instance, int rank, int count, int leaseMinutes, int limit, bool allowSteal) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT message_id FROM claim_orphaned_inbox(
        @inst, @rank, @count, NOW() + (@lease || ' minutes')::INTERVAL, NOW(), 10000, NOW() - INTERVAL '10 minutes', @lim, @steal)";
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.AddWithValue(nameof(rank), rank);
    cmd.Parameters.AddWithValue(nameof(count), count);
    cmd.Parameters.AddWithValue("lease", leaseMinutes);
    cmd.Parameters.AddWithValue("lim", limit);
    cmd.Parameters.AddWithValue("steal", allowSteal);
    var claimed = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      claimed.Add(reader.GetGuid(0));
    }
    return claimed;
  }

  private static async Task _claimOutboxAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM claim_orphaned_outbox(@inst, 0, 1, NOW() + INTERVAL '5 minutes', NOW(), 10000, NOW() - INTERVAL '10 minutes')";
    cmd.Parameters.AddWithValue("inst", instance);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) { /* drain */ }
  }

  private static async Task _claimPerspectiveAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM claim_orphaned_perspective_events(@inst, NOW() + INTERVAL '5 minutes', NOW(), 500, 0, 1)";
    cmd.Parameters.AddWithValue("inst", instance);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) { /* drain */ }
  }

  private static async Task<(Guid? Owner, DateTimeOffset? LeaseExpiry)> _streamLeaseAsync(NpgsqlConnection conn, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT assigned_instance_id, lease_expiry FROM wh_active_streams WHERE stream_id = @sid";
    cmd.Parameters.AddWithValue("sid", streamId);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException($"No wh_active_streams row for {streamId}");
    }
    var owner = reader.IsDBNull(0) ? (Guid?)null : reader.GetGuid(0);
    var lease = reader.IsDBNull(1) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(1);
    return (owner, lease);
  }

  private static async Task _completeRowAsync(NpgsqlConnection conn, Guid messageId) {
    // Completion deletes the row (the inbox keeps no history of completed rows).
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM wh_inbox WHERE message_id = @id";
    cmd.Parameters.AddWithValue("id", messageId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<List<Guid>> _streamsHeldByAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT DISTINCT stream_id FROM wh_inbox WHERE instance_id = @inst";
    cmd.Parameters.AddWithValue("inst", instance);
    var streams = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      streams.Add(reader.GetGuid(0));
    }
    return streams;
  }

  [Test]
  public async Task ClaimOrphanedInbox_LeasesTheStream_WithTheRowLeaseExpiryAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    var stream = await _seedInboxStreamAsync(conn, rows: 2);

    var claimed = await _claimInboxAsync(conn, instance, rank: 0, count: 1, leaseMinutes: 5, limit: 10, allowSteal: false);
    await Assert.That(claimed.Count).IsEqualTo(2);

    var (owner, lease) = await _streamLeaseAsync(conn, stream);
    await Assert.That(owner).IsEqualTo(instance);
    await Assert.That(lease.HasValue).IsTrue()
      .Because("leasing a stream's rows leases the stream; a NULL stream lease makes every stream-level guard inert (#731)");
    await Assert.That(lease!.Value - DateTimeOffset.UtcNow).IsGreaterThan(TimeSpan.FromMinutes(4))
      .Because("the stream lease carries the same expiry the rows were leased with");
    await Assert.That(lease!.Value - DateTimeOffset.UtcNow).IsLessThan(TimeSpan.FromMinutes(6));
  }

  [Test]
  public async Task ClaimOrphanedInbox_RenewsTheStreamLease_WhenTheOwnerClaimsAgainAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    var stream = await _seedInboxStreamAsync(conn, rows: 3);

    _ = await _claimInboxAsync(conn, instance, rank: 0, count: 1, leaseMinutes: 5, limit: 1, allowSteal: false);
    var (_, first) = await _streamLeaseAsync(conn, stream);
    _ = await _claimInboxAsync(conn, instance, rank: 0, count: 1, leaseMinutes: 10, limit: 1, allowSteal: false);
    var (owner, renewed) = await _streamLeaseAsync(conn, stream);

    await Assert.That(owner).IsEqualTo(instance);
    await Assert.That(renewed.HasValue).IsTrue();
    await Assert.That(first.HasValue).IsTrue();
    await Assert.That(renewed!.Value - first!.Value).IsGreaterThan(TimeSpan.FromMinutes(4))
      .Because("the owner's next claim renews the stream lease along with the new row leases, so ownership does not lapse mid-drain");
  }

  [Test]
  public async Task ClaimOrphanedInbox_Steal_NeverTakesAStreamALiveSiblingOwns_EvenWithNoRowLeasedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var owner = Guid.CreateVersion7();
    var idle = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, owner);
    await _registerInstanceAsync(conn, idle);
    var ownedStream = await _seedInboxStreamAsync(conn, rows: 3);
    var freeStream = await _seedInboxStreamAsync(conn, rows: 1);

    // The rank-0 owner takes and completes the head row of its stream. Between that completion and its next
    // claim the stream has no leased row, but the owner still holds the stream: a sibling that stole the
    // remaining rows now would interleave one stream across two instances.
    var head = await _claimInboxAsync(conn, owner, rank: 0, count: 2, leaseMinutes: 5, limit: 1, allowSteal: false);
    await Assert.That(head.Count).IsEqualTo(1);
    await _completeRowAsync(conn, head[0]);

    _ = await _claimInboxAsync(conn, idle, rank: 1, count: 2, leaseMinutes: 5, limit: 10, allowSteal: true);

    var stolen = await _streamsHeldByAsync(conn, idle);
    await Assert.That(stolen).DoesNotContain(ownedStream)
      .Because("a live sibling's stream lease protects the stream's remaining rows, not only the rows it has leased");
    await Assert.That(stolen).Contains(freeStream)
      .Because("streams nobody owns are still stolen, so the guard is the stream lease and not a blanket refusal");
  }

  [Test]
  public async Task ClaimOrphanedOutbox_LeasesTheStream_WithTheRowLeaseExpiryAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    var stream = await _seedOutboxRowAsync(conn);

    await _claimOutboxAsync(conn, instance);

    var (owner, lease) = await _streamLeaseAsync(conn, stream);
    await Assert.That(owner).IsEqualTo(instance);
    await Assert.That(lease.HasValue).IsTrue()
      .Because("outbox acquisition pins the stream the same way inbox acquisition does, and must lease it the same way");
    await Assert.That(lease!.Value - DateTimeOffset.UtcNow).IsGreaterThan(TimeSpan.FromMinutes(4));
  }

  [Test]
  public async Task ClaimOrphanedPerspectiveEvents_LeasesTheStream_WithTheRowLeaseExpiryAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    var stream = await _seedPerspectiveEventAsync(conn);

    await _claimPerspectiveAsync(conn, instance);

    var (owner, lease) = await _streamLeaseAsync(conn, stream);
    await Assert.That(owner).IsEqualTo(instance);
    await Assert.That(lease.HasValue).IsTrue()
      .Because("perspective acquisition pins the stream too; a NULL lease there leaves perspective streams unprotected");
    await Assert.That(lease!.Value - DateTimeOffset.UtcNow).IsGreaterThan(TimeSpan.FromMinutes(4));
  }

  private static async Task<int> _renewAsync(NpgsqlConnection conn, string category, IReadOnlyList<Guid> ids, int leaseSeconds) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT renew_leases(@cat, @ids, @secs)";
    cmd.Parameters.AddWithValue("cat", category);
    cmd.Parameters.Add(new NpgsqlParameter<Guid[]>(nameof(ids), [.. ids]));
    cmd.Parameters.AddWithValue("secs", leaseSeconds);
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _leaseRowsToAsync(NpgsqlConnection conn, IReadOnlyList<Guid> ids, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_inbox SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = ANY(@ids)";
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.Add(new NpgsqlParameter<Guid[]>(nameof(ids), [.. ids]));
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task RenewLeases_ExtendsTheStreamLease_ForTheOwnersStreamsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    var stream = await _seedInboxStreamAsync(conn, rows: 2);
    var claimed = await _claimInboxAsync(conn, instance, rank: 0, count: 1, leaseMinutes: 5, limit: 10, allowSteal: false);
    var (_, before) = await _streamLeaseAsync(conn, stream);

    var renewed = await _renewAsync(conn, "inbox", claimed, leaseSeconds: 1800);
    var (owner, after) = await _streamLeaseAsync(conn, stream);

    await Assert.That(renewed).IsEqualTo(claimed.Count).Because("the row renewal count is the function's contract and must survive the stream renewal");
    await Assert.That(owner).IsEqualTo(instance);
    await Assert.That(before.HasValue).IsTrue();
    await Assert.That(after.HasValue).IsTrue();
    await Assert.That(after!.Value - before!.Value).IsGreaterThan(TimeSpan.FromMinutes(20))
      .Because("a long handler renews its row leases for up to the renewal cap; if the stream lease stayed at the original row lease it would lapse mid-handler and a sibling could take the stream's next row");
  }

  [Test]
  public async Task RenewLeases_LeavesAStreamAssignedToAnotherInstanceAloneAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var owner = Guid.CreateVersion7();
    var other = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, owner);
    await _registerInstanceAsync(conn, other);
    var stream = await _seedInboxStreamAsync(conn, rows: 2);
    var rows = await _claimInboxAsync(conn, owner, rank: 0, count: 1, leaseMinutes: 5, limit: 10, allowSteal: false);
    var (_, before) = await _streamLeaseAsync(conn, stream);

    // The rows moved to another instance (the unowned modulo path after the owner's lease lapsed) while the
    // stream row still names the original owner. Renewing those rows must not vouch for the original owner.
    await _leaseRowsToAsync(conn, rows, other);
    _ = await _renewAsync(conn, "inbox", rows, leaseSeconds: 1800);
    var (assigned, after) = await _streamLeaseAsync(conn, stream);

    await Assert.That(assigned).IsEqualTo(owner);
    await Assert.That(before.HasValue).IsTrue();
    await Assert.That(after.HasValue).IsTrue();
    await Assert.That(after!.Value).IsEqualTo(before!.Value)
      .Because("the stream lease vouches for the assigned instance; a renewal by a different row holder says nothing about that instance");
  }

  [Test]
  public async Task AcquisitionFunctions_HaveExactlyOneOverloadEachAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT proname, count(*) FROM pg_proc
      WHERE proname IN ('claim_orphaned_inbox', 'claim_orphaned_outbox', 'claim_orphaned_perspective_events')
      GROUP BY proname ORDER BY proname";
    var counts = new Dictionary<string, long>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        counts[reader.GetString(0)] = reader.GetInt64(1);
      }
    }
    await Assert.That(counts.Count).IsEqualTo(3);
    foreach (var (name, count) in counts) {
      await Assert.That(count).IsEqualTo(1L).Because($"{name} must keep one overload after 148 redefines it");
    }
  }
}
