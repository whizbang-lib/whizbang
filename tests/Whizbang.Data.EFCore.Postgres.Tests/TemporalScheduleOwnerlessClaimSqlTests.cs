// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2575: a due schedule whose stream has no live owner is claimed. A stream is pinned to an instance by the first
/// write to it, and a schedule's first occurrence is that write, so a schedule on a fresh stream (the framework's own
/// integrity schedules use a control stream nothing else writes to) had no <c>wh_active_streams</c> row, the claim's
/// inner join never saw it, and it never fired. The same held for a stream whose owner had died. The rule now
/// (<c>wh_schedule_owner_admits</c>, migration 598): a schedule is the caller's when its stream is pinned to the
/// caller, or when no live instance holds it (no row, an unassigned row, or an owner the liveness rule calls dead). A
/// stream pinned to another live instance stays that instance's. The row lock and the atomic advance keep the fire
/// exactly-once whoever claims it, and the first fire pins the stream to the claimer.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine#which-instance-fires-a-schedule</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/598_SchedulesWithNoLiveOwnerAreClaimed.sql</code-under-test>
[Category("Shard2")]
public class TemporalScheduleOwnerlessClaimSqlTests : EFCoreTestBase {
  private const int PARTITION_COUNT = 16;
  private const string OCCURRENCE_TYPE = "OwnerlessOccurrence";

  [Test]
  public async Task DueSchedule_OnAStreamNothingHasWritten_FiresOnce_AndPinsItsStreamToTheClaimerAsync() {
    // The deployed shape: two live instances of one service, a cron schedule on a stream with no active-stream row.
    await using var conn = await _openAsync();
    var first = (Guid)TrackedGuid.New();
    var second = (Guid)TrackedGuid.New();
    await _registerLiveAsync(conn, first);
    await _registerLiveAsync(conn, second);
    var stream = (Guid)TrackedGuid.New();
    var schedule = await _insertDueScheduleAsync(conn, stream);

    var firedByFirst = await _claimAsync(conn, first);
    var firedBySecond = await _claimAsync(conn, second);

    await Assert.That(firedByFirst).IsEqualTo(1)
      .Because("no live instance holds the stream, so any instance may fire the schedule; before the fix none could");
    await Assert.That(firedBySecond).IsEqualTo(0)
      .Because("the fire advanced the schedule past now, so the second claim finds nothing due");
    await Assert.That(await _countOutboxAsync(conn)).IsEqualTo(1L);
    await Assert.That(await _countRunsAsync(conn, schedule)).IsEqualTo(1L);
    await Assert.That(await _ownerOfAsync(conn, stream)).IsEqualTo(first)
      .Because("the occurrence is the stream's first write, which pins the stream to the instance that fired it");
  }

  [Test]
  public async Task DueSchedule_WhoseStreamOwnerIsNotLive_IsTakenOverByALiveInstanceAsync() {
    await using var conn = await _openAsync();
    var dead = (Guid)TrackedGuid.New();
    var live = (Guid)TrackedGuid.New();
    await _registerAsync(conn, dead, TimeSpan.FromHours(1));
    await _registerLiveAsync(conn, live);
    var stream = (Guid)TrackedGuid.New();
    await _pinAsync(conn, stream, dead);
    _ = await _insertDueScheduleAsync(conn, stream);

    await Assert.That(await _claimAsync(conn, live)).IsEqualTo(1)
      .Because("an owner the liveness rule calls dead holds nothing, or its schedules would wait for a pod that is gone");
  }

  [Test]
  public async Task DueSchedule_WhoseStreamOwnerHasNoInstanceRow_IsTakenOverAsync() {
    // The reap removes a dead instance's row; the stream row can outlive it until the stream is cleaned.
    await using var conn = await _openAsync();
    var live = (Guid)TrackedGuid.New();
    await _registerLiveAsync(conn, live);
    var stream = (Guid)TrackedGuid.New();
    await _pinAsync(conn, stream, (Guid)TrackedGuid.New());
    _ = await _insertDueScheduleAsync(conn, stream);

    await Assert.That(await _claimAsync(conn, live)).IsEqualTo(1);
  }

  [Test]
  public async Task DueSchedule_OnAnUnassignedStreamRow_IsClaimedAsync() {
    await using var conn = await _openAsync();
    var live = (Guid)TrackedGuid.New();
    await _registerLiveAsync(conn, live);
    var stream = (Guid)TrackedGuid.New();
    await _pinAsync(conn, stream, null);
    _ = await _insertDueScheduleAsync(conn, stream);

    await Assert.That(await _claimAsync(conn, live)).IsEqualTo(1);
  }

  [Test]
  public async Task DueSchedule_WithNoStream_IsClaimedAsync() {
    // A schedule created without a stream is the poll-only shape 066 describes; the inner join never saw it either.
    await using var conn = await _openAsync();
    var live = (Guid)TrackedGuid.New();
    await _registerLiveAsync(conn, live);
    _ = await _insertDueScheduleAsync(conn, null);

    await Assert.That(await _claimAsync(conn, live)).IsEqualTo(1);
  }

  [Test]
  public async Task DueSchedule_WhoseStreamIsPinnedToAnotherLiveInstance_IsLeftToThatInstanceAsync() {
    await using var conn = await _openAsync();
    var owner = (Guid)TrackedGuid.New();
    var other = (Guid)TrackedGuid.New();
    await _registerLiveAsync(conn, owner);
    await _registerLiveAsync(conn, other);
    var stream = (Guid)TrackedGuid.New();
    await _pinAsync(conn, stream, owner);
    _ = await _insertDueScheduleAsync(conn, stream);

    await Assert.That(await _claimAsync(conn, other)).IsEqualTo(0)
      .Because("a live owner keeps its streams: taking its schedule would split one stream across two instances");
    await Assert.That(await _claimAsync(conn, owner)).IsEqualTo(1);
  }

  [Test]
  public async Task DueSchedule_PinnedToTheCaller_IsClaimedEvenWhileTheCallersOwnHeartbeatHasLapsedAsync() {
    // A caller running the claim is alive whatever its row says; its own streams stay its own.
    await using var conn = await _openAsync();
    var caller = (Guid)TrackedGuid.New();
    await _registerAsync(conn, caller, TimeSpan.FromHours(1));
    var stream = (Guid)TrackedGuid.New();
    await _pinAsync(conn, stream, caller);
    _ = await _insertDueScheduleAsync(conn, stream);

    await Assert.That(await _claimAsync(conn, caller)).IsEqualTo(1);
  }

  [Test]
  public async Task OwnerlessSchedule_ClaimedConcurrently_FiresExactlyOnceAsync() {
    // The first claim holds its transaction open with the row locked; the second runs meanwhile and after it commits.
    await using var conn = await _openAsync();
    await using var other = await _openAsync();
    var first = (Guid)TrackedGuid.New();
    var second = (Guid)TrackedGuid.New();
    await _registerLiveAsync(conn, first);
    await _registerLiveAsync(conn, second);
    _ = await _insertDueScheduleAsync(conn, (Guid)TrackedGuid.New());

    await using (var tx = await conn.BeginTransactionAsync()) {
      await Assert.That(await _claimAsync(conn, first, tx)).IsEqualTo(1);
      await Assert.That(await _claimAsync(other, second)).IsEqualTo(0)
        .Because("the first claim holds the row, and SKIP LOCKED passes over it instead of waiting or firing it again");
      await tx.CommitAsync();
    }

    await Assert.That(await _claimAsync(other, second)).IsEqualTo(0)
      .Because("once the first claim commits, the schedule's next fire is in the future");
    await Assert.That(await _countOutboxAsync(conn)).IsEqualTo(1L);
  }

  [Test]
  public async Task OwnerAdmits_FollowsTheRuleForEveryOwnerShapeAsync() {
    await using var conn = await _openAsync();
    var caller = (Guid)TrackedGuid.New();
    var liveOther = (Guid)TrackedGuid.New();
    var deadOther = (Guid)TrackedGuid.New();
    var live = new[] { caller, liveOther };

    await Assert.That(await _admitsAsync(conn, null, caller, live)).IsTrue()
      .Because("a stream no instance holds is anyone's");
    await Assert.That(await _admitsAsync(conn, caller, caller, live)).IsTrue();
    await Assert.That(await _admitsAsync(conn, caller, caller, [])).IsTrue()
      .Because("the caller's own stream is its own even when the live set does not list it");
    await Assert.That(await _admitsAsync(conn, liveOther, caller, live)).IsFalse();
    await Assert.That(await _admitsAsync(conn, deadOther, caller, live)).IsTrue();
    await Assert.That(await _admitsAsync(conn, deadOther, caller, [])).IsTrue();
  }

  // --- helpers ---------------------------------------------------------------------------------

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static Task _registerLiveAsync(NpgsqlConnection conn, Guid instanceId) =>
    _registerAsync(conn, instanceId, TimeSpan.Zero);

  /// <summary>An instance row whose heartbeat is <paramref name="age"/> old by the database clock.</summary>
  private static async Task _registerAsync(NpgsqlConnection conn, Guid instanceId, TimeSpan age) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, connection_mode, liveness_window_seconds)
      VALUES (@id, 'test', 'test-host', 1, NOW() - @age, NOW() - INTERVAL '2 hours', @mode, 30)";
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue(nameof(age), age);
    cmd.Parameters.AddWithValue("mode", InstanceConnectionModes.POOLED);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _pinAsync(NpgsqlConnection conn, Guid streamId, Guid? instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, created_at, last_activity_at)
      VALUES (@s, 0, @i, NOW(), NOW())";
    cmd.Parameters.AddWithValue("s", streamId);
    cmd.Parameters.Add(new NpgsqlParameter("i", NpgsqlDbType.Uuid) { Value = (object?)instanceId ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>An Active cron schedule a minute overdue by the database clock, as the integrity registration makes one.</summary>
  private static async Task<Guid> _insertDueScheduleAsync(NpgsqlConnection conn, Guid? streamId) {
    var id = (Guid)TrackedGuid.New();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_schedules
        (schedule_id, schedule_key, stream_id, partition_number, recurrence_kind, cron, timezone,
         next_fire_at, occurrence_count, status, event_type, event_data)
      VALUES (@id, @key, @stream, 0, 2, '30 4 * * *', 'UTC', NOW() - INTERVAL '1 minute', 0, 0, @etype, '{}'::jsonb)";
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("key", "ownerless-" + id.ToString("N", CultureInfo.InvariantCulture));
    cmd.Parameters.Add(new NpgsqlParameter("stream", NpgsqlDbType.Uuid) { Value = (object?)streamId ?? DBNull.Value });
    cmd.Parameters.AddWithValue("etype", OCCURRENCE_TYPE);
    await cmd.ExecuteNonQueryAsync();
    return id;
  }

  private static async Task<int> _claimAsync(NpgsqlConnection conn, Guid instanceId, NpgsqlTransaction? tx = null) {
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = @"
      SELECT count(*)::int FROM wh_claim_due_schedules(
        p_instance_id => @i, p_lease_expiry => NOW() + INTERVAL '5 minutes', p_partition_count => @pc, p_limit => 100)";
    cmd.Parameters.AddWithValue("i", instanceId);
    cmd.Parameters.AddWithValue("pc", PARTITION_COUNT);
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task<bool> _admitsAsync(NpgsqlConnection conn, Guid? assigned, Guid caller, Guid[] live) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_schedule_owner_admits(@assigned, @caller, @live)";
    cmd.Parameters.Add(new NpgsqlParameter(nameof(assigned), NpgsqlDbType.Uuid) { Value = (object?)assigned ?? DBNull.Value });
    cmd.Parameters.AddWithValue(nameof(caller), caller);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(live), NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = live });
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<long> _countOutboxAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_outbox WHERE message_type = @t";
    cmd.Parameters.AddWithValue("t", OCCURRENCE_TYPE);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task<long> _countRunsAsync(NpgsqlConnection conn, Guid scheduleId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_schedule_runs WHERE schedule_id = @p AND status = 0";
    cmd.Parameters.AddWithValue("p", scheduleId);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task<Guid?> _ownerOfAsync(NpgsqlConnection conn, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT assigned_instance_id FROM wh_active_streams WHERE stream_id = @s";
    cmd.Parameters.AddWithValue("s", streamId);
    return await cmd.ExecuteScalarAsync() is Guid g ? g : null;
  }
}
