// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
    context.RegisterSyntaxNodeAction(_analyzeName, SyntaxKind.IdentifierName);
  }

  private const string DESTRUCTION_TYPE = "Whizbang.Core.Attributes.Destruction";

  // Why each unshipped value has no handler; null for every other name.
  private static string? _reason(string name) => name switch {
    "OnCompaction" => "the framework has no compaction handler for event bodies; return Disposition.Compact from a "
      + "PreDestruction hook with an ICompactionHandler registered instead",
    "Archived" => "the archive store moves a stream's prefix or whole stream, not event bodies; archive the stream "
      + "through IArchiveStore or a prefix truncate instead",
    _ => null,
  };

  // Every name that binds to one of the two members, wherever it is written: an attribute argument, a field initializer,
  // a using static. A member access reports once, on its name.
  private static void _analyzeName(SyntaxNodeAnalysisContext context) {
    var name = (IdentifierNameSyntax)context.Node;
    // A cref in a documentation comment names the value without using it.
    if (name.IsPartOfStructuredTrivia()) {
      return;
    }
    if (_reason(name.Identifier.ValueText) is not { } reason) {
      return;
    }
    if (context.SemanticModel.GetSymbolInfo(name, context.CancellationToken).Symbol is not IFieldSymbol field
        || !Whizbang.Generators.Shared.Utilities.TypeNameUtilities.IsNamed(field.ContainingType, DESTRUCTION_TYPE)) {
      return;
    }
    context.ReportDiagnostic(Diagnostic.Create(UnshippedDestruction, name.GetLocation(), field.Name, reason));
  }
}
