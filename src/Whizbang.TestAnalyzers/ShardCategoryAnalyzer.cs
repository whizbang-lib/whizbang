// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.TestAnalyzers;

/// <summary>
/// WHIZ605: in a sharded test project, a test class without exactly one <c>Shard*</c> category (or
/// <c>Benchmark</c> and none). Stub: reports nothing yet.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ShardCategoryAnalyzer : DiagnosticAnalyzer {
  /// <summary>WHIZ605: Error.</summary>
  public static readonly DiagnosticDescriptor ShardCategoryMismatch = new(
    id: "WHIZ605",
    title: "A test class in a sharded project must carry exactly one Shard category",
    messageFormat: "'{0}' {1}{2}",
    category: "Whizbang.Testing",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    helpLinkUri: "https://github.com/whizbang-lib/whizbang/blob/develop/ai-docs/test-sharding.md");

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [ShardCategoryMismatch];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
  }
}
