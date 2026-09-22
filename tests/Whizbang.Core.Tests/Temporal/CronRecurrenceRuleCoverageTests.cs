using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Temporal;

namespace Whizbang.Core.Tests.Temporal;

/// <summary>
/// Covers <see cref="CronRecurrenceRule.Expression"/>. Its sibling <c>TimeZone</c> is asserted in
/// <c>RecurrenceRuleFactoryTests</c>, but <c>Expression</c> itself is never read anywhere in the
/// suite (or, yet, in production code — it exists "for diagnostics / round-tripping to the DB").
/// </summary>
public class CronRecurrenceRuleCoverageTests {

  /// <summary>
  /// If this stopped round-tripping the constructor argument verbatim, a diagnostic surface or a
  /// DB round-trip reading this property back would report a DIFFERENT cron text than the one
  /// actually parsed and being evaluated — an operator debugging "why didn't this schedule fire"
  /// would be looking at the wrong expression.
  /// </summary>
  [Test]
  public async Task Expression_RoundTripsTheConstructorArgumentVerbatimAsync() {
    var rule = new CronRecurrenceRule("0 9 * * *");

    await Assert.That(rule.Expression).IsEqualTo("0 9 * * *")
      .Because("diagnostics and DB round-tripping depend on this returning EXACTLY the text that "
             + "was parsed, not a normalized or reformatted variant");
  }
}
