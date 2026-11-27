// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.RepoAnalyzers;

/// <summary>
/// WHIZ611: a channel wait raced in <c>Task.WhenAny</c> whose token nothing cancels once the race is decided.
/// </summary>
/// <tests>tests/Whizbang.RepoAnalyzers.Tests/AbandonedChannelWaitAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AbandonedChannelWaitAnalyzer : DiagnosticAnalyzer {
  private static readonly DiagnosticDescriptor _rule = new(
    id: "WHIZ611",
    title: "A channel wait raced in Task.WhenAny is never released",
    messageFormat: "'{0}' is raced in Task.WhenAny and nothing cancels it when another task wins",
    category: "Reliability",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true);

  /// <inheritdoc />
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [_rule];

  /// <inheritdoc />
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
  }
}
