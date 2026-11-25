// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A strict or allow-list id declares <c>static partial void _isGrandfathered(Guid value, ref bool grandfathered)</c>
/// (#2517). An id type that implements it lets its checked entry points (<c>From</c>, <c>Parse</c>, the explicit
/// cast, the JSON converter, the GraphQL scalar) accept the values it names even when their version breaks the
/// id's rule. Left unimplemented, the compiler removes the call and the id behaves as before. <c>Guid.Empty</c> is
/// never grandfathered.
/// </summary>
/// <remarks>
/// The hook is a void partial method with a <c>ref</c> result because only a void partial method may be left
/// unimplemented in C#.
/// </remarks>
/// <code-under-test>src/Whizbang.Generators/WhizbangIdGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public class WhizbangIdGrandfatheredTests {

  private const string HOOK_DECLARATION = "static partial void _isGrandfathered(Guid value, ref bool grandfathered);";

  [RequiresAssemblyFiles()]
  private static string _generatedId(string source, string typeName = "ProductId") {
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    return GeneratorTestHelper.GetGeneratedSource(result, $"{typeName}.g.cs")
      ?? throw new InvalidOperationException($"{typeName}.g.cs was not generated");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task StrictId_DeclaresTheHookAndConsultsItWhenTheVersionFailsAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId]
      public readonly partial struct ProductId;
      """;

    var generated = _generatedId(source);

    await Assert.That(generated).Contains(HOOK_DECLARATION);
    await Assert.That(generated).Contains("if (value.Version != 7 && !_grandfathers(value))");
    await Assert.That(generated).Contains("if (!tracked.IsTimeOrdered && !_grandfathers(tracked.Value))");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AllowListId_ConsultsTheHookWhenTheVersionIsNotListedAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(Versions = UuidVersions.V7 | UuidVersions.V5)]
      public readonly partial struct ProductId;
      """;

    var generated = _generatedId(source);

    await Assert.That(generated).Contains(HOOK_DECLARATION);
    await Assert.That(generated).Contains("(value.Version is (5 or 7) && (value.Variant & 0xC) == 0x8) || _grandfathers(value);");
    await Assert.That(generated).Contains("    if (!IsAccepted(value))");
  }

  /// <summary>An any-version id refuses only the empty Guid, which is never grandfathered, so it never asks.</summary>
  /// <summary>
  /// An any-version id refuses only the empty Guid, so it declares no hook: one implemented there would never run, and
  /// is a compile error (CS0759) instead.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task AnyVersionId_DeclaresNoHook_AndImplementingOneIsCs0759Async() {
    const string declared = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(AcceptAnyVersion = true)]
      public readonly partial struct ProductId;
      """;
    const string implemented = """
      using System;
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(AcceptAnyVersion = true)]
      public readonly partial struct ProductId {
        static partial void _isGrandfathered(Guid value, ref bool grandfathered) => grandfathered = true;
      }
      """;

    var generated = _generatedId(declared);
    var (_, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(new WhizbangIdGenerator(), implemented);

    await Assert.That(generated).DoesNotContain("_isGrandfathered");
    await Assert.That(generated).DoesNotContain("_grandfathers");
    await Assert.That(output.GetDiagnostics().Select(d => d.Id)).Contains("CS0759");
  }

  /// <summary>
  /// The hook is implemented as declared, without an accessibility modifier: C# lets a partial method go unimplemented
  /// only when it has none, and an implementation that adds <c>private</c> is CS8799, as the docs say.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task HookImplementedWithAModifier_IsCs8799Async() {
    const string source = """
      using System;
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId]
      public readonly partial struct ProductId {
        private static partial void _isGrandfathered(Guid value, ref bool grandfathered) => grandfathered = true;
      }
      """;

    var (_, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(new WhizbangIdGenerator(), source);

    await Assert.That(output.GetDiagnostics().Select(d => d.Id)).Contains("CS8799");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Helper_NeverGrandfathersTheEmptyGuidAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId]
      public readonly partial struct ProductId;
      """;

    var generated = _generatedId(source);

    await Assert.That(generated).Contains("private static bool _grandfathers(Guid value) {");
    await Assert.That(generated).Contains("if (value == Guid.Empty)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Unimplemented_GeneratedCodeCompilesAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId]
      public readonly partial struct ProductId;
      """;

    var (_, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(new WhizbangIdGenerator(), source);

    await Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Implemented_GeneratedCodeCompilesAsync() {
    const string source = """
      using System;
      using System.Collections.Frozen;
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId]
      public readonly partial struct ProductId {
        private static readonly FrozenSet<Guid> _grandfatheredValues = new[] {
          new Guid("3f2504e0-4f89-41d3-9a0c-0305e82c3301"),
        }.ToFrozenSet();

        static partial void _isGrandfathered(Guid value, ref bool grandfathered) =>
          grandfathered = _grandfatheredValues.Contains(value);
      }
      """;

    var (_, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(new WhizbangIdGenerator(), source);

    await Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
  }
}
