// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Reports a type with more than one <c>[StreamId]</c>, of which only the first is used (WHIZ006, #1427), on
/// each attribute that is ignored. It was declared and documented, but nothing reported it, so the second
/// attribute passed the build and silently did nothing. Inherited ones count (#1512): an inherited
/// <c>[StreamId]</c> property is used ahead of this type's constructor parameter, and this type's property hides an
/// inherited one. A hide marked <c>[StreamId(Override = true)]</c> is intended and not reported (#1833); an override
/// with no inherited <c>[StreamId]</c> to replace is WHIZ016. The choice of stream id is
/// <see cref="StreamIdSelection"/>, the one the generators make. Any property type may be a stream id: the extractor
/// keys the stream by its string form, so the retired WHIZ005 ("must be a Guid") described no real rule.
/// </summary>
/// <docs>operations/diagnostics/whiz006</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/StreamIdAttributeAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StreamIdAttributeAnalyzer : DiagnosticAnalyzer {
  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
    DiagnosticDescriptors.MultipleStreamIdAttributes,
    DiagnosticDescriptors.StreamIdOverrideWithNothingToOverride,
  ];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyzeType, SymbolKind.NamedType);
  }

  private static void _analyzeType(SymbolAnalysisContext context) {
    var type = (INamedTypeSymbol)context.Symbol;
    // Each [StreamId] the type declares itself, on a property or on a constructor parameter, one per name
    var declared = StreamIdSelection.Declared(type);
    if (declared.Count == 0) {
      return;
    }

    // The one the generators use, and the one the type would inherit without its own
    var used = StreamIdSelection.Select(type)?.Property.Name ?? declared[0].Name;
    var inherited = StreamIdSelection.SelectInherited(type, includeSelf: false)?.Property.Name;
    var anyInherited = StreamIdSelection.AnyInherited(type);

    foreach (var declaration in declared) {
      var location = LocationUtilities.ApplicationOrFallback(declaration.Attribute, Location.None, context.CancellationToken);
      if (declaration.IsOverride && !anyInherited) {
        context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.StreamIdOverrideWithNothingToOverride, location, type.Name, declaration.Name));
      }

      var isUsed = string.Equals(declaration.Name, used, System.StringComparison.OrdinalIgnoreCase);
      var hidesInherited = isUsed && !declaration.IsOverride
        && inherited is not null && !string.Equals(inherited, used, System.StringComparison.OrdinalIgnoreCase);
      if (isUsed && !hidesInherited) {
        continue;
      }
      context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.MultipleStreamIdAttributes, location, type.Name, used));
    }
  }
}
