using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Temporal;

namespace Whizbang.Core.Tests.Temporal;

/// <summary>
/// Covers two edges of <see cref="CronExpression"/> that
/// <c>CronExpressionTests</c>'s malformed-input and next-fire tests never reach: an expression
/// that parses cleanly but can never actually fire (search-horizon exhaustion), and a field whose
/// terms parse individually but collectively match nothing (an all-commas field).
/// </summary>
public class CronExpressionCoverageTests {
  private static readonly TimeZoneInfo _utcZone = TimeZoneInfo.Utc;

  private static DateTimeOffset _utc(int y, int mo, int d, int h, int mi) =>
    new(y, mo, d, h, mi, 0, TimeSpan.Zero);

  /// <summary>
  /// A schedule expression that can never actually fire (e.g. "day 30 of February") must be
  /// reported as such (null) rather than the search silently running forever or throwing — a
  /// scheduled job pinned to this expression would otherwise appear registered and healthy while
  /// never once running, with nothing to signal the mistake.
  /// </summary>
  [Test]
  public async Task NextFireAfter_UnsatisfiableExpression_ReturnsNullAsync() {
    var cron = CronExpression.Parse("0 0 30 2 *"); // Feb 30 never occurs
    var next = cron.NextFireAfter(_utc(2026, 1, 1, 0, 0), _utcZone);

    await Assert.That(next).IsNull()
      .Because("Feb 30 never occurs on any calendar — the search-horizon exhaustion path exists "
             + "so an operator (or a startup validator) can detect a schedule that will silently "
             + "never fire, instead of it appearing registered and healthy forever");
  }

  /// <summary>
  /// A cron field whose individual terms are each syntactically valid but whose combination
  /// matches nothing (e.g. an all-commas field) must be rejected at parse time — accepting it
  /// would produce a CronExpression whose every field-check silently fails forever, identical in
  /// effect to the unsatisfiable-expression case above but reached by a config typo instead of an
  /// impossible calendar date.
  /// </summary>
  [Test]
  public async Task Parse_FieldWithOnlyEmptyCommaSeparatedTerms_ThrowsFormatExceptionAsync() {
    await Assert.That(() => CronExpression.Parse("0 0 1 1 ,"))
      .Throws<FormatException>()
      .Because("a field that reduces to zero terms matches no values at all — this must fail loud "
             + "at parse time, not produce a CronExpression that can never fire");
  }
}
