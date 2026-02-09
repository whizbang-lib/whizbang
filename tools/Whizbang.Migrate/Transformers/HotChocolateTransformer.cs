using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Migrate.Transformers;

/// <summary>
/// Transforms HotChocolate.Data.Marten patterns to Whizbang.Transports.HotChocolate equivalents.
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public sealed class HotChocolateTransformer : ICodeTransformer {
  /// <summary>
  /// Methods that should be replaced with AddWhizbangLenses.
  /// </summary>
  private static readonly HashSet<string> _methodsToReplaceWithLenses = new(StringComparer.Ordinal) {
    "AddMartenFiltering"
  };

  /// <summary>
  /// Methods that should be removed (functionality included in AddWhizbangLenses).
  /// </summary>
  private static readonly HashSet<string> _methodsToRemove = new(StringComparer.Ordinal) {
    "AddMartenSorting"
  };

  /// <summary>
  /// Using directives related to HotChocolate Marten integration.
  /// </summary>
  private static readonly HashSet<string> _martenUsings = new(StringComparer.Ordinal) {
    "HotChocolate.Data.Marten"
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

    // Check if there are any HotChocolate Marten patterns to transform
    if (!_hasHotChocolateMartenPatterns(root)) {
      return Task.FromResult(new TransformationResult(
          sourceCode,
          sourceCode,
          changes,
          warnings));
    }

    var newRoot = root;

    // 1. Transform method calls
    newRoot = _transformMethodCalls(newRoot, changes, warnings);

    // 2. Transform using directives
    newRoot = _transformUsings(newRoot, changes);

    // 3. Transform IMartenQueryable<T> to IQueryable<T>
    newRoot = _transformMartenQueryable(newRoot, changes);

    var transformedCode = newRoot.ToFullString();

    return Task.FromResult(new TransformationResult(
        sourceCode,
        transformedCode,
        changes,
        warnings));
  }

  private static bool _hasHotChocolateMartenPatterns(SyntaxNode root) {
    // Check for method calls
    var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
    foreach (var invocation in invocations) {
      var methodName = _getMethodName(invocation);
      if (methodName != null &&
          (_methodsToReplaceWithLenses.Contains(methodName) ||
           _methodsToRemove.Contains(methodName))) {
        return true;
      }
    }

    // Check for using directives
    if (root is CompilationUnitSyntax compilationUnit) {
      foreach (var usingDirective in compilationUnit.Usings) {
        var name = usingDirective.Name?.ToString();
        if (name != null && _martenUsings.Contains(name)) {
          return true;
        }
      }
    }

    // Check for IMartenQueryable usage
    var genericNames = root.DescendantNodes().OfType<GenericNameSyntax>();
    foreach (var genericName in genericNames) {
      if (genericName.Identifier.Text == "IMartenQueryable") {
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

  private static SyntaxNode _transformMethodCalls(
      SyntaxNode root,
      List<CodeChange> changes,
      List<string> warnings) {
    var rewriter = new HotChocolateMethodCallRewriter(changes, warnings);
    return rewriter.Visit(root);
  }

  private static SyntaxNode _transformUsings(SyntaxNode root, List<CodeChange> changes) {
    var compilationUnit = root as CompilationUnitSyntax;
    if (compilationUnit == null) {
      return root;
    }

    var hasMartenUsing = compilationUnit.Usings
        .Any(u => u.Name?.ToString() != null && _martenUsings.Contains(u.Name.ToString()!));

    if (!hasMartenUsing) {
      return root;
    }

    var newUsings = new List<UsingDirectiveSyntax>();
    var addedWhizbangHotChocolate = false;

    foreach (var usingDirective in compilationUnit.Usings) {
      var name = usingDirective.Name?.ToString();

      if (name != null && _martenUsings.Contains(name)) {
        if (!addedWhizbangHotChocolate) {
          // Replace with Whizbang.Transports.HotChocolate
          var whizbangUsing = usingDirective
              .WithName(SyntaxFactory.ParseName("Whizbang.Transports.HotChocolate")
                  .WithLeadingTrivia(usingDirective.Name?.GetLeadingTrivia() ?? SyntaxFactory.TriviaList())
                  .WithTrailingTrivia(usingDirective.Name?.GetTrailingTrivia() ?? SyntaxFactory.TriviaList()));
          newUsings.Add(whizbangUsing);
          addedWhizbangHotChocolate = true;

          changes.Add(new CodeChange(
              usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
              ChangeType.UsingRemoved,
              $"Replaced 'using {name}' with 'using Whizbang.Transports.HotChocolate'",
              $"using {name};",
              "using Whizbang.Transports.HotChocolate;"));
        } else {
          // Skip duplicate
          changes.Add(new CodeChange(
              usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
              ChangeType.UsingRemoved,
              $"Removed 'using {name}' (consolidated into Whizbang.Transports.HotChocolate)",
              $"using {name};",
              ""));
        }
      } else {
        newUsings.Add(usingDirective);
      }
    }

    return compilationUnit.WithUsings(SyntaxFactory.List(newUsings));
  }

  private static SyntaxNode _transformMartenQueryable(SyntaxNode root, List<CodeChange> changes) {
    var rewriter = new MartenQueryableRewriter(changes);
    return rewriter.Visit(root);
  }

  /// <summary>
  /// Rewriter that transforms HotChocolate Marten method calls to Whizbang equivalents.
  /// </summary>
  private sealed class HotChocolateMethodCallRewriter : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;
    private readonly List<string> _warnings;
    private bool _addedWhizbangLenses;

    public HotChocolateMethodCallRewriter(List<CodeChange> changes, List<string> warnings) {
      _changes = changes;
      _warnings = warnings;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node) {
      var methodName = _getMethodName(node);

      if (methodName == null) {
        return base.VisitInvocationExpression(node);
      }

      // Handle AddMartenFiltering -> AddWhizbangLenses
      if (_methodsToReplaceWithLenses.Contains(methodName)) {
        var newMethodName = "AddWhizbangLenses";
        var newNode = _replaceMethodName(node, newMethodName);
        _addedWhizbangLenses = true;

        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.MethodCallReplacement,
            $"Replaced '{methodName}()' with '{newMethodName}()' (includes filtering, sorting, and projections)",
            $".{methodName}()",
            $".{newMethodName}()"));

        return newNode;
      }

      // Handle AddMartenSorting - remove (functionality in AddWhizbangLenses)
      if (_methodsToRemove.Contains(methodName)) {
        if (node.Expression is MemberAccessExpressionSyntax memberAccess) {
          var description = _addedWhizbangLenses
              ? $"Removed '{methodName}()' (included in AddWhizbangLenses)"
              : $"Removed '{methodName}()' (add AddWhizbangLenses() manually for sorting support)";

          if (!_addedWhizbangLenses) {
            _warnings.Add($"Removed {methodName}() but AddWhizbangLenses() was not added. You may need to add it manually for sorting support.");
          }

          _changes.Add(new CodeChange(
              node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
              ChangeType.MethodCallReplacement,
              description,
              $".{methodName}()",
              ""));

          // Return just the expression part, removing the method call
          return Visit(memberAccess.Expression);
        }
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
        // Clear arguments since AddWhizbangLenses has different signature
        return node.WithExpression(newMemberAccess)
            .WithArgumentList(SyntaxFactory.ArgumentList());
      }

      return node;
    }
  }

  /// <summary>
  /// Rewriter that transforms IMartenQueryable to IQueryable.
  /// </summary>
  private sealed class MartenQueryableRewriter : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;

    public MartenQueryableRewriter(List<CodeChange> changes) {
      _changes = changes;
    }

    public override SyntaxNode? VisitGenericName(GenericNameSyntax node) {
      if (node.Identifier.Text == "IMartenQueryable") {
        var newNode = node.WithIdentifier(
            SyntaxFactory.Identifier("IQueryable")
                .WithLeadingTrivia(node.Identifier.LeadingTrivia)
                .WithTrailingTrivia(node.Identifier.TrailingTrivia));

        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.TypeRename,
            "Replaced 'IMartenQueryable<T>' with 'IQueryable<T>'",
            node.ToString(),
            newNode.ToString()));

        return newNode;
      }

      return base.VisitGenericName(node);
    }
  }
}
