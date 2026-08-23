using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the temporal engine's UTC-safety invariant: because every schedule timestamp is
/// <c>TIMESTAMPTZ</c> (an absolute instant) and the functions pin <c>SET timezone = 'UTC'</c>,
/// next-fire and due-detection produce identical results regardless of the caller's session timezone
/// — so migrating data or running on a server in another zone can never shift a fire.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
[Category("Shard3")]
public class TemporalUtcSafetySqlTests : EFCoreTestBase {
  private static DateTimeOffset _utc(int y, int mo, int d, int h, int mi) =>
    new(y, mo, d, h, mi, 0, TimeSpan.Zero);

  private async Task<DateTimeOffset?> _cronNextUnderSessionTzAsync(string sessionTz, string cron, DateTimeOffset after, string cronTz) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using (var set = conn.CreateCommand()) {
      set.CommandText = $"SET TimeZone = '{sessionTz}'";
      await set.ExecuteNonQueryAsync();
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_cron_next(@cron, @after, @tz)";
    cmd.Parameters.AddWithValue("cron", cron);
    cmd.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.TimestampTz) { Value = after });
    cmd.Parameters.AddWithValue("tz", cronTz);
    var scalar = await cmd.ExecuteScalarAsync();
    if (scalar is null or DBNull) {
      return null;
    }
    return new DateTimeOffset(DateTime.SpecifyKind((DateTime)scalar, DateTimeKind.Utc), TimeSpan.Zero);
  }

  [Test]
  public async Task CronNext_SameInstant_RegardlessOfSessionTimeZoneAsync() {
    // 09:00 UTC daily, queried after 08:00 UTC → 09:00 UTC — must hold whatever the session zone is.
    var underUtc = await _cronNextUnderSessionTzAsync("UTC", "0 9 * * *", _utc(2026, 07, 13, 08, 00), "UTC");
    var underNy = await _cronNextUnderSessionTzAsync("America/New_York", "0 9 * * *", _utc(2026, 07, 13, 08, 00), "UTC");
    var underTokyo = await _cronNextUnderSessionTzAsync("Asia/Tokyo", "0 9 * * *", _utc(2026, 07, 13, 08, 00), "UTC");

    await Assert.That(underUtc).IsEqualTo(_utc(2026, 07, 13, 09, 00));
    await Assert.That(underNy).IsEqualTo(underUtc);
    await Assert.That(underTokyo).IsEqualTo(underUtc);
  }

  [Test]
  public async Task ClaimDueSchedules_FiresCorrectly_UnderNonUtcSessionAsync() {
    var instance = Guid.NewGuid();
    var stream = Guid.NewGuid();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using (var set = conn.CreateCommand()) {
      set.CommandText = "SET TimeZone = 'Asia/Tokyo'";   // deliberately far from UTC
      await set.ExecuteNonQueryAsync();
    }
    await using (var pin = conn.CreateCommand()) {
      pin.CommandText = @"
        INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, created_at, last_activity_at)
        VALUES (@s, 0, @i, NOW(), NOW());";
      pin.Parameters.AddWithValue("s", stream);
      pin.Parameters.AddWithValue("i", instance);
      await pin.ExecuteNonQueryAsync();
    }
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_schedules
          (schedule_id, stream_id, partition_number, recurrence_kind, interval_ms, timezone,
           next_fire_at, occurrence_count, status, event_type, event_data)
        VALUES (gen_random_uuid(), @s, 0, 1, 60000, 'UTC', NOW() - INTERVAL '1 minute', 0, 0, 'TzOcc', '{}'::jsonb);";
      ins.Parameters.AddWithValue("s", stream);
      await ins.ExecuteNonQueryAsync();
    }

    long fired;
    await using (var claim = conn.CreateCommand()) {
      claim.CommandText = @"
        SELECT count(*) FROM wh_claim_due_schedules(
          p_instance_id => @i, p_lease_expiry => NOW() + INTERVAL '5 minutes', p_partition_count => 16, p_limit => 100)";
      claim.Parameters.AddWithValue("i", instance);
      fired = Convert.ToInt64(await claim.ExecuteScalarAsync() ?? 0L, CultureInfo.InvariantCulture);
    }

    await Assert.That(fired).IsEqualTo(1L);
  }
}
