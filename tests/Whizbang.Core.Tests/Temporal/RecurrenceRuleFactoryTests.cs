using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Temporal;

namespace Whizbang.Core.Tests.Temporal;

/// <summary>
/// Unit tests for <see cref="DefaultRecurrenceRuleFactory"/> and the rule wrappers it builds —
/// interval, cron (timezone-applied), and one-shot (never recurs) — plus the required-argument guards.
/// </summary>
/// <docs>fundamentals/temporal/recurrence</docs>
public class RecurrenceRuleFactoryTests {
  private static readonly DefaultRecurrenceRuleFactory _factory = new();
  private static readonly TimeZoneInfo _nyZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

  private static DateTimeOffset _utc(int y, int mo, int d, int h, int mi) =>
    new(y, mo, d, h, mi, 0, TimeSpan.Zero);

  [Test]
  public async Task Interval_BuildsIntervalRuleAsync() {
    var rule = _factory.Create(RecurrenceKind.Interval, cronExpression: null, TimeSpan.FromMinutes(5), timeZone: null);

    await Assert.That(rule).IsTypeOf<IntervalRecurrenceRule>();
    await Assert.That(rule.NextFireAfter(_utc(2026, 07, 13, 09, 00)))
      .IsEqualTo(_utc(2026, 07, 13, 09, 05));
  }

  [Test]
  public async Task Cron_BuildsCronRuleAndAppliesTimeZoneAsync() {
    var rule = _factory.Create(RecurrenceKind.Cron, "0 9 * * *", interval: null, _nyZone);

    await Assert.That(rule).IsTypeOf<CronRecurrenceRule>();
    // 09:00 EDT (UTC-4) in July => 13:00 UTC.
    await Assert.That(rule.NextFireAfter(_utc(2026, 07, 13, 08, 00)))
      .IsEqualTo(_utc(2026, 07, 13, 13, 00));
  }

  [Test]
  public async Task Cron_DefaultsToUtcWhenNoTimeZoneAsync() {
    var rule = (CronRecurrenceRule)_factory.Create(RecurrenceKind.Cron, "0 9 * * *", interval: null, timeZone: null);

    await Assert.That(rule.TimeZone).IsEqualTo(TimeZoneInfo.Utc);
    await Assert.That(rule.NextFireAfter(_utc(2026, 07, 13, 08, 00)))
      .IsEqualTo(_utc(2026, 07, 13, 09, 00));
  }

  [Test]
  public async Task OneShot_NeverRecursAsync() {
    var rule = _factory.Create(RecurrenceKind.OneShot, cronExpression: null, interval: null, timeZone: null);

    await Assert.That(rule).IsTypeOf<OneShotRecurrenceRule>();
    await Assert.That(rule.NextFireAfter(_utc(2026, 07, 13, 09, 00))).IsNull();
  }

  [Test]
  public async Task Interval_WithoutInterval_ThrowsAsync() {
    await Assert.That(() => _factory.Create(RecurrenceKind.Interval, cronExpression: null, interval: null, timeZone: null))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Cron_WithoutExpression_ThrowsAsync() {
    await Assert.That(() => _factory.Create(RecurrenceKind.Cron, cronExpression: null, interval: null, timeZone: null))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task UnknownKind_ThrowsAsync() {
    await Assert.That(() => _factory.Create((RecurrenceKind)99, cronExpression: null, interval: null, timeZone: null))
      .Throws<ArgumentOutOfRangeException>();
  }
}
