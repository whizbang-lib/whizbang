using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Migrate.Transformers;

/// <summary>
/// Transforms Wolverine/Marten DI registrations to Whizbang equivalents.
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public sealed class DIRegistrationTransformer : ICodeTransformer {
  private static readonly HashSet<string> _wolverineMethods = new(StringComparer.Ordinal) {
    "AddWolverine",
    "UseWolverine"
  };

  private static readonly HashSet<string> _martenMethods = new(StringComparer.Ordinal) {
    "AddMarten"
  };

  private static readonly HashSet<string> _methodsToRemove = new(StringComparer.Ordinal) {
    "IntegrateWithWolverine"
  };

  /// <inheritdoc />
  public Task<TransformationResult> TransformAsync(
      string sourceCode,
      string filePath,
      CancellationToken ct = default) {
    var changes = new List<CodeChange>();
    var warnings = new List<string>();

    var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: ct);
    var root = tree.GetRoot(ct);

    // Check if there are any DI registrations to transform
    var hasDIPatterns = _hasDIPatterns(root);
    if (!hasDIPatterns) {
      return Task.FromResult(new TransformationResult(
          sourceCode,
          sourceCode,
          changes,
          warnings));
    }

    var newRoot = root;

    // 1. Transform method calls
    newRoot = _transformMethodCalls(newRoot, changes);

    // 2. Transform using directives
    newRoot = _transformUsings(newRoot, changes);

    var transformedCode = newRoot.ToFullString();

    return Task.FromResult(new TransformationResult(
        sourceCode,
        transformedCode,
        changes,
        warnings));
  }

  private static bool _hasDIPatterns(SyntaxNode root) {
    var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

    foreach (var invocation in invocations) {
      var methodName = _getMethodName(invocation);
      if (methodName != null &&
          (_wolverineMethods.Contains(methodName) ||
           _martenMethods.Contains(methodName) ||
           _methodsToRemove.Contains(methodName))) {
        return true;
      }
    }

    return false;
  }

  private static string? _getMethodName(InvocationExpressionSyntax invocation) {
    return invocation.Expression switch {
      MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
      IdentifierNameSyntax identifier => identifier.Identifier.Text,
      _ => null
    };
  }

  private static SyntaxNode _transformMethodCalls(SyntaxNode root, List<CodeChange> changes) {
    var rewriter = new DIMethodCallRewriter(changes);
    return rewriter.Visit(root);
  }

  private static SyntaxNode _transformUsings(SyntaxNode root, List<CodeChange> changes) {
    var compilationUnit = root as CompilationUnitSyntax;
    if (compilationUnit == null) {
      return root;
    }

    var hasWolverineUsing = compilationUnit.Usings
        .Any(u => u.Name?.ToString() == "Wolverine");
    var hasMartenUsing = compilationUnit.Usings
        .Any(u => u.Name?.ToString() == "Marten");

    if (!hasWolverineUsing && !hasMartenUsing) {
      return root;
    }

    var newUsings = new List<UsingDirectiveSyntax>();
    var addedWhizbang = false;

    foreach (var usingDirective in compilationUnit.Usings) {
      var name = usingDirective.Name?.ToString();

      if (name == "Wolverine" || name == "Marten") {
        if (!addedWhizbang) {
          // Replace with Whizbang.Core
          var whizbangUsing = usingDirective
              .WithName(SyntaxFactory.ParseName("Whizbang.Core")
                  .WithLeadingTrivia(usingDirective.Name?.GetLeadingTrivia() ?? SyntaxFactory.TriviaList())
                  .WithTrailingTrivia(usingDirective.Name?.GetTrailingTrivia() ?? SyntaxFactory.TriviaList()));
          newUsings.Add(whizbangUsing);
          addedWhizbang = true;

          changes.Add(new CodeChange(
              usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
              ChangeType.UsingRemoved,
              $"Replaced 'using {name}' with 'using Whizbang.Core'",
              $"using {name};",
              "using Whizbang.Core;"));
        } else {
          // Skip duplicate - already added Whizbang.Core
          changes.Add(new CodeChange(
              usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
              ChangeType.UsingRemoved,
              $"Removed 'using {name}' (consolidated into Whizbang.Core)",
              $"using {name};",
              ""));
        }
      } else {
        newUsings.Add(usingDirective);
      }
    }

    return compilationUnit.WithUsings(SyntaxFactory.List(newUsings));
  }

  /// <summary>
  /// Rewriter that transforms Wolverine/Marten method calls to Whizbang equivalents.
  /// </summary>
  private sealed class DIMethodCallRewriter : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;

    public DIMethodCallRewriter(List<CodeChange> changes) {
      _changes = changes;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node) {
      var methodName = _getMethodName(node);

      if (methodName == null) {
        return base.VisitInvocationExpression(node);
      }

      // Handle IntegrateWithWolverine - remove entirely
      if (_methodsToRemove.Contains(methodName)) {
        // Get the expression before the method call (the chained call)
        if (node.Expression is MemberAccessExpressionSyntax memberAccess) {
          _changes.Add(new CodeChange(
              node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
              ChangeType.MethodCallReplacement,
              $"Removed '{methodName}()' (not needed in Whizbang)",
              $".{methodName}()",
              ""));

          // Return just the expression part, removing the method call
          return Visit(memberAccess.Expression);
        }
      }

      // Handle AddWolverine -> AddWhizbang
      if (methodName == "AddWolverine") {
        var newMethodName = "AddWhizbang";
        var newNode = _replaceMethodName(node, newMethodName);

        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.MethodCallReplacement,
            $"Replaced '{methodName}' with '{newMethodName}'",
            methodName,
            newMethodName));

        return newNode;
      }

      // Handle UseWolverine -> UseWhizbang
      if (methodName == "UseWolverine") {
        var newMethodName = "UseWhizbang";
        var newNode = _replaceMethodName(node, newMethodName);

        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.MethodCallReplacement,
            $"Replaced '{methodName}' with '{newMethodName}'",
            methodName,
            newMethodName));

        return newNode;
      }

      // Handle AddMarten -> AddWhizbangEventStore
      if (methodName == "AddMarten") {
        var newMethodName = "AddWhizbangEventStore";
        var newNode = _replaceMethodName(node, newMethodName);

        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.MethodCallReplacement,
            $"Replaced '{methodName}' with '{newMethodName}'",
            methodName,
            newMethodName));

        return newNode;
      }

      return base.VisitInvocationExpression(node);
    }

    private static InvocationExpressionSyntax _replaceMethodName(
        InvocationExpressionSyntax node,
        string newMethodName) {
      if (node.Expression is MemberAccessExpressionSyntax memberAccess) {
        var newName = SyntaxFactory.IdentifierName(newMethodName)
            .WithLeadingTrivia(memberAccess.Name.GetLeadingTrivia())
            .WithTrailingTrivia(memberAccess.Name.GetTrailingTrivia());

        var newMemberAccess = memberAccess.WithName(newName);
        return node.WithExpression(newMemberAccess);
      }

      return node;
    }
  }
}
