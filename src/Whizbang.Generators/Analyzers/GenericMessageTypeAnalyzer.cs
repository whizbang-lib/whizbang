// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Reports a generic message type at its declaration (WHIZ015).
/// </summary>
/// <docs>operations/diagnostics/whiz015</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/GenericMessageTypeAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class GenericMessageTypeAnalyzer : DiagnosticAnalyzer {
  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [DiagnosticDescriptors.GenericMessageType];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyze, SymbolKind.NamedType);
  }

  private const string I_MESSAGE = "Whizbang.Core.IMessage";

  private static void _analyze(SymbolAnalysisContext context) {
    // An interface is not a message type the generators name; only a generic class or struct declaration is
    // an open type they would have emitted literally.
    if (context.Symbol is not INamedTypeSymbol { IsGenericType: true, TypeKind: TypeKind.Class or TypeKind.Struct } type) {
      return;
    }
    if (!type.AllInterfaces.Any(static i => TypeNameUtilities.Display(i) == I_MESSAGE)) {
      return;
    }

    // Symbol actions run on the compilation's own source types, so the type is declared in source.
    context.ReportDiagnostic(Diagnostic.Create(
      DiagnosticDescriptors.GenericMessageType,
      type.Locations[0],
      TypeNameUtilities.Display(type)));
  }
}
