using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Temporal;

namespace Whizbang.Core.Tests.Temporal;

/// <summary>
/// Unit tests for <see cref="IntervalRecurrenceRule"/> — the next fire is always exactly
/// <c>after + interval</c>, and a non-positive interval is rejected at construction.
/// </summary>
/// <docs>fundamentals/temporal/recurrence</docs>
public class IntervalRecurrenceRuleTests {
  [Test]
  public async Task NextFireAfter_AddsIntervalAsync() {
    var rule = new IntervalRecurrenceRule(TimeSpan.FromMinutes(15));
    var after = new DateTimeOffset(2026, 07, 13, 09, 00, 00, TimeSpan.Zero);

    var next = rule.NextFireAfter(after);

    await Assert.That(next).IsEqualTo(new DateTimeOffset(2026, 07, 13, 09, 15, 00, TimeSpan.Zero));
  }

  [Test]
  public async Task NextFireAfter_ChainsFromReturnedValueAsync() {
    var rule = new IntervalRecurrenceRule(TimeSpan.FromHours(1));
    var after = new DateTimeOffset(2026, 07, 13, 09, 00, 00, TimeSpan.Zero);

    var first = rule.NextFireAfter(after);
    var second = rule.NextFireAfter(first!.Value);

    await Assert.That(second).IsEqualTo(new DateTimeOffset(2026, 07, 13, 11, 00, 00, TimeSpan.Zero));
  }

  [Test]
  public async Task NextFireAfter_PreservesOffsetAsync() {
    var rule = new IntervalRecurrenceRule(TimeSpan.FromMinutes(30));
    var after = new DateTimeOffset(2026, 07, 13, 09, 00, 00, TimeSpan.FromHours(-5));

    var next = rule.NextFireAfter(after);

    await Assert.That(next!.Value.Offset).IsEqualTo(TimeSpan.FromHours(-5));
    await Assert.That(next!.Value).IsEqualTo(new DateTimeOffset(2026, 07, 13, 09, 30, 00, TimeSpan.FromHours(-5)));
  }

  [Test]
  public async Task Constructor_RejectsNonPositiveIntervalAsync() {
    await Assert.That(() => new IntervalRecurrenceRule(TimeSpan.Zero)).Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => new IntervalRecurrenceRule(TimeSpan.FromSeconds(-1))).Throws<ArgumentOutOfRangeException>();
  }
}
