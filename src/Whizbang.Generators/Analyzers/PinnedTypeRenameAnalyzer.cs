using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Whizbang.Generators.Ledger;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Governance gate for the rename-management platform. Diffs the compiled set of <c>[PinnedId]</c> types
/// (pinned id → current CLR name) against the committed pinned-type ledger and reports:
/// <list type="bullet">
/// <item>WHIZ120 (error) — a pinned type whose current CLR name is neither the ledger's recorded name nor one of
/// its former names: an un-acknowledged RENAME. The code fix records the former name so old events still resolve.</item>
/// <item>WHIZ121 (warning) — a ledger entry whose pinned id has no living type: a removed type or changed id.</item>
/// </list>
/// The analyzer is INERT when no <c>.whizbang/pinned-type-ledger.json</c> is supplied via AdditionalFiles, so adoption
/// is opt-in per project.
/// </summary>
/// <docs>fundamentals/identity/pinned-type-ledger</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/PinnedTypeRenameAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class PinnedTypeRenameAnalyzer : DiagnosticAnalyzer {
  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
    DiagnosticDescriptors.PinnedTypeRenamedWithoutAcknowledgment,
    DiagnosticDescriptors.LedgerEntryHasNoLivingType,
    DiagnosticDescriptors.PinnedTypeLedgerMalformed,
  ];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterCompilationStartAction(_onCompilationStart);
  }

  private static void _onCompilationStart(CompilationStartAnalysisContext context) {
    var ledgerFile = context.Options.AdditionalFiles.FirstOrDefault(f => PinnedTypeLedger.IsLedgerPath(f.Path));
    if (ledgerFile is null) {
      return; // no committed ledger for this project — analyzer is inert (opt-in).
    }

    var ledgerText = ledgerFile.GetText(context.CancellationToken)?.ToString();
    var ledger = PinnedTypeLedger.TryParse(ledgerText, out var malformed);
    if (ledger is null) {
      if (malformed) {
        // Ledger file is present but unparseable → governance would silently go inert, letting an
        // un-acknowledged rename slip through. Surface WHIZ122 (warning, not error — a broken ledger
        // shouldn't fail the build) at compilation end so it composes with the other end-reported rules.
        var ledgerPath = ledgerFile.Path;
        context.RegisterCompilationEndAction(endContext =>
          endContext.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.PinnedTypeLedgerMalformed, Location.None, ledgerPath)));
      }
      return; // no usable baseline — governance inert (absent or malformed ledger).
    }

    // pinned id -> (current clr name, location) for every living [PinnedId] message/perspective type.
    var living = new ConcurrentDictionary<string, LivingPinnedType>(System.StringComparer.OrdinalIgnoreCase);

    context.RegisterSymbolAction(symbolContext => {
      if (_tryGetPinned(symbolContext.Symbol, out var pinnedId, out var clrName, out var location)) {
        living[pinnedId] = new LivingPinnedType(clrName, location);
      }
    }, SymbolKind.NamedType);

    context.RegisterCompilationEndAction(endContext => {
      // WHIZ120 — a living pinned type whose current name the ledger doesn't know (rename, unacknowledged).
      foreach (var kvp in living) {
        var entry = ledger.FindByPinnedId(kvp.Key);
        if (entry is not null && !entry.KnowsName(kvp.Value.ClrTypeName)) {
          endContext.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.PinnedTypeRenamedWithoutAcknowledgment,
            kvp.Value.Location,
            _simpleName(kvp.Value.ClrTypeName), kvp.Key, kvp.Value.ClrTypeName, entry.ClrTypeName));
        }
      }

      // WHIZ121 — a ledger entry with no living type of that pinned id.
      foreach (var entry in ledger.Types) {
        if (!living.ContainsKey(entry.PinnedId)) {
          endContext.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.LedgerEntryHasNoLivingType,
            Location.None,
            entry.PinnedId, entry.ClrTypeName));
        }
      }
    });
  }

  private static bool _tryGetPinned(ISymbol symbol, out string pinnedId, out string clrName, out Location location) {
    pinnedId = ""; clrName = ""; location = Location.None;
    if (symbol is not INamedTypeSymbol typeSymbol || typeSymbol.IsAbstract) {
      return false;
    }
    if (typeSymbol.TypeKind is not (TypeKind.Class or TypeKind.Struct)) {
      return false;
    }

    var isMessage =
      TypeNameHelper.ImplementsInterface(typeSymbol, StandardInterfaceNames.I_EVENT) ||
      TypeNameHelper.ImplementsInterface(typeSymbol, StandardInterfaceNames.I_COMMAND) ||
      TypeNameHelper.ImplementsInterface(typeSymbol, StandardInterfaceNames.I_MESSAGE);
    var isPerspective =
      TypeNameHelper.ImplementsGenericInterface(typeSymbol, StandardInterfaceNames.I_PERSPECTIVE_FOR) ||
      TypeNameHelper.ImplementsGenericInterface(typeSymbol, StandardInterfaceNames.I_PERSPECTIVE_WITH_ACTIONS_FOR);
    if (!isMessage && !isPerspective) {
      return false;
    }

    var pinnedIdAttribute = typeSymbol.GetAttributes().FirstOrDefault(attr =>
      attr.AttributeClass is not null &&
      TypeNameHelper.GetFullyQualifiedName(attr.AttributeClass) == StandardInterfaceNames.PINNED_ID_ATTRIBUTE);
    if (pinnedIdAttribute is null ||
        pinnedIdAttribute.ConstructorArguments.Length == 0 ||
        pinnedIdAttribute.ConstructorArguments[0].Value is not string idValue ||
        string.IsNullOrWhiteSpace(idValue)) {
      return false;
    }

    pinnedId = idValue;
    clrName = TypeNameUtilities.BuildClrTypeName(typeSymbol); // '+'-nested CLR name — same form the catalog stores.
    location = typeSymbol.Locations.FirstOrDefault() ?? Location.None;
    return true;
  }

  private static string _simpleName(string clrName) {
    var lastSep = clrName.LastIndexOfAny(['+', '.']);
    return lastSep >= 0 ? clrName.Substring(lastSep + 1) : clrName;
  }

  private readonly struct LivingPinnedType(string clrTypeName, Location location) {
    public string ClrTypeName { get; } = clrTypeName;
    public Location Location { get; } = location;
  }
}
