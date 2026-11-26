// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Reports WHIZ118 (error) at a call to a strong id's <c>DeriveFrom(handling, purpose)</c> when the id has no pinned id
/// (#2604). The generator gives every strong id <c>DeriveFrom</c>; a pinned id also gets the <c>PinnedId</c> constant the
/// derivation reads, so an id without it has nothing that survives a rename to derive from, and its <c>DeriveFrom</c>
/// only throws.
/// </summary>
/// <docs>operations/diagnostics/whiz118</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/StrongIdDeriveFromAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class StrongIdDeriveFromAnalyzer : DiagnosticAnalyzer {
  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [DiagnosticDescriptors.StrongIdDeriveFromNeedsPinnedId];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
  }
}
