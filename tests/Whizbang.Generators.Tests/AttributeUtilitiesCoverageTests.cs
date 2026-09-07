extern alias shared;

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using AttributeUtilities = shared::Whizbang.Generators.Shared.Utilities.AttributeUtilities;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused test for <see cref="AttributeUtilities.GetStringArrayValue"/>, complementing
/// <c>tests/Whizbang.Generators.Tests/Utilities/AttributeUtilitiesTests.cs</c>. That file's
/// constructor-argument scenarios only ever declare a matching parameter that actually is a
/// <c>string[]</c>; this targets what happens when a constructor parameter's NAME matches the
/// requested property but its declared TYPE is a scalar, not an array.
/// </summary>
public class AttributeUtilitiesCoverageTests {

  /// <summary>
  /// <c>GetStringArrayValue</c> matches constructor parameters by name only, not by declared type —
  /// a caller can legitimately query a property name that happens to collide with a same-named
  /// scalar parameter on some other attribute shape. If the <c>Kind == TypedConstantKind.Array</c>
  /// guard were removed and the code unconditionally cast <c>arg.Values</c>, every one of this
  /// shared utility's callers — used by every generator in this repository — would get an
  /// <c>InvalidOperationException</c> instead of a clean null for that case.
  /// </summary>
  [Test]
  public async Task GetStringArrayValue_ConstructorArgumentNameMatchesButTypeIsNotArray_ReturnsNullAsync() {
    // Arrange — constructor parameter "tag" is a scalar string, not string[].
    const string source = """

using System;

[AttributeUsage(AttributeTargets.Class)]
public class TestAttribute : Attribute {
    public TestAttribute(string tag) { }
}

[Test("not-an-array")]
public class TestClass { }
""";

    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName("TestClass")!;
    var attribute = typeSymbol.GetAttributes()[0];

    // Act
    var result = AttributeUtilities.GetStringArrayValue(attribute, "tag");

    // Assert
    await Assert.That(result).IsNull()
      .Because("the name-matching constructor parameter's TypedConstant.Kind is Primitive, not Array, so the loop must fall through to null rather than casting it");
  }
}
