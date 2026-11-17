// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2345: an inbox row an instance holds whose lease ran out (its working set was full, so the drain had not reached
/// it) is renewed by the claim and offered again, while the instance still owns its stream under acquisition's rule: the
/// stream's ledger lease is its own, or the row's partition falls to its rank and no live peer holds the stream. No
/// attempt is charged: the row waited, it did not fail. When ownership moved (the instance count changed and the
/// partition now falls to a peer, or a live peer holds the stream) the row is left to acquisition as before, and the
/// renewal never steals.
/// </summary>
/// <remarks>
/// Before, such a row was passed over by every poll's re-offer walk until acquisition re-leased it, which it does a
/// batch at a time and not at all while the working set is full, so the walk read it again on every poll.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/462_HeldLeasesAreRenewedByTheirOwner.sql</code-under-test>
/// <docs>fundamentals/work-coordinator/claim-loop#an-owner-renews-the-leases-it-let-run-out</docs>
[Category("Integration")]
[Category("Shard3")]
public class OwnerLeaseRenewalSqlTests : EFCoreTestBase {
  private const int LEASE_SECONDS = 300;

  [Test]
  public async Task Claim_AHeldRowWhoseLeaseRanOut_OnAStreamItsRankOwns_IsRenewedAndOffered_WithoutAnAttemptAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = TrackedGuid.New().Value;
    await _registerAsync(conn, instance);
    var stream = TrackedGuid.New().Value;
    var row = await _seedHeldAsync(conn, instance, stream, partition: 0, leaseSeconds: -60);

    var offered = await _claimAsync(conn, instance, acquireInbox: false);

    await Assert.That(await _leaseIsLiveAsync(conn, row)).IsTrue()
      .Because("the instance still owns the stream (its rank takes partition 0 and no peer holds it), so the claim renews the lease");
    await Assert.That(await _attemptsAsync(conn, row)).IsEqualTo(1)
      .Because("the row waited in a full working set; waiting is not failing, so no attempt is charged");
    await Assert.That(await _errorAsync(conn, row)).IsNull()
      .Because("no attempt ended: the renewal records no lease-expired failure");
    await Assert.That(offered).Contains(stream).Because("a renewed row is offered to the drain again on the same poll");
  }

  [Test]
  public async Task Claim_ARenewedStream_IsLeasedInTheLedgerToTheRenewingInstanceAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = TrackedGuid.New().Value;
    await _registerAsync(conn, instance);
    var stream = TrackedGuid.New().Value;
    await _seedHeldAsync(conn, instance, stream, partition: 0, leaseSeconds: -60);

    await _claimAsync(conn, instance, acquireInbox: false);

    await Assert.That(await _ledgerOwnerAsync(conn, stream)).IsEqualTo(instance)
      .Because("a stream whose rows an instance holds under a live lease must be its stream in the ledger, or a peer's "
        + "acquisition could take the stream's next row and run the stream on two instances");
    await Assert.That(await _ledgerLeaseIsLiveAsync(conn, stream)).IsTrue();
  }

  [Test]
  public async Task Claim_EveryExpiredRowOfARenewedStream_IsRenewedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = TrackedGuid.New().Value;
    await _registerAsync(conn, instance);
    var stream = TrackedGuid.New().Value;
    Guid[] rows = [
      await _seedHeldAsync(conn, instance, stream, partition: 0, leaseSeconds: -60, ageSeconds: 30),
      await _seedHeldAsync(conn, instance, stream, partition: 0, leaseSeconds: -60, ageSeconds: 20),
      await _seedHeldAsync(conn, instance, stream, partition: 0, leaseSeconds: -60, ageSeconds: 10),
    ];

    await _claimAsync(conn, instance, acquireInbox: false);

    foreach (var row in rows) {
      await Assert.That(await _leaseIsLiveAsync(conn, row)).IsTrue()
        .Because("the stream's rows are one stream's work, held by one instance; none is left for a peer to take out of order");
    }
  }

  [Test]
  public async Task Claim_AHeldRowWhoseLeaseIsLive_KeepsItsLeaseAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = TrackedGuid.New().Value;
    await _registerAsync(conn, instance);
    var row = await _seedHeldAsync(conn, instance, TrackedGuid.New().Value, partition: 0, leaseSeconds: 60);
    var before = await _leaseExpiryAsync(conn, row);

    await _claimAsync(conn, instance, acquireInbox: false);

    await Assert.That(await _leaseExpiryAsync(conn, row)).IsEqualTo(before)
      .Because("only a lease that ran out is renewed; a live one is the drain's, and the claim leaves it alone");
  }

  [Test]
  public async Task Claim_PastTheAcquisitionBound_TheRestOfItsExpiredRowsAreRenewed_NotChargedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = TrackedGuid.New().Value;
    await _registerAsync(conn, instance);
    var rows = new List<Guid>();
    for (var i = 0; i < 3; i++) {
      rows.Add(await _seedHeldAsync(conn, instance, TrackedGuid.New().Value, partition: 0, leaseSeconds: -60 - i));
    }

    // Acquisition leases one row (its bound) and charges it, as it always has; the other two are renewed.
    await _claimAsync(conn, instance, acquireInbox: true, maxRows: 1);

    var attempts = new List<int>();
    foreach (var row in rows) {
      await Assert.That(await _leaseIsLiveAsync(conn, row)).IsTrue();
      attempts.Add(await _attemptsAsync(conn, row));
    }
    await Assert.That(attempts.Count(a => a == 2)).IsEqualTo(1).Because("acquisition re-leases one expired row and charges it");
    await Assert.That(attempts.Count(a => a == 1)).IsEqualTo(2)
      .Because("the rows past acquisition's bound are renewed by their owner, and renewing charges nothing");
  }

  [Test]
  public async Task Claim_RenewsAtMostTheClaimWindow_OldestLeaseFirstAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = TrackedGuid.New().Value;
    await _registerAsync(conn, instance);
    var rows = new List<Guid>();
    for (var i = 0; i < 10; i++) {
      // The first row's lease ran out longest ago.
      rows.Add(await _seedHeldAsync(conn, instance, TrackedGuid.New().Value, partition: 0, leaseSeconds: -100 + i));
    }

    // A batch of one: the renewal's window is eight rows.
    await _claimAsync(conn, instance, acquireInbox: false, maxRows: 1, maxStreams: 1);

    var renewed = new List<bool>();
    foreach (var row in rows) {
      renewed.Add(await _leaseIsLiveAsync(conn, row));
    }
    await Assert.That(renewed.Take(8).All(r => r)).IsTrue().Because("the eight leases that ran out first are renewed");
    await Assert.That(renewed.Skip(8).Any(r => r)).IsFalse()
      .Because("one poll renews a bounded window, so its cost follows the batch and not the holdings; the next poll takes the rest");
  }

  [Test]
  public async Task Claim_WhenTheInstanceCountChanged_AndThePartitionFallsToALivePeer_LeavesTheRowToAcquisitionAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var (holder, peer) = _twoInstances();
    await _registerAsync(conn, holder);
    await _registerAsync(conn, peer);
    var stream = TrackedGuid.New().Value;
    // Two live instances: partition 1 falls to rank 1, the peer.
    var row = await _seedHeldAsync(conn, holder, stream, partition: 1, leaseSeconds: -60);

    var offered = await _claimAsync(conn, holder, acquireInbox: false);

    await Assert.That(await _leaseIsLiveAsync(conn, row)).IsFalse()
      .Because("the partition moved to the peer's rank and the holder has no live ledger lease, so it no longer owns the stream");
    await Assert.That(offered).DoesNotContain(stream);

    await _claimAsync(conn, peer, acquireInbox: true);

    await Assert.That(await _holderAsync(conn, row)).IsEqualTo(peer)
      .Because("the row goes to acquisition as before, and the peer whose rank takes the partition acquires it");
    await Assert.That(await _attemptsAsync(conn, row)).IsEqualTo(2).Because("acquisition charges an attempt, as it always has");
  }

  [Test]
  public async Task Claim_WhenThePartitionFallsToAPeer_ButTheHoldersLedgerLeaseIsLive_RenewsTheRowAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var (holder, peer) = _twoInstances();
    await _registerAsync(conn, holder);
    await _registerAsync(conn, peer);
    var stream = TrackedGuid.New().Value;
    var row = await _seedHeldAsync(conn, holder, stream, partition: 1, leaseSeconds: -60);
    await _seedLedgerAsync(conn, stream, partition: 1, owner: holder, leaseSeconds: 120);

    await _claimAsync(conn, holder, acquireInbox: false);

    await Assert.That(await _leaseIsLiveAsync(conn, row)).IsTrue()
      .Because("a stream's live owner keeps it whatever the partition arithmetic says (acquisition's owner path), so its rows stay its own");
  }

  [Test]
  public async Task Claim_WhenALivePeerHoldsTheStream_LeavesTheRowAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var (holder, peer) = _twoInstances();
    await _registerAsync(conn, holder);
    await _registerAsync(conn, peer);
    var stream = TrackedGuid.New().Value;
    // Partition 0 falls to the holder's rank, but the ledger says a live peer is draining the stream.
    var row = await _seedHeldAsync(conn, holder, stream, partition: 0, leaseSeconds: -60);
    await _seedLedgerAsync(conn, stream, partition: 0, owner: peer, leaseSeconds: 120);

    var offered = await _claimAsync(conn, holder, acquireInbox: false);

    await Assert.That(await _leaseIsLiveAsync(conn, row)).IsFalse()
      .Because("a stream a live peer holds is the peer's; renewing the row would run the stream on two instances");
    await Assert.That(offered).DoesNotContain(stream);
  }

  [Test]
  public async Task Claim_WithStealingAllowed_TheRenewalStillDoesNotSteal_AcquisitionDoesAsBeforeAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var (holder, peer) = _twoInstances();
    await _registerAsync(conn, holder);
    await _registerAsync(conn, peer);
    var stream = TrackedGuid.New().Value;
    var row = await _seedHeldAsync(conn, holder, stream, partition: 1, leaseSeconds: -60);

    await _claimAsync(conn, holder, acquireInbox: false, allowSteal: true);

    await Assert.That(await _leaseIsLiveAsync(conn, row)).IsFalse()
      .Because("renewal is for the stream's owner only; taking a partition that falls to a peer is stealing, which is acquisition's to decide");

    await _claimAsync(conn, holder, acquireInbox: true, allowSteal: true);

    await Assert.That(await _holderAsync(conn, row)).IsEqualTo(holder);
    await Assert.That(await _leaseIsLiveAsync(conn, row)).IsTrue();
    await Assert.That(await _attemptsAsync(conn, row)).IsEqualTo(2)
      .Because("with stealing allowed, acquisition takes the row as before, and charges it as before");
  }

  /// <summary>Two instance ids, the first ranking below the second (rank is the order of the ids).</summary>
  private static (Guid Low, Guid High) _twoInstances() {
    var a = TrackedGuid.New().Value;
    var b = TrackedGuid.New().Value;
    return a.CompareTo(b) < 0 ? (a, b) : (b, a);
  }

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  private static async Task _registerAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
      VALUES (@inst, 'test', 'test-host', 1, NOW(), NOW())
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()
      """;
    cmd.Parameters.AddWithValue("inst", instance);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>A command (no event chain to wait on) held by <paramref name="holder"/>, charged one attempt, its lease
  /// <paramref name="leaseSeconds"/> from now (negative: it ran out).</summary>
  private static async Task<Guid> _seedHeldAsync(
      NpgsqlConnection conn, Guid holder, Guid stream, int partition, int leaseSeconds, int ageSeconds = 60) {
    var message = TrackedGuid.New().Value;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority)
        VALUES (@message, 'TestHandler', 'TestCommand', '{}', '{}', NOW() - make_interval(secs => @age), @stream, FALSE, 150)
        RETURNING message_id, stream_id, received_at
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, instance_id, lease_expiry)
      SELECT m.message_id, m.stream_id, m.received_at, 150, FALSE, 0, 1, @partition, @holder, NOW() + make_interval(secs => @lease)
      FROM m
      """;
    cmd.Parameters.AddWithValue(nameof(message), message);
    cmd.Parameters.AddWithValue(nameof(stream), stream);
    cmd.Parameters.AddWithValue("age", (double)ageSeconds);
    cmd.Parameters.AddWithValue(nameof(partition), partition);
    cmd.Parameters.AddWithValue(nameof(holder), holder);
    cmd.Parameters.AddWithValue("lease", (double)leaseSeconds);
    await cmd.ExecuteNonQueryAsync();
    return message;
  }

  private static async Task _seedLedgerAsync(NpgsqlConnection conn, Guid stream, int partition, Guid owner, int leaseSeconds) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at, lease_expiry)
      VALUES (@stream, @partition, @owner, NOW(), NOW() + make_interval(secs => @lease))
      """;
    cmd.Parameters.AddWithValue(nameof(stream), stream);
    cmd.Parameters.AddWithValue(nameof(partition), partition);
    cmd.Parameters.AddWithValue(nameof(owner), owner);
    cmd.Parameters.AddWithValue("lease", (double)leaseSeconds);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>Runs one claim and returns the streams it offered from the inbox.</summary>
  private static async Task<List<Guid>> _claimAsync(
      NpgsqlConnection conn, Guid instance, bool acquireInbox, int maxRows = 100, int maxStreams = 100, bool allowSteal = false) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT work_stream_id FROM claim_work(
        p_instance_id => @inst, p_service_name => 'test', p_host_name => 'test-host', p_process_id => 1,
        p_max_streams => @streams, p_partition_count => 10000, p_lease_seconds => @lease, p_max_rows => @rows,
        p_allow_steal => @steal, p_acquire_inbox => @acquire)
      WHERE source = 'inbox'
      """;
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.AddWithValue("streams", maxStreams);
    cmd.Parameters.AddWithValue("lease", LEASE_SECONDS);
    cmd.Parameters.AddWithValue("rows", maxRows);
    cmd.Parameters.AddWithValue("steal", allowSteal);
    cmd.Parameters.AddWithValue("acquire", acquireInbox);
    var offered = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      offered.Add(reader.GetGuid(0));
    }
    return offered;
  }

  private static async Task<object?> _scalarAsync(NpgsqlConnection conn, string sql, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue(nameof(id), id);
    var value = await cmd.ExecuteScalarAsync();
    return value is DBNull ? null : value;
  }

  private static async Task<bool> _leaseIsLiveAsync(NpgsqlConnection conn, Guid message) =>
    (bool)(await _scalarAsync(conn, "SELECT lease_expiry > NOW() FROM wh_inbox_state WHERE message_id = @id", message))!;

  private static async Task<DateTime> _leaseExpiryAsync(NpgsqlConnection conn, Guid message) =>
    Convert.ToDateTime(await _scalarAsync(conn, "SELECT lease_expiry FROM wh_inbox_state WHERE message_id = @id", message),
      CultureInfo.InvariantCulture);

  private static async Task<int> _attemptsAsync(NpgsqlConnection conn, Guid message) =>
    Convert.ToInt32(await _scalarAsync(conn, "SELECT attempts FROM wh_inbox_state WHERE message_id = @id", message),
      CultureInfo.InvariantCulture);

  private static async Task<string?> _errorAsync(NpgsqlConnection conn, Guid message) =>
    (string?)await _scalarAsync(conn, "SELECT error FROM wh_inbox_state WHERE message_id = @id", message);

  private static async Task<Guid?> _holderAsync(NpgsqlConnection conn, Guid message) =>
    (Guid?)await _scalarAsync(conn, "SELECT instance_id FROM wh_inbox_state WHERE message_id = @id", message);

  private static async Task<Guid?> _ledgerOwnerAsync(NpgsqlConnection conn, Guid stream) =>
    (Guid?)await _scalarAsync(conn, "SELECT assigned_instance_id FROM wh_active_streams WHERE stream_id = @id", stream);

  private static async Task<bool> _ledgerLeaseIsLiveAsync(NpgsqlConnection conn, Guid stream) =>
    await _scalarAsync(conn, "SELECT lease_expiry > NOW() FROM wh_active_streams WHERE stream_id = @id", stream) is true;
}
