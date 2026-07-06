using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Ledger;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Emits the committable pinned-type ledger (<c>.whizbang/pinned-type-ledger.json</c>) — the rename-platform lockfile.
/// Discovers every <c>[PinnedId]</c> message/perspective type in the assembly, MERGES it with the existing committed
/// ledger (supplied via AdditionalFiles), and emits the merged JSON as a C# constant in <c>PinnedTypeLedger.g.cs</c>.
/// An MSBuild target extracts that constant to the on-disk ledger (mirroring the message-registry extraction), so the
/// ledger bootstraps itself on first build and grows monotonically as new pinned types appear.
/// </summary>
/// <remarks>
/// The merge NEVER refreshes an existing entry's <c>clrTypeName</c>, so a rename still trips WHIZ120 until acknowledged;
/// it only ADDS newly-introduced pinned ids (empty <c>formerNames</c>). This keeps every pinned type governed from birth.
/// </remarks>
/// <tests>tests/Whizbang.Generators.Tests/PinnedTypeLedgerGeneratorTests.cs</tests>
/// <docs>fundamentals/identity/pinned-type-ledger</docs>
[Generator]
public class PinnedTypeLedgerGenerator : IIncrementalGenerator {
  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover every [PinnedId] message/perspective type in this assembly.
    var pinnedTypes = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is TypeDeclarationSyntax { AttributeLists.Count: > 0 },
        transform: static (ctx, ct) => _tryDiscover(ctx, ct))
      .Where(static t => t is not null)
      .Select(static (t, _) => t!)
      .Collect();

    // Read the existing committed ledger (if any) supplied via AdditionalFiles.
    var existingLedger = context.AdditionalTextsProvider
        .Where(static f => PinnedTypeLedger.IsLedgerPath(f.Path))
        .Select(static (f, ct) => f.GetText(ct)?.ToString())
        .Collect();

    context.RegisterSourceOutput(pinnedTypes.Combine(existingLedger),
        static (ctx, data) => _emit(ctx, data.Left, data.Right));
  }

  /// <summary>Discovers a single <c>[PinnedId]</c> message/perspective type, or null if the node is neither.</summary>
  private static DiscoveredPinnedType? _tryDiscover(GeneratorSyntaxContext context, CancellationToken ct) {
    if (context.Node is not TypeDeclarationSyntax typeDeclaration) {
      return null;
    }
    if (context.SemanticModel.GetDeclaredSymbol(typeDeclaration, ct) is not INamedTypeSymbol type ||
        type.IsAbstract ||
        type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) {
      return null;
    }

    string? kind = null;
    if (TypeNameHelper.ImplementsInterface(type, StandardInterfaceNames.I_COMMAND)) {
      kind = "command";
    } else if (TypeNameHelper.ImplementsInterface(type, StandardInterfaceNames.I_EVENT)) {
      kind = "event";
    } else if (TypeNameHelper.ImplementsGenericInterface(type, StandardInterfaceNames.I_PERSPECTIVE_FOR) ||
               TypeNameHelper.ImplementsGenericInterface(type, StandardInterfaceNames.I_PERSPECTIVE_WITH_ACTIONS_FOR)) {
      kind = "perspective";
    } else if (TypeNameHelper.ImplementsInterface(type, StandardInterfaceNames.I_MESSAGE)) {
      kind = "message";
    }
    if (kind is null) {
      return null;
    }

    var pinnedIdAttribute = type.GetAttributes().FirstOrDefault(attr =>
      attr.AttributeClass is not null &&
      TypeNameHelper.GetFullyQualifiedName(attr.AttributeClass) == StandardInterfaceNames.PINNED_ID_ATTRIBUTE);
    if (pinnedIdAttribute is null ||
        pinnedIdAttribute.ConstructorArguments.Length == 0 ||
        pinnedIdAttribute.ConstructorArguments[0].Value is not string idValue ||
        string.IsNullOrWhiteSpace(idValue)) {
      return null;
    }

    return new DiscoveredPinnedType(idValue, TypeNameUtilities.BuildClrTypeName(type), kind);
  }

  /// <summary>Merges discovered types with the committed ledger and emits the JSON-bearing C# constant.</summary>
  private static void _emit(
      SourceProductionContext context,
      ImmutableArray<DiscoveredPinnedType> discovered,
      ImmutableArray<string?> ledgerTexts) {

    if (discovered.IsDefaultOrEmpty) {
      return; // no pinned types → don't materialize a ledger for this assembly
    }

    // De-dupe by pinned id (partial types / multiple declarations of the same symbol).
    var byPinnedId = new Dictionary<string, DiscoveredPinnedType>(System.StringComparer.OrdinalIgnoreCase);
    foreach (var type in discovered) {
      byPinnedId[type.PinnedId] = type;
    }

    var existingJson = ledgerTexts.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
    var existing = PinnedTypeLedger.TryParse(existingJson);
    var merged = PinnedTypeLedger.Merge(existing, byPinnedId.Values);

    context.AddSource("PinnedTypeLedger.g.cs", _wrap(merged.ToJson()));
  }

  /// <summary>
  /// Wraps the ledger JSON in a C# constant using the exact <c>internal const string Json = @"…"</c> marker the
  /// MSBuild extraction target keys on (verbatim string; embedded quotes doubled).
  /// </summary>
  private static string _wrap(string json) {
    var escaped = json.Replace("\"", "\"\"");
    return
      "// <auto-generated/>\n" +
      "// Generated by Whizbang.Generators.PinnedTypeLedgerGenerator\n" +
      "#nullable enable\n\n" +
      "namespace Whizbang.Generated;\n\n" +
      "/// <summary>\n" +
      "/// Auto-generated pinned-type ledger (rename platform). An MSBuild target extracts <c>Json</c> to\n" +
      "/// .whizbang/pinned-type-ledger.json — the committed lockfile consumed by the analyzer + generators.\n" +
      "/// </summary>\n" +
      "internal static class PinnedTypeLedgerSource {\n" +
      "  internal const string Json = @\"" + escaped + "\";\n" +
      "}\n";
  }
}
