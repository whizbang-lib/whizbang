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
/// attribute passed the build and silently did nothing. Inherited ones count (#1512): an inherited
/// <c>[StreamId]</c> property is used ahead of this type's constructor parameter, and this type's property hides an
/// inherited one. Any property type may be a stream id: the extractor keys the stream by its string form, so the
/// retired WHIZ005 ("must be a Guid") described no real rule.
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
      .Select(p => (p.Name, Attribute: _streamIdAttribute(p), IsProperty: true))
      .Concat(type.InstanceConstructors.SelectMany(c => c.Parameters)
        .Select(p => (p.Name, Attribute: _streamIdAttribute(p), IsProperty: false)))
      .Where(d => d.Attribute is not null)
      .GroupBy(d => d.Name, System.StringComparer.OrdinalIgnoreCase)
      .Select(g => g.First())
      .ToList();
    if (declared.Count == 0) {
      return;
    }

    // The one the generator uses (#1512): a [StreamId] property, searched from this type up through its bases, and
    // only without one a constructor parameter. So an inherited property beats this type's parameter, and this
    // type's property hides an inherited one.
    var inherited = _inheritedStreamIdPropertyName(type);
    var used = declared[0].IsProperty ? declared[0].Name : inherited ?? declared[0].Name;

    foreach (var (name, attribute, _) in declared) {
      var hidesInherited = name == declared[0].Name && declared[0].IsProperty
        && inherited is not null && !string.Equals(inherited, used, System.StringComparison.OrdinalIgnoreCase);
      if (string.Equals(name, used, System.StringComparison.OrdinalIgnoreCase) && !hidesInherited) {
        continue;
      }
      context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.MultipleStreamIdAttributes, LocationUtilities.ApplicationOrFallback(attribute!, Location.None, context.CancellationToken), type.Name, used));
    }
  }

  // The nearest base type's [StreamId] property, the one the generator would reach after this type's own properties.
  private static string? _inheritedStreamIdPropertyName(INamedTypeSymbol type) {
    for (var current = type.BaseType; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType) {
      var property = current.GetMembers().OfType<IPropertySymbol>().FirstOrDefault(p => _streamIdAttribute(p) is not null);
      if (property is not null) {
        return property.Name;
      }
    }
    return null;
  }

  private static AttributeData? _streamIdAttribute(ISymbol symbol) =>
    symbol.GetAttributes().FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, STREAM_ID_ATTRIBUTE));
}
