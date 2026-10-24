// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Whizbang.RepoAnalyzers;

/// <summary>
/// WHZ0005: a <c>JsonDocument</c> created and dropped in one expression, as in
/// <c>JsonDocument.Parse(json).RootElement</c>. Nothing holds the document, so nothing can dispose it,
/// and the buffers it rented from the shared pool are never returned.
/// </summary>
/// <remarks>
/// The SDK's own rule for this, CA2026, ships only with the SDK 11 analyzers, and this repository builds
/// with SDK 10. This rule enforces the same thing on every SDK; retire it once the analysis level moves
/// to an SDK that ships CA2026 (.editorconfig already raises CA2026 to an error for that day).
/// A document counts as created when it comes from a <c>JsonDocument</c> factory
/// (<c>Parse</c>, <c>ParseAsync</c>, <c>ParseValue</c>) or a <c>JsonSerializer</c> method, awaited or not.
/// A document returned by any other method may be owned and disposed by the code that returned it,
/// so it is not reported.
/// </remarks>
/// <tests>tests/Whizbang.RepoAnalyzers.Tests/UndisposedJsonDocumentAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UndisposedJsonDocumentAnalyzer : DiagnosticAnalyzer {
  private static readonly DiagnosticDescriptor _rule = new(
    id: "WHZ0005",
    title: "A JsonDocument is created and dropped without being disposed",
    messageFormat: "'{0}' creates a JsonDocument that nothing can dispose, so its pooled buffers are never returned; use JsonElement.Parse or JsonSerializer.SerializeToElement, or dispose the document and Clone the element that must outlive it",
    category: "Reliability",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    description: "Repository guard standing in for CA2026 on SDKs whose analyzers do not ship it.");

  /// <inheritdoc />
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [_rule];

  /// <inheritdoc />
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterCompilationStartAction(start => {
      var jsonDocument = start.Compilation.GetTypeByMetadataName("System.Text.Json.JsonDocument");
      if (jsonDocument is null) {
        return;
      }

      var rootElement = jsonDocument.GetMembers("RootElement").Single();
      var jsonSerializer = start.Compilation.GetTypeByMetadataName("System.Text.Json.JsonSerializer");
      start.RegisterOperationAction(
        operation => _analyzeRootElement(operation, rootElement, jsonDocument, jsonSerializer),
        OperationKind.PropertyReference);
    });
  }

  private static void _analyzeRootElement(
      OperationAnalysisContext context,
      ISymbol rootElement,
      INamedTypeSymbol jsonDocument,
      INamedTypeSymbol? jsonSerializer) {
    var reference = (IPropertyReferenceOperation)context.Operation;
    if (!SymbolEqualityComparer.Default.Equals(reference.Property, rootElement)) {
      return;
    }

    var instance = reference.Instance is IAwaitOperation awaited ? awaited.Operation : reference.Instance;
    if (instance is not IInvocationOperation invocation) {
      return;
    }

    var factory = invocation.TargetMethod.ContainingType;
    if (!SymbolEqualityComparer.Default.Equals(factory, jsonDocument)
        && !SymbolEqualityComparer.Default.Equals(factory, jsonSerializer)) {
      return;
    }

    context.ReportDiagnostic(Diagnostic.Create(
      _rule, reference.Syntax.GetLocation(), $"{factory.Name}.{invocation.TargetMethod.Name}"));
  }
}
