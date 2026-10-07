// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A pinned-type ledger the host cannot read (an <c>AdditionalText</c> whose text is null) is the
/// same as no ledger at all, for every generator and analyzer that consults one. It is not a
/// malformed ledger: there is nothing to warn about, and nothing to rename from.
/// </summary>
/// <tests>src/Whizbang.Generators/MessageJsonContextGenerator.cs</tests>
/// <tests>src/Whizbang.Generators/MessageTypeCatalogGenerator.cs</tests>
/// <tests>src/Whizbang.Generators/PinnedTypeLedgerGenerator.cs</tests>
/// <tests>src/Whizbang.Generators/Analyzers/PinnedTypeRenameAnalyzer.cs</tests>
[Category("SourceGenerators")]
public class UnreadableLedgerTests {
  private const string LEDGER_PATH = "/repo/src/TestAssembly/.whizbang/pinned-type-ledger.json";

  private const string SOURCE = """
    using Whizbang.Core;
    using Whizbang.Core.Attributes;

    namespace MyApp.Events;

    [PinnedId("11111111-2222-3333-4444-555555555555")]
    public record OrderPlacedEvent(string OrderId) : IEvent;
    """;

  private static string _generated(GeneratorDriverRunResult result) =>
    string.Join("\n", result.Results
      .SelectMany(r => r.GeneratedSources)
      .OrderBy(s => s.HintName, StringComparer.Ordinal)
      .Select(s => s.HintName + "\n" + s.SourceText));

  private static async Task _assertSameAsNoLedgerAsync<TGenerator>() where TGenerator : IIncrementalGenerator, new() {
    var withoutLedger = _generated(GeneratorTestHelper.RunGenerator<TGenerator>(SOURCE));
    var withUnreadableLedger = _generated(GeneratorTestHelper.RunGenerator<TGenerator>(SOURCE, [(LEDGER_PATH, null)]));

    await Assert.That(withoutLedger).IsNotEmpty()
      .Because("the comparison means nothing if the generator emitted nothing either way");
    await Assert.That(withUnreadableLedger).IsEqualTo(withoutLedger);
  }

  [Test]
  public Task MessageJsonContext_UnreadableLedger_GeneratesAsWithoutOneAsync() =>
    _assertSameAsNoLedgerAsync<MessageJsonContextGenerator>();

  [Test]
  public Task MessageTypeCatalog_UnreadableLedger_GeneratesAsWithoutOneAsync() =>
    _assertSameAsNoLedgerAsync<MessageTypeCatalogGenerator>();

  [Test]
  public Task PinnedTypeLedger_UnreadableLedger_GeneratesAsWithoutOneAsync() =>
    _assertSameAsNoLedgerAsync<PinnedTypeLedgerGenerator>();

  /// <summary>
  /// The rename analyzer warns about a ledger it cannot parse (WHIZ122), because a broken ledger
  /// silently turns governance off. One it cannot read at all is treated as absent instead.
  /// </summary>
  [Test]
  public async Task PinnedTypeRenameAnalyzer_UnreadableLedger_IsInertWithoutWarningAsync() {
    var unreadable = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedTypeRenameAnalyzer>(SOURCE, [(LEDGER_PATH, null)]);
    var malformed = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedTypeRenameAnalyzer>(SOURCE, [(LEDGER_PATH, "{ not json")]);

    await Assert.That(malformed.Select(d => d.Id)).Contains("WHIZ122")
      .Because("the control: text that is present but unparseable is warned about");
    await Assert.That(unreadable).IsEmpty();
  }
}
