using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Versioning;

namespace Whizbang.Core.Tests.Versioning;

/// <summary>
/// Precedence, equality, and the malformed inputs that must not parse.
/// <para>
/// Every comparison here funnels through <c>CompareTo</c>, which means equality is defined as equal
/// PRECEDENCE rather than as identical text. That is the semver rule and it is the one an upgrade
/// check depends on: two versions that rank the same must compare equal through every surface —
/// operators, <c>Equals</c>, and the object overload — or the same pair of versions answers
/// differently depending on which one a caller happened to use.
/// </para>
/// <para>
/// The parse guards matter for the same reason. A version string that is accepted but wrong ranks
/// against real versions and silently mis-orders an upgrade decision; rejecting it makes the
/// caller deal with the bad input at the point it entered.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Versioning/SemanticVersion.cs</code-under-test>
public class SemanticVersionEqualityTests {

  /// <summary>Parses a version the tests treat as well-formed, failing loudly if it is not.</summary>
  private static SemanticVersion _v(string s) {
    if (!SemanticVersion.TryParse(s, out var v)) {
      throw new ArgumentException($"fixture version '{s}' does not parse", nameof(s));
    }
    return v;
  }

  [Test]
  [Arguments("1.2.3-")]
  [Arguments("1..3")]
  [Arguments("1.2.")]
  [Arguments(".2.3")]
  public async Task AMalformedVersion_IsRejectedRatherThanCoercedAsync(string malformed) {
    await Assert.That(SemanticVersion.TryParse(malformed, out _)).IsFalse()
      .Because("a version accepted but misread ranks against real versions and mis-orders an "
             + "upgrade decision, where a rejection surfaces the bad input where it entered");
  }

  [Test]
  public async Task EqualPrecedence_AgreesAcrossEveryComparisonSurfaceAsync() {
    var a = _v("1.2.3");
    var b = _v("1.2.3");

    await Assert.That(a.Equals(b)).IsTrue();
    await Assert.That(a.Equals((object)b)).IsTrue()
      .Because("the object overload is what collections use; disagreeing with the typed one makes "
             + "a set and a direct comparison answer differently about the same pair");
    await Assert.That(a == b).IsTrue();
    await Assert.That(a != b).IsFalse();
    await Assert.That(a <= b).IsTrue();
    await Assert.That(a >= b).IsTrue();
    await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode())
      .Because("equal values that hash differently go missing from every hash-based lookup");
  }

  [Test]
  public async Task ANonVersion_IsNotEqualThroughTheObjectOverloadAsync() {
    await Assert.That(_v("1.2.3").Equals("1.2.3")).IsFalse()
      .Because("a string that looks like the version is not the version, and treating it as equal "
             + "would let untyped comparisons succeed by accident");
  }

  [Test]
  public async Task Ordering_PlacesAPreReleaseBelowItsReleaseAsync() {
    var pre = _v("1.2.3-alpha.1");
    var release = _v("1.2.3");

    await Assert.That(pre < release).IsTrue()
      .Because("semver ranks a pre-release below the release it precedes — inverting this ships a "
             + "prerelease to anyone asking for the newest stable version");
    await Assert.That(release > pre).IsTrue();
    await Assert.That(pre == release).IsFalse();
  }

  [Test]
  public async Task ToString_RoundTripsThroughParseAsync() {
    foreach (var text in new[] { "1.2.3", "0.0.1", "2.0.0-alpha.7" }) {
      await Assert.That(_v(text).ToString()).IsEqualTo(text)
        .Because("the rendered form is what gets written to logs and package metadata, so it must "
               + "parse back to the same precedence it came from");
    }
  }
}
