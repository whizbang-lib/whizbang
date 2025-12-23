using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators;

/// <summary>
/// Roslyn analyzer that enforces purity requirements for perspective Apply methods.
/// Apply methods must be pure functions: no async/await, no Task returns, no I/O operations.
/// This analyzer provides compile-time verification of the purity contract.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class PerspectivePurityAnalyzer : DiagnosticAnalyzer {
  // Diagnostic IDs: WHIZ100-199 reserved for purity enforcement (100s range)
  // Similar to HTTP status codes: 100s = purity, 200s = future category, etc.
  private const string CATEGORY = "Whizbang.Purity";

  /// <summary>
  /// WHIZ100: Error - Apply method returns Task (must be synchronous).
  /// </summary>
  public static readonly DiagnosticDescriptor ApplyMethodIsAsync = new(
      id: "WHIZ100",
      title: "Apply method must be synchronous",
      messageFormat: "Apply method '{0}' returns Task but must be synchronous for purity. Change return type from 'Task<{1}>' to '{1}'.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "Perspective Apply methods must be pure functions and cannot be async. Remove async/await and return the model directly."
  );

  /// <summary>
  /// WHIZ101: Error - Apply method uses async/await keywords.
  /// </summary>
  public static readonly DiagnosticDescriptor ApplyMethodUsesAwait = new(
      id: "WHIZ101",
      title: "Apply method cannot use async/await",
      messageFormat: "Apply method '{0}' uses 'await' keyword but must be a pure function. Remove all async operations.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "Perspective Apply methods must be pure functions without async operations. Use synchronous logic only."
  );

  /// <summary>
  /// WHIZ102: Error - Apply method calls database I/O operations.
  /// </summary>
  public static readonly DiagnosticDescriptor ApplyMethodCallsDatabase = new(
      id: "WHIZ102",
      title: "Apply method cannot call database I/O",
      messageFormat: "Apply method '{0}' calls '{1}' which performs database I/O. Apply methods must be pure functions.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "Perspective Apply methods must be pure functions and cannot perform database I/O. Move I/O operations outside the Apply method."
  );

  /// <summary>
  /// WHIZ103: Error - Apply method calls HTTP/network operations.
  /// </summary>
  public static readonly DiagnosticDescriptor ApplyMethodCallsHttp = new(
      id: "WHIZ103",
      title: "Apply method cannot call HTTP/network operations",
      messageFormat: "Apply method '{0}' calls '{1}' which performs HTTP/network I/O. Apply methods must be pure functions.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "Perspective Apply methods must be pure functions and cannot perform HTTP or network operations."
  );

  /// <summary>
  /// WHIZ104: Warning - Apply method may use non-deterministic DateTime.
  /// </summary>
  public static readonly DiagnosticDescriptor ApplyMethodUsesDateTime = new(
      id: "WHIZ104",
      title: "Apply method should use event timestamps instead of DateTime.UtcNow",
      messageFormat: "Apply method '{0}' uses DateTime.UtcNow which is non-deterministic. Use event timestamps instead for purity.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "Perspective Apply methods should be deterministic. Use timestamps from the event instead of DateTime.UtcNow."
  );

  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
      ApplyMethodIsAsync,
      ApplyMethodUsesAwait,
      ApplyMethodCallsDatabase,
      ApplyMethodCallsHttp,
      ApplyMethodUsesDateTime
  );

  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();

    // Register for method declarations
    context.RegisterSyntaxNodeAction(_analyzeMethod, SyntaxKind.MethodDeclaration);
  }

  private static void _analyzeMethod(SyntaxNodeAnalysisContext context) {
    var methodDeclaration = (MethodDeclarationSyntax)context.Node;
    var methodSymbol = context.SemanticModel.GetDeclaredSymbol(methodDeclaration);

    if (methodSymbol is null) {
      return;
    }

    // Only analyze methods named "Apply"
    if (methodSymbol.Name != "Apply") {
      return;
    }

    // Check if method is in a type implementing IPerspectiveFor or IGlobalPerspectiveFor
    var containingType = methodSymbol.ContainingType;
    if (!_implementsPerspectiveInterface(containingType)) {
      return;
    }

    // Check 1: Apply method must not return Task
    if (_returnsTask(methodSymbol)) {
      var returnType = methodSymbol.ReturnType.ToDisplayString();
      var modelType = _extractModelTypeFromTask(methodSymbol.ReturnType);

      var diagnostic = Diagnostic.Create(
          ApplyMethodIsAsync,
          methodDeclaration.Identifier.GetLocation(),
          methodSymbol.Name,
          modelType ?? "TModel"
      );
      context.ReportDiagnostic(diagnostic);
    }

    // Check 2: Apply method must not use await
    if (_usesAwait(methodDeclaration)) {
      var diagnostic = Diagnostic.Create(
          ApplyMethodUsesAwait,
          methodDeclaration.Identifier.GetLocation(),
          methodSymbol.Name
      );
      context.ReportDiagnostic(diagnostic);
    }

    // Check 3: Apply method must not call database I/O
    var dbCalls = _findDatabaseCalls(methodDeclaration, context.SemanticModel);
    foreach (var (call, methodName) in dbCalls) {
      var diagnostic = Diagnostic.Create(
          ApplyMethodCallsDatabase,
          call.GetLocation(),
          methodSymbol.Name,
          methodName
      );
      context.ReportDiagnostic(diagnostic);
    }

    // Check 4: Apply method must not call HTTP operations
    var httpCalls = _findHttpCalls(methodDeclaration, context.SemanticModel);
    foreach (var (call, methodName) in httpCalls) {
      var diagnostic = Diagnostic.Create(
          ApplyMethodCallsHttp,
          call.GetLocation(),
          methodSymbol.Name,
          methodName
      );
      context.ReportDiagnostic(diagnostic);
    }

    // Check 5: Warn about DateTime.UtcNow usage
    var dateTimeCalls = _findDateTimeNowCalls(methodDeclaration);
    foreach (var call in dateTimeCalls) {
      var diagnostic = Diagnostic.Create(
          ApplyMethodUsesDateTime,
          call.GetLocation(),
          methodSymbol.Name
      );
      context.ReportDiagnostic(diagnostic);
    }
  }

  private static bool _implementsPerspectiveInterface(INamedTypeSymbol typeSymbol) {
    // Check if type implements IPerspectiveFor<TModel, TEvent...> or IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent...>
    foreach (var iface in typeSymbol.AllInterfaces) {
      var interfaceName = iface.ToDisplayString();
      if (interfaceName.StartsWith("Whizbang.Core.Perspectives.IPerspectiveFor<", StringComparison.Ordinal) ||
          interfaceName.StartsWith("Whizbang.Core.Perspectives.IGlobalPerspectiveFor<", StringComparison.Ordinal)) {
        return true;
      }
    }
    return false;
  }

  private static bool _returnsTask(IMethodSymbol methodSymbol) {
    var returnType = methodSymbol.ReturnType;
    if (returnType is INamedTypeSymbol namedType) {
      var fullName = namedType.ConstructedFrom.ToDisplayString();
      return fullName == "System.Threading.Tasks.Task<TResult>" ||
             fullName == "System.Threading.Tasks.Task" ||
             fullName == "System.Threading.Tasks.ValueTask<TResult>" ||
             fullName == "System.Threading.Tasks.ValueTask";
    }
    return false;
  }

  private static string? _extractModelTypeFromTask(ITypeSymbol returnType) {
    if (returnType is INamedTypeSymbol namedType && namedType.IsGenericType) {
      var typeArgs = namedType.TypeArguments;
      if (typeArgs.Length > 0) {
        return typeArgs[0].ToDisplayString();
      }
    }
    return null;
  }

  private static bool _usesAwait(MethodDeclarationSyntax methodDeclaration) {
    return methodDeclaration.DescendantNodes()
        .OfType<AwaitExpressionSyntax>()
        .Any();
  }

  private static IEnumerable<(InvocationExpressionSyntax, string)> _findDatabaseCalls(
      MethodDeclarationSyntax methodDeclaration,
      SemanticModel semanticModel) {

    var invocations = methodDeclaration.DescendantNodes().OfType<InvocationExpressionSyntax>();

    foreach (var invocation in invocations) {
      var symbolInfo = semanticModel.GetSymbolInfo(invocation);
      if (symbolInfo.Symbol is IMethodSymbol methodSymbol) {
        var containingType = methodSymbol.ContainingType?.ToDisplayString() ?? "";
        var methodName = methodSymbol.Name;

        // Check for common database operation patterns
        if (containingType.Contains("DbContext") ||
            containingType.Contains("DbSet") ||
            containingType.Contains("IPerspectiveStore") ||
            containingType.Contains("ILensQuery") ||
            methodName.EndsWith("Async", StringComparison.Ordinal) && (
                methodName.Contains("Save") ||
                methodName.Contains("Insert") ||
                methodName.Contains("Update") ||
                methodName.Contains("Delete") ||
                methodName.Contains("Upsert") ||
                methodName.Contains("Query") ||
                methodName.Contains("Get") ||
                methodName.Contains("Find")
            )) {
          yield return (invocation, $"{containingType}.{methodName}");
        }
      }
    }
  }

  private static IEnumerable<(InvocationExpressionSyntax, string)> _findHttpCalls(
      MethodDeclarationSyntax methodDeclaration,
      SemanticModel semanticModel) {

    var invocations = methodDeclaration.DescendantNodes().OfType<InvocationExpressionSyntax>();

    foreach (var invocation in invocations) {
      var symbolInfo = semanticModel.GetSymbolInfo(invocation);
      if (symbolInfo.Symbol is IMethodSymbol methodSymbol) {
        var containingType = methodSymbol.ContainingType?.ToDisplayString() ?? "";
        var methodName = methodSymbol.Name;

        // Check for HTTP operation patterns
        if (containingType.Contains("HttpClient") ||
            containingType.Contains("HttpMessageInvoker") ||
            methodName.StartsWith("Get", StringComparison.Ordinal) && containingType.Contains("Http") ||
            methodName.StartsWith("Post", StringComparison.Ordinal) && containingType.Contains("Http") ||
            methodName.StartsWith("Put", StringComparison.Ordinal) && containingType.Contains("Http") ||
            methodName.StartsWith("Delete", StringComparison.Ordinal) && containingType.Contains("Http")) {
          yield return (invocation, $"{containingType}.{methodName}");
        }
      }
    }
  }

  private static IEnumerable<MemberAccessExpressionSyntax> _findDateTimeNowCalls(
      MethodDeclarationSyntax methodDeclaration) {

    return methodDeclaration.DescendantNodes()
        .OfType<MemberAccessExpressionSyntax>()
        .Where(m => {
          var expr = m.Expression.ToString();
          var name = m.Name.ToString();
          return (expr == "DateTime" || expr == "DateTimeOffset") &&
                 (name == "Now" || name == "UtcNow");
        });
  }
}
