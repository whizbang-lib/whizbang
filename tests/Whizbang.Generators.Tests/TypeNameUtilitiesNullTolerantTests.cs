// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;

using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using TypeNameUtilities = shared::Whizbang.Generators.Shared.Utilities.TypeNameUtilities;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The null-tolerant name helpers every generator uses on <see cref="AttributeData.AttributeClass"/>.
/// </summary>
/// <remarks>
/// Roslyn declares <c>AttributeClass</c> nullable but binds an unresolved C# attribute to an error type,
/// so no compilation hands a generator a null one. The guard lives in these helpers, once, instead of at
/// every call site, and this is where its null answer is pinned: through a generator it cannot be reached.
/// </remarks>
/// <code-under-test>src/Whizbang.Generators.Shared/Utilities/TypeNameUtilities.cs</code-under-test>
public class TypeNameUtilitiesNullTolerantTests {
  private static INamedTypeSymbol _attributeClass() {
    var compilation = GeneratorTestHelper.CreateCompilation("""
      namespace Sample.Attributes;
      public sealed class MarkerAttribute : System.Attribute { }
      [Marker] public class Marked { }
      """);
    return compilation.GetTypeByMetadataName("Sample.Attributes.Marked")!.GetAttributes().Single().AttributeClass!;
  }

  [Test]
  public async Task IsNamed_NoSymbol_IsFalseAsync() {
    await Assert.That(TypeNameUtilities.IsNamed(null, "Sample.Attributes.MarkerAttribute")).IsFalse();
  }

  [Test]
  public async Task IsFullyQualifiedNamed_MatchesOnlyTheFullyQualifiedFormAsync() {
    var symbol = _attributeClass();

    await Assert.That(TypeNameUtilities.IsFullyQualifiedNamed(symbol, "global::Sample.Attributes.MarkerAttribute")).IsTrue();
    await Assert.That(TypeNameUtilities.IsFullyQualifiedNamed(symbol, "Sample.Attributes.MarkerAttribute")).IsFalse()
      .Because("the display form is not the fully qualified form");
    await Assert.That(TypeNameUtilities.IsFullyQualifiedNamed(null, "global::Sample.Attributes.MarkerAttribute")).IsFalse();
  }

  [Test]
  public async Task SimpleNameOrNull_IsTheNameOrNullAsync() {
    await Assert.That(TypeNameUtilities.SimpleNameOrNull(_attributeClass())).IsEqualTo("MarkerAttribute");
    await Assert.That(TypeNameUtilities.SimpleNameOrNull(null)).IsNull();
  }

  [Test]
  public async Task DisplayOrNull_IsTheDisplayFormOrNullAsync() {
    await Assert.That(TypeNameUtilities.DisplayOrNull(_attributeClass())).IsEqualTo("Sample.Attributes.MarkerAttribute");
    await Assert.That(TypeNameUtilities.DisplayOrNull(null)).IsNull();
  }

  [Test]
  public async Task DisplayOrEmpty_IsTheDisplayFormOrEmptyAsync() {
    await Assert.That(TypeNameUtilities.DisplayOrEmpty(_attributeClass())).IsEqualTo("Sample.Attributes.MarkerAttribute");
    await Assert.That(TypeNameUtilities.DisplayOrEmpty(null)).IsEmpty();
  }
}
