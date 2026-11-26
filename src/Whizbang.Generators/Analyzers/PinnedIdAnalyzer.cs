// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Roslyn analyzer enforcing <c>[PinnedId]</c> on concrete IMessage and IPerspectiveFor&lt;&gt; types.
/// </summary>
/// <remarks>
/// <para>
/// Reports WHIZ110 (warning) when a concrete IEvent / ICommand is missing <c>[PinnedId]</c>,
/// WHIZ111 (warning) for the perspective variant, and WHIZ112 (error) when the attribute value
/// is not a parseable GUID. A strong id's pin (<c>[PinnedId]</c> on a <c>[WhizbangId]</c> struct, or
/// <c>[WhizbangId(PinnedId = ...)]</c>) is checked the same way, and WHIZ123 (error) reports a struct pinned two ways
/// (#2604). A strong id is never required to be pinned here: WHIZ118 requires it where <c>DeriveFrom</c> is called.
/// </para>
/// <para>
/// Abstract types, interfaces, and non-message types are skipped so adoption can be gradual.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/pinned-type-ledger</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/PinnedIdAnalyzerTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/PinnedIdAnalyzerStrongIdTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class PinnedIdAnalyzer : DiagnosticAnalyzer {
  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
    DiagnosticDescriptors.MessageMissingPinnedId,
    DiagnosticDescriptors.PerspectiveMissingPinnedId,
    DiagnosticDescriptors.PinnedIdNotAValidGuid,
    DiagnosticDescriptors.WhizbangIdPinnedIdDisagrees,
  ];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyzeNamedType, SymbolKind.NamedType);
  }

  private static void _analyzeNamedType(SymbolAnalysisContext context) {
    var typeSymbol = (INamedTypeSymbol)context.Symbol;

    // A strong id's pin is checked wherever it is declared (#2604); a strong id is never required to be pinned
    _analyzeStrongIdPins(context, typeSymbol);

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
        TypeNameUtilities.IsFullyQualifiedNamed(attr.AttributeClass, StandardInterfaceNames.PINNED_ID_ATTRIBUTE));

    var location = LocationUtilities.FirstOrNone(typeSymbol);

    if (pinnedIdAttribute is null) {
      if (isPerspective) {
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.PerspectiveMissingPinnedId,
            location,
            TypeNameUtilities.SimpleName(typeSymbol)));
      } else {
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.MessageMissingPinnedId,
            location,
            TypeNameUtilities.SimpleName(typeSymbol)));
      }
      return;
    }

    // Attribute is present — validate the GUID value.
    if (pinnedIdAttribute.ConstructorArguments.Length == 0 ||
        pinnedIdAttribute.ConstructorArguments[0].Value is not string pinnedIdValue) {
      return;
    }

    if (!Guid.TryParse(pinnedIdValue, out _)) {
      var attributeLocation = LocationUtilities.ApplicationOrFallback(pinnedIdAttribute, location, context.CancellationToken);
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.PinnedIdNotAValidGuid,
          attributeLocation,
          pinnedIdValue,
          TypeNameUtilities.SimpleName(typeSymbol)));
    }
  }

  /// <summary>
  /// WHIZ112 for a strong id pin that is not a GUID, on the struct, a property or a parameter, and WHIZ123 for a struct
  /// pinned two different ways (#2604).
  /// </summary>
  private static void _analyzeStrongIdPins(SymbolAnalysisContext context, INamedTypeSymbol typeSymbol) {
    foreach (var declared in WhizbangIdDeclaration.ReadAll(typeSymbol, context.CancellationToken)) {
      if (declared.RawPinnedId is { } raw && declared.PinnedId is null) {
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.PinnedIdNotAValidGuid, declared.PinLocation, raw, declared.TypeName));
      }
      if (declared.ConflictingRawPinnedId is { } conflict) {
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.WhizbangIdPinnedIdDisagrees, declared.PinLocation, conflict, declared.FullyQualifiedName, declared.RawPinnedId));
      }
    }
  }
}
