using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// Tests for PinnedTypeRenameAnalyzer (WHIZ120/121) — the rename-management governance gate.
/// Diffs the compiled [PinnedId] types against the committed pinned-type ledger.
/// </summary>
[Category("Analyzers")]
public class PinnedTypeRenameAnalyzerTests {
  private const string LEDGER_PATH = "/repo/src/MyApp/.whizbang/pinned-type-ledger.json";
  private const string PINNED_ID = "11111111-2222-3333-4444-555555555555";

  private const string RENAMED_SOURCE = """
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace TestApp;
      [PinnedId("11111111-2222-3333-4444-555555555555")]
      public record OrderPlacedEvent : IEvent;
      """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task Rename_NotAcknowledgedInLedger_ReportsWhiz120Async() {
    // Ledger records the SAME pinned id under a DIFFERENT (old) name with no former-name alias.
    // The compiled type is now TestApp.OrderPlacedEvent -> unacknowledged rename.
    var ledger = _ledger(PINNED_ID, currentName: "TestApp.OrderCreatedEvent");

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedTypeRenameAnalyzer>(
      RENAMED_SOURCE, [(LEDGER_PATH, ledger)]);

    var matches = diagnostics.Where(d => d.Id == "WHIZ120").ToList();
    await Assert.That(matches).Count().IsEqualTo(1);
    await Assert.That(matches[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    var msg = matches[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(msg).Contains("TestApp.OrderPlacedEvent");   // current name
    await Assert.That(msg).Contains("TestApp.OrderCreatedEvent");  // ledger's recorded name
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Rename_AcknowledgedAsFormerName_NoDiagnosticAsync() {
    // Post-code-fix state: ledger current == the compiled name; the old name is recorded as former.
    var ledger = _ledger(PINNED_ID, currentName: "TestApp.OrderPlacedEvent", formerNames: "TestApp.OrderCreatedEvent");

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedTypeRenameAnalyzer>(
      RENAMED_SOURCE, [(LEDGER_PATH, ledger)]);

    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ120" or "WHIZ121")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Name_UnchangedFromLedger_NoDiagnosticAsync() {
    var ledger = _ledger(PINNED_ID, currentName: "TestApp.OrderPlacedEvent");

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedTypeRenameAnalyzer>(
      RENAMED_SOURCE, [(LEDGER_PATH, ledger)]);

    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ120" or "WHIZ121")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task NoLedgerSupplied_AnalyzerInert_NoDiagnosticAsync() {
    // Opt-in: without a committed ledger the analyzer reports nothing, even for a would-be rename.
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedTypeRenameAnalyzer>(RENAMED_SOURCE);

    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ120" or "WHIZ121")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task LedgerEntryWithNoLivingType_ReportsWhiz121Async() {
    // The ledger references a pinned id that no [PinnedId] type in the compilation carries.
    var ledger = _ledger("99999999-9999-9999-9999-999999999999", currentName: "TestApp.DeletedEvent");

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedTypeRenameAnalyzer>(
      RENAMED_SOURCE, [(LEDGER_PATH, ledger)]);

    var matches = diagnostics.Where(d => d.Id == "WHIZ121").ToList();
    await Assert.That(matches).Count().IsEqualTo(1);
    await Assert.That(matches[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    // ...and no false WHIZ120 for the living OrderPlacedEvent (it simply isn't in the ledger).
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ120")).IsEmpty();
  }

  private static string _ledger(string pinnedId, string currentName, string? formerNames = null) {
    var former = formerNames is null ? "" : $"\"{formerNames}\"";
    return $$"""
      { "version": 1, "types": [
        { "pinnedId": "{{pinnedId}}", "clrTypeName": "{{currentName}}", "kind": "event", "formerNames": [{{former}}] }
      ] }
      """;
  }
}
