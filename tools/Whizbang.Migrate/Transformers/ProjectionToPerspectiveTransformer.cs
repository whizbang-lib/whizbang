using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Migrate.Transformers;

/// <summary>
/// Transforms Marten projections to Whizbang perspectives.
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public sealed class ProjectionToPerspectiveTransformer : ICodeTransformer {
  /// <inheritdoc />
  public Task<TransformationResult> TransformAsync(
      string sourceCode,
      string filePath,
      CancellationToken ct = default) {
    var changes = new List<CodeChange>();
    var warnings = new List<string>();

    var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: ct);
    var root = tree.GetRoot(ct);

    // Check if there are any projections to transform
    var hasProjections = _hasMartenProjections(root);
    if (!hasProjections) {
      return Task.FromResult(new TransformationResult(
          sourceCode,
          sourceCode,
          changes,
          warnings));
    }

    // Apply transformations
    var newRoot = root;

    // 1. Transform using directives
    newRoot = _transformUsings(newRoot, changes);

    // 2. Transform class declarations (projections -> perspectives)
    newRoot = _transformClasses(newRoot, changes);

    var transformedCode = newRoot.ToFullString();

    return Task.FromResult(new TransformationResult(
        sourceCode,
        transformedCode,
        changes,
        warnings));
  }

  private static bool _hasMartenProjections(SyntaxNode root) {
    var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

    foreach (var classDecl in classes) {
      if (classDecl.BaseList == null) {
        continue;
      }

      foreach (var baseType in classDecl.BaseList.Types) {
        var typeName = baseType.Type.ToString();
        if (typeName.StartsWith("SingleStreamProjection<", StringComparison.Ordinal) ||
            typeName.StartsWith("MultiStreamProjection<", StringComparison.Ordinal)) {
          return true;
        }
      }
    }

    return false;
  }

  private static SyntaxNode _transformUsings(SyntaxNode root, List<CodeChange> changes) {
    var compilationUnit = root as CompilationUnitSyntax;
    if (compilationUnit == null) {
      return root;
    }

    var hasMartenUsing = compilationUnit.Usings
        .Any(u => u.Name?.ToString() == "Marten.Events.Aggregation");

    if (!hasMartenUsing) {
      return root;
    }

    var newUsings = new List<UsingDirectiveSyntax>();

    foreach (var usingDirective in compilationUnit.Usings) {
      var name = usingDirective.Name?.ToString();

      if (name == "Marten.Events.Aggregation") {
        // Replace with Whizbang.Core.Perspectives
        var whizbangUsing = usingDirective
            .WithName(SyntaxFactory.ParseName("Whizbang.Core.Perspectives")
                .WithLeadingTrivia(usingDirective.Name?.GetLeadingTrivia() ?? SyntaxFactory.TriviaList())
                .WithTrailingTrivia(usingDirective.Name?.GetTrailingTrivia() ?? SyntaxFactory.TriviaList()));
        newUsings.Add(whizbangUsing);

        changes.Add(new CodeChange(
            usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.UsingRemoved,
            "Replaced 'using Marten.Events.Aggregation' with 'using Whizbang.Core.Perspectives'",
            "using Marten.Events.Aggregation;",
            "using Whizbang.Core.Perspectives;"));
      } else {
        newUsings.Add(usingDirective);
      }
    }

    return compilationUnit.WithUsings(SyntaxFactory.List(newUsings));
  }

  private static SyntaxNode _transformClasses(SyntaxNode root, List<CodeChange> changes) {
    var rewriter = new ProjectionToPerspectiveRewriter(changes);
    return rewriter.Visit(root);
  }

  /// <summary>
  /// Rewriter that transforms projection base types to perspective interfaces.
  /// </summary>
  private sealed class ProjectionToPerspectiveRewriter : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;

    public ProjectionToPerspectiveRewriter(List<CodeChange> changes) {
      _changes = changes;
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) {
      if (node.BaseList == null) {
        return base.VisitClassDeclaration(node);
      }

      var newBaseTypes = new List<BaseTypeSyntax>();
      var transformed = false;

      foreach (var baseType in node.BaseList.Types) {
        var typeName = baseType.Type.ToString();

        if (typeName.StartsWith("SingleStreamProjection<", StringComparison.Ordinal)) {
          // Extract aggregate type and event types from Apply methods
          var aggregateType = _extractGenericArgument(typeName);
          var eventTypes = _extractEventTypesFromClass(node);

          // Build IPerspectiveFor<TAggregate, TEvent1, TEvent2, ...>
          var perspectiveType = eventTypes.Count > 0
              ? $"IPerspectiveFor<{aggregateType}, {string.Join(", ", eventTypes)}>"
              : $"IPerspectiveFor<{aggregateType}>";

          var newType = SyntaxFactory.SimpleBaseType(
              SyntaxFactory.ParseTypeName(perspectiveType)
                  .WithLeadingTrivia(baseType.Type.GetLeadingTrivia())
                  .WithTrailingTrivia(baseType.Type.GetTrailingTrivia()));

          newBaseTypes.Add(newType);
          transformed = true;

          _changes.Add(new CodeChange(
              baseType.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
              ChangeType.BaseClassReplacement,
              $"Replaced '{typeName}' with '{perspectiveType}'",
              typeName,
              perspectiveType));
        } else if (typeName.StartsWith("MultiStreamProjection<", StringComparison.Ordinal)) {
          // Extract aggregate type
          var aggregateType = _extractGenericArgument(typeName);

          // Build IGlobalPerspectiveFor<TAggregate>
          var perspectiveType = $"IGlobalPerspectiveFor<{aggregateType}>";

          var newType = SyntaxFactory.SimpleBaseType(
              SyntaxFactory.ParseTypeName(perspectiveType)
                  .WithLeadingTrivia(baseType.Type.GetLeadingTrivia())
                  .WithTrailingTrivia(baseType.Type.GetTrailingTrivia()));

          newBaseTypes.Add(newType);
          transformed = true;

          _changes.Add(new CodeChange(
              baseType.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
              ChangeType.BaseClassReplacement,
              $"Replaced '{typeName}' with '{perspectiveType}'",
              typeName,
              perspectiveType));
        } else {
          newBaseTypes.Add(baseType);
        }
      }

      if (transformed) {
        var newBaseList = node.BaseList.WithTypes(
            SyntaxFactory.SeparatedList(newBaseTypes));
        return node.WithBaseList(newBaseList);
      }

      return base.VisitClassDeclaration(node);
    }

    private static string _extractGenericArgument(string typeName) {
      var start = typeName.IndexOf('<');
      var end = typeName.IndexOf(',');
      if (end < 0) {
        end = typeName.LastIndexOf('>');
      }

      if (start >= 0 && end > start) {
        return typeName.Substring(start + 1, end - start - 1).Trim();
      }

      return "unknown";
    }

    private static List<string> _extractEventTypesFromClass(ClassDeclarationSyntax classDecl) {
      var eventTypes = new List<string>();

      var applyMethods = classDecl.Members
          .OfType<MethodDeclarationSyntax>()
          .Where(m => m.Identifier.Text == "Apply");

      foreach (var method in applyMethods) {
        var parameters = method.ParameterList.Parameters;
        if (parameters.Count >= 1) {
          var eventType = parameters[0].Type?.ToString();
          if (!string.IsNullOrEmpty(eventType) && !eventTypes.Contains(eventType)) {
            eventTypes.Add(eventType);
          }
        }
      }

      return eventTypes;
    }
  }
}
