// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// With the service's switch on, every claim path decides through the partition ownership table alone (#2555), and no
/// claimer ranks itself or claims without a valid map (#2554). A stream's stored owner wins nothing: when the map moves a
/// partition, the new owner claims its pending work at once and the old owner claims none of it, though the stream
/// ledger still names the old owner and it is live. With the switch off, the stored owner keeps its stream as before.
/// </summary>
/// <remarks>
/// Instances <c>A</c> and <c>B</c> sort in that order, so in the map [A, B] even partitions are A's and odd ones B's.
/// Each test publishes the map [A] first (A owns everything and is the stream's stored owner, as an instance that
/// acquired it before a rebalance is), then [A, B], which moves the odd partitions to B.
/// </remarks>
/// <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/566_ClaimsDecideThroughTheOwnershipTable.sql</code-under-test>
[Category("Shard5")]
public class OwnershipByMapClaimSqlTests : EFCoreTestBase {
  private const string PERSPECTIVE = "Ownership.Perspective";
  private const string SUPPORTS_MAP = """{"partition_ownership_level":1}""";
  private const int MOVED = 1;
  private const int KEPT = 2;
  private static readonly Guid _a = new("00000000-0000-0000-0000-00000000000a");
  private static readonly Guid _b = new("00000000-0000-0000-0000-00000000000b");
  private static readonly Guid _outsider = new("00000000-0000-0000-0000-00000000000c");

  // --- one authority: the map moves busy streams ------------------------------------------------

  [Test]
  public async Task Perspective_TheMapMovesThePartition_TheNewOwnerClaimsTheStream_AndTheStoredOwnerNothingAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await _perspectiveEventAsync(conn, stream, MOVED);
    await _ledgerAsync(conn, stream, MOVED, _a);
    await _publishAsync(conn, epoch, [_a, _b]);

    await _claimAsync(conn, _a);
    var afterOld = await _holderAsync(conn, "wh_perspective_events", stream);
    await _claimAsync(conn, _b);

    await Assert.That(afterOld).IsNull().Because("the old owner no longer owns the partition, whatever the ledger says");
    await Assert.That(await _holderAsync(conn, "wh_perspective_events", stream)).IsEqualTo(_b)
      .Because("the map moved the partition to B, so B claims the stream although its stored owner A is live");
    await Assert.That(await _ledgerOwnerAsync(conn, stream)).IsEqualTo(_b).Because("the ledger names who claimed it");
  }

  [Test]
  public async Task Outbox_TheMapMovesThePartition_TheNewOwnerClaimsTheStream_AndTheStoredOwnerNothingAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    _ = await _outboxRowAsync(conn, stream, MOVED);
    await _ledgerAsync(conn, stream, MOVED, _a);
    await _publishAsync(conn, epoch, [_a, _b]);

    await _claimAsync(conn, _a);
    var afterOld = await _holderAsync(conn, "wh_outbox", stream);
    await _claimAsync(conn, _b);

    await Assert.That(afterOld).IsNull();
    await Assert.That(await _holderAsync(conn, "wh_outbox", stream)).IsEqualTo(_b);
    await Assert.That(await _ledgerOwnerAsync(conn, stream)).IsEqualTo(_b);
  }

  [Test]
  public async Task Inbox_TheMapMovesThePartition_TheNewOwnerClaimsTheStream_AndTheStoredOwnerNothingAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await _inboxRowAsync(conn, stream, MOVED);
    await _ledgerAsync(conn, stream, MOVED, _a);
    await _publishAsync(conn, epoch, [_a, _b]);

    await _claimAsync(conn, _a);
    var afterOld = await _holderAsync(conn, "wh_inbox_state", stream);
    await _claimAsync(conn, _b);

    await Assert.That(afterOld).IsNull();
    await Assert.That(await _holderAsync(conn, "wh_inbox_state", stream)).IsEqualTo(_b);
    await Assert.That(await _ledgerOwnerAsync(conn, stream)).IsEqualTo(_b);
  }

  [Test]
  public async Task Inbox_StealingNeverTakesAPartitionThisInstanceDoesNotOwnAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await _publishAsync(conn, epoch, [_a, _b]);
    await _inboxRowAsync(conn, stream, MOVED);

    await _claimAsync(conn, _a, allowSteal: true);

    await Assert.That(await _holderAsync(conn, "wh_inbox_state", stream)).IsNull()
      .Because("stealing was a way round the rank; the map has no way round it");
  }

  [Test]
  public async Task Inbox_AnExpiredLeaseOnAPartitionThatMovedAway_IsNotRenewedByItsHolderAsync() {
    var moved = (Guid)TrackedGuid.New();
    var kept = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await _inboxRowAsync(conn, moved, MOVED, holder: _a, leaseSeconds: -60);
    await _inboxRowAsync(conn, kept, KEPT, holder: _a, leaseSeconds: -60);
    await _ledgerAsync(conn, moved, MOVED, _a);
    await _ledgerAsync(conn, kept, KEPT, _a);
    await _publishAsync(conn, epoch, [_a, _b]);

    await _claimAsync(conn, _a, acquireInbox: false);

    await Assert.That(await _leaseIsLiveAsync(conn, kept)).IsTrue()
      .Because("A still owns the partition, so the row it holds is renewed and offered again");
    await Assert.That(await _leaseIsLiveAsync(conn, moved)).IsFalse()
      .Because("the partition is B's now, so A does not renew it though the ledger names A under a live lease");
  }

  [Test]
  public async Task Fetch_TheNewOwnerFetchesTheStream_AndTheStoredOwnerNothingAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await _perspectiveEventAsync(conn, stream, MOVED, committed: true);
    await _ledgerAsync(conn, stream, MOVED, _a);
    await _publishAsync(conn, epoch, [_a, _b]);

    var byOld = await _fetchAsync(conn, _a, stream);
    var byNew = await _fetchAsync(conn, _b, stream);

    await Assert.That(byOld).IsEqualTo(0).Because("the fetch gate admits the partition's owner only");
    await Assert.That(byNew).IsEqualTo(1).Because("the new owner fetches though the ledger names a live A");
  }

  [Test]
  public async Task MultiStream_TheNewOwnerClaimsTheStream_AndTheStoredOwnerNothingAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await _ledgerAsync(conn, stream, MOVED, _a);
    await _multiStreamRunAsync(conn, stream, MOVED);
    await _publishAsync(conn, epoch, [_a, _b]);

    var byOld = await _claimMultiStreamAsync(conn, _a);
    var byNew = await _claimMultiStreamAsync(conn, _b);

    await Assert.That(byOld).IsEmpty();
    await Assert.That(byNew).IsEquivalentTo([stream]);
  }

  [Test]
  public async Task Ledger_TheNewOwnersClaimNamesItAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await _ledgerAsync(conn, stream, MOVED, _a);
    await _publishAsync(conn, epoch, [_a, _b]);

    await _execAsync(conn, "SELECT wh_lease_claimed_streams(@b, NOW(), NOW() + INTERVAL '5 minutes', ARRAY[@stream]::uuid[], ARRAY[1])",
      ("b", _b), ("stream", stream));

    await Assert.That(await _ledgerOwnerAsync(conn, stream)).IsEqualTo(_b)
      .Because("the claim was the partition owner's; the ledger records it and no longer keeps a live owner's name");
  }

  [Test]
  public async Task Continuation_ContinuesOnlyAStreamOnAPartitionThisInstanceOwnsAsync() {
    var moved = (Guid)TrackedGuid.New();
    var kept = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    var movedHead = await _outboxRowAsync(conn, moved, MOVED, holder: _a);
    _ = await _outboxRowAsync(conn, moved, MOVED);
    var keptHead = await _outboxRowAsync(conn, kept, KEPT, holder: _a);
    _ = await _outboxRowAsync(conn, kept, KEPT);
    await _ledgerAsync(conn, moved, MOVED, _a);
    await _ledgerAsync(conn, kept, KEPT, _a);
    await _publishAsync(conn, epoch, [_a, _b]);

    var continued = await _continueAsync(conn, _a, [moved, kept], [movedHead, keptHead]);

    await Assert.That(continued).Contains(kept).Because("A still owns the partition, so its stream's next run is leased");
    await Assert.That(continued).DoesNotContain(moved)
      .Because("the partition is B's now, so A leases no more of the stream though the ledger names A");
  }

  [Test]
  public async Task Schedules_FireOnThePartitionsOwner_NotTheStoredOwnerAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await _ledgerAsync(conn, stream, MOVED, _a);
    await _execAsync(conn, """
      INSERT INTO wh_schedules (schedule_id, stream_id, partition_number, recurrence_kind, next_fire_at, status, event_type)
      VALUES (gen_random_uuid(), @stream, @partition, 0, NOW() - INTERVAL '1 second', 0, 'TestOccurrence')
      """, ("stream", stream), ("partition", MOVED));
    await _publishAsync(conn, epoch, [_a, _b]);

    var byOld = await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_claim_due_schedules(@inst, NOW() + INTERVAL '5 minutes', 10000)", ("inst", _a));
    var byNew = await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_claim_due_schedules(@inst, NOW() + INTERVAL '5 minutes', 10000)", ("inst", _b));

    await Assert.That(byOld).IsEqualTo(0L);
    await Assert.That(byNew).IsEqualTo(1L).Because("the schedule's partition is B's, so B fires it though the ledger names A");
  }

  // --- one map: nobody ranks itself, and nothing is claimed without a valid map -------------------

  [Test]
  public async Task AClaimerOutsideTheMap_ClaimsNothing_InAnyQueueAsync() {
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await _heartbeatAsync(conn, _outsider);
    await _publishAsync(conn, epoch, [_a, _b]);
    for (var partition = 0; partition < 4; partition++) {
      await _perspectiveEventAsync(conn, (Guid)TrackedGuid.New(), partition);
      _ = await _outboxRowAsync(conn, (Guid)TrackedGuid.New(), partition);
      await _inboxRowAsync(conn, (Guid)TrackedGuid.New(), partition);
    }
    var run = (Guid)TrackedGuid.New();
    await _multiStreamRunAsync(conn, run, 0);

    await _claimAsync(conn, _outsider);

    await Assert.That(await _heldByAsync(conn, _outsider)).IsEqualTo(0L)
      .Because("an instance the map does not name owns no partition; it never ranks itself into a share");
    await Assert.That(await _claimMultiStreamAsync(conn, _outsider)).IsEmpty();
  }

  [Test]
  public async Task WithNoValidMap_NothingIsClaimed_AndTheClaimSaysSoAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await _publishAsync(conn, epoch, [_a, _b]);
    await _perspectiveEventAsync(conn, stream, KEPT);
    await _multiStreamRunAsync(conn, (Guid)TrackedGuid.New(), KEPT);
    await _execAsync(conn, "UPDATE wh_published_partition_assignment SET lease_expires_at = NOW() - INTERVAL '1 second'");
    await _execAsync(conn, "UPDATE wh_service_instances SET last_heartbeat_at = NOW() - INTERVAL '1 hour' WHERE instance_id = @b", ("b", _b));

    var notices = new List<string>();
    conn.Notice += (_, e) => notices.Add(e.Notice.MessageText);
    await _claimAsync(conn, _a);

    await Assert.That(await _holderAsync(conn, "wh_perspective_events", stream)).IsNull()
      .Because("past its lease, a map that names a member who is not live is not claimed by; the claimer waits");
    await Assert.That(notices).Contains("whizbang.partition_map_unavailable=true");
    await Assert.That(await _claimMultiStreamAsync(conn, _a)).IsEmpty();
  }

  [Test]
  public async Task AnAssignerLapse_DoesNotStallClaims_WhileTheMapsMembersAreLiveAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await _publishAsync(conn, epoch, [_a, _b]);
    await _perspectiveEventAsync(conn, stream, KEPT);
    await _execAsync(conn, "UPDATE wh_published_partition_assignment SET lease_expires_at = NOW() - INTERVAL '1 second'");

    await _claimAsync(conn, _a);

    await Assert.That(await _holderAsync(conn, "wh_perspective_events", stream)).IsEqualTo(_a);
  }

  [Test]
  public async Task APartitionHandingOver_IsClaimedByNeitherSideAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await _publishAsync(conn, epoch, [_a, _b]);
    await _perspectiveEventAsync(conn, stream, KEPT);
    await _execAsync(conn,
      "UPDATE wh_partition_ownership SET state = 'handing_over', next_owner_instance_id = @b, handover_deadline = NOW() + INTERVAL '30 seconds' WHERE partition_number = @p",
      ("b", _b), ("p", KEPT));

    await _claimAsync(conn, _a);
    await _claimAsync(conn, _b);

    await Assert.That(await _holderAsync(conn, "wh_perspective_events", stream)).IsNull();
  }

  [Test]
  public async Task PartitionsIOwn_IsWhIOwnsAnswerForEveryPartitionAsync() {
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await _publishAsync(conn, epoch, [_a, _b]);
    await _execAsync(conn, "UPDATE wh_partition_ownership SET state = 'handing_over', next_owner_instance_id = @b WHERE partition_number = @p",
      ("b", _b), ("p", KEPT));

    var disagreements = await _scalarAsync<long>(conn, """
      SELECT count(*) FROM generate_series(0, 9999) AS g(p)
      WHERE (wh_i_own(g.p, @a) IS NOT NULL) <> (g.p IN (SELECT o FROM wh_partitions_i_own(@a) AS s(o)))
      """, ("a", _a));
    var owned = await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_partitions_i_own(@a)", ("a", _a));

    await Assert.That(disagreements).IsEqualTo(0L).Because("the set form is the same rule as the predicate, partition by partition");
    await Assert.That(owned).IsEqualTo(4_999L).Because("A owns the even partitions, less the one handing over");
  }

  // --- the wake-up follows the map -------------------------------------------------------------

  [Test]
  public async Task WakeUp_RingsThePartitionsOwner_NotTheStoredOwnerAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await _ledgerAsync(conn, stream, MOVED, _a);
    await _publishAsync(conn, epoch, [_a, _b]);

    await using var tx = await conn.BeginTransactionAsync();
    await _execAsync(conn, tx, "SELECT notify_instance_owners_with_payload('inbox', 'inbox', ARRAY[@stream]::uuid[])", ("stream", stream));
    var toOld = await _doorbellsAsync(conn, tx, _a);
    var toNew = await _doorbellsAsync(conn, tx, _b);
    await tx.RollbackAsync();

    await Assert.That(toNew).IsEqualTo(1L).Because("the wake-up goes to the instance the map says owns the stream's partition");
    await Assert.That(toOld).IsEqualTo(0L);
  }

  [Test]
  public async Task WakeUp_ForAStreamTheLedgerNeverHeld_RingsTheOwnerOfItsComputedPartitionAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var epoch = await _mapModeAsync(conn);
    await _publishAsync(conn, epoch, [_a, _b]);
    var partition = await _scalarAsync<int>(conn, "SELECT compute_partition(@stream)", ("stream", stream));
    var owner = partition % 2 == 0 ? _a : _b;
    var other = owner == _a ? _b : _a;

    await using var tx = await conn.BeginTransactionAsync();
    await _execAsync(conn, tx, "SELECT notify_instance_owners_with_payload('signal', 'signal', ARRAY[@stream]::uuid[])", ("stream", stream));
    var toOwner = await _doorbellsAsync(conn, tx, owner);
    var toOther = await _doorbellsAsync(conn, tx, other);
    await tx.RollbackAsync();

    await Assert.That(toOwner).IsEqualTo(1L).Because("every kind, a signal's included, is routed by the map");
    await Assert.That(toOther).IsEqualTo(0L);
  }

  // --- the switch off is exactly as before ------------------------------------------------------

  [Test]
  public async Task SwitchOff_TheStoredOwnerKeepsItsStream_AsBeforeAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a);
    await _heartbeatAsync(conn, _b);
    var epoch = await _voteAsync(conn);
    await _publishAsync(conn, epoch, [_a, _b]);
    await _perspectiveEventAsync(conn, stream, MOVED);
    await _ledgerAsync(conn, stream, MOVED, _a);

    await _claimAsync(conn, _b);

    await Assert.That(await _scalarAsync<bool>(conn, "SELECT wh_partition_ownership_by_map()")).IsFalse();
    await Assert.That(await _holderAsync(conn, "wh_perspective_events", stream)).IsNull()
      .Because("with the switch off the live stored owner still wins, exactly as before");
  }

  // --- helpers --------------------------------------------------------------------------------

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  /// <summary>
  /// Registers A and B live and supporting ownership by map, elects A, publishes [A], and turns the switch on; returns A's
  /// role epoch.
  /// </summary>
  private static async Task<long> _mapModeAsync(NpgsqlConnection conn) {
    await _heartbeatAsync(conn, _a);
    await _heartbeatAsync(conn, _b);
    var epoch = await _voteAsync(conn);
    await _publishAsync(conn, epoch, [_a]);
    var answer = await _scalarAsync<string>(conn, "SELECT wh_open_partition_ownership_by_map(@role, @a, @epoch)",
      ("role", PartitionAssignerOptions.ROLE), ("a", _a), ("epoch", epoch));
    if (answer != "opened") {
      throw new InvalidOperationException($"precondition: the switch did not turn on ({answer})");
    }
    return epoch;
  }

  private static async Task _heartbeatAsync(NpgsqlConnection conn, Guid instance) =>
    await _execAsync(conn, "SELECT record_heartbeat(@inst, 'test', 'test-host', 1, @metadata::jsonb, NULL, NULL, NULL, 'pooled')",
      ("inst", instance), ("metadata", SUPPORTS_MAP));

  private static async Task<long> _voteAsync(NpgsqlConnection conn) =>
    Convert.ToInt64(await _scalarAsync<object>(conn,
      "SELECT epoch FROM wh_vote_role(@role, @a, INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL) v WHERE v.outcome = 'granted'",
      ("role", PartitionAssignerOptions.ROLE), ("a", _a)), CultureInfo.InvariantCulture);

  private static async Task _publishAsync(NpgsqlConnection conn, long epoch, Guid[] members) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_publish_partition_assignment(@role, @a, @epoch, @members, INTERVAL '90 seconds')";
    cmd.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
    cmd.Parameters.AddWithValue("a", _a);
    cmd.Parameters.AddWithValue(nameof(epoch), epoch);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(members), NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = members });
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>The ledger names <paramref name="owner"/> under a live lease, as after that instance's claim.</summary>
  private static Task _ledgerAsync(NpgsqlConnection conn, Guid stream, int partition, Guid owner) =>
    _execAsync(conn, """
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at, lease_expiry)
      VALUES (@stream, @partition, @owner, NOW(), NOW() + INTERVAL '5 minutes')
      """, ("stream", stream), ("partition", partition), ("owner", owner));

  /// <summary>One pending, unleased perspective event on the stream; committed (in the event store) when asked.</summary>
  private static async Task _perspectiveEventAsync(NpgsqlConnection conn, Guid stream, int partition, bool committed = false) {
    var eventId = (Guid)TrackedGuid.New();
    if (committed) {
      await _execAsync(conn, """
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
        VALUES (@event, @stream, @stream, 'TestAggregate', 'TestEvent', 1, NOW() - INTERVAL '1 minute')
        """, ("event", eventId), ("stream", stream));
    }
    await _execAsync(conn, """
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at)
      VALUES (gen_random_uuid(), @stream, @perspective, @event, @partition, 0, 0, NOW() - INTERVAL '1 minute')
      """, ("stream", stream), ("perspective", PERSPECTIVE), ("event", eventId), ("partition", partition));
  }

  /// <summary>One pending outbox row on the stream, arriving now; held by <paramref name="holder"/> under a live lease when one is named.</summary>
  private static async Task<Guid> _outboxRowAsync(NpgsqlConnection conn, Guid stream, int partition, Guid? holder = null) {
    var id = (Guid)TrackedGuid.New();
    await _execAsync(conn, """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number,
         instance_id, lease_expiry)
      VALUES (@id, 'test-topic', 'TestEvent', '{}', '{}', 0, 0, clock_timestamp(), @stream, @partition,
              @holder::uuid, CASE WHEN @holder::uuid IS NULL THEN NULL ELSE NOW() + INTERVAL '5 minutes' END)
      """, ("id", id), ("stream", stream), ("partition", partition), ("holder", (object?)holder ?? DBNull.Value));
    return id;
  }

  private static async Task<List<Guid>> _continueAsync(NpgsqlConnection conn, Guid instance, Guid[] streams, Guid[] after) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT DISTINCT stream_id FROM wh_continue_outbox_streams(@inst, @streams, @after, 10)";
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.AddWithValue(nameof(streams), streams);
    cmd.Parameters.AddWithValue(nameof(after), after);
    var continued = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      continued.Add(reader.GetGuid(0));
    }
    return continued;
  }

  /// <summary>One pending inbox command on the stream; held by <paramref name="holder"/> under the given lease when one is named.</summary>
  private static Task _inboxRowAsync(NpgsqlConnection conn, Guid stream, int partition, Guid? holder = null, int leaseSeconds = 0) =>
    _execAsync(conn, """
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id, is_event, priority)
        VALUES (@id, 'TestHandler', 'TestCommand', '{}', '{}', NOW() - INTERVAL '1 minute', @stream, FALSE, 150)
        RETURNING message_id, stream_id, received_at
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number, instance_id, lease_expiry)
      SELECT m.message_id, m.stream_id, m.received_at, 150, FALSE, 0, 0, @partition, @holder::uuid,
             CASE WHEN @holder::uuid IS NULL THEN NULL ELSE NOW() + make_interval(secs => @lease) END
      FROM m
      """, ("id", (Guid)TrackedGuid.New()), ("stream", stream), ("partition", partition),
      ("holder", (object?)holder ?? DBNull.Value), ("lease", (double)leaseSeconds));

  private static async Task _multiStreamRunAsync(NpgsqlConnection conn, Guid stream, int partition) {
    var run = (Guid)TrackedGuid.New();
    await _execAsync(conn, """
      INSERT INTO wh_multi_stream_runs (run_id, command_type, payload, scope, stream_count, created_at)
      VALUES (@run, 'TestCommand', '{}', NULL, 1, NOW())
      """, ("run", run));
    await _execAsync(conn, """
      INSERT INTO wh_multi_stream_run_streams (run_id, stream_id, partition_number)
      VALUES (@run, @stream, @partition)
      """, ("run", run), ("stream", stream), ("partition", partition));
  }

  private static async Task<List<Guid>> _claimMultiStreamAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT stream_id FROM wh_claim_multi_stream_work(@inst, 10, 300)";
    cmd.Parameters.AddWithValue("inst", instance);
    var streams = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      streams.Add(reader.GetGuid(0));
    }
    return streams;
  }

  private static Task _claimAsync(NpgsqlConnection conn, Guid instance, bool allowSteal = false, bool acquireInbox = true) =>
    _execAsync(conn, """
      SELECT count(*) FROM claim_work(
        p_instance_id => @inst, p_service_name => 'test', p_host_name => 'test-host', p_process_id => 1,
        p_max_streams => 25, p_partition_count => 10000, p_lease_seconds => 300, p_max_rows => 100,
        p_allow_steal => @steal, p_acquire_inbox => @acquire)
      """, ("inst", instance), ("steal", allowSteal), ("acquire", acquireInbox));

  private static async Task<int> _fetchAsync(NpgsqlConnection conn, Guid instance, Guid stream) =>
    (int)await _scalarAsync<long>(conn, "SELECT count(*) FROM get_stream_events(@inst, ARRAY[@stream]::uuid[])",
      ("inst", instance), ("stream", stream));

  private static Task<long> _heldByAsync(NpgsqlConnection conn, Guid instance) =>
    _scalarAsync<long>(conn, """
      SELECT (SELECT count(*) FROM wh_perspective_events WHERE instance_id = @inst)
           + (SELECT count(*) FROM wh_outbox WHERE instance_id = @inst)
           + (SELECT count(*) FROM wh_inbox_state WHERE instance_id = @inst)
      """, ("inst", instance));

  private static async Task<Guid?> _holderAsync(NpgsqlConnection conn, string table, Guid stream) =>
    await _scalarAsync<object>(conn, $"SELECT instance_id FROM {table} WHERE stream_id = @stream", ("stream", stream)) is Guid holder
      ? holder : null;

  private static async Task<Guid?> _ledgerOwnerAsync(NpgsqlConnection conn, Guid stream) =>
    await _scalarAsync<object>(conn, "SELECT assigned_instance_id FROM wh_active_streams WHERE stream_id = @stream", ("stream", stream)) is Guid owner
      ? owner : null;

  private static Task<bool> _leaseIsLiveAsync(NpgsqlConnection conn, Guid stream) =>
    _scalarAsync<bool>(conn, "SELECT lease_expiry > NOW() FROM wh_inbox_state WHERE stream_id = @stream", ("stream", stream));

  private static async Task<long> _doorbellsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = $"SELECT count(*) FROM wh_doorbell_queue WHERE channel = 'wh_work_i_{instance}'";
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
  }

  private static Task _execAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) =>
    _execAsync(conn, null, sql, parameters);

  private static async Task _execAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync();
  }
}
