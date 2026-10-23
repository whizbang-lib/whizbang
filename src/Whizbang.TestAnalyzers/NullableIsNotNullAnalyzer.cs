// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Whizbang.TestAnalyzers;

/// <summary>
/// WHZ0004: reports TUnit's <c>IsNotNull()</c> called on a <c>Nullable&lt;T&gt;</c> subject, an assertion
/// that passes when the subject is null under the TUnit this repository pins.
/// </summary>
/// <remarks>
/// <para>
/// TUnit 1.12.125 implements <c>IsNotNull()</c> for a nullable value type by mapping the subject through
/// <c>.Value</c>. On a null subject that throws; the mapping records the exception with a default
/// <c>T</c>, and the null check reads only the default, which is never null, so the assertion passes.
/// TUnit 1.13.8 fixed the check (upstream pull request 4685). Until this repository moves past it, every
/// such call verifies nothing, and the replacement is <c>Assert.That(x.HasValue).IsTrue()</c> or an
/// assertion on the value itself.
/// </para>
/// <para>
/// The rule binds to the one TUnit overload,
/// <c>AssertionExtensions.IsNotNull&lt;TValue&gt;(IAssertionSource&lt;TValue?&gt;) where TValue : struct</c>,
/// by symbol, so a call is reported however the subject is spelled and an
/// unrelated method named <c>IsNotNull</c> is not. Repository-internal: loaded only by this repository's
/// test projects.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.TestAnalyzers.Tests/NullableIsNotNullAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NullableIsNotNullAnalyzer : DiagnosticAnalyzer {
  private const string ASSERTION_EXTENSIONS = "TUnit.Assertions.Extensions.AssertionExtensions";
  private const string IS_NOT_NULL = "IsNotNull";
  private const string ASSERT_THAT = "That";

  /// <summary>
  /// WHZ0004: Error. <c>IsNotNull()</c> on a <c>Nullable&lt;T&gt;</c> subject cannot fail under the pinned TUnit.
  /// </summary>
  /// <tests>tests/Whizbang.TestAnalyzers.Tests/NullableIsNotNullAnalyzerTests.cs:IsNotNull_OnNullableInt_ReportsWhz0004Async</tests>
  public static readonly DiagnosticDescriptor IsNotNullOnNullable = new(
    id: "WHZ0004",
    title: "IsNotNull() on a Nullable<T> subject cannot fail",
    messageFormat: "'{0}' is a Nullable<{1}>, and IsNotNull() on it passes when it is null; assert "
      + "Assert.That({0}.HasValue).IsTrue() or assert on the value",
    category: "Whizbang.Testing",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    description: "In the pinned TUnit (1.12.125), IsNotNull() on a nullable value type maps the subject "
      + "through .Value, records the exception a null subject throws, and then checks only the default "
      + "value left behind, which is never null: the assertion passes whether or not the subject has a "
      + "value. Assert .HasValue with IsTrue(), or assert on the value itself (IsEqualTo, IsGreaterThan), "
      + "which a null subject fails. TUnit 1.13.8 fixed the check; this rule can be retired once the "
      + "repository is past it.",
    helpLinkUri: "https://github.com/whizbang-lib/whizbang/blob/develop/ai-docs/testing-tunit.md#isnotnull-on-a-nullable-value-type-whz0004");

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [IsNotNullOnNullable];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterCompilationStartAction(_onCompilationStart);
  }

  private static void _onCompilationStart(CompilationStartAnalysisContext context) {
    var extensions = context.Compilation.GetTypeByMetadataName(ASSERTION_EXTENSIONS);
    if (extensions is null) {
      return;
    }

    var overloads = extensions.GetMembers(IS_NOT_NULL)
      .OfType<IMethodSymbol>()
      .Where(_isNullableValueTypeOverload)
      .ToImmutableArray();
    context.RegisterOperationAction(operation => _analyzeInvocation(operation, overloads), OperationKind.Invocation);
  }

  /// <summary>True for <c>IsNotNull&lt;TValue&gt;(this IAssertionSource&lt;TValue?&gt;) where TValue : struct</c>.</summary>
  private static bool _isNullableValueTypeOverload(IMethodSymbol method) =>
    method.TypeParameters.Length == 1 && method.TypeParameters[0].HasValueTypeConstraint;

  private static void _analyzeInvocation(OperationAnalysisContext context, ImmutableArray<IMethodSymbol> overloads) {
    var invocation = (IInvocationOperation)context.Operation;
    // The operation tree presents an extension call in its static form, receiver first, however the source spells it.
    if (!overloads.Contains(invocation.TargetMethod.OriginalDefinition, SymbolEqualityComparer.Default)) {
      return;
    }

    var underlying = invocation.TargetMethod.TypeArguments[0];
    context.ReportDiagnostic(Diagnostic.Create(
      IsNotNullOnNullable,
      _nameLocation(invocation.Syntax),
      _subjectText(invocation.Arguments[0].Value.Syntax),
      underlying.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
  }

  /// <summary>
  /// The <c>IsNotNull</c> name of the call, so the squiggle sits on the method rather than on the whole chain.
  /// </summary>
  /// <remarks>
  /// The last token spelling the name is the method's, in every form the call can take (<c>a.IsNotNull()</c>,
  /// <c>a?.IsNotNull()</c>, <c>AssertionExtensions.IsNotNull(a)</c>): the receiver precedes it, and in the static
  /// form the receiver is an argument, outside the invoked expression.
  /// </remarks>
  private static Location _nameLocation(SyntaxNode invocation) =>
    ((InvocationExpressionSyntax)invocation).Expression.DescendantTokens()
      .Last(token => token.ValueText == IS_NOT_NULL)
      .GetLocation();

  /// <summary>
  /// The text of the asserted subject: the argument of the <c>Assert.That(...)</c> the receiver chain starts from,
  /// or the receiver itself when the chain does not start at one (an assertion source held in a variable).
  /// </summary>
  private static string _subjectText(SyntaxNode receiver) {
    var node = receiver;
    while (true) {
      switch (node) {
        case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: ASSERT_THAT } } that:
          return that.ArgumentList.Arguments[0].Expression.ToString();
        case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax call }:
          node = call.Expression;
          break;
        case MemberAccessExpressionSyntax access:
          node = access.Expression;
          break;
        default:
          return receiver.ToString();
      }
    }
  }
}
