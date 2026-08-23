using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for the DB half of the dual recurrence engine (migration 067):
/// <c>wh_cron_next</c> and the <c>wh_schedule_next_fire</c> dispatcher. Each case mirrors a
/// <c>CronExpressionTests</c> / <c>IntervalRecurrenceRuleTests</c> assertion so the SQL and C#
/// next-fire computations stay in lockstep.
/// </summary>
/// <docs>fundamentals/temporal/recurrence</docs>
[Category("Shard4")]
public class WhScheduleNextFireSqlTests : EFCoreTestBase {
  private static DateTimeOffset _utc(int y, int mo, int d, int h, int mi) =>
    new(y, mo, d, h, mi, 0, TimeSpan.Zero);

  private async Task<DateTimeOffset?> _cronNextAsync(string cron, DateTimeOffset after, string tz) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_cron_next(@cron, @after, @tz)";
    cmd.Parameters.AddWithValue("cron", cron);
    cmd.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.TimestampTz) { Value = after });
    cmd.Parameters.AddWithValue("tz", tz);
    return _readInstant(await cmd.ExecuteScalarAsync());
  }

  private async Task<DateTimeOffset?> _scheduleNextAsync(
      short kind, string? cron, long? intervalMs, string? tz, DateTimeOffset after) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_schedule_next_fire(@kind, @cron, @interval, @tz, @after)";
    cmd.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Smallint) { Value = kind });
    cmd.Parameters.AddWithValue("cron", (object?)cron ?? DBNull.Value);
    cmd.Parameters.Add(new NpgsqlParameter("interval", NpgsqlDbType.Bigint) { Value = (object?)intervalMs ?? DBNull.Value });
    cmd.Parameters.AddWithValue("tz", (object?)tz ?? DBNull.Value);
    cmd.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.TimestampTz) { Value = after });
    return _readInstant(await cmd.ExecuteScalarAsync());
  }

  private static DateTimeOffset? _readInstant(object? scalar) {
    if (scalar is null || scalar is DBNull) {
      return null;
    }
    var dt = (DateTime)scalar;
    return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero);
  }

  // ---- wh_cron_next: mirrors CronExpressionTests ----

  [Test]
  public async Task CronNext_EveryMinuteAsync() =>
    await Assert.That(await _cronNextAsync("* * * * *", _utc(2026, 07, 13, 09, 00), "UTC"))
      .IsEqualTo(_utc(2026, 07, 13, 09, 01));

  [Test]
  public async Task CronNext_DailyBeforeAsync() =>
    await Assert.That(await _cronNextAsync("0 9 * * *", _utc(2026, 07, 13, 08, 30), "UTC"))
      .IsEqualTo(_utc(2026, 07, 13, 09, 00));

  [Test]
  public async Task CronNext_DailyRollsToNextDayAsync() =>
    await Assert.That(await _cronNextAsync("0 9 * * *", _utc(2026, 07, 13, 09, 00), "UTC"))
      .IsEqualTo(_utc(2026, 07, 14, 09, 00));

  [Test]
  public async Task CronNext_StepEveryFifteenAsync() =>
    await Assert.That(await _cronNextAsync("*/15 * * * *", _utc(2026, 07, 13, 09, 07), "UTC"))
      .IsEqualTo(_utc(2026, 07, 13, 09, 15));

  [Test]
  public async Task CronNext_WeekdaysSkipWeekendAsync() =>
    await Assert.That(await _cronNextAsync("0 9 * * MON-FRI", _utc(2026, 07, 17, 10, 00), "UTC"))
      .IsEqualTo(_utc(2026, 07, 20, 09, 00));

  [Test]
  public async Task CronNext_SundayAsSevenEqualsZeroAsync() {
    var from = _utc(2026, 07, 13, 12, 00);   // Monday
    await Assert.That(await _cronNextAsync("0 0 * * 7", from, "UTC")).IsEqualTo(_utc(2026, 07, 19, 00, 00));
    await Assert.That(await _cronNextAsync("0 0 * * 0", from, "UTC")).IsEqualTo(_utc(2026, 07, 19, 00, 00));
  }

  [Test]
  public async Task CronNext_NamedMonthAndDomAsync() =>
    await Assert.That(await _cronNextAsync("0 0 1 JAN *", _utc(2026, 07, 13, 12, 00), "UTC"))
      .IsEqualTo(_utc(2027, 01, 01, 00, 00));

  [Test]
  public async Task CronNext_VixieOrSemanticsAsync() {
    // "0 0 1 * MON": 1st OR any Monday.
    await Assert.That(await _cronNextAsync("0 0 1 * MON", _utc(2026, 07, 15, 12, 00), "UTC"))
      .IsEqualTo(_utc(2026, 07, 20, 00, 00));   // next Monday earlier than the 1st
    await Assert.That(await _cronNextAsync("0 0 1 * MON", _utc(2026, 07, 30, 12, 00), "UTC"))
      .IsEqualTo(_utc(2026, 08, 01, 00, 00));   // the 1st earlier than next Monday
  }

  [Test]
  public async Task CronNext_TimeZoneNineAmLocalAsync() =>
    // 09:00 America/New_York (EDT, UTC-4) in July => 13:00 UTC.
    await Assert.That(await _cronNextAsync("0 9 * * *", _utc(2026, 07, 13, 08, 00), "America/New_York"))
      .IsEqualTo(_utc(2026, 07, 13, 13, 00));

  [Test]
  public async Task CronNext_SpringForwardSkipsMissingHourAsync() =>
    // 02:30 does not exist on 2026-03-08 in America/New_York => next valid fire is 03-09 02:30 EDT = 06:30 UTC.
    await Assert.That(await _cronNextAsync("30 2 * * *", _utc(2026, 03, 08, 00, 00), "America/New_York"))
      .IsEqualTo(_utc(2026, 03, 09, 06, 30));

  // ---- wh_schedule_next_fire dispatcher ----

  [Test]
  public async Task ScheduleNext_IntervalAdvancesAsync() =>
    await Assert.That(await _scheduleNextAsync(kind: 1, cron: null, intervalMs: 900_000, tz: null, _utc(2026, 07, 13, 09, 00)))
      .IsEqualTo(_utc(2026, 07, 13, 09, 15));

  [Test]
  public async Task ScheduleNext_OneShotIsNullAsync() =>
    await Assert.That(await _scheduleNextAsync(kind: 0, cron: null, intervalMs: null, tz: null, _utc(2026, 07, 13, 09, 00)))
      .IsNull();

  [Test]
  public async Task ScheduleNext_CronDelegatesToCronNextAsync() =>
    await Assert.That(await _scheduleNextAsync(kind: 2, cron: "0 9 * * *", intervalMs: null, tz: "UTC", _utc(2026, 07, 13, 08, 00)))
      .IsEqualTo(_utc(2026, 07, 13, 09, 00));

  // ---- malformed => raises ----

  [Test]
  public async Task CronNext_RejectsMalformedAsync() {
    await Assert.That(async () => await _cronNextAsync("* * * *", _utc(2026, 07, 13, 09, 00), "UTC"))
      .Throws<PostgresException>();
    await Assert.That(async () => await _cronNextAsync("60 * * * *", _utc(2026, 07, 13, 09, 00), "UTC"))
      .Throws<PostgresException>();
    await Assert.That(async () => await _cronNextAsync("* * * FOO *", _utc(2026, 07, 13, 09, 00), "UTC"))
      .Throws<PostgresException>();
  }
}
