using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Whizbang.Data.EFCore.Postgres.Generators;

/// <summary>
/// Roslyn analyzer that validates type arguments to Query&lt;T&gt;() and GetByIdAsync&lt;T&gt;()
/// on multi-generic ILensQuery interfaces (ILensQuery&lt;T1, T2&gt;, ILensQuery&lt;T1, T2, T3&gt;, etc).
/// </summary>
/// <remarks>
/// <para>
/// When using ILensQuery&lt;T1, T2, ...&gt;, the type argument to Query&lt;T&gt;() and
/// GetByIdAsync&lt;T&gt;() must be one of the interface's type parameters. Using an
/// invalid type will cause a runtime ArgumentException.
/// </para>
/// <para>
/// This analyzer provides compile-time detection of invalid type arguments, reporting
/// WHIZ400 error to prevent runtime failures.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz400</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LensQueryTypeArgumentAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LensQueryTypeArgumentAnalyzer : DiagnosticAnalyzer {
  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
      [DiagnosticDescriptors.InvalidLensQueryTypeArgument];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterOperationAction(_analyzeInvocation, OperationKind.Invocation);
  }

  private static void _analyzeInvocation(OperationAnalysisContext context) {
    var invocation = (IInvocationOperation)context.Operation;
    var method = invocation.TargetMethod;

    // Check if this is Query<T>() or GetByIdAsync<T>()
    if (!_isTargetMethod(method)) {
      return;
    }

    // Get the receiver type (the type of the object the method is called on)
    var receiverType = _getReceiverType(invocation);
    if (receiverType == null) {
      return;
    }

    // Check if receiver is ILensQuery<T1, T2, ...> with 2+ type parameters
    var lensQueryInterface = _findMultiGenericLensQueryInterface(receiverType);
    if (lensQueryInterface == null) {
      return;
    }

    // Get the type argument passed to Query<T>() or GetByIdAsync<T>()
    var methodTypeArg = method.TypeArguments.FirstOrDefault();
    if (methodTypeArg == null) {
      return;
    }

    // Check if the type argument is one of the interface's type parameters
    var validTypes = lensQueryInterface.TypeArguments;
    if (_isValidTypeArgument(methodTypeArg, validTypes)) {
      return;
    }

    // Report diagnostic - invalid type argument
    var typeArgName = methodTypeArg.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    var interfaceTypeParams = string.Join(", ",
        validTypes.Select(t => t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
    var validTypesList = string.Join(", ",
        validTypes.Select(t => t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));

    var diagnostic = Diagnostic.Create(
        DiagnosticDescriptors.InvalidLensQueryTypeArgument,
        invocation.Syntax.GetLocation(),
        typeArgName,
        interfaceTypeParams,
        validTypesList);

    context.ReportDiagnostic(diagnostic);
  }

  private static bool _isTargetMethod(IMethodSymbol method) {
    // Must be a generic method with exactly one type argument
    if (!method.IsGenericMethod || method.TypeArguments.Length != 1) {
      return false;
    }

    // Must be Query or GetByIdAsync
    return method.Name is "Query" or "GetByIdAsync";
  }

  private static INamedTypeSymbol? _getReceiverType(IInvocationOperation invocation) {
    // Get the instance (receiver) of the method call
    var instance = invocation.Instance;
    if (instance == null) {
      return null;
    }

    return instance.Type as INamedTypeSymbol;
  }

  private static INamedTypeSymbol? _findMultiGenericLensQueryInterface(INamedTypeSymbol type) {
    // Check if the type itself is ILensQuery<...> with 2+ type parameters
    if (_isMultiGenericLensQuery(type)) {
      return type;
    }

    // Check all interfaces the type implements
    return type.AllInterfaces.FirstOrDefault(_isMultiGenericLensQuery);
  }

  private static bool _isMultiGenericLensQuery(INamedTypeSymbol type) {
    // Must be an interface named ILensQuery with 2+ type arguments
    if (type.TypeKind != TypeKind.Interface) {
      return false;
    }

    if (!type.Name.Equals("ILensQuery", StringComparison.Ordinal)) {
      return false;
    }

    // Must have at least 2 type arguments (multi-generic)
    if (type.TypeArguments.Length < 2) {
      return false;
    }

    // Check namespace is Whizbang.Core.Lenses
    var containingNamespace = type.ContainingNamespace?.ToDisplayString();
    return containingNamespace == "Whizbang.Core.Lenses";
  }

  private static bool _isValidTypeArgument(
      ITypeSymbol typeArg,
      ImmutableArray<ITypeSymbol> validTypes) {
    // Check if typeArg matches any of the valid types
    return validTypes.Any(validType => SymbolEqualityComparer.Default.Equals(typeArg, validType));
  }
}
