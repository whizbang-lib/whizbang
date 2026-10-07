// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// An attribute that does not resolve to anything cannot be the suppression attribute, so a
/// method carrying only such an attribute is intercepted as usual.
/// </summary>
/// <tests>src/Whizbang.Generators/GuidInterceptorGenerator.cs</tests>
[Category("SourceGenerators")]
public class GuidInterceptorUnresolvedAttributeTests {
  private static readonly Dictionary<string, string> _interceptionEnabled = new() {
    ["build_property.WhizbangGuidInterceptionEnabled"] = "true"
  };

  [Test]
  [RequiresAssemblyFiles]
  public async Task UnresolvedAttributeOnMethod_DoesNotSuppressAsync() {
    var result = GeneratorTestHelper.RunGenerator<GuidInterceptorGenerator>("""
      using System;

      namespace TestApp;

      public class MyService {
        [NotARealAttribute]
        public Guid CreateId() => Guid.NewGuid();
      }
      """, _interceptionEnabled);

    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs")).IsNotNull();
    await Assert.That(result.Diagnostics.Any(d => d.Id == "WHIZ059")).IsFalse();
  }
}
