// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Reports a type with more than one <c>[StreamId]</c>, of which only the first is used (WHIZ006, #1427), on
/// each attribute that is ignored. It was declared and documented, but nothing reported it, so the second
/// attribute passed the build and silently did nothing. Any property type may be a stream id: the extractor
/// keys the stream by its string form, so the retired WHIZ005 ("must be a Guid") described no real rule.
/// </summary>
/// <docs>operations/diagnostics/whiz006</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/StreamIdAttributeAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StreamIdAttributeAnalyzer : DiagnosticAnalyzer {
  private const string STREAM_ID_ATTRIBUTE = "Whizbang.Core.StreamIdAttribute";

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
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
      .Select(p => (Name: p.Name, Attribute: _streamIdAttribute(p)))
      .Concat(type.InstanceConstructors.SelectMany(c => c.Parameters)
        .Select(p => (Name: p.Name, Attribute: _streamIdAttribute(p))))
      .Where(d => d.Attribute is not null)
      .GroupBy(d => d.Name, System.StringComparer.OrdinalIgnoreCase)
      .Select(g => g.First())
      .ToList();

    for (var i = 1; i < declared.Count; i++) {
      context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.MultipleStreamIdAttributes, _location(declared[i].Attribute!, context.CancellationToken), type.Name, declared[0].Name));
    }
  }

  private static AttributeData? _streamIdAttribute(ISymbol symbol) =>
    symbol.GetAttributes().FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, STREAM_ID_ATTRIBUTE));

  private static Location _location(AttributeData attribute, System.Threading.CancellationToken cancellationToken) =>
    attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? Location.None;

}
