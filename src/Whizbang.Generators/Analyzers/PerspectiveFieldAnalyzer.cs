// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Reports perspective-model field misuse where it is declared (#1427): a <c>[VectorField]</c> on a property
/// that is not <c>float[]</c> (WHIZ801), a <c>[VectorField]</c> whose dimensions are not positive (WHIZ802),
/// and a model stored <c>Split</c> with no physical or vector field, which stores exactly as JsonOnly does
/// (WHIZ805). All three were declared and documented, but nothing reported them.
/// </summary>
/// <docs>operations/diagnostics/whiz801</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/PerspectiveFieldAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PerspectiveFieldAnalyzer : DiagnosticAnalyzer {
  private const string VECTOR_FIELD_ATTRIBUTE = "Whizbang.Core.Perspectives.VectorFieldAttribute";
  private const string PHYSICAL_FIELD_ATTRIBUTE = "Whizbang.Core.Perspectives.PhysicalFieldAttribute";
  private const string STORAGE_ATTRIBUTE = "Whizbang.Core.Perspectives.PerspectiveStorageAttribute";
  // FieldStorageMode.Split
  private const int SPLIT_MODE = 2;

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
    DiagnosticDescriptors.VectorFieldInvalidType,
    DiagnosticDescriptors.VectorFieldInvalidDimensions,
    DiagnosticDescriptors.SplitModeNoPhysicalFields,
  ];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyzeProperty, SymbolKind.Property);
    context.RegisterSymbolAction(_analyzeType, SymbolKind.NamedType);
  }

  private static void _analyzeProperty(SymbolAnalysisContext context) {
    var property = (IPropertySymbol)context.Symbol;
    if (_attribute(property, VECTOR_FIELD_ATTRIBUTE) is not { } vector) {
      return;
    }
    var location = vector.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? Location.None;
    if (property.Type is not IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Single }) {
      context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.VectorFieldInvalidType, location, property.ContainingType.Name, property.Name));
    }
    if (vector.ConstructorArguments is [{ Value: int dimensions }] && dimensions < 1) {
      context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.VectorFieldInvalidDimensions, location, property.ContainingType.Name, property.Name, dimensions));
    }
  }

  private static void _analyzeType(SymbolAnalysisContext context) {
    var type = (INamedTypeSymbol)context.Symbol;
    if (_attribute(type, STORAGE_ATTRIBUTE) is not { ConstructorArguments: [{ Value: int mode }] } storage || mode != SPLIT_MODE) {
      return;
    }
    var hasField = _propertiesWithBases(type).Any(p =>
      _attribute(p, PHYSICAL_FIELD_ATTRIBUTE) is not null || _attribute(p, VECTOR_FIELD_ATTRIBUTE) is not null);
    if (!hasField) {
      context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.SplitModeNoPhysicalFields,
        storage.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? Location.None,
        type.Name));
    }
  }

  private static System.Collections.Generic.IEnumerable<IPropertySymbol> _propertiesWithBases(INamedTypeSymbol type) {
    for (var current = type; current is not null; current = current.BaseType) {
      foreach (var property in current.GetMembers().OfType<IPropertySymbol>()) {
        yield return property;
      }
    }
  }

  private static AttributeData? _attribute(ISymbol symbol, string name) =>
    symbol.GetAttributes().FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, name));
}
