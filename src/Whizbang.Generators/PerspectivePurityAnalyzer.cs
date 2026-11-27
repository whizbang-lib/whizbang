// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Utilities;

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
  /// <docs>operations/diagnostics/whiz100</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectivePurityAnalyzerTests.cs:Analyzer_AsyncApplyMethod_ReportsWHIZ100ErrorAsync</tests>
  public static readonly DiagnosticDescriptor ApplyMethodIsAsync = new(
      id: "WHIZ100",
      title: "Apply method must be synchronous",
      messageFormat: "Apply method '{0}' returns '{2}' but must be synchronous for purity. Change the return type to '{1}'.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "Perspective Apply methods must be pure functions and cannot be async. Remove async/await and return the model directly."
  );

  /// <summary>
  /// WHIZ101: Error - Apply method uses async/await keywords.
  /// </summary>
  /// <docs>operations/diagnostics/whiz101</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectivePurityAnalyzerTests.cs:Analyzer_ApplyAwaitsInsideBody_ReportsWHIZ101Async</tests>
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
  /// <docs>operations/diagnostics/whiz102</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectivePurityAnalyzerTests.cs:Analyzer_ApplyCallsPerspectiveStore_ReportsWHIZ102Async</tests>
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
  /// <docs>operations/diagnostics/whiz103</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectivePurityAnalyzerTests.cs:Analyzer_ApplyCallsHttpClient_ReportsWHIZ103Async</tests>
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
  /// <docs>operations/diagnostics/whiz104</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectivePurityAnalyzerTests.cs:Analyzer_DateTimeUtcNowUsage_ReportsWHIZ104WarningAsync</tests>
  public static readonly DiagnosticDescriptor ApplyMethodUsesDateTime = new(
      id: "WHIZ104",
      title: "Apply method should use event timestamps instead of reading the clock",
      messageFormat: "Apply method '{0}' uses {1} which is non-deterministic. Use event timestamps instead for purity.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "Perspective Apply methods should be deterministic. Use timestamps from the event instead of DateTime.Now, DateTime.UtcNow, DateTimeOffset.Now or DateTimeOffset.UtcNow."
  );

  /// <summary>
  /// WHIZ105: Warning - Perspective injects a service not marked with [PureService].
  /// </summary>
  /// <docs>operations/diagnostics/whiz105</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectivePurityAnalyzerCoverageTests.cs:GenericTypeParameterConstructorInjection_ReportsWHIZ105Async</tests>
  public static readonly DiagnosticDescriptor NonPureServiceInjected = new(
      id: "WHIZ105",
      title: "Perspective injects non-pure service",
      messageFormat: "Perspective '{0}' injects '{1}' which is not marked with [PureService]. Services injected into perspectives must be pure for replay determinism.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "Perspectives may be replayed during system recovery. Injected services must be pure (deterministic, no side effects). Mark the service with [PureService] or suppress this warning if you're certain the service is pure."
  );

  /// <summary>
  /// WHIZ106: Error - a set-based apply (a collective apply or a fan-out's Apply) depends on more than the row and the
  /// event: an apply-time cross-perspective query, or state of the perspective (#2618).
  /// </summary>
  /// <docs>operations/diagnostics/whiz106</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectivePurityAnalyzerTests.cs:Analyzer_CollectiveApplyUsesCollectiveQuery_ReportsWHIZ106Async</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveFanOutPurityTests.cs:Whiz106_ASetThatReadsStateBesidesTheRowAndTheEvent_IsAnErrorAsync</tests>
  public static readonly DiagnosticDescriptor CollectiveApplyUsesCollectiveQuery = new(
      id: "WHIZ106",
      title: "Set-based apply must depend only on the row and the event",
      messageFormat: "Set-based apply '{0}' {1}. A set-based apply is folded per row during a replay or rebuild against that row and the event alone, so it must not depend on anything else. Resolve it before the event is created and carry it on the event as payload.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "A [CollectiveApplyFor] spec and a fan-out's Apply are folded per row during replay or rebuild against a single stream in isolation. An apply-time query against a sibling perspective (ICollectiveQuery.Of<>()), the perspective's own fields, properties or methods, or a static field that can change, cannot be reconstructed then, so the effect would differ on rebuild. Facts from elsewhere are resolved on the command side, when the event is created, and carried on the event."
  );

  private const string CROSS_PERSPECTIVE_QUERY = "calls ICollectiveQuery.Of<>(), an apply-time cross-perspective query";

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
      ApplyMethodIsAsync,
      ApplyMethodUsesAwait,
      ApplyMethodCallsDatabase,
      ApplyMethodCallsHttp,
      ApplyMethodUsesDateTime,
      NonPureServiceInjected,
      CollectiveApplyUsesCollectiveQuery
  );

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();

    // Register for method declarations (Apply method purity)
    context.RegisterSyntaxNodeAction(_analyzeMethod, SyntaxKind.MethodDeclaration);

    // Register for constructor declarations (pure service injection)
    context.RegisterSyntaxNodeAction(_analyzeConstructor, SyntaxKind.ConstructorDeclaration);
  }

  private static void _analyzeMethod(SyntaxNodeAnalysisContext context) {
    var methodDeclaration = (MethodDeclarationSyntax)context.Node;
    // A method declaration in its own compilation always has its symbol.
    var methodSymbol = context.SemanticModel.GetDeclaredSymbol(methodDeclaration, context.CancellationToken)!;

    // Collective-apply replay purity (WHIZ106): a [CollectiveApplyFor] spec is folded per-row against a
    // single stream in isolation during replay/rebuild, so it must not query sibling perspectives at apply
    // time. This is independent of the Apply-name / perspective-interface gating below.
    var isCollectiveApply = _isCollectiveApply(methodSymbol);

    // A fan-out's Apply is a set-based apply too, folded per row on a rebuild (#2618): the same determinism checks, and
    // WHIZ106 for anything it reads besides the row and the event.
    var isFanOutApply = FanOutShape.IsApply(methodSymbol);

    // The determinism checks (async/await, DB/HTTP I/O, DateTime.UtcNow) apply to BOTH perspective Apply
    // methods and collective applies — both are folded during replay and must be deterministic.
    var isPerspectiveApply = methodSymbol.Name == "Apply"
        && _implementsPerspectiveInterface(methodSymbol.ContainingType);

    // A declaration Roslyn bound no symbol for is neither of those, so the bind guard shares this
    // exit instead of standing on a line of its own that no input reaches. Nothing below this point
    // runs for a null symbol, and the collective-apply analysis moves under the same gate — it only
    // ever ran when isCollectiveApply was true, which cannot be true for a null symbol.
    if (!isPerspectiveApply && !isCollectiveApply && !isFanOutApply) {
      return;
    }

    if (isCollectiveApply) {
      _analyzeCollectiveApply(context, methodDeclaration, methodSymbol);
    }

    if (isFanOutApply) {
      _analyzeFanOutApply(context, methodDeclaration, methodSymbol.ContainingType);
    }

    // Check 1: Apply method must not return Task
    if (_returnsTask(methodSymbol)) {
      var modelType = _extractModelTypeFromTask(methodSymbol.ReturnType);

      var diagnostic = Diagnostic.Create(
          ApplyMethodIsAsync,
          methodDeclaration.Identifier.GetLocation(),
          methodSymbol.Name,
          modelType ?? "TModel",
          TypeNameUtilities.MinimallyQualified(methodSymbol.ReturnType)
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
    var dateTimeCalls = _findDateTimeNowCalls(methodDeclaration, context.SemanticModel);
    foreach (var (call, clockRead) in dateTimeCalls) {
      var diagnostic = Diagnostic.Create(
          ApplyMethodUsesDateTime,
          call.GetLocation(),
          methodSymbol.Name,
          clockRead
      );
      context.ReportDiagnostic(diagnostic);
    }
  }

  private const string COLLECTIVE_APPLY_FOR_ATTRIBUTE_FULL_NAME =
      "Whizbang.Core.Perspectives.CollectiveApplyForAttribute";
  private const string COLLECTIVE_QUERY_FULL_NAME =
      "Whizbang.Core.Perspectives.ICollectiveQuery";

  private static bool _isCollectiveApply(IMethodSymbol methodSymbol) {
    return methodSymbol.GetAttributes()
        .Any(a => TypeNameUtilities.IsNamed(a.AttributeClass, COLLECTIVE_APPLY_FOR_ATTRIBUTE_FULL_NAME));
  }

  /// <summary>
  /// WHIZ106: flags any invocation of a member on an <c>ICollectiveQuery</c> (i.e. <c>q.Of&lt;&gt;()</c>)
  /// inside a <c>[CollectiveApplyFor]</c> method — an apply-time cross-perspective query that cannot be
  /// re-evaluated per-row during a single-stream replay/rebuild.
  /// </summary>
  private static void _analyzeCollectiveApply(
      SyntaxNodeAnalysisContext context,
      MethodDeclarationSyntax methodDeclaration,
      IMethodSymbol methodSymbol) {
    // Flag any member access whose RECEIVER is an ICollectiveQuery (i.e. `query.Of<>()`). Keying on the
    // receiver's type — not the invocation's resolved symbol — is robust: the outer `.Any(...)` on the
    // returned IQueryable may fail to bind (System.Linq.Queryable not in scope), which would null the
    // invocation symbol, but `query`'s own type still resolves.
    foreach (var memberAccess in methodDeclaration.DescendantNodes().OfType<MemberAccessExpressionSyntax>()) {
      var receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression, context.CancellationToken).Type;
      if (!TypeNameUtilities.IsNamed(receiverType, COLLECTIVE_QUERY_FULL_NAME)) {
        continue;
      }

      var diagnostic = Diagnostic.Create(
          CollectiveApplyUsesCollectiveQuery,
          memberAccess.GetLocation(),
          methodSymbol.Name,
          CROSS_PERSPECTIVE_QUERY);
      context.ReportDiagnostic(diagnostic);
    }
  }

  /// <summary>
  /// WHIZ106 for a fan-out's <c>Apply</c> (#2618): flags each read of the perspective's own state (<c>this</c>,
  /// <c>base</c>, or an instance member of the perspective) and each read of a static field that can change. The row
  /// and the event are reached only through the setters' lambdas and the event parameter, which are never flagged.
  /// </summary>
  private static void _analyzeFanOutApply(
      SyntaxNodeAnalysisContext context, MethodDeclarationSyntax methodDeclaration, INamedTypeSymbol perspective) {
    var name = methodDeclaration.Identifier.ValueText;
    foreach (var node in methodDeclaration.DescendantNodes()) {
      var (reported, reason) = node switch {
        ThisExpressionSyntax or BaseExpressionSyntax => (node, "reads 'this', the perspective's own state"),
        IdentifierNameSyntax identifier => _fanOutSetRead(context, identifier, perspective),
        _ => (null, null),
      };
      if (reported is not null) {
        context.ReportDiagnostic(Diagnostic.Create(CollectiveApplyUsesCollectiveQuery, reported.GetLocation(), name, reason));
      }
    }
  }

  /// <summary>
  /// What <paramref name="identifier"/> reads that a fan-out's <c>Apply</c> must not, and the node to report; nulls when it
  /// reads nothing of the kind. A member read through <c>this</c> is reported once, at <c>this</c>; a static field read
  /// through its type is reported at the whole access.
  /// </summary>
  private static (SyntaxNode? Reported, string? Reason) _fanOutSetRead(
      SyntaxNodeAnalysisContext context, IdentifierNameSyntax identifier, INamedTypeSymbol perspective) {
    var access = identifier.Parent as MemberAccessExpressionSyntax;
    var isMemberName = access is not null && access.Name == identifier;
    if (isMemberName && access!.Expression is ThisExpressionSyntax or BaseExpressionSyntax) {
      return (null, null);
    }
    var symbol = context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken).Symbol;
    if (symbol is IFieldSymbol { IsStatic: true, IsConst: false, IsReadOnly: false }) {
      return (isMemberName ? access : identifier, $"reads '{TypeNameUtilities.SimpleName(symbol.ContainingType)}.{symbol.Name}', a static field that can change");
    }
    return !isMemberName && _isPerspectiveState(symbol, perspective)
      ? (identifier, $"reads '{symbol!.Name}', the perspective's own state")
      : (null, null);
  }

  /// <summary>Whether <paramref name="symbol"/> is an instance member of <paramref name="perspective"/> or a base of it.</summary>
  private static bool _isPerspectiveState(ISymbol? symbol, INamedTypeSymbol perspective) {
    if (symbol is not (IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary }) || symbol.IsStatic) {
      return false;
    }
    for (var type = perspective; type is not null; type = type.BaseType) {
      if (SymbolEqualityComparer.Default.Equals(type, symbol.ContainingType)) {
        return true;
      }
    }
    return false;
  }

  private static void _analyzeConstructor(SyntaxNodeAnalysisContext context) {
    var constructorDeclaration = (ConstructorDeclarationSyntax)context.Node;
    // Check if constructor is in a type implementing IPerspectiveFor or IGlobalPerspectiveFor. The
    // bind guard shares that exit: a declaration Roslyn bound no symbol for has no containing type to
    // test, so it leaves here without the symbol ever being dereferenced.
    if (context.SemanticModel.GetDeclaredSymbol(constructorDeclaration, context.CancellationToken) is not { } constructorSymbol
        || !_implementsPerspectiveInterface(constructorSymbol.ContainingType)) {
      return;
    }

    var containingType = constructorSymbol.ContainingType;

    // Check each constructor parameter for [PureService] attribute
    foreach (var parameter in constructorSymbol.Parameters) {
      var parameterType = parameter.Type;

      // Skip value types and built-in types
      if (parameterType.IsValueType ||
          parameterType.SpecialType != SpecialType.None ||
          parameterType.TypeKind == TypeKind.Enum) {
        continue;
      }

      // Check if parameter type (or its interface) has [PureService] attribute
      if (!_hasPureServiceAttribute(parameterType)) {
        // The parameter's own syntax, by position: matched by name, a parameter written @event never matched the
        // symbol's name, event, and the diagnostic fell back to the constructor (#1178).
        var location = constructorDeclaration.ParameterList.Parameters[parameter.Ordinal].GetLocation();

        var diagnostic = Diagnostic.Create(
            NonPureServiceInjected,
            location,
            TypeNameUtilities.SimpleName(containingType),
            TypeNameUtilities.Display(parameterType)
        );
        context.ReportDiagnostic(diagnostic);
      }
    }
  }

  private static bool _hasPureServiceAttribute(ITypeSymbol typeSymbol) {
    // Check the type itself
    if (_hasAttribute(typeSymbol, "Whizbang.Core.Attributes.PureServiceAttribute")) {
      return true;
    }

    // For class types, also check implemented interfaces
    if (typeSymbol is INamedTypeSymbol namedType) {
      return namedType.AllInterfaces.Any(iface =>
        _hasAttribute(iface, "Whizbang.Core.Attributes.PureServiceAttribute"));
    }

    return false;
  }

  private static bool _hasAttribute(ITypeSymbol typeSymbol, string attributeFullName) {
    return typeSymbol.GetAttributes()
        .Any(a => TypeNameUtilities.IsNamed(a.AttributeClass, attributeFullName));
  }

  private static bool _implementsPerspectiveInterface(INamedTypeSymbol typeSymbol) {
    // Use shared discovery helper for consistent perspective detection
    return Utilities.PerspectiveDiscoveryHelper.IsPerspectiveClass(typeSymbol);
  }

  private static bool _returnsTask(IMethodSymbol methodSymbol) {
    var returnType = methodSymbol.ReturnType;
    if (returnType is INamedTypeSymbol namedType) {
      var fullName = TypeNameUtilities.Display(namedType.ConstructedFrom);
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
        return TypeNameUtilities.Display(typeArgs[0]);
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
        var containingType = TypeNameUtilities.DisplayOrEmpty(methodSymbol.ContainingType);
        var methodName = methodSymbol.Name;

        // Check for common database operation patterns. An HTTP type's Get/Post methods match the name heuristic,
        // but they are HTTP calls, reported once as WHIZ103 (#1428).
        if (_isHttpType(containingType)) {
          continue;
        }
        if (containingType.Contains("DbContext") ||
            containingType.Contains("DbSet") ||
            containingType.Contains("IPerspectiveStore") ||
            containingType.Contains("ILensQuery") ||
            (methodName.EndsWith("Async", StringComparison.Ordinal) && (
                methodName.Contains("Save") ||
                methodName.Contains("Insert") ||
                methodName.Contains("Update") ||
                methodName.Contains("Delete") ||
                methodName.Contains("Upsert") ||
                methodName.Contains("Query") ||
                methodName.Contains("Get") ||
                methodName.Contains("Find")
            ))) {
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
        var containingType = TypeNameUtilities.DisplayOrEmpty(methodSymbol.ContainingType);
        var methodName = methodSymbol.Name;

        // Check for HTTP operation patterns
        if (_isHttpType(containingType) ||
            (methodName.StartsWith("Get", StringComparison.Ordinal) && containingType.Contains("Http")) ||
            (methodName.StartsWith("Post", StringComparison.Ordinal) && containingType.Contains("Http")) ||
            (methodName.StartsWith("Put", StringComparison.Ordinal) && containingType.Contains("Http")) ||
            (methodName.StartsWith("Delete", StringComparison.Ordinal) && containingType.Contains("Http"))) {
          yield return (invocation, $"{containingType}.{methodName}");
        }
      }
    }
  }

  private static bool _isHttpType(string containingType) =>
      containingType.Contains("HttpClient") || containingType.Contains("HttpMessageInvoker");

  /// <summary>
  /// The clock reads in the method, found by the property they bind to rather than how they are spelled, so
  /// <c>System.DateTime.UtcNow</c> and <c>global::System.DateTimeOffset.Now</c> are found too (#1428).
  /// </summary>
  private static IEnumerable<(MemberAccessExpressionSyntax Call, string ClockRead)> _findDateTimeNowCalls(
      MethodDeclarationSyntax methodDeclaration, SemanticModel semanticModel) {
    foreach (var access in methodDeclaration.DescendantNodes().OfType<MemberAccessExpressionSyntax>()) {
      if (semanticModel.GetSymbolInfo(access).Symbol is IPropertySymbol { IsStatic: true, Name: "Now" or "UtcNow" } property
          && TypeNameUtilities.DisplayOrEmpty(property.ContainingType) is "System.DateTime" or "System.DateTimeOffset") {
        yield return (access, $"{TypeNameUtilities.SimpleName(property.ContainingType)}.{property.Name}");
      }
    }
  }
}
