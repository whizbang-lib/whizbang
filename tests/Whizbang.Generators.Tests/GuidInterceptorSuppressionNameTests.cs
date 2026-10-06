// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// An assembly-level suppression is recognized by the framework attribute's name and by its simple
/// name, with or without the <c>Attribute</c> suffix, so a look-alike declared by the consumer
/// suppresses as well. Any other assembly attribute leaves interception on.
/// </summary>
/// <tests>src/Whizbang.Generators/GuidInterceptorGenerator.cs</tests>
[Category("SourceGenerators")]
public class GuidInterceptorSuppressionNameTests {
  private static readonly Dictionary<string, string> _interceptionEnabled = new() {
    ["build_property.WhizbangGuidInterceptionEnabled"] = "true"
  };

  private static GeneratorDriverRunResult _run(string assemblyAttribute, string declarations) =>
    GeneratorTestHelper.RunGenerator<GuidInterceptorGenerator>($$"""
      using System;

      {{assemblyAttribute}}

      {{declarations}}

      namespace TestApp {
        public class MyService {
          public Guid CreateId() => Guid.NewGuid();
        }
      }
      """, _interceptionEnabled);

  private static async Task _assertSuppressedAtAssemblyAsync(GeneratorDriverRunResult result) {
    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs")).IsNull();
    var notes = result.Diagnostics.Where(d => d.Id == "WHIZ059").ToList();
    await Assert.That(notes).Count().IsEqualTo(1);
    await Assert.That(notes[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("assembly");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task LookAlikeWithSuffix_SuppressesAsync() {
    var result = _run(
      "[assembly: Other.SuppressGuidInterception]",
      "namespace Other { public sealed class SuppressGuidInterceptionAttribute : Attribute { } }");

    await _assertSuppressedAtAssemblyAsync(result);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task LookAlikeWithoutSuffix_SuppressesAsync() {
    var result = _run(
      "[assembly: Other.SuppressGuidInterception]",
      "namespace Other { public sealed class SuppressGuidInterception : Attribute { } }");

    await _assertSuppressedAtAssemblyAsync(result);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task UnrelatedAssemblyAttribute_LeavesInterceptionOnAsync() {
    var result = _run("[assembly: System.Reflection.AssemblyTitle(\"x\")]", "");

    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs")).IsNotNull();
    await Assert.That(result.Diagnostics.Any(d => d.Id == "WHIZ059")).IsFalse();
  }
}
