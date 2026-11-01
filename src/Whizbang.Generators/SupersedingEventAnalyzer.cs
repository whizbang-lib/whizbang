// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators;

/// <summary>
/// Roslyn analyzer for superseding (latest-wins) event types (#1729): WHIZ135 and WHIZ136.
/// </summary>
/// <docs>fundamentals/events/superseding-events</docs>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class SupersedingEventAnalyzer : DiagnosticAnalyzer {
  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
  }
}
