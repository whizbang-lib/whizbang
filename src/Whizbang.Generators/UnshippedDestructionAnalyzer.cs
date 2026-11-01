// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators;

/// <summary>
/// WHIZ135 (#1735): a <c>Whizbang.Core.Attributes.Destruction</c> value whose handler has not shipped is a compile error,
/// so a declaration the reaper cannot carry out never compiles into a silent delete.
/// </summary>
/// <docs>operations/diagnostics/whiz135</docs>
/// <tests>tests/Whizbang.Generators.Tests/UnshippedDestructionAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class UnshippedDestructionAnalyzer : DiagnosticAnalyzer {
  /// <summary>WHIZ135: Error. A <c>Destruction</c> value whose handler has not shipped.</summary>
  /// <docs>operations/diagnostics/whiz135</docs>
  /// <tests>tests/Whizbang.Generators.Tests/UnshippedDestructionAnalyzerTests.cs:OnCompaction_OnAnEphemeralEvent_IsAnErrorAsync</tests>
  public static readonly DiagnosticDescriptor UnshippedDestruction = new(
    id: "WHIZ135",
    title: "Destruction value has not shipped",
    messageFormat: "Destruction.{0} has not shipped: {1}",
    category: "Whizbang.Ephemeral",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true);

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [UnshippedDestruction];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
  }
}
