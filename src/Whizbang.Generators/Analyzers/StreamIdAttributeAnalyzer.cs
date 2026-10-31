// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
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
    DiagnosticDescriptors.GenerationOptOutAcrossAssemblies,
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

    _reportOptOutAcrossAssemblies(context, type, declared[0]);

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

  /// <summary>
  /// WHIZ017 (#1904): a type with its own <c>[StreamId]</c> and no <c>[GenerateStreamId]</c> opts out of its base's
  /// generation policy (#1893), but the opt-out holds only within one assembly. When a base compiled in another
  /// assembly declares <c>[GenerateStreamId]</c>, that assembly's extractor answers first, so the base's policy still
  /// applies; reported on the type's own <c>[StreamId]</c>. A base in the same assembly is not reported.
  /// </summary>
  /// <docs>operations/diagnostics/whiz017</docs>
  /// <tests>tests/Whizbang.Generators.Tests/Analyzers/StreamIdCrossAssemblyGenerationAnalyzerTests.cs:OwnStreamId_UnderAGeneratingBaseInAnotherAssembly_ReportsWhiz017Async</tests>
  /// <tests>tests/Whizbang.Generators.Tests/Analyzers/StreamIdCrossAssemblyGenerationAnalyzerTests.cs:OwnStreamId_UnderAGeneratingBaseInTheSameAssembly_IsNotReportedAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/Analyzers/StreamIdCrossAssemblyGenerationAnalyzerTests.cs:ADerivedTypeThatDeclaresGenerateStreamId_IsNotReportedAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/Analyzers/StreamIdCrossAssemblyGenerationAnalyzerTests.cs:OwnStreamId_UnderABaseInAnotherAssemblyThatDoesNotGenerate_IsNotReportedAsync</tests>
  private static void _reportOptOutAcrossAssemblies(
      SymbolAnalysisContext context, INamedTypeSymbol type, StreamIdSelection.Declaration ownStreamId) {
    if (_declaresGenerateStreamId(type)) {
      return;
    }
    for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType) {
      if (!SymbolEqualityComparer.Default.Equals(baseType.ContainingAssembly, type.ContainingAssembly)
          && _declaresGenerateStreamId(baseType)) {
        var location = LocationUtilities.ApplicationOrFallback(ownStreamId.Attribute, Location.None, context.CancellationToken);
        context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.GenerationOptOutAcrossAssemblies, location,
          type.Name, ownStreamId.Name, baseType.Name, baseType.ContainingAssembly.Name));
        return;
      }
    }
  }

  /// <summary>
  /// Whether the type itself (not a base) declares <c>[GenerateStreamId]</c>: on the type, on one of its
  /// properties, or on a constructor parameter, the three places the generator reads it.
  /// </summary>
  private static bool _declaresGenerateStreamId(INamedTypeSymbol type) =>
    _hasGenerateStreamId(type.GetAttributes())
    || type.GetMembers().OfType<IPropertySymbol>().Any(p => _hasGenerateStreamId(p.GetAttributes()))
    || type.InstanceConstructors.Any(c => c.Parameters.Any(p => _hasGenerateStreamId(p.GetAttributes())));

  private static bool _hasGenerateStreamId(ImmutableArray<AttributeData> attributes) =>
    attributes.Any(a => TypeNameUtilities.IsNamed(a.AttributeClass, GENERATE_STREAM_ID));

  private const string GENERATE_STREAM_ID = "Whizbang.Core.GenerateStreamIdAttribute";
}
