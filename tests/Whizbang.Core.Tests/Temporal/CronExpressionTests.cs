using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Temporal;

namespace Whizbang.Core.Tests.Temporal;

/// <summary>
/// Unit tests for the home-grown <see cref="CronExpression"/> parser + next-fire calculator: field
/// forms (<c>*</c>, values, ranges, steps, lists, names), the Vixie DOM/DOW OR rule, UTC and
/// timezone/DST-aware next-fire, and malformed-input rejection.
/// </summary>
/// <docs>fundamentals/temporal/recurrence</docs>
public class CronExpressionTests {
  private static readonly TimeZoneInfo _utcZone = TimeZoneInfo.Utc;
  // IANA id resolves on all target platforms (.NET normalizes Windows ids too).
  private static readonly TimeZoneInfo _nyZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

  private static DateTimeOffset _utc(int y, int mo, int d, int h, int mi) =>
    new(y, mo, d, h, mi, 0, TimeSpan.Zero);

  // ---- every-minute ----
  [Test]
  public async Task EveryMinute_AdvancesOneMinuteAsync() {
    var cron = CronExpression.Parse("* * * * *");
    var next = cron.NextFireAfter(_utc(2026, 07, 13, 09, 00), _utcZone);
    await Assert.That(next).IsEqualTo(_utc(2026, 07, 13, 09, 01));
  }

  // ---- daily at a fixed time ----
  [Test]
  public async Task DailyAtNine_NextIsSameDayWhenBeforeAsync() {
    var cron = CronExpression.Parse("0 9 * * *");
    var next = cron.NextFireAfter(_utc(2026, 07, 13, 08, 30), _utcZone);
    await Assert.That(next).IsEqualTo(_utc(2026, 07, 13, 09, 00));
  }

  [Test]
  public async Task DailyAtNine_RollsToNextDayWhenAfterAsync() {
    var cron = CronExpression.Parse("0 9 * * *");
    var next = cron.NextFireAfter(_utc(2026, 07, 13, 09, 00), _utcZone);   // strictly-after excludes 09:00 today
    await Assert.That(next).IsEqualTo(_utc(2026, 07, 14, 09, 00));
  }

  // ---- step ----
  [Test]
  public async Task EveryFifteenMinutes_StepAsync() {
    var cron = CronExpression.Parse("*/15 * * * *");
    await Assert.That(cron.NextFireAfter(_utc(2026, 07, 13, 09, 07), _utcZone)).IsEqualTo(_utc(2026, 07, 13, 09, 15));
    await Assert.That(cron.NextFireAfter(_utc(2026, 07, 13, 09, 45), _utcZone)).IsEqualTo(_utc(2026, 07, 13, 10, 00));
  }

  // ---- list + range ----
  [Test]
  public async Task ListOfHours_PicksNextAsync() {
    var cron = CronExpression.Parse("0 9,12,17 * * *");
    await Assert.That(cron.NextFireAfter(_utc(2026, 07, 13, 10, 00), _utcZone)).IsEqualTo(_utc(2026, 07, 13, 12, 00));
    await Assert.That(cron.NextFireAfter(_utc(2026, 07, 13, 17, 30), _utcZone)).IsEqualTo(_utc(2026, 07, 14, 09, 00));
  }

  [Test]
  public async Task RangeOfMinutes_MatchesWithinAsync() {
    var cron = CronExpression.Parse("30-35 9 * * *");
    await Assert.That(cron.NextFireAfter(_utc(2026, 07, 13, 09, 31), _utcZone)).IsEqualTo(_utc(2026, 07, 13, 09, 32));
    await Assert.That(cron.NextFireAfter(_utc(2026, 07, 13, 09, 35), _utcZone)).IsEqualTo(_utc(2026, 07, 14, 09, 30));
  }

  // ---- day-of-week (named + numeric) ----
  [Test]
  public async Task WeekdaysOnly_SkipsWeekendAsync() {
    var cron = CronExpression.Parse("0 9 * * MON-FRI");
    // 2026-07-17 is a Friday; next weekday 09:00 after Fri 10:00 is Mon 2026-07-20.
    var next = cron.NextFireAfter(_utc(2026, 07, 17, 10, 00), _utcZone);
    await Assert.That(next).IsEqualTo(_utc(2026, 07, 20, 09, 00));
  }

  [Test]
  public async Task SundayAsSeven_EqualsZeroAsync() {
    var zero = CronExpression.Parse("0 0 * * 0");
    var seven = CronExpression.Parse("0 0 * * 7");
    var from = _utc(2026, 07, 13, 12, 00);   // Monday
    await Assert.That(seven.NextFireAfter(from, _utcZone)).IsEqualTo(zero.NextFireAfter(from, _utcZone));
    // next Sunday 00:00 after Mon 2026-07-13 is 2026-07-19.
    await Assert.That(zero.NextFireAfter(from, _utcZone)).IsEqualTo(_utc(2026, 07, 19, 00, 00));
  }

  // ---- month (named) + day-of-month ----
  [Test]
  public async Task NamedMonthAndDom_MatchesAsync() {
    var cron = CronExpression.Parse("0 0 1 JAN *");
    var next = cron.NextFireAfter(_utc(2026, 07, 13, 12, 00), _utcZone);
    await Assert.That(next).IsEqualTo(_utc(2027, 01, 01, 00, 00));
  }

  // ---- Vixie OR rule: both DOM and DOW restricted => OR ----
  [Test]
  public async Task DomAndDowBothRestricted_OrSemanticsAsync() {
    // "fire at 00:00 on the 1st OR on any Monday"
    var cron = CronExpression.Parse("0 0 1 * MON");
    // From Wed 2026-07-15 12:00: next Monday is 07-20, but the 1st does not occur until 08-01;
    // OR picks the earlier => Monday 07-20.
    await Assert.That(cron.NextFireAfter(_utc(2026, 07, 15, 12, 00), _utcZone)).IsEqualTo(_utc(2026, 07, 20, 00, 00));
    // From Thu 2026-07-30 12:00: next Monday is 08-03, the 1st is 08-01 => OR picks 08-01.
    await Assert.That(cron.NextFireAfter(_utc(2026, 07, 30, 12, 00), _utcZone)).IsEqualTo(_utc(2026, 08, 01, 00, 00));
  }

  // ---- timezone / DST ----
  [Test]
  public async Task TimeZone_NineAmLocalConvertsToUtcAsync() {
    // 09:00 America/New_York in July (EDT, UTC-4) => 13:00 UTC.
    var cron = CronExpression.Parse("0 9 * * *");
    var next = cron.NextFireAfter(_utc(2026, 07, 13, 08, 00), _nyZone);
    await Assert.That(next).IsEqualTo(_utc(2026, 07, 13, 13, 00));
  }

  [Test]
  public async Task TimeZone_SpringForwardSkipsMissingHourAsync() {
    // DST begins 2026-03-08 in America/New_York: local 02:00–02:59 does not exist.
    // "30 2 * * *" (02:30 daily) has no valid 02:30 on 03-08, so the next fire is 03-09 02:30 EDT.
    var cron = CronExpression.Parse("30 2 * * *");
    var next = cron.NextFireAfter(_utc(2026, 03, 08, 00, 00), _nyZone);
    // 2026-03-09 02:30 EDT (UTC-4) => 06:30 UTC.
    await Assert.That(next).IsEqualTo(_utc(2026, 03, 09, 06, 30));
  }

  // ---- malformed ----
  [Test]
  public async Task Parse_RejectsMalformedAsync() {
    await Assert.That(() => CronExpression.Parse("* * * *")).Throws<FormatException>();          // 4 fields
    await Assert.That(() => CronExpression.Parse("60 * * * *")).Throws<FormatException>();        // minute out of range
    await Assert.That(() => CronExpression.Parse("* 24 * * *")).Throws<FormatException>();        // hour out of range
    await Assert.That(() => CronExpression.Parse("* * 0 * *")).Throws<FormatException>();         // dom < 1
    await Assert.That(() => CronExpression.Parse("* * * 13 *")).Throws<FormatException>();        // month > 12
    await Assert.That(() => CronExpression.Parse("* * * * 8")).Throws<FormatException>();         // dow > 7
    await Assert.That(() => CronExpression.Parse("*/0 * * * *")).Throws<FormatException>();       // zero step
    await Assert.That(() => CronExpression.Parse("5-2 * * * *")).Throws<FormatException>();       // inverted range
    await Assert.That(() => CronExpression.Parse("")).Throws<FormatException>();                  // empty
    await Assert.That(() => CronExpression.Parse("* * * FOO *")).Throws<FormatException>();       // bad name
  }

  [Test]
  public async Task Parse_RejectsNullAsync() {
    await Assert.That(() => CronExpression.Parse(null!)).Throws<ArgumentNullException>();
  }
}
