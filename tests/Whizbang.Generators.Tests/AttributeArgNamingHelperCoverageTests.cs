using Whizbang.Generators.Utilities;

#pragma warning disable CA1707 // test method names use underscores
#pragma warning disable IDE1006 // test method names

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused test for <see cref="AttributeArgNamingHelper"/>, complementing
/// <c>tests/Whizbang.Generators.Tests/AttributeArgNamingHelperTests.cs</c>. That file exercises
/// each of the six named <see cref="AttributeArgNamingConvention"/> members; this targets the
/// switch's default arm, reachable because <c>MessageTagDiscoveryGenerator._resolveNamingConvention</c>
/// casts a raw Roslyn <c>TypedConstant</c> int straight to this enum with no range check.
/// </summary>
public class AttributeArgNamingHelperCoverageTests {

  /// <summary>
  /// A user's tag-attribute subclass can carry <c>[AttributeArgNaming((AttributeArgNamingConvention)99)]</c>
  /// — an out-of-range cast the C# compiler does not reject. If <c>Convert</c>'s default arm did
  /// anything other than pass the parameter name through unchanged, that out-of-range value would
  /// either throw or silently corrupt the generated property name in the AttributeFactory's object
  /// initializer, instead of degrading to the same identity behavior as
  /// <see cref="AttributeArgNamingConvention.Identity"/>.
  /// </summary>
  [Test]
  public async Task Convert_UnrecognizedConventionValue_ReturnsParameterNameUnchangedAsync() {
    var result = AttributeArgNamingHelper.Convert("tagValue", (AttributeArgNamingConvention)99);

    await Assert.That(result).IsEqualTo("tagValue")
      .Because("an out-of-range convention value must fall through the switch's default arm and return the input unchanged, not throw or mutate it");
  }
}
