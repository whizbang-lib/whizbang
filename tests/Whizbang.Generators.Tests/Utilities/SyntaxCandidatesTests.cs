// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using SyntaxCandidates = shared::Whizbang.Generators.Shared.Utilities.SyntaxCandidates;

namespace Whizbang.Generators.Tests.Utilities;

/// <summary>
/// The syntactic checks the generators' predicates are built from. Each must admit every declaration that
/// could have what a transform looks for (a partial declaration always), or the transform silently stops
/// seeing it; and nothing else, or ordinary code reaches the transform.
/// </summary>
/// <code-under-test>src/Whizbang.Generators.Shared/Utilities/SyntaxCandidates.cs</code-under-test>
[Category("BuildCost")]
public class SyntaxCandidatesTests {
  [Test]
  [Arguments("public class C { }", false)]
  [Arguments("public partial class C { }", true)]
  [Arguments("public sealed partial record C;", true)]
  public async Task IsPartial_ReadsThePartialModifierAsync(string source, bool expected) {
    await Assert.That(SyntaxCandidates.IsPartial(_type(source))).IsEqualTo(expected);
  }

  [Test]
  [Arguments("public class C { }", false)]
  [Arguments("public class C : Base { }", true)]
  [Arguments("public record C(int X) : IThing;", true)]
  [Arguments("public partial class C { }", true)]
  [Arguments("[Marker] public class C { }", false)]
  public async Task MayHaveBaseTypes_NeedsABaseListOrPartialAsync(string source, bool expected) {
    await Assert.That(SyntaxCandidates.MayHaveBaseTypes(_type(source))).IsEqualTo(expected);
  }

  [Test]
  [Arguments("public class C { }", false)]
  [Arguments("[Marker] public class C { }", true)]
  [Arguments("public partial struct C { }", true)]
  [Arguments("public class C : Base { }", false)]
  public async Task MayHaveAttributes_NeedsAnAttributeOrPartialAsync(string source, bool expected) {
    await Assert.That(SyntaxCandidates.MayHaveAttributes(_type(source))).IsEqualTo(expected);
  }

  [Test]
  [Arguments("public class C { public int P { get; set; } public int M() => 1; }", false)]
  [Arguments("public class C { [Marker] public int P { get; set; } }", true)]
  [Arguments("public class C { [Marker] public int this[int i] => i; }", true)]
  [Arguments("public class C { [Marker] public event System.Action E { add { } remove { } } }", true)]
  [Arguments("public record C([property: Marker] int X);", true)]
  [Arguments("public record C(int X);", false)]
  [Arguments("public class C { [Marker] public int M() => 1; [Marker] public int F; }", false)]
  public async Task DeclaresAttributedProperty_FindsAnAttributedPropertyOrPositionalParameterAsync(string source, bool expected) {
    await Assert.That(SyntaxCandidates.DeclaresAttributedProperty(_type(source))).IsEqualTo(expected);
  }

  private static TypeDeclarationSyntax _type(string source) =>
      CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().First();
}
