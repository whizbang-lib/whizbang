// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// A strong id's pinned id is checked like a message's (#2604): WHIZ112 when it is not a GUID, on every route that can
/// pin it, and WHIZ123 when one struct pins itself two ways. A strong id is never required to be pinned (no WHIZ110):
/// the pin is required only where it is used, by WHIZ118 at a <c>DeriveFrom</c> call.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/Analyzers/PinnedIdAnalyzer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/WhizbangIdDeclaration.cs</code-under-test>
[Category("Analyzers")]
public class PinnedIdAnalyzerStrongIdTests {
  private const string PIN = "3f2c8a1e-5b7d-4c9e-a1f0-6d2b8e4c7a91";

  [RequiresAssemblyFiles]
  private static async Task<List<Diagnostic>> _diagnosticsAsync(string source) =>
    [.. await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedIdAnalyzer>(source)];

  [Test]
  [RequiresAssemblyFiles]
  public async Task AnUnpinnedStrongId_ReportsNothingAsync() {
    var diagnostics = await _diagnosticsAsync("""
      using Whizbang.Core;
      namespace App;
      [WhizbangId]
      public readonly partial struct ProductId;
      public class CreateProduct { [WhizbangId] public ProductId Id { get; set; } }
      """);

    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ110" or "WHIZ111" or "WHIZ112" or "WHIZ123")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ValidStrongIdPins_ReportNothingAsync() {
    var diagnostics = await _diagnosticsAsync($$"""
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace App;
      [WhizbangId(PinnedId = "{{PIN.ToUpperInvariant()}}")]
      [PinnedId("{{PIN}}")]
      public readonly partial struct ProductId;
      public record CreateOrder([WhizbangId(PinnedId = "{{PIN}}")] OrderId Id);
      """);

    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ112" or "WHIZ123")).IsEmpty()
      .Because("one pin spelled two ways on one struct is one pin");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AStrongIdPinThatIsNotAGuid_ReportsWhiz112OnEveryRouteAsync() {
    var diagnostics = await _diagnosticsAsync("""
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace App;
      [WhizbangId]
      [PinnedId("struct-pin")]
      public readonly partial struct ProductId;
      public class CreateOrder { [WhizbangId(PinnedId = "property-pin")] public OrderId Id { get; set; } }
      public record CreateLine([WhizbangId(PinnedId = "parameter-pin")] LineId Id);
      """);

    var messages = diagnostics.Where(d => d.Id == "WHIZ112").Select(d => d.GetMessage(CultureInfo.InvariantCulture)).ToList();
    await Assert.That(messages).Count().IsEqualTo(3);
    await Assert.That(messages.Any(m => m.Contains("'struct-pin' on 'ProductId'"))).IsTrue();
    await Assert.That(messages.Any(m => m.Contains("'property-pin' on 'OrderId'"))).IsTrue();
    await Assert.That(messages.Any(m => m.Contains("'parameter-pin' on 'LineId'"))).IsTrue();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AStructPinnedTwoWays_ReportsWhiz123Async() {
    var diagnostics = await _diagnosticsAsync($$"""
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace App;
      [WhizbangId(PinnedId = "7b1d9e2f-0c3a-4e5b-8f6d-1a2b3c4d5e6f")]
      [PinnedId("{{PIN}}")]
      public readonly partial struct ProductId;
      """);

    var matches = diagnostics.Where(d => d.Id == "WHIZ123").ToList();
    await Assert.That(matches).Count().IsEqualTo(1);
    await Assert.That(matches[0].GetMessage(CultureInfo.InvariantCulture)).Contains("7b1d9e2f-0c3a-4e5b-8f6d-1a2b3c4d5e6f");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AStructPinnedTwoWaysWithANonGuid_ReportsBothAsync() {
    var diagnostics = await _diagnosticsAsync("""
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace App;
      [WhizbangId(PinnedId = "declared-pin")]
      [PinnedId("struct-pin")]
      public readonly partial struct ProductId;
      """);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ123")).Count().IsEqualTo(1);
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ112")).Count().IsEqualTo(1)
      .Because("the struct's [PinnedId] is its pin, and it is not a GUID");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AnEmptyOrMalformedPin_PinsNothingAsync() {
    // A [PinnedId] the compiler refuses (no argument, null) and a blank pin carry no pin: nothing to check
    var diagnostics = await _diagnosticsAsync("""
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace App;
      [WhizbangId]
      [PinnedId]
      public readonly partial struct ProductId;
      [WhizbangId]
      [PinnedId(null)]
      public readonly partial struct OrderId;
      [WhizbangId(PinnedId = " ")]
      [PinnedId("  ")]
      public readonly partial struct LineId;
      """);

    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ112" or "WHIZ123")).IsEmpty();
  }
}
