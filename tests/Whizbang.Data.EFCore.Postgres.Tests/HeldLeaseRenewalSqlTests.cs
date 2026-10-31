// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Issue #1852: a lease renewal extends only the leases the renewing instance holds. An instance whose lease lapsed,
/// and whose row another instance then claimed, used to extend the new holder's lease, which kept the row away from
/// the instance actually working it and let both treat it as theirs. <c>wh_renew_held_leases</c> renews the rows the
/// caller holds, with their stream leases, and returns the ids it did not renew, so the caller stops holding them.
/// </summary>
/// <remarks>
/// Every lapse is set on the database clock (<c>NOW() - interval</c>), the clock the claims compare against, and every
/// claim is the real claim function, so the second instance takes the row the way it would in a deployed service.
/// </remarks>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/255_LeaseRenewalIsScopedToTheHolder.sql</code-under-test>
[Category("Shard1")]
public class HeldLeaseRenewalSqlTests : EFCoreTestBase {

  /// <summary>A category's work table, as the statements that read and lapse one row's lease.</summary>
  private sealed record Table(string ReadLease, string Lapse);

  private static readonly Table _inbox = new(
    "SELECT instance_id, lease_expiry FROM wh_inbox_state WHERE message_id = @row",
    "UPDATE wh_inbox_state SET lease_expiry = NOW() - INTERVAL '1 second' WHERE message_id = @row");
  private static readonly Table _outbox = new(
    "SELECT instance_id, lease_expiry FROM wh_outbox WHERE message_id = @row",
    "UPDATE wh_outbox SET lease_expiry = NOW() - INTERVAL '1 second' WHERE message_id = @row");
  private static readonly Table _perspective = new(
    "SELECT instance_id, lease_expiry FROM wh_perspective_events WHERE event_work_id = @row",
    "UPDATE wh_perspective_events SET lease_expiry = NOW() - INTERVAL '1 second' WHERE event_work_id = @row");

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) =>
    await _execAsync(conn, @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
      VALUES (@inst, 'test', 'test-host', 1, NOW(), NOW())
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()", ("inst", instanceId));

  private static async Task<(Guid Row, Guid Stream)> _seedInboxRowAsync(NpgsqlConnection conn) {
    var row = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    await _execAsync(conn, """
      WITH m AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event)
        VALUES (@row, 'TestHandler', 'TestEvent', '{"p": {}}', '{}', NOW() - INTERVAL '10 minutes', @sid, TRUE)
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts,
         partition_number, instance_id, lease_expiry, error, failure_reason)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 0, 0, NULL::uuid, NULL::timestamptz, NULL::text, 99
      FROM m
      """, ("row", row), ("sid", stream));
    return (row, stream);
  }

  private static async Task<(Guid Row, Guid Stream)> _seedOutboxRowAsync(NpgsqlConnection conn) {
    var row = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    await _execAsync(conn, @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number, instance_id, lease_expiry)
      VALUES (@row, 'topic', 'TestEvent', 'TestEnvelope', '{}', '{}', 1, 0,
              NOW() - INTERVAL '10 minutes', @sid, 0, NULL, NULL)", ("row", row), ("sid", stream));
    return (row, stream);
  }

  private static async Task<(Guid Row, Guid Stream)> _seedPerspectiveEventAsync(NpgsqlConnection conn) {
    var row = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    await _execAsync(conn, @"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry,
         partition_number, status, attempts, created_at)
      VALUES (@row, @sid, 'TestPerspective', gen_random_uuid(), NULL, NULL, 0, 0, 0, NOW() - INTERVAL '10 minutes')",
      ("row", row), ("sid", stream));
    return (row, stream);
  }

  private static async Task _drainAsync(NpgsqlConnection conn, string sql, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("inst", instance);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) { /* the claim's rows are read back from the table */ }
  }

  private static Task _claimInboxAsync(NpgsqlConnection conn, Guid instance) => _drainAsync(conn, @"
    SELECT message_id FROM claim_orphaned_inbox(
      @inst, 0, 1, NOW() + INTERVAL '5 minutes', NOW(), 10000, NOW() - INTERVAL '10 minutes', 10, TRUE)", instance);

  private static Task _claimOutboxAsync(NpgsqlConnection conn, Guid instance) => _drainAsync(conn,
    "SELECT * FROM claim_orphaned_outbox(@inst, 0, 1, NOW() + INTERVAL '5 minutes', NOW(), 10000, NOW() - INTERVAL '10 minutes')",
    instance);

  private static Task _claimPerspectiveAsync(NpgsqlConnection conn, Guid instance) => _drainAsync(conn,
    "SELECT * FROM claim_orphaned_perspective_events(@inst, NOW() + INTERVAL '5 minutes', NOW(), 500, 0, 1)", instance);

  /// <summary>
  /// Lapses the row's lease and its stream's lease, as time would, on the database clock, and reaps the holder's
  /// registration: an instance paused past its lease (a long collection, a lost connection) stops heartbeating, the
  /// stale-instance cleanup removes it, and a claim takes over the stream of an instance it does not see. The paused
  /// instance still holds the row in memory when it resumes, which is when it renews.
  /// </summary>
  private static async Task _lapseAsync(NpgsqlConnection conn, Table table, Guid holder, Guid row, Guid stream) {
    await _execAsync(conn, "DELETE FROM wh_service_instances WHERE instance_id = @inst", ("inst", holder));
    await _execAsync(conn, table.Lapse, ("row", row));
    await _execAsync(conn, "UPDATE wh_active_streams SET lease_expiry = NOW() - INTERVAL '1 second' WHERE stream_id = @sid",
      ("sid", stream));
  }

  private static async Task<(Guid? Holder, DateTime? Lease)> _rowLeaseAsync(NpgsqlConnection conn, Table table, Guid row) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = table.ReadLease;
    cmd.Parameters.AddWithValue(nameof(row), row);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException($"No row {row} for: {table.ReadLease}");
    }
    return (await reader.IsDBNullAsync(0) ? null : reader.GetGuid(0), await reader.IsDBNullAsync(1) ? null : reader.GetDateTime(1));
  }

  private static async Task<(Guid? Holder, DateTime? Lease)> _streamLeaseAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT assigned_instance_id, lease_expiry FROM wh_active_streams WHERE stream_id = @sid";
    cmd.Parameters.AddWithValue("sid", stream);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException($"No wh_active_streams row for {stream}");
    }
    return (await reader.IsDBNullAsync(0) ? null : reader.GetGuid(0), await reader.IsDBNullAsync(1) ? null : reader.GetDateTime(1));
  }

  private static async Task<Guid[]> _renewAsync(NpgsqlConnection conn, string category, Guid instance, Guid[] ids, int leaseSeconds) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_renew_held_leases(@category, @inst, @ids, @lease)";
    cmd.Parameters.AddWithValue(nameof(category), category);
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(ids), NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
    cmd.Parameters.AddWithValue("lease", leaseSeconds);
    return (Guid[])(await cmd.ExecuteScalarAsync())!;
  }

  /// <summary>
  /// A holds the row, its lease lapses, B claims it: the scenario of #1852, for one category. Returns the row, its
  /// stream and both instances once B holds the row.
  /// </summary>
  private static async Task<(Guid Row, Guid Stream, Guid A, Guid B)> _reclaimedByAnotherAsync(
      NpgsqlConnection conn, Table table,
      Func<NpgsqlConnection, Task<(Guid Row, Guid Stream)>> seed,
      Func<NpgsqlConnection, Guid, Task> claim) {
    var a = (Guid)TrackedGuid.New();
    var b = (Guid)TrackedGuid.New();
    await _registerInstanceAsync(conn, a);
    await _registerInstanceAsync(conn, b);
    var (row, stream) = await seed(conn);

    await claim(conn, a);
    var (firstHolder, _) = await _rowLeaseAsync(conn, table, row);
    await Assert.That(firstHolder).IsEqualTo(a).Because("the scenario starts with A holding the row");

    await _lapseAsync(conn, table, a, row, stream);
    await claim(conn, b);
    var (holder, _) = await _rowLeaseAsync(conn, table, row);
    await Assert.That(holder).IsEqualTo(b).Because("the scenario needs B to have claimed the row A's lapsed lease left");
    var (streamHolder, _) = await _streamLeaseAsync(conn, stream);
    await Assert.That(streamHolder).IsEqualTo(b).Because("the scenario needs B to have taken over the stream with the row");
    return (row, stream, a, b);
  }

  private static async Task _theFormerHolderRenewsNothingAsync(
      NpgsqlConnection conn, Table table, string category,
      Func<NpgsqlConnection, Task<(Guid Row, Guid Stream)>> seed,
      Func<NpgsqlConnection, Guid, Task> claim) {
    var (row, stream, a, b) = await _reclaimedByAnotherAsync(conn, table, seed, claim);
    var (_, leaseBefore) = await _rowLeaseAsync(conn, table, row);
    var (_, streamLeaseBefore) = await _streamLeaseAsync(conn, stream);

    var notRenewed = await _renewAsync(conn, category, a, [row], 1800);

    await Assert.That(notRenewed).IsEquivalentTo([row])
      .Because("A no longer holds the row, so its renewal reports it, and A stops holding it");
    var (holderAfter, leaseAfter) = await _rowLeaseAsync(conn, table, row);
    await Assert.That(holderAfter).IsEqualTo(b);
    await Assert.That(leaseAfter).IsEqualTo(leaseBefore)
      .Because("A's renewal must not extend the lease B holds, or the row stays away from the instance working it");
    var (streamHolderAfter, streamLeaseAfter) = await _streamLeaseAsync(conn, stream);
    await Assert.That(streamHolderAfter).IsEqualTo(b);
    await Assert.That(streamLeaseAfter).IsEqualTo(streamLeaseBefore)
      .Because("the stream lease is B's too, and A's renewal leaves it alone");
  }

  private static async Task _theHolderRenewsItsLeaseAsync(
      NpgsqlConnection conn, Table table, string category,
      Func<NpgsqlConnection, Task<(Guid Row, Guid Stream)>> seed,
      Func<NpgsqlConnection, Guid, Task> claim) {
    var (row, stream, _, b) = await _reclaimedByAnotherAsync(conn, table, seed, claim);
    var (_, leaseBefore) = await _rowLeaseAsync(conn, table, row);
    var (_, streamLeaseBefore) = await _streamLeaseAsync(conn, stream);

    var notRenewed = await _renewAsync(conn, category, b, [row], 1800);

    await Assert.That(notRenewed).IsEmpty().Because("B holds the row, so its renewal covers it");
    var (_, leaseAfter) = await _rowLeaseAsync(conn, table, row);
    await Assert.That(leaseAfter!.Value - leaseBefore!.Value).IsGreaterThan(TimeSpan.FromMinutes(20))
      .Because("a renewal to 1,800 seconds moves a five minute lease well past where the claim left it");
    var (_, streamLeaseAfter) = await _streamLeaseAsync(conn, stream);
    await Assert.That(streamLeaseAfter!.Value - streamLeaseBefore!.Value).IsGreaterThan(TimeSpan.FromMinutes(20))
      .Because("the holder's renewal keeps its stream lease with its rows (148)");
  }

  [Test]
  public async Task RenewHeldLeases_Inbox_AFormerHolder_DoesNotExtendTheNewHoldersLease_AndReportsTheRowAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _theFormerHolderRenewsNothingAsync(conn, _inbox, "inbox", _seedInboxRowAsync, _claimInboxAsync);
  }

  [Test]
  public async Task RenewHeldLeases_Inbox_TheHolder_RenewsTheRowAndItsStreamAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _theHolderRenewsItsLeaseAsync(conn, _inbox, "inbox", _seedInboxRowAsync, _claimInboxAsync);
  }

  [Test]
  public async Task RenewHeldLeases_Outbox_AFormerHolder_DoesNotExtendTheNewHoldersLease_AndReportsTheRowAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _theFormerHolderRenewsNothingAsync(conn, _outbox, "outbox", _seedOutboxRowAsync, _claimOutboxAsync);
  }

  [Test]
  public async Task RenewHeldLeases_Outbox_TheHolder_RenewsTheRowAndItsStreamAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _theHolderRenewsItsLeaseAsync(conn, _outbox, "outbox", _seedOutboxRowAsync, _claimOutboxAsync);
  }

  [Test]
  public async Task RenewHeldLeases_PerspectiveEvent_AFormerHolder_DoesNotExtendTheNewHoldersLease_AndReportsTheRowAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _theFormerHolderRenewsNothingAsync(conn, _perspective, "perspective_event", _seedPerspectiveEventAsync, _claimPerspectiveAsync);
  }

  [Test]
  public async Task RenewHeldLeases_PerspectiveEvent_TheHolder_RenewsTheRowAndItsStreamAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _theHolderRenewsItsLeaseAsync(conn, _perspective, "perspective_event", _seedPerspectiveEventAsync, _claimPerspectiveAsync);
  }

  [Test]
  public async Task RenewHeldLeases_ARowThatIsGoneOrCompleted_IsReportedNotRenewedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = (Guid)TrackedGuid.New();
    await _registerInstanceAsync(conn, instance);
    var (held, _) = await _seedInboxRowAsync(conn);
    var (completed, _) = await _seedInboxRowAsync(conn);
    await _claimInboxAsync(conn, instance);
    await _execAsync(conn, "UPDATE wh_inbox_state SET processed_at = NOW() WHERE message_id = @row", ("row", completed));
    var gone = (Guid)TrackedGuid.New();

    var notRenewed = await _renewAsync(conn, "inbox", instance, [held, completed, gone], 600);

    await Assert.That(notRenewed).IsEquivalentTo([completed, gone])
      .Because("a completed row and a row the store no longer has are not held, so the caller stops holding them");
  }

  [Test]
  public async Task RenewHeldLeases_NoIds_ReportsNothingAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);

    var notRenewed = await _renewAsync(conn, "inbox", (Guid)TrackedGuid.New(), [], 600);

    await Assert.That(notRenewed).IsEmpty();
  }

  [Test]
  public async Task RenewHeldLeases_AnUnknownCategory_IsRefusedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);

    var refused = await Assert.ThrowsAsync<PostgresException>(
      () => _renewAsync(conn, "nonsense", (Guid)TrackedGuid.New(), [(Guid)TrackedGuid.New()], 600));

    await Assert.That(refused!.MessageText).Contains("unknown category");
  }
}
