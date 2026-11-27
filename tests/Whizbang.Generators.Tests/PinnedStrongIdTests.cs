// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A strong id's pinned id is governed like a message's (#2604): the pinned-type ledger records it (kind
/// <c>strong-id</c>) from every declaration route, the pinned-id registry maps the generated type to it, and the rename
/// analyzer sees it living. A rename still needs acknowledging (WHIZ120); a strong id entry with no living pin is an
/// error (WHIZ119), not the warning a message gets (WHIZ121), because a changed pin changes every id the type derives.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/PinnedTypeLedgerGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/PinnedIdRegistryGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/Analyzers/PinnedTypeRenameAnalyzer.cs</code-under-test>
[Category("SourceGenerators")]
[Category("RenamePlatform")]
public class PinnedStrongIdTests {
  private const string LEDGER_PATH = "/repo/src/MyApp/.whizbang/pinned-type-ledger.json";
  private const string STRUCT_PIN = "3f2c8a1e-5b7d-4c9e-a1f0-6d2b8e4c7a91";
  private const string PROPERTY_PIN = "7b1d9e2f-0c3a-4e5b-8f6d-1a2b3c4d5e6f";
  private const string PARAMETER_PIN = "9c8b7a6d-5e4f-4a3b-9c2d-1e0f2a3b4c5d";

  private const string THREE_ROUTES = $$"""
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace App;
      [WhizbangId]
      [PinnedId("{{STRUCT_PIN}}")]
      public readonly partial struct ProductId;
      public class CreateOrder {
        [WhizbangId(PinnedId = "{{PROPERTY_PIN}}", Namespace = "App.Ids")]
        public OrderId Id { get; set; }
        public string Note { get; set; } = "";
      }
      public record CreateLine([WhizbangId(PinnedId = "{{PARAMETER_PIN}}")] LineId Id, string Sku);
      public readonly partial struct NotAnId;
      [WhizbangId]
      public readonly partial struct UnpinnedId;
      """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task Ledger_RecordsAStrongIdFromEveryRouteAsync() {
    var result = GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(THREE_ROUTES);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PinnedTypeLedger.g.cs");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains(STRUCT_PIN);
    await Assert.That(generated).Contains("App.ProductId");
    await Assert.That(generated).Contains(PROPERTY_PIN);
    await Assert.That(generated).Contains("App.Ids.OrderId")
      .Because("the ledger names the type the generator emits, in the namespace [WhizbangId] puts it in");
    await Assert.That(generated).Contains(PARAMETER_PIN);
    await Assert.That(generated).Contains("App.LineId");
    await Assert.That(generated).Contains("\"\"strong-id\"\"");
    await Assert.That(generated).DoesNotContain("UnpinnedId");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Ledger_RecordsTheNormalizedPinAsync() {
    var result = GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(
      $"using Whizbang.Core;\nnamespace App;\n[WhizbangId(PinnedId = \"{STRUCT_PIN.ToUpperInvariant()}\")] public readonly partial struct ProductId;");
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PinnedTypeLedger.g.cs");

    await Assert.That(generated).Contains(STRUCT_PIN);
    await Assert.That(generated).DoesNotContain(STRUCT_PIN.ToUpperInvariant());
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Registry_MapsEachPinnedStrongIdAsync() {
    var result = GeneratorTestHelper.RunGenerator<PinnedIdRegistryGenerator>(THREE_ROUTES);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PinnedIdRegistry.g.cs");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains($"(typeof(global::App.ProductId), \"{STRUCT_PIN}\")");
    await Assert.That(generated).Contains($"(typeof(global::App.Ids.OrderId), \"{PROPERTY_PIN}\")");
    await Assert.That(generated).Contains($"(typeof(global::App.LineId), \"{PARAMETER_PIN}\")");
    await Assert.That(generated).DoesNotContain("UnpinnedId");
  }

  // A strong id declared outside any namespace is emitted as a top-level type, so the registry must name it
  // global::ProductId; a "global::.ProductId" entry would not compile.
  [Test]
  [RequiresAssemblyFiles]
  public async Task Registry_AStrongIdInTheGlobalNamespace_IsNamedWithoutANamespaceAsync() {
    var result = GeneratorTestHelper.RunGenerator<PinnedIdRegistryGenerator>(
      $"using Whizbang.Core;\n[WhizbangId(PinnedId = \"{STRUCT_PIN}\")] public readonly partial struct ProductId;");
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PinnedIdRegistry.g.cs");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains($"(typeof(global::ProductId), \"{STRUCT_PIN}\")");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Registry_ForAnIdDeclaredTwice_ListsItOnceAsync() {
    var result = GeneratorTestHelper.RunGenerator<PinnedIdRegistryGenerator>($$"""
      using Whizbang.Core;
      namespace App;
      public class CreateOrder { [WhizbangId(PinnedId = "{{PROPERTY_PIN}}")] public OrderId Id { get; set; } }
      public record UpdateOrder([WhizbangId(PinnedId = "{{PROPERTY_PIN}}")] OrderId Id);
      """);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PinnedIdRegistry.g.cs")!;

    await Assert.That(generated.Split("(typeof(global::App.OrderId)").Length - 1).IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task RenameAnalyzer_ALivingStrongId_ReportsNothingAsync() {
    var ledger = _ledger(
      (STRUCT_PIN, "App.ProductId", "strong-id"),
      (PROPERTY_PIN, "App.Ids.OrderId", "strong-id"),
      (PARAMETER_PIN, "App.LineId", "strong-id"));

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedTypeRenameAnalyzer>(THREE_ROUTES, [(LEDGER_PATH, ledger)]);

    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ119" or "WHIZ120" or "WHIZ121")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task RenameAnalyzer_ARenamedStrongId_ReportsWhiz120Async() {
    var ledger = _ledger((STRUCT_PIN, "App.ItemId", "strong-id"));

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedTypeRenameAnalyzer>(
      $"using Whizbang.Core;\nnamespace App;\n[WhizbangId(PinnedId = \"{STRUCT_PIN}\")] public readonly partial struct ProductId;",
      [(LEDGER_PATH, ledger)]);

    var matches = diagnostics.Where(d => d.Id == "WHIZ120").ToList();
    await Assert.That(matches).Count().IsEqualTo(1);
    await Assert.That(matches[0].GetMessage(CultureInfo.InvariantCulture)).Contains("App.ItemId");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task RenameAnalyzer_AStrongIdWhosePinChanged_ReportsWhiz119AsAnErrorAsync() {
    // The ledger knows the id under its old pin; the compilation pins it differently now
    var ledger = _ledger((STRUCT_PIN, "App.ProductId", "strong-id"), ("11111111-2222-3333-4444-555555555555", "App.Gone", "event"));

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedTypeRenameAnalyzer>(
      $"using Whizbang.Core;\nnamespace App;\n[WhizbangId(PinnedId = \"{PROPERTY_PIN}\")] public readonly partial struct ProductId;",
      [(LEDGER_PATH, ledger)]);

    var whiz119 = diagnostics.Where(d => d.Id == "WHIZ119").ToList();
    await Assert.That(whiz119).Count().IsEqualTo(1);
    await Assert.That(whiz119[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(whiz119[0].GetMessage(CultureInfo.InvariantCulture)).Contains(STRUCT_PIN);
    var whiz121 = diagnostics.Where(d => d.Id == "WHIZ121").ToList();
    await Assert.That(whiz121).Count().IsEqualTo(1)
      .Because("a message entry with no living type stays the warning it was");
    await Assert.That(whiz121[0].GetMessage(CultureInfo.InvariantCulture)).Contains("App.Gone");
  }

  private static string _ledger(params (string PinnedId, string ClrTypeName, string Kind)[] entries) {
    var types = string.Join(",\n", entries.Select(e =>
      $$"""    { "pinnedId": "{{e.PinnedId}}", "clrTypeName": "{{e.ClrTypeName}}", "kind": "{{e.Kind}}", "formerNames": [] }"""));
    return $$"""
      { "version": 1, "types": [
      {{types}}
      ] }
      """;
  }
}
