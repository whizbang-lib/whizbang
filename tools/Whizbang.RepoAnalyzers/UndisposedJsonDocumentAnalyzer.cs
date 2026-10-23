// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.RepoAnalyzers;

/// <summary>
/// WHZ0004 (not yet implemented).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UndisposedJsonDocumentAnalyzer : DiagnosticAnalyzer {
  private static readonly DiagnosticDescriptor _rule = new(
    id: "WHZ0004",
    title: "Not yet implemented",
    messageFormat: "Not yet implemented",
    category: "Reliability",
    defaultSeverity: DiagnosticSeverity.Warning,
    isEnabledByDefault: false);

  /// <inheritdoc />
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [_rule];

  /// <inheritdoc />
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
  }
}
