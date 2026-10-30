// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators;

/// <summary>
/// Checks what a perspective's <c>Apply</c> assumes about the current model it is handed. The generated runner never
/// passes a null model (a stream with no row gets an empty one), so a null test of the model is dead code, and when it
/// is meant to detect a stream's first event it silently never does (#1878).
/// </summary>
/// <docs>fundamentals/perspectives/perspectives-with-actions#no-row-contract</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveCurrentModelAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class PerspectiveCurrentModelAnalyzer : DiagnosticAnalyzer {
  private const string CATEGORY = "Whizbang.Perspectives";

  /// <summary>
  /// WHIZ107: Warning - an Apply tests its current model for null, which it never is.
  /// </summary>
  /// <docs>operations/diagnostics/whiz107</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveCurrentModelAnalyzerTests.cs:AnApplyThatTestsItsCurrentModelForNull_IsWarnedThatItNeverIsAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveCurrentModelAnalyzerTests.cs:EveryFormOfTheNullCheck_IsWarnedAsync</tests>
  public static readonly DiagnosticDescriptor ApplyTestsCurrentModelForNull = new(
      id: "WHIZ107",
      title: "Apply never receives a null model",
      messageFormat: "Apply method '{0}' tests its current model '{1}' for null, but the runner never passes a null model: a stream with no row gets an empty one. To detect the stream's first event, use ApplyContext.Current.IsFirstEventInStream.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "The perspective runner hands every Apply a model: the stored row, or an empty model for a stream that has none. A null test of that model is never true, and row state cannot tell a stream's first event anyway, because a purge, an eviction, a rebuild or a replay changes it. ApplyContext.Current.IsFirstEventInStream is decided by the event's position in its stream, so every replay reaches the same answer."
  );

  /// <summary>
  /// WHIZ108: Warning - an Apply of an [OverwriteOnly] perspective reads its current model, which the marker says it never
  /// does (#1869).
  /// </summary>
  /// <docs>operations/diagnostics/whiz108</docs>
  /// <tests>tests/Whizbang.Generators.Tests/OverwriteOnlyAnalyzerTests.cs:AnOverwriteOnlyApplyThatReadsItsCurrentModel_IsWarnedAsync</tests>
  public static readonly DiagnosticDescriptor OverwriteOnlyApplyReadsCurrentModel = new(
      id: "WHIZ108",
      title: "An overwrite-only perspective's Apply must not read its current model",
      messageFormat: "Apply method '{0}' of overwrite-only perspective '{1}' reads its current model '{2}'. A rewind of an [OverwriteOnly] perspective applies only the stream's last event to an empty model, so whatever this Apply carries forward from the model would be lost. Build the model from the event alone, or remove [OverwriteOnly].",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "[OverwriteOnly] declares that every Apply builds the row from its event alone, so the row after any event is that event's. The runner relies on it: a rewind applies only the stream's last event instead of replaying the stream. An Apply that reads its current-model parameter folds earlier events into the row, which the rewind would drop."
  );

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    ImmutableArray.Create(ApplyTestsCurrentModelForNull, OverwriteOnlyApplyReadsCurrentModel);

  private const string OVERWRITE_ONLY_ATTRIBUTE = "Whizbang.Core.Attributes.OverwriteOnlyAttribute";

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSyntaxNodeAction(_analyzeMethod, SyntaxKind.MethodDeclaration);
  }

  private static void _analyzeMethod(SyntaxNodeAnalysisContext context) {
    var method = (MethodDeclarationSyntax)context.Node;
    // A perspective Apply is named Apply and takes (current model, event); the syntactic test goes first so the
    // semantic one runs for Apply methods only.
    if (method.Identifier.ValueText != "Apply" || method.ParameterList.Parameters.Count != 2) {
      return;
    }
    // A method declaration in its own compilation always binds its symbol.
    var symbol = context.SemanticModel.GetDeclaredSymbol(method, context.CancellationToken)!;
    if (!Utilities.PerspectiveDiscoveryHelper.IsPerspectiveClass(symbol.ContainingType)) {
      return;
    }

    var current = symbol.Parameters[0];
    foreach (var node in method.DescendantNodes()) {
      if (_testsForNull(node, current, context.SemanticModel, context.CancellationToken)) {
        context.ReportDiagnostic(Diagnostic.Create(
            ApplyTestsCurrentModelForNull, node.GetLocation(), symbol.Name, current.Name));
      }
    }

    // WHIZ108 (#1869): one report per Apply, at the first read, is enough to name the contradiction.
    if (symbol.ContainingType.GetAttributes().Any(static a => Shared.Utilities.TypeNameUtilities.IsNamed(a.AttributeClass, OVERWRITE_ONLY_ATTRIBUTE))
        && _firstRead(method, current, context.SemanticModel, context.CancellationToken) is { } read) {
      context.ReportDiagnostic(Diagnostic.Create(
          OverwriteOnlyApplyReadsCurrentModel, read.GetLocation(), symbol.Name, symbol.ContainingType.Name, current.Name));
    }
  }

  /// <summary>The first use of the current-model parameter in the Apply that reads it; <c>nameof</c> reads nothing.</summary>
  private static IdentifierNameSyntax? _firstRead(MethodDeclarationSyntax method, IParameterSymbol current, SemanticModel model, System.Threading.CancellationToken ct) =>
    method.DescendantNodes().OfType<IdentifierNameSyntax>().FirstOrDefault(identifier =>
      !_insideNameof(identifier)
      && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier, ct).Symbol, current));

  private static bool _insideNameof(SyntaxNode node) =>
    node.Ancestors().OfType<InvocationExpressionSyntax>()
      .Any(static invocation => invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" });

  /// <summary>
  /// True for <c>current is null</c>, <c>current is not null</c>, <c>current == null</c>, <c>null == current</c> and
  /// <c>current != null</c>.
  /// </summary>
  private static bool _testsForNull(SyntaxNode node, IParameterSymbol current, SemanticModel model, System.Threading.CancellationToken ct) =>
    node switch {
      IsPatternExpressionSyntax isPattern =>
        _isNullPattern(isPattern.Pattern) && _isParameter(isPattern.Expression, current, model, ct),
      BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression) =>
        (_isNullLiteral(binary.Right) && _isParameter(binary.Left, current, model, ct))
        || (_isNullLiteral(binary.Left) && _isParameter(binary.Right, current, model, ct)),
      _ => false,
    };

  private static bool _isNullPattern(PatternSyntax pattern) => pattern switch {
    ConstantPatternSyntax constant => _isNullLiteral(constant.Expression),
    UnaryPatternSyntax { Pattern: ConstantPatternSyntax negated } => _isNullLiteral(negated.Expression),
    _ => false,
  };

  private static bool _isNullLiteral(ExpressionSyntax expression) => expression.IsKind(SyntaxKind.NullLiteralExpression);

  private static bool _isParameter(ExpressionSyntax expression, IParameterSymbol current, SemanticModel model, System.Threading.CancellationToken ct) =>
    expression is IdentifierNameSyntax
    && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(expression, ct).Symbol, current);
}
