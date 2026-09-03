using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Reports a service constructed inside a DI factory that omits an injectable dependency.
/// </summary>
/// <remarks>
/// <para>
/// When a registration builds an object with <c>new</c>, the container is not resolving its
/// constructor; the author is listing arguments by hand. Anything not listed is supplied by the
/// compiler as the parameter's default, which for an injected service is null. Nothing reports it:
/// the code compiles, the container is satisfied, the service runs without the dependency, and the
/// missing behavior is indistinguishable from behavior nobody asked for.
/// </para>
/// <para>
/// This is not hypothetical. An audit decorator gained a logger and a service instance provider,
/// both registration sites kept passing three of six arguments, and the feature was absent in every
/// composed application while every unit test passed, because a test that constructs the type
/// supplies the argument itself and so cannot observe that the container does not.
/// </para>
/// <para>
/// The rule is deliberately syntactic and dependency-agnostic. It does not know what a logger is,
/// so it covers loggers, telemetry providers, decision hooks and anything added later, without a
/// rule per dependency and without an attribute anyone has to remember to apply.
/// </para>
/// <para>
/// Passing null explicitly is allowed. The defect is omission, which is invisible in review; a
/// deliberate null is a decision a reader can see and question.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz500</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/DiFactoryConstructionAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class DiFactoryConstructionAnalyzer : DiagnosticAnalyzer {

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    ImmutableArray.Create(DiagnosticDescriptors.DiFactoryOmitsDependency);

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSyntaxNodeAction(_analyze, SyntaxKind.ObjectCreationExpression);
  }

  private static void _analyze(SyntaxNodeAnalysisContext context) {
    var creation = (ObjectCreationExpressionSyntax)context.Node;

    if (!_isInsideServiceProviderFactory(creation)) {
      return;
    }

    // The operation model reports which parameter each argument actually bound to, including
    // arguments the compiler filled in. Counting syntax instead gets this wrong: C# allows named
    // arguments to precede positional ones, so argument position does not imply parameter index,
    // and a naive count reports supplied dependencies as missing. A rule that fires on correct code
    // is worse than no rule, because it teaches everyone to suppress it.
    if (context.SemanticModel.GetOperation(creation, context.CancellationToken)
        is not IObjectCreationOperation operation) {
      return;
    }

    foreach (var argument in operation.Arguments) {
      if (argument.ArgumentKind != ArgumentKind.DefaultValue) {
        continue;
      }
      var parameter = argument.Parameter;
      if (parameter is null) {
        continue;
      }
      // Only interfaces are container-resolved services. A retry count or a name with a sensible
      // default is not a dependency, and flagging it would make the rule fire on correct code.
      if (parameter.Type.TypeKind != TypeKind.Interface) {
        continue;
      }

      context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.DiFactoryOmitsDependency,
        creation.GetLocation(),
        operation.Constructor?.ContainingType.Name ?? "service",
        parameter.Name,
        parameter.Type.Name));
    }
  }

  /// <summary>
  /// True when this construction sits inside a lambda that takes an <c>IServiceProvider</c>.
  /// </summary>
  /// <remarks>
  /// That lambda is the shape of a registration factory: it stands in for the container, so an
  /// argument it does not pass is one the container never gets to supply. Ordinary code and tests
  /// construct these types all the time and supply what they need, so the rule must not reach them.
  /// </remarks>
  private static bool _isInsideServiceProviderFactory(SyntaxNode node) {
    for (var current = node.Parent; current is not null; current = current.Parent) {
      var parameters = current switch {
        SimpleLambdaExpressionSyntax simple => new[] { simple.Parameter },
        ParenthesizedLambdaExpressionSyntax paren => paren.ParameterList.Parameters.ToArray(),
        _ => null,
      };
      if (parameters is null) {
        continue;
      }
      foreach (var p in parameters) {
        // An explicitly typed IServiceProvider parameter is conclusive. An implicitly typed one
        // (sp => ...) is the overwhelmingly common registration form, and the conventional name is
        // the only signal available without resolving the enclosing invocation.
        var typeText = p.Type?.ToString();
        if (typeText is not null && typeText.EndsWith("IServiceProvider", System.StringComparison.Ordinal)) {
          return true;
        }
        if (p.Type is null && p.Identifier.ValueText is "sp" or "provider" or "serviceProvider") {
          return true;
        }
      }
    }
    return false;
  }
}
