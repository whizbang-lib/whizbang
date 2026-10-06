// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests;

/// <summary>
/// WHIZ120 names a renamed pinned type by its simple name, which for a type in the global namespace
/// is its whole name: there is no namespace or containing type to strip.
/// </summary>
/// <tests>src/Whizbang.Generators/Analyzers/PinnedTypeRenameAnalyzer.cs</tests>
public class PinnedTypeRenameAnalyzerBranchTests {
  private const string LEDGER_PATH = "/repo/src/MyApp/.whizbang/pinned-type-ledger.json";

  [Test]
  [RequiresAssemblyFiles]
  public async Task RenamedGlobalNamespaceType_IsNamedWholeAsync() {
    const string source = """
      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      [PinnedId("77777777-7777-7777-7777-777777777777")]
      public record ShipmentDispatched : IEvent;
      """;
    const string ledger = """
      { "version": 1, "types": [
        { "pinnedId": "77777777-7777-7777-7777-777777777777", "clrTypeName": "ShipmentSent", "kind": "event", "formerNames": [] }
      ] }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedTypeRenameAnalyzer>(source, [(LEDGER_PATH, ledger)]);
    var whiz120 = diagnostics.Where(d => d.Id == "WHIZ120").ToList();

    await Assert.That(whiz120).Count().IsEqualTo(1);
    await Assert.That(whiz120[0].GetMessage(CultureInfo.InvariantCulture)).Contains("ShipmentDispatched");
  }
}
