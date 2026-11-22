// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Every generated id declares <c>static partial void IsGrandfathered(Guid value, ref bool grandfathered)</c>
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

  private const string HOOK_DECLARATION = "static partial void IsGrandfathered(Guid value, ref bool grandfathered);";

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
    await Assert.That(generated).Contains("if (value.Version != 7 && !_isGrandfathered(value))");
    await Assert.That(generated).Contains("if (!tracked.IsTimeOrdered && !_isGrandfathered(tracked.Value))");
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
    await Assert.That(generated).Contains("if (value.Version is not (5 or 7) && !_isGrandfathered(value))");
  }

  /// <summary>An any-version id refuses only the empty Guid, which is never grandfathered, so it never asks.</summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task AnyVersionId_DeclaresTheHookButNeverConsultsItAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(AcceptAnyVersion = true)]
      public readonly partial struct ProductId;
      """;

    var generated = _generatedId(source);

    await Assert.That(generated).Contains(HOOK_DECLARATION);
    await Assert.That(generated).DoesNotContain("_isGrandfathered(");
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

    await Assert.That(generated).Contains("private static bool _isGrandfathered(Guid value) {");
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
        private static readonly FrozenSet<Guid> _grandfathered = new[] {
          new Guid("3f2504e0-4f89-41d3-9a0c-0305e82c3301"),
        }.ToFrozenSet();

        static partial void IsGrandfathered(Guid value, ref bool grandfathered) =>
          grandfathered = _grandfathered.Contains(value);
      }
      """;

    var (_, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(new WhizbangIdGenerator(), source);

    await Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
  }
}
