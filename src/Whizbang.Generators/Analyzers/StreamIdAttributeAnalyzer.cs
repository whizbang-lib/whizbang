// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Reports <c>[StreamId]</c> misuse where it is declared (#1427): a stream id that is not a Guid (WHIZ005), and
/// a type with more than one <c>[StreamId]</c>, of which only the first is used (WHIZ006). Both were declared
/// and documented, but nothing reported them, so the mistakes passed the build silently.
/// </summary>
/// <docs>operations/diagnostics/whiz005</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/StreamIdAttributeAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StreamIdAttributeAnalyzer : DiagnosticAnalyzer {
  private const string STREAM_ID_ATTRIBUTE = "Whizbang.Core.StreamIdAttribute";

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
    DiagnosticDescriptors.StreamIdMustBeGuid,
    DiagnosticDescriptors.MultipleStreamIdAttributes,
  ];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyzeType, SymbolKind.NamedType);
  }

  private static void _analyzeType(SymbolAnalysisContext context) {
    var type = (INamedTypeSymbol)context.Symbol;
    // Each [StreamId] the type declares itself, on a property or on a primary-constructor parameter that is
    // not also the target of a property-targeted [StreamId]
    var declared = type.GetMembers().OfType<IPropertySymbol>()
      .Select(p => (Name: p.Name, Type: p.Type, Attribute: _streamIdAttribute(p)))
      .Concat(type.InstanceConstructors.SelectMany(c => c.Parameters)
        .Select(p => (Name: p.Name, Type: p.Type, Attribute: _streamIdAttribute(p))))
      .Where(d => d.Attribute is not null)
      .GroupBy(d => d.Name, System.StringComparer.OrdinalIgnoreCase)
      .Select(g => g.First())
      .ToList();

    foreach (var (name, propertyType, attribute) in declared) {
      if (!_isGuidLike(propertyType)) {
        context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.StreamIdMustBeGuid, _location(attribute!, context.CancellationToken), type.Name, name));
      }
    }
    for (var i = 1; i < declared.Count; i++) {
      context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.MultipleStreamIdAttributes, _location(declared[i].Attribute!, context.CancellationToken), type.Name, declared[0].Name));
    }
  }

  private static AttributeData? _streamIdAttribute(ISymbol symbol) =>
    symbol.GetAttributes().FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, STREAM_ID_ATTRIBUTE));

  private static Location _location(AttributeData attribute, System.Threading.CancellationToken cancellationToken) =>
    attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? Location.None;

  /// <summary>Guid, Guid?, or a type whose <c>Value</c> property is a Guid (a strongly-typed id).</summary>
  private static bool _isGuidLike(ITypeSymbol type) {
    var underlying = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
      ? nullable.TypeArguments[0]
      : type;
    if (TypeNameUtilities.IsNamed(underlying, "System.Guid")) {
      return true;
    }
    return underlying.GetMembers("Value").OfType<IPropertySymbol>().Any(p => TypeNameUtilities.IsNamed(p.Type, "System.Guid"));
  }
}
