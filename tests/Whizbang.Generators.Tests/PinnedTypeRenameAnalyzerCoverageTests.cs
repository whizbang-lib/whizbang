using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for <see cref="PinnedTypeRenameAnalyzer"/> (WHIZ120/121/122),
/// complementing <c>tests/Whizbang.Generators.Tests/Analyzers/PinnedTypeRenameAnalyzerTests.cs</c>.
/// These target the four ways <c>_tryGetPinned</c> can decide a symbol is NOT a living pinned type —
/// abstract, the wrong <see cref="Microsoft.CodeAnalysis.TypeKind"/>, not message/perspective-shaped,
/// and a blank pinned-id value — each proven via the WHIZ121 ("ledger entry has no living type")
/// diagnostic that fires when the excluded symbol fails to satisfy an otherwise-matching ledger entry.
/// </summary>
public class PinnedTypeRenameAnalyzerCoverageTests {
  private const string LEDGER_PATH = "/repo/src/MyApp/.whizbang/pinned-type-ledger.json";

  // An abstract [PinnedId] type never becomes a "living" type (PinnedTypeRenameAnalyzer.cs:99-100):
  // if this guard regressed and an abstract base class counted as living, a ledger entry for a pinned
  // id whose only concrete implementor was actually removed could be masked — WHIZ121 would stay
  // silent about a genuinely orphaned pinned id.
  [Test]
  [RequiresAssemblyFiles]
  public async Task AbstractPinnedType_DoesNotCountAsLiving_ReportsWhiz121Async() {
    const string pinnedId = "11111111-2222-3333-4444-555555555555";
    const string source = """
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace TestApp;
      [PinnedId("11111111-2222-3333-4444-555555555555")]
      public abstract record AbstractPinnedEvent : IEvent;
      """;
    var ledger = _ledger(pinnedId, currentName: "TestApp.AbstractPinnedEvent");

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedTypeRenameAnalyzer>(
      source, [(LEDGER_PATH, ledger)]);

    var whiz121 = diagnostics.Where(d => d.Id == "WHIZ121").ToList();
    await Assert.That(whiz121.Count).IsEqualTo(1)
      .Because("an abstract [PinnedId] type must not count as a living type, or a genuinely orphaned ledger entry would be masked");
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ120")).IsEmpty();
  }

  // An unrelated enum in the same compilation is a NamedType (RegisterSymbolAction(SymbolKind.NamedType)
  // fires for it too) but its TypeKind is neither Class nor Struct (PinnedTypeRenameAnalyzer.cs:102-103).
  // If this guard were missing and the analyzer instead tried to treat every named type uniformly, an
  // ordinary enum sitting alongside a real pinned type could interfere with — or crash — governance for
  // the type that actually matters.
  [Test]
  [RequiresAssemblyFiles]
  public async Task EnumTypeInCompilation_IsIgnoredWithoutInterferingAsync() {
    const string pinnedId = "22222222-3333-4444-5555-666666666666";
    const string source = """
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace TestApp;
      public enum Color { Red, Green, Blue }
      [PinnedId("22222222-3333-4444-5555-666666666666")]
      public record RealPinnedEvent : IEvent;
      """;
    var ledger = _ledger(pinnedId, currentName: "TestApp.RealPinnedEvent");

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedTypeRenameAnalyzer>(
      source, [(LEDGER_PATH, ledger)]);

    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ120" or "WHIZ121")).IsEmpty()
      .Because("an unrelated enum in the compilation must not affect governance for the genuinely pinned, matching type");
  }

  // A [PinnedId] attribute on a type that implements neither a message nor a perspective interface
  // never becomes a "living" type (PinnedTypeRenameAnalyzer.cs:113-114) — e.g. a leftover attribute
  // from a refactor where the type lost its IEvent/ICommand base. If this guard regressed, that stray
  // attribute would silently satisfy the ledger entry, hiding the fact that the real pinned type is
  // gone.
  [Test]
  [RequiresAssemblyFiles]
  public async Task NonMessageTypeWithPinnedIdAttribute_DoesNotCountAsLivingAsync() {
    const string pinnedId = "33333333-4444-5555-6666-777777777777";
    const string source = """
      using Whizbang.Core.Attributes;
      namespace TestApp;
      [PinnedId("33333333-4444-5555-6666-777777777777")]
      public class StrayPin { }
      """;
    var ledger = _ledger(pinnedId, currentName: "TestApp.StrayPin");

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedTypeRenameAnalyzer>(
      source, [(LEDGER_PATH, ledger)]);

    var whiz121 = diagnostics.Where(d => d.Id == "WHIZ121").ToList();
    await Assert.That(whiz121.Count).IsEqualTo(1)
      .Because("a [PinnedId] on a type that implements neither a message nor a perspective interface must not satisfy governance");
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ120")).IsEmpty();
  }

  // A blank/whitespace-only [PinnedId] value never becomes a "living" type
  // (PinnedTypeRenameAnalyzer.cs:120-124) — mirroring the same exclusion applied by
  // PinnedIdRegistryGenerator and MessageTypeCatalogGenerator. If this guard regressed, a type whose
  // pinned id was accidentally cleared would still be treated as satisfying its ledger entry, hiding
  // a real loss of pinned identity.
  [Test]
  [RequiresAssemblyFiles]
  public async Task BlankPinnedIdValue_DoesNotCountAsLivingAsync() {
    const string pinnedId = "44444444-5555-6666-7777-888888888888";
    const string source = """
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace TestApp;
      [PinnedId("")]
      public record BlankPinnedEvent : IEvent;
      """;
    var ledger = _ledger(pinnedId, currentName: "TestApp.BlankPinnedEvent");

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PinnedTypeRenameAnalyzer>(
      source, [(LEDGER_PATH, ledger)]);

    var whiz121 = diagnostics.Where(d => d.Id == "WHIZ121").ToList();
    await Assert.That(whiz121.Count).IsEqualTo(1)
      .Because("a type whose [PinnedId] value is blank carries no usable identity and must not satisfy governance for any ledger entry");
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
