// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// WHIZ118: a call to <c>DeriveFrom</c> on a strong id with no pinned id does not compile (#2604). The tests run the
/// strong id generator first, so the analyzer sees the <c>DeriveFrom</c> and <c>PinnedId</c> members the generator
/// really emits.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/Analyzers/StrongIdDeriveFromAnalyzer.cs</code-under-test>
[Category("Analyzers")]
public class StrongIdDeriveFromAnalyzerTests {
  private const string PIN = "3f2c8a1e-5b7d-4c9e-a1f0-6d2b8e4c7a91";

  [RequiresAssemblyFiles]
  private static async Task<List<Diagnostic>> _whiz118Async(string source) {
    var (_, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(new WhizbangIdGenerator(), source);
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StrongIdDeriveFromAnalyzer>(output);
    return [.. diagnostics.Where(d => d.Id == "WHIZ118")];
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task DeriveFromOnAnUnpinnedId_ReportsWhiz118Async() {
    var matches = await _whiz118Async("""
      using Whizbang.Core;
      using Whizbang.Core.Messaging;
      namespace App;
      [WhizbangId]
      public readonly partial struct ProductId;
      public static class Use {
        public static ProductId Make() => ProductId.DeriveFrom(Handling.Current, "product");
      }
      """);

    await Assert.That(matches).Count().IsEqualTo(1);
    await Assert.That(matches[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(matches[0].GetMessage(CultureInfo.InvariantCulture)).Contains("'ProductId' has no pinned id");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task DeriveFromOnAPinnedId_ReportsNothingAsync() {
    var matches = await _whiz118Async($$"""
      using Whizbang.Core;
      using Whizbang.Core.Messaging;
      namespace App;
      public record CreateProduct([WhizbangId(PinnedId = "{{PIN}}")] ProductId Id);
      public static class Use {
        public static ProductId Make() => ProductId.DeriveFrom(Handling.Current, "product");
      }
      """);

    await Assert.That(matches).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AMemberNamedPinnedIdThatIsNotAConstant_DoesNotPinTheIdAsync() {
    // Only the constant the generator bakes is read by the derivation; a hand-written member of that name is not a pin
    var matches = await _whiz118Async("""
      using Whizbang.Core;
      using Whizbang.Core.Messaging;
      namespace App;
      [WhizbangId]
      public readonly partial struct ProductId { public static string PinnedId => "x"; }
      [WhizbangId]
      public readonly partial struct OrderId { public static readonly string PinnedId = "x"; }
      public static class Use {
        public static object Make() => (ProductId.DeriveFrom(Handling.Current, "p"), OrderId.DeriveFrom(Handling.Current, "o"));
      }
      """);

    await Assert.That(matches).Count().IsEqualTo(2);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task OtherCallsNamedDeriveFrom_ReportNothingAsync() {
    // Only a strong id's DeriveFrom(Handling, string) is the derivation: another type's, another shape, or an
    // instance method of that name is someone else's
    var matches = await _whiz118Async("""
      using Whizbang.Core;
      using Whizbang.Core.Messaging;
      namespace App;
      [WhizbangId]
      public readonly partial struct ProductId;
      public static class Elsewhere {
        public static int DeriveFrom(Handling handling, string purpose) => 0;
        public static int DeriveFrom(string purpose, Handling handling) => 0;
        public static int DeriveFrom(Handling handling) => 0;
      }
      public sealed class Instance {
        public int DeriveFrom(Handling handling, string purpose) => 0;
      }
      public static class Use {
        public static int Make() =>
          Elsewhere.DeriveFrom(Handling.Current, "p") + Elsewhere.DeriveFrom("p", Handling.Current) +
          Elsewhere.DeriveFrom(Handling.Current) + new Instance().DeriveFrom(Handling.Current, "p") + ProductId.New().GetHashCode();
      }
      """);

    await Assert.That(matches).IsEmpty();
  }
}
