using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Roslyn analyzer enforcing <c>[PinnedId]</c> on concrete IMessage and IPerspectiveFor&lt;&gt; types.
/// </summary>
/// <remarks>
/// <para>
/// Reports WHIZ100 (warning) when a concrete IEvent / ICommand is missing <c>[PinnedId]</c>,
/// WHIZ101 (warning) for the perspective variant, and WHIZ102 (error) when the attribute value
/// is not a parseable GUID.
/// </para>
/// <para>
/// Abstract types, interfaces, and non-message types are skipped so adoption can be gradual.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/pinned-type-ledger</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/PinnedIdAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class PinnedIdAnalyzer : DiagnosticAnalyzer {
  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
    DiagnosticDescriptors.MessageMissingPinnedId,
    DiagnosticDescriptors.PerspectiveMissingPinnedId,
    DiagnosticDescriptors.PinnedIdNotAValidGuid,
  ];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyzeNamedType, SymbolKind.NamedType);
  }

  private static void _analyzeNamedType(SymbolAnalysisContext context) {
    var typeSymbol = (INamedTypeSymbol)context.Symbol;

    // Skip abstract types, interfaces, and anything that isn't a concrete Class / Struct / Record / Record-Struct.
    if (typeSymbol.IsAbstract) {
      return;
    }
    if (typeSymbol.TypeKind is not (TypeKind.Class or TypeKind.Struct)) {
      return;
    }

    var isMessage =
        TypeNameHelper.ImplementsInterface(typeSymbol, StandardInterfaceNames.I_EVENT) ||
        TypeNameHelper.ImplementsInterface(typeSymbol, StandardInterfaceNames.I_COMMAND) ||
        TypeNameHelper.ImplementsInterface(typeSymbol, StandardInterfaceNames.I_MESSAGE);

    var isPerspective = TypeNameHelper.ImplementsGenericInterface(
        typeSymbol,
        StandardInterfaceNames.I_PERSPECTIVE_FOR) ||
      TypeNameHelper.ImplementsGenericInterface(
        typeSymbol,
        StandardInterfaceNames.I_PERSPECTIVE_WITH_ACTIONS_FOR);

    if (!isMessage && !isPerspective) {
      return;
    }

    var pinnedIdAttribute = typeSymbol.GetAttributes().FirstOrDefault(attr =>
        attr.AttributeClass is not null &&
        TypeNameHelper.GetFullyQualifiedName(attr.AttributeClass) == StandardInterfaceNames.PINNED_ID_ATTRIBUTE);

    var location = typeSymbol.Locations.FirstOrDefault() ?? Location.None;

    if (pinnedIdAttribute is null) {
      if (isPerspective) {
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.PerspectiveMissingPinnedId,
            location,
            typeSymbol.Name));
      } else {
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.MessageMissingPinnedId,
            location,
            typeSymbol.Name));
      }
      return;
    }

    // Attribute is present — validate the GUID value.
    if (pinnedIdAttribute.ConstructorArguments.Length == 0 ||
        pinnedIdAttribute.ConstructorArguments[0].Value is not string pinnedIdValue) {
      return;
    }

    if (!Guid.TryParse(pinnedIdValue, out _)) {
      var attributeLocation = pinnedIdAttribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? location;
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.PinnedIdNotAValidGuid,
          attributeLocation,
          pinnedIdValue,
          typeSymbol.Name));
    }
  }
}
