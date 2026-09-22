using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Versioning;

namespace Whizbang.Core.Tests.Versioning;

/// <summary>
/// Tail-of-round coverage for <see cref="SemanticVersion"/>: the empty-identifier branch of the
/// numeric-vs-alphanumeric check in pre-release precedence comparison. <c>TryParse</c> accepts a
/// pre-release string without validating that every dot-separated identifier is non-empty, so a
/// double dot (e.g. <c>alpha..1</c>) parses successfully and later needs to be compared.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Versioning/SemanticVersion.cs</code-under-test>
[Category("Versioning")]
public class SemanticVersionCoverageTests {

  private static SemanticVersion _parse(string text) {
    var ok = SemanticVersion.TryParse(text, out var version);
    if (!ok) {
      throw new System.InvalidOperationException($"expected '{text}' to parse");
    }
    return version;
  }

  /// <summary>
  /// A migration-precedence check that mishandles a malformed-but-parseable pre-release picks the
  /// wrong instance to hold the schema lock — silently, since the comparison never throws. This
  /// pins the actual (surprising) result: an empty identifier produced by a double dot is treated
  /// as non-numeric, so it currently outranks a genuine numeric identifier in the same position.
  /// </summary>
  [Test]
  public async Task CompareTo_PreReleaseWithEmptyDotSeparatedIdentifier_ComparesWithoutThrowingAsync() {
    var withEmptyIdentifier = _parse("1.0.0-alpha..1");
    var withNumericIdentifier = _parse("1.0.0-alpha.1.1");

    var comparison = withEmptyIdentifier.CompareTo(withNumericIdentifier);

    await Assert.That(comparison).IsGreaterThan(0)
      .Because("the empty identifier between the double dot is not numeric, and a non-numeric "
             + "identifier always outranks a numeric one in the same position — the comparison "
             + "must resolve deterministically rather than throw or loop");
  }
}
