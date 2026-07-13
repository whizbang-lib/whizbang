using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for <c>wh_claim_due_schedules</c> (migration 068) — the temporal engine's
/// authoritative fire: lease due owned schedules (<c>FOR UPDATE SKIP LOCKED</c>), spawn the occurrence
/// into the outbox via <c>store_outbox_messages</c>, advance <c>next_fire_at</c> via
/// <c>wh_schedule_next_fire</c> (honoring until/max bounds), and log the run — all in one transaction so
/// occurrence creation is exactly-once even under concurrent claimers.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public class TemporalScheduleClaimSqlTests : EFCoreTestBase {
  private const int PARTITION_COUNT = 16;

  private async Task _pinStreamAsync(NpgsqlConnection conn, Guid streamId, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, created_at, last_activity_at)
      VALUES (@s, 0, @i, NOW(), NOW())
      ON CONFLICT (stream_id) DO UPDATE SET assigned_instance_id = EXCLUDED.assigned_instance_id;";
    cmd.Parameters.AddWithValue("s", streamId);
    cmd.Parameters.AddWithValue("i", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task _insertScheduleAsync(
      NpgsqlConnection conn, Guid scheduleId, Guid streamId, DateTimeOffset nextFireAt,
      short kind = 1, long? intervalMs = 60_000, string? cron = null, string eventType = "TestOccurrence",
      short status = 0, DateTimeOffset? untilAt = null, long? maxOccurrences = null, long occurrenceCount = 0) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_schedules
        (schedule_id, stream_id, partition_number, recurrence_kind, interval_ms, cron, timezone,
         next_fire_at, until_at, max_occurrences, occurrence_count, status, event_type, event_data)
      VALUES (@id, @stream, 0, @kind, @interval, @cron, 'UTC',
         @next, @until, @maxocc, @occ, @status, @etype, '{}'::jsonb);";
    cmd.Parameters.AddWithValue("id", scheduleId);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Smallint) { Value = kind });
    cmd.Parameters.AddWithValue("interval", (object?)intervalMs ?? DBNull.Value);
    cmd.Parameters.AddWithValue("cron", (object?)cron ?? DBNull.Value);
    cmd.Parameters.Add(new NpgsqlParameter("next", NpgsqlDbType.TimestampTz) { Value = nextFireAt });
    cmd.Parameters.Add(new NpgsqlParameter("until", NpgsqlDbType.TimestampTz) { Value = (object?)untilAt ?? DBNull.Value });
    cmd.Parameters.AddWithValue("maxocc", (object?)maxOccurrences ?? DBNull.Value);
    cmd.Parameters.AddWithValue("occ", occurrenceCount);
    cmd.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Smallint) { Value = status });
    cmd.Parameters.AddWithValue("etype", eventType);
    await cmd.ExecuteNonQueryAsync();
  }

  // Calls the claim function in autocommit; returns the number of claimed rows.
  private async Task<int> _claimAsync(NpgsqlConnection conn, Guid instanceId, DateTimeOffset now) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT count(*) FROM wh_claim_due_schedules(@i, @now, @lease, @pc, 100)";
    cmd.Parameters.AddWithValue("i", instanceId);
    cmd.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
    cmd.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.TimestampTz) { Value = now.AddMinutes(5) });
    cmd.Parameters.Add(new NpgsqlParameter("pc", NpgsqlDbType.Integer) { Value = PARTITION_COUNT });
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private async Task<long> _scalarAsync(NpgsqlConnection conn, string sql, Guid p) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("p", p);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L, CultureInfo.InvariantCulture);
  }

  private async Task<(DateTimeOffset? NextFire, short Status, long Count)> _getScheduleAsync(
      NpgsqlConnection conn, Guid scheduleId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT next_fire_at, status, occurrence_count FROM wh_schedules WHERE schedule_id = @p";
    cmd.Parameters.AddWithValue("p", scheduleId);
    await using var r = await cmd.ExecuteReaderAsync();
    _ = await r.ReadAsync();
    var next = r.IsDBNull(0) ? (DateTimeOffset?)null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(0), DateTimeKind.Utc), TimeSpan.Zero);
    return (next, r.GetInt16(1), r.GetInt64(2));
  }

  [Test]
  public async Task DueOwned_SpawnsOccurrence_AdvancesNextFire_LogsRunAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var instance = Guid.NewGuid();
    var stream = Guid.NewGuid();
    var schedule = Guid.NewGuid();
    var next = DateTimeOffset.UtcNow.AddMinutes(-1);
    await _pinStreamAsync(conn, stream, instance);
    await _insertScheduleAsync(conn, schedule, stream, next, kind: 1, intervalMs: 60_000, eventType: "OccA");

    var claimed = await _claimAsync(conn, instance, DateTimeOffset.UtcNow);

    await Assert.That(claimed).IsEqualTo(1);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_outbox WHERE message_type = 'OccA'", Guid.Empty))
      .IsEqualTo(1L);   // exactly one occurrence spawned
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_schedule_runs WHERE schedule_id = @p", schedule))
      .IsEqualTo(1L);
    var (nextFire, status, count) = await _getScheduleAsync(conn, schedule);
    await Assert.That(status).IsEqualTo((short)0);           // still Active
    await Assert.That(count).IsEqualTo(1L);                  // occurrence_count advanced
    await Assert.That(nextFire).IsEqualTo(next.AddMinutes(1));   // next = old + interval
  }

  [Test]
  public async Task NotOwned_NotClaimedAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var stream = Guid.NewGuid();
    await _pinStreamAsync(conn, stream, Guid.NewGuid());   // owned by someone else
    await _insertScheduleAsync(conn, Guid.NewGuid(), stream, DateTimeOffset.UtcNow.AddMinutes(-1), eventType: "OccOther");

    var claimed = await _claimAsync(conn, Guid.NewGuid(), DateTimeOffset.UtcNow);

    await Assert.That(claimed).IsEqualTo(0);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_outbox WHERE message_type = 'OccOther'", Guid.Empty))
      .IsEqualTo(0L);
  }

  [Test]
  public async Task PausedAndFuture_NotClaimedAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var instance = Guid.NewGuid();
    var s1 = Guid.NewGuid();
    var s2 = Guid.NewGuid();
    await _pinStreamAsync(conn, s1, instance);
    await _pinStreamAsync(conn, s2, instance);
    await _insertScheduleAsync(conn, Guid.NewGuid(), s1, DateTimeOffset.UtcNow.AddMinutes(-1), status: 1, eventType: "OccPaused");
    await _insertScheduleAsync(conn, Guid.NewGuid(), s2, DateTimeOffset.UtcNow.AddHours(1), eventType: "OccFuture");

    var claimed = await _claimAsync(conn, instance, DateTimeOffset.UtcNow);

    await Assert.That(claimed).IsEqualTo(0);
  }

  [Test]
  public async Task OneShot_CompletesAndDoesNotRefireAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var instance = Guid.NewGuid();
    var stream = Guid.NewGuid();
    var schedule = Guid.NewGuid();
    await _pinStreamAsync(conn, stream, instance);
    await _insertScheduleAsync(conn, schedule, stream, DateTimeOffset.UtcNow.AddMinutes(-1),
      kind: 0, intervalMs: null, eventType: "OccOnce");

    var first = await _claimAsync(conn, instance, DateTimeOffset.UtcNow);
    var second = await _claimAsync(conn, instance, DateTimeOffset.UtcNow);

    await Assert.That(first).IsEqualTo(1);
    await Assert.That(second).IsEqualTo(0);   // completed → not re-detected
    var (_, status, _) = await _getScheduleAsync(conn, schedule);
    await Assert.That(status).IsEqualTo((short)2);   // Completed
  }

  [Test]
  public async Task MaxOccurrences_CompletesAtCapAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var instance = Guid.NewGuid();
    var stream = Guid.NewGuid();
    var schedule = Guid.NewGuid();
    await _pinStreamAsync(conn, stream, instance);
    // max 1 occurrence: the single fire completes it.
    await _insertScheduleAsync(conn, schedule, stream, DateTimeOffset.UtcNow.AddMinutes(-1),
      kind: 1, intervalMs: 60_000, maxOccurrences: 1, eventType: "OccCap");

    _ = await _claimAsync(conn, instance, DateTimeOffset.UtcNow);

    var (_, status, count) = await _getScheduleAsync(conn, schedule);
    await Assert.That(count).IsEqualTo(1L);
    await Assert.That(status).IsEqualTo((short)2);   // Completed at cap
  }

  [Test]
  public async Task UntilAt_CompletesWhenNextWouldExceedAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var instance = Guid.NewGuid();
    var stream = Guid.NewGuid();
    var schedule = Guid.NewGuid();
    var next = DateTimeOffset.UtcNow.AddMinutes(-1);
    await _pinStreamAsync(conn, stream, instance);
    // until_at is before the next interval (next + 1min) => completes after this fire.
    await _insertScheduleAsync(conn, schedule, stream, next,
      kind: 1, intervalMs: 60_000, untilAt: next.AddSeconds(30), eventType: "OccUntil");

    _ = await _claimAsync(conn, instance, DateTimeOffset.UtcNow);

    var (_, status, _) = await _getScheduleAsync(conn, schedule);
    await Assert.That(status).IsEqualTo((short)2);   // Completed (next would exceed until_at)
  }

  [Test]
  public async Task ConcurrentClaimers_SpawnOccurrenceExactlyOnceAsync() {
    var instance = Guid.NewGuid();
    var stream = Guid.NewGuid();
    var schedule = Guid.NewGuid();
    await using (var setup = new NpgsqlConnection(ConnectionString)) {
      await setup.OpenAsync();
      await _pinStreamAsync(setup, stream, instance);
      await _insertScheduleAsync(setup, schedule, stream, DateTimeOffset.UtcNow.AddMinutes(-1),
        kind: 1, intervalMs: 60_000, eventType: "OccRace");
    }

    // Two overlapping transactions both try to claim; SKIP LOCKED must let only one succeed.
    await using var connA = new NpgsqlConnection(ConnectionString);
    await using var connB = new NpgsqlConnection(ConnectionString);
    await connA.OpenAsync();
    await connB.OpenAsync();
    var txA = await connA.BeginTransactionAsync();
    var claimedA = await _claimAsync(connA, instance, DateTimeOffset.UtcNow);   // locks the row
    var txB = await connB.BeginTransactionAsync();
    var claimedB = await _claimAsync(connB, instance, DateTimeOffset.UtcNow);   // SKIP LOCKED → skips it
    await txA.CommitAsync();
    await txB.CommitAsync();

    await Assert.That(claimedA + claimedB).IsEqualTo(1);
    await using var verify = new NpgsqlConnection(ConnectionString);
    await verify.OpenAsync();
    await Assert.That(await _scalarAsync(verify, "SELECT count(*) FROM wh_outbox WHERE message_type = 'OccRace'", Guid.Empty))
      .IsEqualTo(1L);
    await Assert.That(await _scalarAsync(verify, "SELECT occurrence_count FROM wh_schedules WHERE schedule_id = @p", schedule))
      .IsEqualTo(1L);
  }
}
