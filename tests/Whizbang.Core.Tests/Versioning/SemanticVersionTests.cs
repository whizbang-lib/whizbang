using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Versioning;

namespace Whizbang.Core.Tests.Versioning;

/// <summary>
/// Precedence here decides whether an instance may write to the schema. Getting it wrong does not
/// produce a wrong version string — it produces an instance that concludes it may migrate when it
/// must stand down, or that re-applies its own older definitions over newer ones. The pre-release
/// rules are the load-bearing part: everything before 1.0 ships with a pre-release label, so
/// <c>alpha.2</c> versus <c>alpha.10</c> is an ordinary comparison on every deploy rather than an
/// edge case.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Versioning/SemanticVersion.cs</code-under-test>
[Category("Versioning")]
public class SemanticVersionTests {

  private static SemanticVersion _parse(string s) {
    if (!SemanticVersion.TryParse(s, out var v)) {
      throw new InvalidOperationException($"expected '{s}' to parse");
    }
    return v;
  }

  // ── parsing ─────────────────────────────────────────────────────────────

  [Test]
  [Arguments("1.2.3", 1, 2, 3)]
  [Arguments("0.9.4", 0, 9, 4)]
  [Arguments("0.100.0", 0, 100, 0)]
  [Arguments("10.20.30", 10, 20, 30)]
  public async Task TryParse_CoreTriple_ReadsEachComponentAsync(string text, int major, int minor, int patch) {
    var v = _parse(text);
    await Assert.That(v.Major).IsEqualTo(major);
    await Assert.That(v.Minor).IsEqualTo(minor);
    await Assert.That(v.Patch).IsEqualTo(patch);
  }

  [Test]
  [Arguments("1.2.3-alpha.1", "alpha.1")]
  [Arguments("0.100.0-local.111", "local.111")]
  [Arguments("1.0.0-rc.1", "rc.1")]
  public async Task TryParse_PreRelease_IsCapturedAsync(string text, string expected) {
    await Assert.That(_parse(text).PreRelease).IsEqualTo(expected);
  }

  [Test]
  public async Task TryParse_Release_HasNoPreReleaseAsync() {
    await Assert.That(_parse("1.2.3").PreRelease).IsEmpty();
  }

  // Build metadata is explicitly excluded from precedence by the specification, so it must be
  // stripped rather than retained and accidentally compared.
  [Test]
  [Arguments("1.2.3+build.5")]
  [Arguments("1.2.3+sha.abc123")]
  public async Task TryParse_BuildMetadata_IsDiscardedAsync(string text) {
    var v = _parse(text);
    await Assert.That(v.PreRelease).IsEmpty();
    await Assert.That(v.CompareTo(_parse("1.2.3"))).IsEqualTo(0);
  }

  [Test]
  public async Task TryParse_PreReleaseAndBuildMetadata_KeepsOnlyPreReleaseAsync() {
    await Assert.That(_parse("1.2.3-alpha.1+build.9").PreRelease).IsEqualTo("alpha.1");
  }

  [Test]
  [Arguments("")]
  [Arguments("   ")]
  [Arguments("1")]
  [Arguments("1.2")]
  [Arguments("1.2.3.4")]
  [Arguments("v1.2.3")]
  [Arguments("1.2.x")]
  [Arguments("not-a-version")]
  [Arguments("-1.2.3")]
  public async Task TryParse_Unparseable_ReturnsFalseAsync(string text) {
    await Assert.That(SemanticVersion.TryParse(text, out _)).IsFalse();
  }

  [Test]
  public async Task TryParse_Null_ReturnsFalseAsync() {
    await Assert.That(SemanticVersion.TryParse(null, out _)).IsFalse();
  }

  // ── core precedence ─────────────────────────────────────────────────────

  [Test]
  [Arguments("2.0.0", "1.0.0")]
  [Arguments("1.1.0", "1.0.0")]
  [Arguments("1.0.1", "1.0.0")]
  [Arguments("0.10.0", "0.9.0")]
  [Arguments("0.100.0", "0.99.0")]
  public async Task Compare_HigherCore_IsGreaterAsync(string higher, string lower) {
    await Assert.That(_parse(higher).CompareTo(_parse(lower))).IsGreaterThan(0);
    await Assert.That(_parse(lower).CompareTo(_parse(higher))).IsLessThan(0);
  }

  [Test]
  public async Task Compare_IdenticalVersions_AreEqualAsync() {
    await Assert.That(_parse("1.2.3").CompareTo(_parse("1.2.3"))).IsEqualTo(0);
    await Assert.That(_parse("1.2.3-alpha.1").CompareTo(_parse("1.2.3-alpha.1"))).IsEqualTo(0);
  }

  // ── pre-release precedence — the part that is easy to get wrong ──────────

  // A pre-release version has LOWER precedence than the release it precedes.
  [Test]
  [Arguments("1.0.0", "1.0.0-alpha")]
  [Arguments("1.0.0", "1.0.0-rc.1")]
  [Arguments("0.9.4", "0.9.4-alpha.1")]
  public async Task Compare_ReleaseOutranksItsPreRelease_Async(string release, string preRelease) {
    await Assert.That(_parse(release).CompareTo(_parse(preRelease))).IsGreaterThan(0);
  }

  // The classic defect: numeric identifiers compare NUMERICALLY, so alpha.10 outranks alpha.2.
  // Compared as strings the answer inverts, and an instance concludes it may migrate when it
  // must stand down.
  [Test]
  [Arguments("1.0.0-alpha.10", "1.0.0-alpha.2")]
  [Arguments("1.0.0-alpha.11", "1.0.0-alpha.9")]
  [Arguments("0.100.0-local.111", "0.100.0-local.99")]
  [Arguments("1.0.0-rc.20", "1.0.0-rc.3")]
  public async Task Compare_NumericPreReleaseIdentifiers_CompareNumericallyNotLexicallyAsync(
      string higher, string lower) {
    await Assert.That(_parse(higher).CompareTo(_parse(lower))).IsGreaterThan(0);
    await Assert.That(_parse(lower).CompareTo(_parse(higher))).IsLessThan(0);
  }

  [Test]
  [Arguments("1.0.0-beta", "1.0.0-alpha")]
  [Arguments("1.0.0-rc", "1.0.0-beta")]
  public async Task Compare_AlphanumericIdentifiers_CompareLexicallyAsync(string higher, string lower) {
    await Assert.That(_parse(higher).CompareTo(_parse(lower))).IsGreaterThan(0);
  }

  // Specification: numeric identifiers always have LOWER precedence than alphanumeric ones.
  [Test]
  public async Task Compare_NumericIdentifier_RanksBelowAlphanumericAsync() {
    await Assert.That(_parse("1.0.0-alpha").CompareTo(_parse("1.0.0-1"))).IsGreaterThan(0);
  }

  // Specification: a larger set of pre-release fields outranks a smaller one when all preceding
  // identifiers are equal.
  [Test]
  public async Task Compare_MoreIdentifiers_OutranksFewerWhenPrefixEqualAsync() {
    await Assert.That(_parse("1.0.0-alpha.1").CompareTo(_parse("1.0.0-alpha"))).IsGreaterThan(0);
  }

  // The worked example from the specification, in order.
  [Test]
  public async Task Compare_SpecificationExampleChain_OrdersCorrectlyAsync() {
    string[] ascending = [
      "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
      "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0"
    ];
    for (var i = 1; i < ascending.Length; i++) {
      await Assert.That(_parse(ascending[i]).CompareTo(_parse(ascending[i - 1])))
        .IsGreaterThan(0)
        .Because($"'{ascending[i]}' must outrank '{ascending[i - 1]}'");
    }
  }

  // ── ordering is a total order ───────────────────────────────────────────

  [Test]
  public async Task Compare_IsAntisymmetricAsync() {
    string[] all = ["1.0.0", "1.0.0-alpha", "2.0.0", "0.9.4-beta.2", "0.100.0-local.111"];
    foreach (var a in all) {
      foreach (var b in all) {
        var ab = Math.Sign(_parse(a).CompareTo(_parse(b)));
        var ba = Math.Sign(_parse(b).CompareTo(_parse(a)));
        await Assert.That(ab).IsEqualTo(-ba).Because($"compare('{a}','{b}') must invert");
      }
    }
  }
}
