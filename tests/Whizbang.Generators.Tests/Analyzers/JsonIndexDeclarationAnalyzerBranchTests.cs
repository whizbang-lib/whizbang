// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// How WHIZ304 names the member that forces opaque storage: the first of the model's own properties
/// whose type is polymorphic, skipping a property that is not a named type at all, and a general
/// description when the polymorphism comes from further down the graph.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/Analyzers/JsonIndexDeclarationAnalyzer.cs</code-under-test>
public class JsonIndexDeclarationAnalyzerBranchTests {
  private static async Task<string> _whiz304MessageAsync(string members) {
    var source = $$"""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public abstract class PaymentMethod {
        public string Name { get; init; } = "";
      }

      public record PaymentHolder {
        public PaymentMethod? Payment { get; init; }
      }

      [IndexAllFields]
      public record OrderModel {
        [StreamId]
        public Guid OrderId { get; init; }

      {{members}}
      }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JsonIndexDeclarationAnalyzer>(source);
    return diagnostics.Single(d => d.Id == "WHIZ304").GetMessage(CultureInfo.InvariantCulture);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ArrayPropertyBeforeThePolymorphicOne_IsSkippedWhenNamingTheMemberAsync() {
    var message = await _whiz304MessageAsync("""
        public int[] Scores { get; init; } = [];
        public PaymentMethod? Payment { get; init; }
      """);

    await Assert.That(message).Contains("Payment is TestApp.PaymentMethod");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task PolymorphismFromFurtherDownTheGraph_IsDescribedGenerallyAsync() {
    var message = await _whiz304MessageAsync("""
        public PaymentHolder? Holder { get; init; }
      """);

    await Assert.That(message).Contains("the model holds a polymorphic member in its graph");
  }
}
