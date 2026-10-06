// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The ledger shapes that parse to no ledger (a JSON <c>null</c>, a <c>types</c> of <c>null</c>), an
/// entry written without <c>formerNames</c>, and a ledger file named without any directory.
/// </summary>
/// <tests>src/Whizbang.Generators/Ledger/PinnedTypeLedger.cs</tests>
public class PinnedTypeLedgerShapeTests {
  private const string SOURCE = """
    using Whizbang.Core;
    using Whizbang.Core.Attributes;

    namespace MyApp.Events;

    [PinnedId("11111111-2222-3333-4444-555555555555")]
    public record OrderPlacedEvent(string OrderId) : IEvent;
    """;

  private static string _generated(GeneratorDriverRunResult result) =>
    string.Join("\n", result.Results.SelectMany(r => r.GeneratedSources)
      .OrderBy(s => s.HintName, StringComparer.Ordinal)
      .Select(s => s.HintName + "\n" + s.SourceText));

  /// <summary>A ledger whose JSON is <c>null</c>, or whose types are, records nothing to rename from.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("null")]
  [Arguments("{ \"version\": 1, \"types\": null }")]
  public async Task LedgerWithNoTypes_GeneratesAsWithoutOneAsync(string ledger) {
    var without = _generated(GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(SOURCE));
    var with = _generated(GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(
      SOURCE, [("/repo/src/MyApp/.whizbang/pinned-type-ledger.json", ledger)]));

    await Assert.That(without).IsNotEmpty();
    await Assert.That(with).IsEqualTo(without);
  }

  /// <summary>
  /// An entry written without <c>formerNames</c> acknowledges no former name, so a rename of its
  /// type is still reported; and a ledger file named with no directory at all is still the ledger.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task EntryWithoutFormerNames_AtABarePath_StillGovernsRenamesAsync() {
    const string ledger = """
      { "version": 1, "types": [
        { "pinnedId": "11111111-2222-3333-4444-555555555555", "clrTypeName": "MyApp.Events.OrderCreatedEvent", "kind": "event", "formerNames": null }
      ] }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedTypeRenameAnalyzer>(SOURCE, [("pinned-type-ledger.json", ledger)]);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ120")).IsEqualTo(1);
  }
}
