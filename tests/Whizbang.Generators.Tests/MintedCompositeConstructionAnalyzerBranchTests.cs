// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The builder exemption is the property on the framework's own type, not any property with that
/// name: a lambda assigned to a consumer's <c>BuildComposite</c> or <c>CompositeFactory</c> is
/// ordinary code, and constructing a minted composite there is WHIZ150.
/// </summary>
/// <tests>src/Whizbang.Generators/Analyzers/MintedCompositeConstructionAnalyzer.cs</tests>
public class MintedCompositeConstructionAnalyzerBranchTests {
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("BuildComposite")]
  [Arguments("CompositeFactory")]
  public async Task LookAlikeBuilderProperty_IsNotTheSanctionedSeamAsync(string propertyName) {
    var source = $$"""
      using System;
      using Whizbang.Core.Minting;

      namespace ConsumerApp;

      public sealed class DigestComposite : CompositeEventBase;

      public sealed class HomegrownOptions {
        public Func<object>? {{propertyName}} { get; set; }
      }

      public class DigestShipper {
        public HomegrownOptions Configure() => new HomegrownOptions {
          {{propertyName}} = () => new DigestComposite(),
        };
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MintedCompositeConstructionAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ150")).IsEqualTo(1);
  }
}
