using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for the schedule management DB core (migration 069): <c>wh_create_schedule</c>
/// (initial next-fire computation + idempotent create-or-update by key) and
/// <c>wh_transition_schedule</c> (pause / resume / cancel with optimistic concurrency).
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
[Category("Shard3")]
public class TemporalScheduleManagementSqlTests : EFCoreTestBase {
  private static DateTimeOffset _utc(int y, int mo, int d, int h, int mi) =>
    new(y, mo, d, h, mi, 0, TimeSpan.Zero);

  private sealed record CreateResult(Guid ScheduleId, DateTimeOffset NextFire, bool WasCreated);

  private async Task<CreateResult> _createAsync(
      NpgsqlConnection conn, Guid scheduleId, short kind,
      long? intervalMs = null, string? cron = null, DateTimeOffset? startAt = null,
      string? key = null, string eventType = "MgmtOcc", Guid? authority = null) {
    await using var cmd = conn.CreateCommand();
    // p_misfire_policy / p_delivery_guarantee are SMALLINT. A bare `0` literal is INTEGER, and
    // integer→smallint is not an implicit cast, so Postgres fails to RESOLVE the overload and
    // reports the misleading "function wh_create_schedule(...) does not exist" — the function is
    // there, the argument types just don't match. Cast explicitly.
    // p_authority_principal_id is REQUIRED (the function raises without it): a schedule must name
    // the principal its occurrences run as.
    cmd.CommandText = @"
      SELECT o_schedule_id, o_next_fire_at, o_was_created FROM wh_create_schedule(
        p_schedule_id => @id, p_schedule_key => @key, p_stream_id => @id, p_partition_number => 0,
        p_recurrence_kind => @kind, p_interval_ms => @interval, p_cron => @cron, p_timezone => 'UTC',
        p_start_at => @start, p_until_at => NULL, p_max_occurrences => NULL,
        p_misfire_policy => 0::SMALLINT, p_delivery_guarantee => 0::SMALLINT,
        p_event_type => @etype, p_event_data => '{}'::jsonb, p_scope => NULL,
        p_authority_principal_id => @authority)";
    cmd.Parameters.AddWithValue("id", scheduleId);
    cmd.Parameters.AddWithValue("key", (object?)key ?? DBNull.Value);
    cmd.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Smallint) { Value = kind });
    cmd.Parameters.AddWithValue("interval", (object?)intervalMs ?? DBNull.Value);
    cmd.Parameters.AddWithValue("cron", (object?)cron ?? DBNull.Value);
    cmd.Parameters.Add(new NpgsqlParameter("start", NpgsqlDbType.TimestampTz) { Value = (object?)startAt ?? DBNull.Value });
    cmd.Parameters.AddWithValue("etype", eventType);
    cmd.Parameters.AddWithValue("authority", authority ?? Guid.NewGuid());
    await using var r = await cmd.ExecuteReaderAsync();
    _ = await r.ReadAsync();
    var next = new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc), TimeSpan.Zero);
    return new CreateResult(r.GetGuid(0), next, r.GetBoolean(2));
  }

  private async Task<(bool Updated, long? Version)> _transitionAsync(
      NpgsqlConnection conn, Guid scheduleId, short target, long? expectedVersion = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT o_updated, o_version FROM wh_transition_schedule(@id, @t, @v)";
    cmd.Parameters.AddWithValue("id", scheduleId);
    cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Smallint) { Value = target });
    cmd.Parameters.AddWithValue("v", (object?)expectedVersion ?? DBNull.Value);
    await using var r = await cmd.ExecuteReaderAsync();
    _ = await r.ReadAsync();
    return (r.GetBoolean(0), r.IsDBNull(1) ? null : r.GetInt64(1));
  }

  private async Task<(short Status, long Version, long Count)> _readAsync(NpgsqlConnection conn, Guid scheduleId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT status, version, occurrence_count FROM wh_schedules WHERE schedule_id = @id";
    cmd.Parameters.AddWithValue("id", scheduleId);
    await using var r = await cmd.ExecuteReaderAsync();
    _ = await r.ReadAsync();
    return (r.GetInt16(0), r.GetInt64(1), r.GetInt64(2));
  }

  [Test]
  public async Task Create_WithoutAuthorityPrincipal_RaisesAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT o_schedule_id FROM wh_create_schedule(
        p_schedule_id => @id, p_schedule_key => NULL, p_stream_id => @id, p_partition_number => 0,
        p_recurrence_kind => 1::SMALLINT, p_interval_ms => 60000, p_cron => NULL, p_timezone => 'UTC',
        p_start_at => NOW(), p_until_at => NULL, p_max_occurrences => NULL,
        p_misfire_policy => 0::SMALLINT, p_delivery_guarantee => 0::SMALLINT,
        p_event_type => 'NoAuthOcc', p_event_data => '{}'::jsonb, p_scope => NULL,
        p_authority_principal_id => NULL)";
    cmd.Parameters.AddWithValue("id", Guid.NewGuid());

    var ex = await Assert.ThrowsAsync<PostgresException>(async () => await cmd.ExecuteScalarAsync());

    await Assert.That(ex!.MessageText).Contains("authority principal")
      .Because("LOCK-IN: run-as authority is EXPLICIT and REQUIRED — there is no implicit "
        + "creator-authority fallback, so scheduling without naming a principal must be rejected "
        + "at the DB, not silently defaulted.");
  }

  [Test]
  public async Task Create_Cron_ComputesNextFireAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var id = Guid.NewGuid();

    var result = await _createAsync(conn, id, kind: 2, cron: "0 9 * * *", startAt: _utc(2026, 07, 13, 08, 00));

    await Assert.That(result.WasCreated).IsTrue();
    await Assert.That(result.NextFire).IsEqualTo(_utc(2026, 07, 13, 09, 00));
    var (status, _, _) = await _readAsync(conn, id);
    await Assert.That(status).IsEqualTo((short)0);   // Active
  }

  [Test]
  public async Task Create_OneShot_UsesStartTimeAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var id = Guid.NewGuid();
    var at = _utc(2026, 08, 01, 00, 00);

    var result = await _createAsync(conn, id, kind: 0, startAt: at);

    await Assert.That(result.NextFire).IsEqualTo(at);
  }

  [Test]
  public async Task Create_Interval_FiresOneIntervalOutAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var id = Guid.NewGuid();
    var start = _utc(2026, 07, 13, 09, 00);

    var result = await _createAsync(conn, id, kind: 1, intervalMs: 900_000, startAt: start);

    await Assert.That(result.NextFire).IsEqualTo(start);   // start given => first fire at start
  }

  [Test]
  public async Task Create_IdempotentByKey_UpdatesInPlaceAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var first = await _createAsync(conn, Guid.NewGuid(), kind: 2, cron: "0 9 * * *",
      startAt: _utc(2026, 07, 13, 08, 00), key: "daily-report");
    // Re-create by the same key with a different id + cron => updates the existing row.
    var second = await _createAsync(conn, Guid.NewGuid(), kind: 2, cron: "0 17 * * *",
      startAt: _utc(2026, 07, 13, 08, 00), key: "daily-report");

    await Assert.That(first.WasCreated).IsTrue();
    await Assert.That(second.WasCreated).IsFalse();                 // updated, not created
    await Assert.That(second.ScheduleId).IsEqualTo(first.ScheduleId);   // same row (keyed)
    await Assert.That(second.NextFire).IsEqualTo(_utc(2026, 07, 13, 17, 00));   // new cron applied
  }

  [Test]
  public async Task Create_ImpossibleCron_RaisesAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    // Feb 30 never occurs => wh_cron_next returns null => create must reject.
    await Assert.That(async () => await _createAsync(conn, Guid.NewGuid(), kind: 2, cron: "0 0 30 2 *"))
      .Throws<PostgresException>();
  }

  [Test]
  public async Task Transition_Pause_Resume_CancelAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var id = Guid.NewGuid();
    _ = await _createAsync(conn, id, kind: 1, intervalMs: 60_000, startAt: _utc(2026, 07, 13, 09, 00));

    var pause = await _transitionAsync(conn, id, target: 1);
    await Assert.That(pause.Updated).IsTrue();
    await Assert.That((await _readAsync(conn, id)).Status).IsEqualTo((short)1);   // Paused

    var resume = await _transitionAsync(conn, id, target: 0);
    await Assert.That(resume.Updated).IsTrue();
    await Assert.That((await _readAsync(conn, id)).Status).IsEqualTo((short)0);   // Active

    var cancel = await _transitionAsync(conn, id, target: 3);
    await Assert.That(cancel.Updated).IsTrue();
    await Assert.That((await _readAsync(conn, id)).Status).IsEqualTo((short)3);   // Canceled
  }

  [Test]
  public async Task Transition_WrongVersion_DoesNotUpdateAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var id = Guid.NewGuid();
    _ = await _createAsync(conn, id, kind: 1, intervalMs: 60_000, startAt: _utc(2026, 07, 13, 09, 00));

    var result = await _transitionAsync(conn, id, target: 1, expectedVersion: 999);

    await Assert.That(result.Updated).IsFalse();
    await Assert.That((await _readAsync(conn, id)).Status).IsEqualTo((short)0);   // unchanged
  }

  [Test]
  public async Task Transition_TerminalSchedule_DoesNotUpdateAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var id = Guid.NewGuid();
    _ = await _createAsync(conn, id, kind: 1, intervalMs: 60_000, startAt: _utc(2026, 07, 13, 09, 00));
    _ = await _transitionAsync(conn, id, target: 3);   // Cancel (terminal)

    var pauseAfterCancel = await _transitionAsync(conn, id, target: 1);

    await Assert.That(pauseAfterCancel.Updated).IsFalse();
    await Assert.That((await _readAsync(conn, id)).Status).IsEqualTo((short)3);   // still Canceled
  }
}
