using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators;

/// <summary>
/// Roslyn analyzer that detects incorrect Guid usage patterns.
/// Enforces the use of TrackedGuid or [WhizbangId] types instead of raw Guid generation.
/// </summary>
/// <docs>core-concepts/whizbang-ids#analyzer</docs>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class GuidUsageAnalyzer : DiagnosticAnalyzer {
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
      DiagnosticDescriptors.GuidNewGuidUsage,
      DiagnosticDescriptors.GuidCreateVersion7Usage,
      DiagnosticDescriptors.RawGuidWhereIdExpected
  );

  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();

    // Register for method invocations
    context.RegisterSyntaxNodeAction(_analyzeInvocation, SyntaxKind.InvocationExpression);
  }

  private static void _analyzeInvocation(SyntaxNodeAnalysisContext context) {
    var invocation = (InvocationExpressionSyntax)context.Node;

    // Check what method is being invoked
    var memberAccess = invocation.Expression as MemberAccessExpressionSyntax;
    if (memberAccess is null) {
      return;
    }

    var methodName = memberAccess.Name.Identifier.Text;
    var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation);

    if (symbolInfo.Symbol is not IMethodSymbol methodSymbol) {
      return;
    }

    var containingType = methodSymbol.ContainingType?.ToDisplayString();

    // Check for Guid.NewGuid()
    if (containingType == "System.Guid" && methodName == "NewGuid") {
      var diagnostic = Diagnostic.Create(
          DiagnosticDescriptors.GuidNewGuidUsage,
          invocation.GetLocation()
      );
      context.ReportDiagnostic(diagnostic);
      return;
    }

    // Check for Guid.CreateVersion7()
    if (containingType == "System.Guid" && methodName == "CreateVersion7") {
      var diagnostic = Diagnostic.Create(
          DiagnosticDescriptors.GuidCreateVersion7Usage,
          invocation.GetLocation()
      );
      context.ReportDiagnostic(diagnostic);
    }
  }
}
