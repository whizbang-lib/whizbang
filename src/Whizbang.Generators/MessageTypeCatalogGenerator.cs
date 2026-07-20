using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Ledger;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Generates an AOT-safe <c>IMessageTypeCatalog</c> implementation listing every concrete
/// <c>IMessage</c> and <c>IPerspectiveFor&lt;&gt;</c> type in the consumer assembly with its
/// kind and optional pinned_id.
/// </summary>
/// <remarks>
/// Feeds <c>IMessageTypeRegistryPopulator</c> (Phase 3b) and the rename tool (Phase 5).
/// Independent of <c>PinnedIdRegistryGenerator</c> for cache isolation — pinned and
/// unpinned types both land in this catalog, so a change to one [PinnedId] here does
/// not invalidate the pinned-id-only registry's cache.
/// </remarks>
[Generator]
public class MessageTypeCatalogGenerator : IIncrementalGenerator {
  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    var entries = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) =>
            node is ClassDeclarationSyntax
                 or RecordDeclarationSyntax
                 or StructDeclarationSyntax,
        transform: static (ctx, ct) => _extractEntry(ctx, ct)
    ).Where(static info => info is not null);

    // Rename platform: read the committed .whizbang/pinned-type-ledger.json so each pinned entry can carry the
    // FORMER names the type has had. Empty when no ledger is present (opt-in), so the catalog is unchanged.
    var ledgerFormers = context.AdditionalTextsProvider
        .Where(static f => PinnedTypeLedger.IsLedgerPath(f.Path))
        .Select(static (f, ct) => _readPinnedFormerNames(f, ct))
        .SelectMany(static (a, _) => a)
        .Collect();

    var combined = context.CompilationProvider.Combine(entries.Collect()).Combine(ledgerFormers);

    context.RegisterSourceOutput(
        combined,
        static (ctx, data) => {
          var compilation = data.Left.Left;
          var infos = data.Left.Right;
          var formerNames = data.Right;
          _generateCatalog(ctx, compilation, infos!, formerNames);
        }
    );
  }

  /// <summary>Parses a ledger additional file into flattened (pinned id → former name) pairs. Empty on missing/blank/invalid.</summary>
  private static ImmutableArray<PinnedFormerName> _readPinnedFormerNames(
      AdditionalText file, System.Threading.CancellationToken ct) {
    var ledger = PinnedTypeLedger.TryParse(file.GetText(ct)?.ToString());
    return ledger is null
      ? ImmutableArray<PinnedFormerName>.Empty
      : ledger.ToPinnedFormerNames().ToImmutableArray();
  }

  private static MessageTypeCatalogEntryInfo? _extractEntry(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {
    var typeDeclaration = (TypeDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    if (semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) is not INamedTypeSymbol typeSymbol) {
      return null;
    }

    if (typeSymbol.IsAbstract) {
      return null;
    }

    if (typeSymbol.DeclaredAccessibility != Accessibility.Public) {
      return null;
    }

    var isEvent = TypeNameHelper.ImplementsInterface(typeSymbol, StandardInterfaceNames.I_EVENT);
    var isCommand = TypeNameHelper.ImplementsInterface(typeSymbol, StandardInterfaceNames.I_COMMAND);
    var isMessage = isEvent || isCommand || TypeNameHelper.ImplementsInterface(typeSymbol, StandardInterfaceNames.I_MESSAGE);

    var isPerspective = TypeNameHelper.ImplementsGenericInterface(
        typeSymbol,
        StandardInterfaceNames.I_PERSPECTIVE_FOR) ||
      TypeNameHelper.ImplementsGenericInterface(
        typeSymbol,
        StandardInterfaceNames.I_PERSPECTIVE_WITH_ACTIONS_FOR);

    if (!isMessage && !isPerspective) {
      return null;
    }

    var fullTypeName = TypeNameHelper.GetFullyQualifiedName(typeSymbol);
    // CLR type name (no assembly, '+' for nested types) — the SAME shared builder used by
    // PerspectiveDiscoveryGenerator / PerspectiveRunnerRegistryGenerator for perspective
    // clr_type_name, so a nested MESSAGE type registers as "Ns.Outer+Nested" and matches the
    // registry/rename-tool comparison instead of the C# '.' display form.
    var clrTypeName = TypeNameUtilities.BuildClrTypeName(typeSymbol);

    var messageKind = isCommand ? "command" : "event";
    var kind = isPerspective ? "perspective" : messageKind;

    var pinnedIdAttribute = typeSymbol.GetAttributes().FirstOrDefault(attr =>
        attr.AttributeClass is not null &&
        TypeNameHelper.GetFullyQualifiedName(attr.AttributeClass) == StandardInterfaceNames.PINNED_ID_ATTRIBUTE);

    string? pinnedId = null;
    if (pinnedIdAttribute is not null &&
        pinnedIdAttribute.ConstructorArguments.Length > 0 &&
        pinnedIdAttribute.ConstructorArguments[0].Value is string pinnedIdValue &&
        !string.IsNullOrWhiteSpace(pinnedIdValue)) {
      pinnedId = pinnedIdValue;
    }

    // Ephemeral mode is an EVENT property. Commands are never ephemeral, and a perspective's effective
    // mode is DERIVED from the events it applies (a separate step), not read from an attribute here.
    // EphemeralResolver is the single source of truth shared with the analyzer.
    (string Destruction, string Storage)? ephemeral = kind == "event" ? EphemeralResolver.Resolve(typeSymbol) : null;
    var rewindGrace = kind == "event" ? EphemeralResolver.ResolveRewindGraceSeconds(typeSymbol) : -1;
    var ttlSeconds = kind == "event" ? EphemeralResolver.ResolveTtlSeconds(typeSymbol) : -1;

    return new MessageTypeCatalogEntryInfo(
        TypeName: fullTypeName,
        ClrTypeName: clrTypeName,
        Kind: kind,
        PinnedId: pinnedId,
        EphemeralDestruction: ephemeral?.Destruction,
        EphemeralStorage: ephemeral?.Storage,
        EphemeralRewindGraceSeconds: rewindGrace,
        EphemeralTtlSeconds: ttlSeconds,
        SettingsHash: _computeSettingsHash(ephemeral),
        SchemaHash: _computeSchemaHash(typeSymbol)
    );
  }

  // ── Type-definition fingerprint (F-3) ────────────────────────────────────────────────────────────
  // Deterministic per-type content hashes stamped onto the catalog entry so the startup reconciler can
  // diff the code's current definition against wh_type_definitions and detect drift. Determinism is
  // load-bearing: identical definitions MUST hash identically across builds/machines, so every input is
  // canonically ordered and fully-qualified.

  /// <summary>Hash of the type's behavioral settings — its Sourced/Ephemeral mode + destruction/storage.</summary>
  private static string _computeSettingsHash((string Destruction, string Storage)? ephemeral) {
    var canonical = ephemeral is { } e
      ? $"ephemeral;destruction={e.Destruction};storage={e.Storage}"
      : "sourced";
    return _sha256Hex(canonical);
  }

  /// <summary>
  /// Hash of the payload schema: a canonical, ORDERED signature of the type's serializable properties
  /// (name + fully-qualified declared type), ONE level deep. One level keeps it incremental-safe — the
  /// hash depends only on the type's own declaration, so a sibling type's internal change can't leave it
  /// stale — while still catching the common changes (property added / removed / renamed / retyped). Deep
  /// nested-DTO evolution is the job of explicit <c>[SchemaVersion]</c>.
  /// </summary>
  private static string _computeSchemaHash(INamedTypeSymbol type) {
    var props = _serializableProperties(type)
      .Select(p => p.Name + ":" + p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
      .OrderBy(s => s, System.StringComparer.Ordinal);
    return _sha256Hex("{" + string.Join(",", props) + "}");
  }

  /// <summary>
  /// Public instance properties with a public getter, across the type and its base types, deduped by name
  /// (derived wins). Excludes indexers and the compiler-generated record <c>EqualityContract</c>.
  /// </summary>
  private static IEnumerable<IPropertySymbol> _serializableProperties(INamedTypeSymbol type) {
    var seen = new HashSet<string>(System.StringComparer.Ordinal);
    for (INamedTypeSymbol? t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType) {
      foreach (var member in t.GetMembers().OfType<IPropertySymbol>()) {
        if (member.IsStatic || member.IsIndexer) {
          continue;
        }
        if (member.DeclaredAccessibility != Accessibility.Public) {
          continue;
        }
        if (member.GetMethod is null || member.GetMethod.DeclaredAccessibility != Accessibility.Public) {
          continue;
        }
        if (member.Name == "EqualityContract") {
          continue;
        }
        if (seen.Add(member.Name)) {
          yield return member;
        }
      }
    }
  }

  private static string _sha256Hex(string input) {
    using var sha = System.Security.Cryptography.SHA256.Create();
    var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
    var sb = new StringBuilder(bytes.Length * 2);
    foreach (var b in bytes) {
      sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
    }
    return sb.ToString();
  }

  private static void _generateCatalog(
      SourceProductionContext context,
      Compilation compilation,
      ImmutableArray<MessageTypeCatalogEntryInfo> infos,
      ImmutableArray<PinnedFormerName> formerNames) {

    if (infos.IsEmpty) {
      return;
    }

    var assemblyName = compilation.AssemblyName ?? "UnknownAssembly";
    var namespaceName = $"{assemblyName}.Generated";

    // pinned id -> distinct former names, from the committed ledger.
    var formerByPinnedId = new Dictionary<string, List<string>>(System.StringComparer.OrdinalIgnoreCase);
    foreach (var pf in formerNames) {
      if (!formerByPinnedId.TryGetValue(pf.PinnedId, out var list)) {
        list = new List<string>();
        formerByPinnedId[pf.PinnedId] = list;
      }
      if (!list.Contains(pf.FormerClrTypeName)) {
        list.Add(pf.FormerClrTypeName);
      }
    }

    var ordered = infos.OrderBy(i => i.TypeName, System.StringComparer.Ordinal).ToList();

    var source = new StringBuilder();
    source.AppendLine("// <auto-generated/>");
    source.AppendLine($"// Generated by MessageTypeCatalogGenerator at {System.DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
    source.AppendLine("// DO NOT EDIT - Changes will be overwritten");
    source.AppendLine("#nullable enable");
    source.AppendLine();
    source.AppendLine("using System;");
    source.AppendLine("using System.Collections.Generic;");
    source.AppendLine("using Microsoft.Extensions.DependencyInjection;");
    source.AppendLine("using Whizbang.Core;");
    source.AppendLine();
    source.AppendLine($"namespace {namespaceName};");
    source.AppendLine();

    source.AppendLine("/// <summary>");
    source.AppendLine($"/// Auto-generated catalog of {ordered.Count} concrete IMessage / IPerspectiveFor&lt;&gt; type(s).");
    source.AppendLine("/// Provides zero-reflection enumeration for the type registry populator and rename tool.");
    source.AppendLine("/// </summary>");
    source.AppendLine("public sealed class GeneratedMessageTypeCatalog : IMessageTypeCatalog {");
    source.AppendLine();

    source.AppendLine("  private static readonly IReadOnlyList<MessageTypeCatalogEntry> _all = new MessageTypeCatalogEntry[] {");
    foreach (var info in ordered) {
      var pinnedIdLiteral = info.PinnedId is null ? "null" : $"\"{info.PinnedId}\"";
      var initParts = new List<string>();
      if (info.PinnedId is not null &&
          formerByPinnedId.TryGetValue(info.PinnedId, out var formers) &&
          formers.Count > 0) {
        var arr = string.Join(", ", formers.Select(f => $"\"{f}\""));
        initParts.Add($"FormerNames = new string[] {{ {arr} }}");
      }
      if (info.EphemeralDestruction is not null) {
        initParts.Add(
          "Ephemeral = new global::Whizbang.Core.Attributes.EphemeralInfo(" +
          $"global::Whizbang.Core.Attributes.Destruction.{info.EphemeralDestruction}, " +
          $"global::Whizbang.Core.Attributes.TransientStorage.{info.EphemeralStorage}, " +
          $"{info.EphemeralRewindGraceSeconds}, " +
          $"{info.EphemeralTtlSeconds})");
      }
      // Type-definition fingerprint (F-3): every entry carries its deterministic settings + schema hashes.
      initParts.Add($"SettingsHash = \"{info.SettingsHash}\"");
      initParts.Add($"SchemaHash = \"{info.SchemaHash}\"");
      var initializer = initParts.Count > 0 ? $" {{ {string.Join(", ", initParts)} }}" : "";
      source.AppendLine($"    new(typeof({info.TypeName}), \"{info.ClrTypeName}\", \"{info.Kind}\", {pinnedIdLiteral}){initializer},");
    }
    source.AppendLine("  };");
    source.AppendLine();

    source.AppendLine("  /// <inheritdoc/>");
    source.AppendLine("  public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => _all;");
    source.AppendLine("}");
    source.AppendLine();

    source.AppendLine("/// <summary>");
    source.AppendLine("/// Module initializer that wires GeneratedMessageTypeCatalog into AddWhizbang().");
    source.AppendLine("/// </summary>");
    source.AppendLine("internal static class MessageTypeCatalogModuleInitializer {");
    source.AppendLine("  [System.Runtime.CompilerServices.ModuleInitializer]");
    source.AppendLine("  internal static void Initialize() {");
    source.AppendLine("    ServiceRegistrationCallbacks.MessageTypeCatalog = static services =>");
    source.AppendLine("      services.AddSingleton<IMessageTypeCatalog, GeneratedMessageTypeCatalog>();");
    source.AppendLine("  }");
    source.AppendLine("}");

    context.AddSource("MessageTypeCatalog.g.cs", source.ToString());
  }
}

/// <summary>
/// Value type containing information about a discovered catalog entry.
/// Uses value equality for incremental generator caching.
/// </summary>
internal sealed record MessageTypeCatalogEntryInfo(
    string TypeName,
    string ClrTypeName,
    string Kind,
    string? PinnedId,
    string? EphemeralDestruction = null,
    string? EphemeralStorage = null,
    int EphemeralRewindGraceSeconds = -1,
    int EphemeralTtlSeconds = -1,
    string SettingsHash = "",
    string SchemaHash = ""
);
