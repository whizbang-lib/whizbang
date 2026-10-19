// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Reports <c>[TopicFilter]</c> on a type that is not a command, where the filter is ignored (WHIZ025, #1427).
/// Topic filters route commands; the generator skips every other type, so the attribute passed the build and
/// silently did nothing.
/// </summary>
/// <docs>operations/diagnostics/whiz025</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/TopicFilterTargetAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TopicFilterTargetAnalyzer : DiagnosticAnalyzer {
  private const string TOPIC_FILTER_ATTRIBUTE = "Whizbang.Core.TopicFilterAttribute";
  private const string COMMAND_INTERFACE = "Whizbang.Core.ICommand";

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [DiagnosticDescriptors.TopicFilterOnNonCommand];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyzeType, SymbolKind.NamedType);
  }

  private static void _analyzeType(SymbolAnalysisContext context) {
    var type = (INamedTypeSymbol)context.Symbol;
    var filter = type.GetAttributes().FirstOrDefault(a => _isTopicFilter(a.AttributeClass));
    if (filter is null || type.AllInterfaces.Any(i => TypeNameUtilities.IsNamed(i, COMMAND_INTERFACE))) {
      return;
    }
    context.ReportDiagnostic(Diagnostic.Create(
      DiagnosticDescriptors.TopicFilterOnNonCommand,
      filter.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? Location.None,
      type.Name));
  }

  /// <summary>The attribute or its generic <c>TopicFilterAttribute&lt;TEnum&gt;</c> form, which derives from it.</summary>
  private static bool _isTopicFilter(INamedTypeSymbol? attribute) {
    for (var current = attribute; current is not null; current = current.BaseType) {
      if (TypeNameUtilities.IsNamed(current.OriginalDefinition, TOPIC_FILTER_ATTRIBUTE)) {
        return true;
      }
    }
    return false;
  }
}
