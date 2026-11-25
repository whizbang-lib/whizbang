// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A rebalance hands each moved partition over (#2557, phase 5 of #2540; design 3.4). In map mode a publish that moves a
/// partition away from a live owner sets it 'handing_over' with the next owner and a deadline, keeping the owner and the
/// epoch; nobody claims it until the flip. The old owner acknowledges once it has finished, which releases its unstarted
/// rows there and flips the partition to the next owner at epoch + 1 in one statement fenced on the old epoch. Past the
/// deadline the assigner flips it itself and releases the old owner's rows. A renewal never keeps a row of a partition
/// the instance no longer owns. With the switch off, a publish moves a partition at once, as before.
/// </summary>
/// <remarks>
/// Instances <c>A</c> and <c>B</c> sort in that order; <c>A</c> holds the assigner role. The map [A] gives A everything;
/// [A, B] then moves the odd partitions to B. Deadlines are driven on the database's clock, never the host's.
/// </remarks>
/// <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/571_MovedPartitionsAreHandedOver.sql</code-under-test>
[Category("Shard5")]
public class PartitionHandoverSqlTests : EFCoreTestBase {
  private const string PERSPECTIVE = "Handover.Perspective";
  private const string SUPPORTS_MAP = """{"partition_ownership_level":1}""";
  private const int MOVED = 1;
  private const int KEPT = 2;
  private static readonly Guid _a = new("00000000-0000-0000-0000-00000000000a");
  private static readonly Guid _b = new("00000000-0000-0000-0000-00000000000b");
  private static readonly Guid _c = new("00000000-0000-0000-0000-00000000000c");

  // --- the publish hands over ---------------------------------------------------------------------

  [Test]
  public async Task Publish_InMapMode_AMovedPartitionWithALiveOwner_HandsOver_KeepingTheOwnerAndEpochAsync() {
    await using var conn = await _openAsync();
    var role = await _mapModeAsync(conn);
    var before = await _ownershipAsync(conn, MOVED);

    await _publishAsync(conn, role, [_a, _b]);

    var after = await _ownershipAsync(conn, MOVED);
    await Assert.That(after.State).IsEqualTo("handing_over");
    await Assert.That(after.Owner).IsEqualTo(_a).Because("ownership stays with the old owner until the flip");
    await Assert.That(after.Epoch).IsEqualTo(before.Epoch).Because("the epoch rises only at the flip");
    await Assert.That(after.Next).IsEqualTo(_b);
    await Assert.That(await _scalarAsync<bool>(conn,
      "SELECT handover_deadline BETWEEN NOW() + INTERVAL '29 seconds' AND clock_timestamp() + INTERVAL '30 seconds' FROM wh_partition_ownership WHERE partition_number = @p",
      ("p", MOVED))).IsTrue().Because("the deadline defaults to 30 seconds on the database's clock");
    await Assert.That((await _ownershipAsync(conn, KEPT)).State).IsEqualTo("active").Because("a partition that does not move is untouched");
  }

  [Test]
  public async Task Publish_InMapMode_TheDeadlineIsTheOneThePublishNamesAsync() {
    await using var conn = await _openAsync();
    var role = await _mapModeAsync(conn);

    await _publishAsync(conn, role, [_a, _b], deadline: TimeSpan.FromSeconds(90));

    await Assert.That(await _scalarAsync<bool>(conn,
      "SELECT handover_deadline > NOW() + INTERVAL '89 seconds' FROM wh_partition_ownership WHERE partition_number = @p",
      ("p", MOVED))).IsTrue();
  }

  // #2577 changed this contract: before 610 the partition moved at once, and 571 and 581 still link this test as
  // Publish_InMapMode_AMovedPartitionWhoseOwnerIsNotLive_FlipsAtOnceAsync.
  [Test]
  public async Task Publish_InMapMode_AMovedPartitionWhoseOwnerIsNotLive_HandsOverWithAnImmediateDeadline_AndTheSweepFlipsItAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _c);
    var role = await _mapModeAsync(conn);
    await _publishAsync(conn, role, [_a, _c]);
    await _handOverAllAsync(conn, _a);
    var before = await _ownershipAsync(conn, MOVED);
    await _execAsync(conn, "UPDATE wh_service_instances SET last_heartbeat_at = NOW() - INTERVAL '1 hour' WHERE instance_id = @c", ("c", _c));

    await _publishAsync(conn, role, [_a, _b]);
    var published = await _ownershipAsync(conn, MOVED);
    var dueAtOnce = await _scalarAsync<bool>(conn,
      "SELECT handover_deadline <= clock_timestamp() FROM wh_partition_ownership WHERE partition_number = @p", ("p", MOVED));
    var swept = await _expireAsync(conn, role);
    var after = await _ownershipAsync(conn, MOVED);

    await Assert.That(before.Owner).IsEqualTo(_c);
    await Assert.That(published.State).IsEqualTo("handing_over")
      .Because("#2577: a publish in map mode never raises an epoch itself, so it never waits on a writer holding one");
    await Assert.That(published.Owner).IsEqualTo(_c);
    await Assert.That(published.Epoch).IsEqualTo(before.Epoch);
    await Assert.That(published.Next).IsEqualTo(_b);
    await Assert.That(dueAtOnce).IsTrue().Because("an owner that is not live finishes nothing, so nothing waits for it");
    await Assert.That(swept).IsGreaterThan(0);
    await Assert.That(after.State).IsEqualTo("active");
    await Assert.That(after.Owner).IsEqualTo(_b).Because("the sweep moves it on the same assigner tick");
    await Assert.That(after.Epoch).IsEqualTo(before.Epoch + 1).Because("its late writes are fenced by the new epoch");
  }

  [Test]
  public async Task Publish_InMapMode_APartitionHandingOverWhoseOwnerIsNoLongerLive_IsDueAtOnceAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _c);
    var role = await _mapModeAsync(conn);
    await _publishAsync(conn, role, [_a, _b]);
    var handingOver = await _ownershipAsync(conn, MOVED);
    await _execAsync(conn, "UPDATE wh_service_instances SET last_heartbeat_at = NOW() - INTERVAL '1 hour' WHERE instance_id = @a", ("a", _a));

    await _publishAsync(conn, role, [_a, _c]);
    var redirected = await _ownershipAsync(conn, MOVED);
    var dueAtOnce = await _scalarAsync<bool>(conn,
      "SELECT handover_deadline <= clock_timestamp() FROM wh_partition_ownership WHERE partition_number = @p", ("p", MOVED));

    await Assert.That(handingOver.State).IsEqualTo("handing_over");
    await Assert.That(redirected.State).IsEqualTo("handing_over");
    await Assert.That(redirected.Owner).IsEqualTo(_a);
    await Assert.That(redirected.Epoch).IsEqualTo(handingOver.Epoch).Because("the publish raises no epoch, even for a dead owner");
    await Assert.That(redirected.Next).IsEqualTo(_c);
    await Assert.That(dueAtOnce).IsTrue().Because("its owner is no longer live, so its deadline is brought forward to now");
  }

  [Test]
  public async Task Publish_SwitchOff_MovesAPartitionAtOnce_AsBeforeAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a);
    await _heartbeatAsync(conn, _b);
    var role = await _voteAsync(conn);
    await _publishAsync(conn, role, [_a]);
    var before = await _ownershipAsync(conn, MOVED);

    await _publishAsync(conn, role, [_a, _b]);

    var after = await _ownershipAsync(conn, MOVED);
    await Assert.That(after.State).IsEqualTo("active");
    await Assert.That(after.Owner).IsEqualTo(_b);
    await Assert.That(after.Epoch).IsEqualTo(before.Epoch + 1);
  }

  [Test]
  public async Task Republish_GivingAPartitionBackToItsOwnerWhileHandingOver_CancelsTheHandoverAsync() {
    await using var conn = await _openAsync();
    var role = await _mapModeAsync(conn);
    await _publishAsync(conn, role, [_a, _b]);
    var handingOver = await _ownershipAsync(conn, MOVED);

    await _publishAsync(conn, role, [_a]);

    var after = await _ownershipAsync(conn, MOVED);
    await Assert.That(after.State).IsEqualTo("active");
    await Assert.That(after.Owner).IsEqualTo(_a);
    await Assert.That(after.Epoch).IsEqualTo(handingOver.Epoch).Because("the owner never changed, so neither did the epoch");
    await Assert.That(after.Next).IsNull();
  }

  [Test]
  public async Task Republish_NamingAnotherNextOwnerWhileHandingOver_RedirectsTheHandoverAsync() {
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _c);
    var role = await _mapModeAsync(conn);
    await _publishAsync(conn, role, [_a, _b]);

    await _publishAsync(conn, role, [_a, _c]);

    var after = await _ownershipAsync(conn, MOVED);
    await Assert.That(after.State).IsEqualTo("handing_over");
    await Assert.That(after.Owner).IsEqualTo(_a);
    await Assert.That(after.Next).IsEqualTo(_c);
  }

  // --- nobody claims a partition handing over -------------------------------------------------------

  [Test]
  public async Task Claims_APartitionHandingOver_IsClaimableByNeitherSide_UntilTheFlipAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var role = await _mapModeAsync(conn);
    await _publishAsync(conn, role, [_a, _b]);
    await _perspectiveEventAsync(conn, stream, MOVED);

    await _claimAsync(conn, _a);
    var byOld = await _holderAsync(conn, stream);
    await _claimAsync(conn, _b);
    var byNew = await _holderAsync(conn, stream);
    await _handOverAllAsync(conn, _a);
    await _claimAsync(conn, _b);

    await Assert.That(byOld).IsNull().Because("the old owner stops claiming new work in a partition it is handing over");
    await Assert.That(byNew).IsNull().Because("the next owner owns nothing there until the flip");
    await Assert.That(await _holderAsync(conn, stream)).IsEqualTo(_b).Because("after the flip the new owner claims it");
  }

  // --- the acknowledgement flips, fenced on the old epoch --------------------------------------------

  [Test]
  public async Task Ack_FlipsToTheNextOwnerAtEpochPlusOne_AndReleasesTheOldOwnersUnstartedRowsAsync() {
    var moved = (Guid)TrackedGuid.New();
    var kept = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var role = await _mapModeAsync(conn);
    var movedWork = await _perspectiveEventAsync(conn, moved, MOVED, holder: _a, attempts: 1);
    var keptWork = await _perspectiveEventAsync(conn, kept, KEPT, holder: _a, attempts: 1);
    await _publishAsync(conn, role, [_a, _b]);
    var handingOver = await _ownershipAsync(conn, MOVED);

    var flipped = await _ackAsync(conn, _a, [MOVED], [handingOver.Epoch]);

    var after = await _ownershipAsync(conn, MOVED);
    await Assert.That(flipped).IsEquivalentTo([MOVED]);
    await Assert.That(after.State).IsEqualTo("active");
    await Assert.That(after.Owner).IsEqualTo(_b);
    await Assert.That(after.Epoch).IsEqualTo(handingOver.Epoch + 1);
    await Assert.That(await _workHolderAsync(conn, movedWork)).IsNull().Because("the old owner's unstarted row is released to the new owner");
    await Assert.That(await _attemptsAsync(conn, movedWork)).IsEqualTo(0).Because("an unstarted row's attempt is refunded");
    await Assert.That(await _workHolderAsync(conn, keptWork)).IsEqualTo(_a).Because("a partition it keeps is untouched");
  }

  [Test]
  public async Task Ack_UnderAStaleEpoch_OrByAnInstanceThatIsNotTheOwner_FlipsNothingAsync() {
    await using var conn = await _openAsync();
    var role = await _mapModeAsync(conn);
    await _publishAsync(conn, role, [_a, _b]);
    var handingOver = await _ownershipAsync(conn, MOVED);

    var stale = await _ackAsync(conn, _a, [MOVED], [handingOver.Epoch - 1]);
    var notOwner = await _ackAsync(conn, _b, [MOVED], [handingOver.Epoch]);

    await Assert.That(stale).IsEmpty();
    await Assert.That(notOwner).IsEmpty();
    await Assert.That((await _ownershipAsync(conn, MOVED)).State).IsEqualTo("handing_over");
  }

  [Test]
  public async Task Ack_OfAnActivePartition_FlipsNothingAsync() {
    await using var conn = await _openAsync();
    _ = await _mapModeAsync(conn);
    var active = await _ownershipAsync(conn, MOVED);

    var flipped = await _ackAsync(conn, _a, [MOVED], [active.Epoch]);

    await Assert.That(flipped).IsEmpty().Because("only a partition handing over can be acknowledged");
    await Assert.That((await _ownershipAsync(conn, MOVED)).Owner).IsEqualTo(_a);
  }

  [Test]
  public async Task HandingOver_ListsWhatTheOldOwnerMustHandOver_AndNothingForAnyoneElseAsync() {
    await using var conn = await _openAsync();
    var role = await _mapModeAsync(conn);
    await _publishAsync(conn, role, [_a, _b]);

    var forOld = await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_partitions_handing_over(@a)", ("a", _a));
    var forNew = await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_partitions_handing_over(@b)", ("b", _b));

    await Assert.That(forOld).IsEqualTo(5_000L).Because("A hands over every odd partition");
    await Assert.That(forNew).IsEqualTo(0L);
  }

  // --- the deadline -------------------------------------------------------------------------------------

  [Test]
  public async Task ExpiredHandover_PastTheDeadline_TheAssignerFlips_AndReleasesTheOldOwnersRowsWithoutRefundAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var role = await _mapModeAsync(conn);
    var work = await _perspectiveEventAsync(conn, stream, MOVED, holder: _a, attempts: 1);
    await _publishAsync(conn, role, [_a, _b]);
    var handingOver = await _ownershipAsync(conn, MOVED);
    await _execAsync(conn, "UPDATE wh_partition_ownership SET handover_deadline = NOW() - INTERVAL '1 second' WHERE partition_number = @p",
      ("p", MOVED));

    var completed = await _expireAsync(conn, role);

    var after = await _ownershipAsync(conn, MOVED);
    await Assert.That(completed).IsEqualTo(1);
    await Assert.That(after.Owner).IsEqualTo(_b);
    await Assert.That(after.Epoch).IsEqualTo(handingOver.Epoch + 1).Because("the old owner's late writes are fenced from here on");
    await Assert.That(await _workHolderAsync(conn, work)).IsNull().Because("the new owner replays what the old owner held");
    await Assert.That(await _attemptsAsync(conn, work)).IsEqualTo(1).Because("the row may have been started, so its attempt stands");
  }

  [Test]
  public async Task ExpiredHandover_BeforeTheDeadline_FlipsNothingAsync() {
    await using var conn = await _openAsync();
    var role = await _mapModeAsync(conn);
    await _publishAsync(conn, role, [_a, _b]);

    var completed = await _expireAsync(conn, role);

    await Assert.That(completed).IsEqualTo(0);
    await Assert.That((await _ownershipAsync(conn, MOVED)).State).IsEqualTo("handing_over");
  }

  [Test]
  public async Task ExpiredHandover_ByADeposedAssigner_IsRefusedAsync() {
    await using var conn = await _openAsync();
    var role = await _mapModeAsync(conn);
    await _publishAsync(conn, role, [_a, _b]);

    var refused = await Assert.ThrowsAsync<PostgresException>(() => _expireAsync(conn, role + 1));

    await Assert.That(refused!.SqlState).IsEqualTo("WHF01");
  }

  // --- order across the handover -------------------------------------------------------------------------

  [Test]
  public async Task Order_TheNewOwnerTakesTheStreamAfterTheOldOwnersCompletedEvent_AndNotBeforeAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var role = await _mapModeAsync(conn);
    var first = await _perspectiveEventAsync(conn, stream, MOVED, holder: _a);
    var second = await _perspectiveEventAsync(conn, stream, MOVED, holder: _a);
    await _publishAsync(conn, role, [_a, _b]);
    var handingOver = await _ownershipAsync(conn, MOVED);

    await _claimAsync(conn, _b);
    var takenEarly = await _workHolderAsync(conn, second);
    await _execAsync(conn, "SELECT complete_perspective('[]'::jsonb, ARRAY[@w]::uuid[], FALSE, ARRAY[@e]::bigint[])",
      ("w", first), ("e", handingOver.Epoch));
    _ = await _ackAsync(conn, _a, [MOVED], [handingOver.Epoch]);
    await _claimAsync(conn, _b);

    await Assert.That(takenEarly).IsEqualTo(_a).Because("while handing over, the next owner cannot take the stream's later event");
    await Assert.That(await _countAsync(conn, first)).IsEqualTo(0L).Because("the old owner's in-flight completion lands under its epoch");
    await Assert.That(await _workHolderAsync(conn, second)).IsEqualTo(_b)
      .Because("the new owner continues the stream from the event after the one the old owner completed");
  }

  // --- renewals stop for moved partitions -------------------------------------------------------------

  [Test]
  public async Task Renewal_KeepsARowWhileHandingOver_ButNotOnceThePartitionHasMovedAsync() {
    var moved = (Guid)TrackedGuid.New();
    var kept = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    var role = await _mapModeAsync(conn);
    var movedWork = await _perspectiveEventAsync(conn, moved, MOVED, holder: _a);
    var keptWork = await _perspectiveEventAsync(conn, kept, KEPT, holder: _a);
    await _publishAsync(conn, role, [_a, _b]);

    var whileHandingOver = await _renewAsync(conn, _a, [movedWork, keptWork]);
    await _execAsync(conn, """
      UPDATE wh_partition_ownership SET owner_instance_id = @b, epoch = epoch + 1, state = 'active',
        next_owner_instance_id = NULL, handover_deadline = NULL
      WHERE partition_number = @p
      """, ("b", _b), ("p", MOVED));
    var afterTheFlip = await _renewAsync(conn, _a, [movedWork, keptWork]);

    await Assert.That(whileHandingOver).IsEmpty().Because("the old owner still owns the partition until the flip");
    await Assert.That(afterTheFlip).IsEquivalentTo([movedWork])
      .Because("a renewal never keeps a row of a partition the instance no longer owns");
  }

  [Test]
  public async Task Renewal_SwitchOff_RenewsAsBeforeAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var conn = await _openAsync();
    await _heartbeatAsync(conn, _a);
    await _heartbeatAsync(conn, _b);
    var role = await _voteAsync(conn);
    await _publishAsync(conn, role, [_a, _b]);
    var work = await _perspectiveEventAsync(conn, stream, MOVED, holder: _a);

    var notRenewed = await _renewAsync(conn, _a, [work]);

    await Assert.That(notRenewed).IsEmpty().Because("off, the holder renews what it holds, as before");
  }

  // --- helpers --------------------------------------------------------------------------------

  private sealed record Ownership(Guid Owner, long Epoch, string State, Guid? Next);

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

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
      ("role", PartitionAssignerOptions.ROLE), ("a", _a)), System.Globalization.CultureInfo.InvariantCulture);

  private static async Task _publishAsync(NpgsqlConnection conn, long epoch, Guid[] members, TimeSpan? deadline = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = deadline is null
      ? "SELECT wh_publish_partition_assignment(@role, @a, @epoch, @members, INTERVAL '90 seconds')"
      : "SELECT wh_publish_partition_assignment(@role, @a, @epoch, @members, INTERVAL '90 seconds', NULL, @deadline)";
    cmd.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
    cmd.Parameters.AddWithValue("a", _a);
    cmd.Parameters.AddWithValue(nameof(epoch), epoch);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(members), NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = members });
    if (deadline is { } d) {
      cmd.Parameters.AddWithValue(nameof(deadline), d);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>A acknowledges every partition it is handing over, as its handover worker does once it has finished.</summary>
  private static Task _handOverAllAsync(NpgsqlConnection conn, Guid owner) =>
    _execAsync(conn, """
      SELECT count(*) FROM wh_acknowledge_handover(@owner,
        (SELECT array_agg(h.partition_number) FROM wh_partitions_handing_over(@owner) h),
        (SELECT array_agg(h.epoch) FROM wh_partitions_handing_over(@owner) h))
      """, ("owner", owner));

  private static async Task<List<int>> _ackAsync(NpgsqlConnection conn, Guid instance, int[] partitions, long[] epochs) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT partition_number FROM wh_acknowledge_handover(@i, @p, @e)";
    cmd.Parameters.AddWithValue("i", instance);
    cmd.Parameters.AddWithValue("p", partitions);
    cmd.Parameters.AddWithValue("e", epochs);
    var flipped = new List<int>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      flipped.Add(reader.GetInt32(0));
    }
    return flipped;
  }

  private static Task<int> _expireAsync(NpgsqlConnection conn, long role) =>
    _scalarAsync<int>(conn, "SELECT wh_complete_expired_handovers(@role, @a, @epoch)",
      ("role", PartitionAssignerOptions.ROLE), ("a", _a), ("epoch", role));

  private static async Task<Ownership> _ownershipAsync(NpgsqlConnection conn, int partition) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT owner_instance_id, epoch, state, next_owner_instance_id FROM wh_partition_ownership WHERE partition_number = @p";
    cmd.Parameters.AddWithValue("p", partition);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return new Ownership(reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2), await reader.IsDBNullAsync(3) ? null : reader.GetGuid(3));
  }

  private static async Task<Guid> _perspectiveEventAsync(NpgsqlConnection conn, Guid stream, int partition, Guid? holder = null, int attempts = 0) {
    var work = (Guid)TrackedGuid.New();
    await _execAsync(conn, """
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at, instance_id, lease_expiry)
      VALUES (@w, @stream, @perspective, @event, @p, 0, @attempts, clock_timestamp() - INTERVAL '1 minute', @holder::uuid,
              CASE WHEN @holder::uuid IS NULL THEN NULL ELSE NOW() + INTERVAL '5 minutes' END)
      """, ("w", work), ("stream", stream), ("perspective", PERSPECTIVE), ("event", (Guid)TrackedGuid.New()), ("p", partition),
      ("attempts", attempts), ("holder", (object?)holder ?? DBNull.Value));
    return work;
  }

  private static Task _claimAsync(NpgsqlConnection conn, Guid instance) =>
    _execAsync(conn, """
      SELECT count(*) FROM claim_work(
        p_instance_id => @inst, p_service_name => 'test', p_host_name => 'test-host', p_process_id => 1,
        p_max_streams => 25, p_partition_count => 10000, p_lease_seconds => 300, p_max_rows => 100)
      """, ("inst", instance));

  private static async Task<Guid[]> _renewAsync(NpgsqlConnection conn, Guid instance, Guid[] ids) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_renew_held_leases('perspective_event', @i, @ids, 300)";
    cmd.Parameters.AddWithValue("i", instance);
    cmd.Parameters.AddWithValue(nameof(ids), ids);
    return (Guid[])(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<Guid?> _holderAsync(NpgsqlConnection conn, Guid stream) =>
    await _scalarAsync<object>(conn, "SELECT instance_id FROM wh_perspective_events WHERE stream_id = @s LIMIT 1", ("s", stream)) is Guid holder
      ? holder : null;

  private static async Task<Guid?> _workHolderAsync(NpgsqlConnection conn, Guid work) =>
    await _scalarAsync<object>(conn, "SELECT instance_id FROM wh_perspective_events WHERE event_work_id = @w", ("w", work)) is Guid holder
      ? holder : null;

  private static Task<int> _attemptsAsync(NpgsqlConnection conn, Guid work) =>
    _scalarAsync<int>(conn, "SELECT attempts FROM wh_perspective_events WHERE event_work_id = @w", ("w", work));

  private static Task<long> _countAsync(NpgsqlConnection conn, Guid work) =>
    _scalarAsync<long>(conn, "SELECT count(*) FROM wh_perspective_events WHERE event_work_id = @w", ("w", work));

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync();
  }
}
