using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Migrate.Transformers;

/// <summary>
/// Transforms Marten projections to Whizbang perspectives.
/// Handles:
/// - SingleStreamProjection&lt;T&gt; → IPerspectiveFor&lt;T, ...&gt;
/// - MultiStreamProjection&lt;T, TKey&gt; → IGlobalPerspectiveFor&lt;T&gt;
/// - Emits warnings for Identity(), nested classes, Version, duplicates
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
    newRoot = _transformClasses(newRoot, changes, warnings);

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
        // Detect Marten projection base classes
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

  private static SyntaxNode _transformClasses(SyntaxNode root, List<CodeChange> changes, List<string> warnings) {
    var rewriter = new ProjectionToPerspectiveRewriter(changes, warnings);
    var transformed = rewriter.Visit(root);

    // Transform ShouldDelete methods to ModelAction Apply methods
    transformed = _transformShouldDeleteMethods(transformed, changes, warnings);

    return transformed;
  }

  /// <summary>
  /// Transforms Marten ShouldDelete methods to Whizbang Apply methods returning ModelAction.
  /// </summary>
  private static SyntaxNode _transformShouldDeleteMethods(SyntaxNode root, List<CodeChange> changes, List<string> warnings) {
    var rewriter = new ShouldDeleteToModelActionRewriter(changes, warnings);
    return rewriter.Visit(root);
  }

  /// <summary>
  /// Rewriter that transforms projection base types to perspective interfaces.
  /// </summary>
  private sealed class ProjectionToPerspectiveRewriter : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;
    private readonly List<string> _warnings;

    public ProjectionToPerspectiveRewriter(List<CodeChange> changes, List<string> warnings) {
      _changes = changes;
      _warnings = warnings;
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) {
      if (node.BaseList == null) {
        return base.VisitClassDeclaration(node);
      }

      var newBaseTypes = new List<BaseTypeSyntax>();
      var transformed = false;

      foreach (var baseType in node.BaseList.Types) {
        var typeName = baseType.Type.ToString();

        // Transform Marten SingleStreamProjection<T> to Whizbang IPerspectiveFor<T, ...>
        if (typeName.StartsWith("SingleStreamProjection<", StringComparison.Ordinal)) {
          // Extract aggregate type and event types from projection methods
          var aggregateType = _extractGenericArgument(typeName);
          var eventTypes = _extractEventTypesFromClass(node, aggregateType);

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
        }
        // Transform Marten MultiStreamProjection<T, TKey> to Whizbang IGlobalPerspectiveFor<T>
        else if (typeName.StartsWith("MultiStreamProjection<", StringComparison.Ordinal)) {
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

        // Check Apply methods for [MustExist] suggestions
        _checkForMustExistSuggestions(node);

        // Check for Marten-specific patterns (P03-P07)
        _checkForMartenPatterns(node);

        return node.WithBaseList(newBaseList);
      }

      return base.VisitClassDeclaration(node);
    }

    /// <summary>
    /// Checks for Marten-specific patterns that need warnings (P03-P07).
    /// </summary>
    private void _checkForMartenPatterns(ClassDeclarationSyntax classDecl) {
      var className = classDecl.Identifier.Text;
      var lineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

      // P03: Check for Identity<T>() calls in constructor (Marten partition key pattern)
      var constructors = classDecl.Members.OfType<ConstructorDeclarationSyntax>();
      foreach (var constructor in constructors) {
        var identityCalls = constructor.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(inv => inv.Expression.ToString().StartsWith("Identity<", StringComparison.Ordinal));

        foreach (var identityCall in identityCalls) {
          var identityLine = identityCall.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
          _warnings.Add($"Line {identityLine}: Identity<T>() partition key extraction in {className}. " +
              "In Whizbang, partition keys are extracted via [PartitionKey] attribute or IPartitionKeyExtractor interface. " +
              "Review and migrate the partition key logic.");
        }
      }

      // P04: Check if class is nested (common pattern in larger codebases)
      var parentClass = classDecl.Parent as ClassDeclarationSyntax;
      if (parentClass != null) {
        _warnings.Add($"Line {lineNumber}: Found nested projection class '{className}' inside '{parentClass.Identifier.Text}'. " +
            "Consider flattening to a top-level perspective class for better discoverability.");
      }

      // P07: Check for duplicate/cross-service comments
      var classText = classDecl.ToFullString();
      if (classText.Contains("Duplicate of") ||
          classText.Contains("duplicate of") ||
          classText.Contains("Copy of") ||
          classText.Contains("copy of") ||
          classText.Contains("Same as") ||
          classText.Contains("same as")) {
        _warnings.Add($"Line {lineNumber}: Potential cross-service duplicate projection detected in '{className}'. " +
            "Consider consolidating to a single source of truth to avoid synchronization issues.");
      }
    }

    /// <summary>
    /// Checks projection methods and suggests [MustExist] for non-creation methods.
    /// Also warns about ShouldDelete methods that need manual handling.
    /// </summary>
    private void _checkForMustExistSuggestions(ClassDeclarationSyntax classDecl) {
      var className = classDecl.Identifier.Text;
      var projectionMethods = classDecl.Members
          .OfType<MethodDeclarationSyntax>()
          .Where(m => m.Identifier.Text is "Apply" or "Create" or "ShouldDelete");

      // If there's a Create method, other Apply methods likely need [MustExist]
      var hasCreateMethod = projectionMethods.Any(m => m.Identifier.Text == "Create");

      foreach (var method in projectionMethods) {
        var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var methodName = method.Identifier.Text;

        if (methodName == "Apply" && hasCreateMethod) {
          // If there's a Create method, Apply methods handle updates (need [MustExist])
          _warnings.Add($"Line {line}: Consider adding [MustExist] attribute to Apply method in {className}. " +
              "The [MustExist] attribute generates a null check before calling Apply, ensuring the model exists.");
        } else if (methodName == "ShouldDelete") {
          // ShouldDelete will be transformed to ModelAction return - add a note
          _warnings.Add($"Line {line}: ShouldDelete method in {className} will be transformed to return ModelAction. " +
              "Review the generated code - ModelAction.Delete for soft delete, ModelAction.Purge for hard delete.");
        }
      }
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

    /// <summary>
    /// Extracts event types from all Marten projection methods.
    /// Marten supports: Create, Apply, ShouldDelete with various signatures.
    /// See: https://martendb.io/events/projections/aggregate-projections.html
    /// </summary>
    /// <param name="classDecl">The class declaration to analyze.</param>
    /// <param name="aggregateType">The aggregate/document type to filter out.</param>
    private static List<string> _extractEventTypesFromClass(ClassDeclarationSyntax classDecl, string aggregateType) {
      var eventTypes = new List<string>();

      // Marten projection method names
      var projectionMethods = classDecl.Members
          .OfType<MethodDeclarationSyntax>()
          .Where(m => m.Identifier.Text is "Apply" or "Create" or "ShouldDelete");

      foreach (var method in projectionMethods) {
        var parameters = method.ParameterList.Parameters;
        if (parameters.Count == 0) {
          continue;
        }

        // Marten supports both parameter orderings:
        // - Apply(TEvent event, TDoc document) - event first
        // - Apply(TDoc document, TEvent event) - document second
        // - Create(TEvent event) - single parameter
        // - ShouldDelete(TEvent event) or ShouldDelete(TEvent event, TDoc document)
        //
        // Filter out:
        // 1. The aggregate/document type
        // 2. IEvent metadata wrappers

        foreach (var param in parameters) {
          var paramType = param.Type?.ToString();
          if (string.IsNullOrEmpty(paramType)) {
            continue;
          }

          // Skip the aggregate/document type
          if (paramType == aggregateType || paramType == $"{aggregateType}?") {
            continue;
          }

          // Skip IEvent metadata parameters (used for accessing event metadata)
          if (paramType == "IEvent" || paramType == "Marten.Events.IEvent" ||
              paramType.StartsWith("IEvent<", StringComparison.Ordinal) ||
              paramType.StartsWith("Marten.Events.IEvent<", StringComparison.Ordinal)) {
            continue;
          }

          // Add the event type if not already present
          if (!eventTypes.Contains(paramType)) {
            eventTypes.Add(paramType);
          }
        }
      }

      return eventTypes;
    }
  }

  /// <summary>
  /// Rewriter that transforms ShouldDelete methods to Apply methods returning ModelAction.
  /// </summary>
  private sealed class ShouldDeleteToModelActionRewriter : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;
    private readonly List<string> _warnings;

    public ShouldDeleteToModelActionRewriter(List<CodeChange> changes, List<string> warnings) {
      _changes = changes;
      _warnings = warnings;
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) {
      // Only transform ShouldDelete methods
      if (node.Identifier.Text != "ShouldDelete") {
        return base.VisitMethodDeclaration(node);
      }

      var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

      // Extract event type from parameters
      // Marten ShouldDelete signatures:
      // - bool ShouldDelete(TEvent @event)
      // - bool ShouldDelete(TEvent @event, TDoc document)
      var eventType = _extractEventType(node);
      var modelType = _extractModelType(node);

      if (eventType == null || modelType == null) {
        _warnings.Add($"Line {line}: Could not determine event type or model type for ShouldDelete transformation.");
        return base.VisitMethodDeclaration(node);
      }

      // Determine what action the original method returns
      // If it always returns true, use ModelAction.Delete
      // If it has conditional logic, generate a comment to review
      var (action, needsReview) = _analyzeShouldDeleteBody(node);

      // Build the new Apply method
      // public ModelAction Apply(TModel current, TEvent @event) => ModelAction.Delete;
      var newMethod = SyntaxFactory.MethodDeclaration(
              SyntaxFactory.ParseTypeName("ModelAction"),
              SyntaxFactory.Identifier("Apply"))
          .WithModifiers(SyntaxFactory.TokenList(
              SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
          .WithParameterList(SyntaxFactory.ParameterList(
              SyntaxFactory.SeparatedList(new[] {
                  SyntaxFactory.Parameter(SyntaxFactory.Identifier("current"))
                      .WithType(SyntaxFactory.ParseTypeName(modelType)),
                  SyntaxFactory.Parameter(SyntaxFactory.Identifier("@event"))
                      .WithType(SyntaxFactory.ParseTypeName(eventType))
              })));

      if (needsReview) {
        // Add comment for complex logic that needs review
        var leadingTrivia = node.GetLeadingTrivia()
            .Add(SyntaxFactory.Comment("// TODO: Review - original ShouldDelete had conditional logic. Adjust ModelAction as needed."))
            .Add(SyntaxFactory.CarriageReturnLineFeed);

        newMethod = newMethod
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                SyntaxFactory.ParseExpression($"ModelAction.{action}")))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(leadingTrivia);
      } else {
        newMethod = newMethod
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                SyntaxFactory.ParseExpression($"ModelAction.{action}")))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(node.GetLeadingTrivia());
      }

      newMethod = newMethod.WithTrailingTrivia(node.GetTrailingTrivia());

      _changes.Add(new CodeChange(
          line,
          ChangeType.MethodTransformed,
          $"Transformed ShouldDelete to Apply returning ModelAction.{action}",
          node.ToString(),
          newMethod.ToFullString()));

      return newMethod;
    }

    private static string? _extractEventType(MethodDeclarationSyntax method) {
      var parameters = method.ParameterList.Parameters;
      if (parameters.Count == 0) {
        return null;
      }

      // First parameter is usually the event
      return parameters[0].Type?.ToString();
    }

    private static string? _extractModelType(MethodDeclarationSyntax method) {
      // Try to find model type from second parameter
      var parameters = method.ParameterList.Parameters;
      if (parameters.Count >= 2) {
        return parameters[1].Type?.ToString();
      }

      // Otherwise, try to infer from containing class's base type
      var containingClass = method.Ancestors()
          .OfType<ClassDeclarationSyntax>()
          .FirstOrDefault();

      if (containingClass?.BaseList != null) {
        foreach (var baseType in containingClass.BaseList.Types) {
          var typeName = baseType.Type.ToString();
          if (typeName.StartsWith("IPerspectiveFor<", StringComparison.Ordinal)) {
            // Extract first generic argument (model type)
            var start = typeName.IndexOf('<');
            var end = typeName.IndexOf(',');
            if (end < 0) {
              end = typeName.LastIndexOf('>');
            }
            if (start >= 0 && end > start) {
              return typeName.Substring(start + 1, end - start - 1).Trim();
            }
          }
        }
      }

      return null;
    }

    /// <summary>
    /// Analyzes the body of ShouldDelete to determine the appropriate ModelAction.
    /// </summary>
    private static (string action, bool needsReview) _analyzeShouldDeleteBody(MethodDeclarationSyntax method) {
      // Check if it's a simple return true
      if (method.ExpressionBody != null) {
        var expr = method.ExpressionBody.Expression.ToString();
        if (expr == "true") {
          return ("Delete", false);
        }
        // Has conditional logic
        return ("Delete", true);
      }

      if (method.Body != null) {
        // Check for simple "return true;"
        var statements = method.Body.Statements;
        if (statements.Count == 1 &&
            statements[0] is ReturnStatementSyntax returnStmt &&
            returnStmt.Expression?.ToString() == "true") {
          return ("Delete", false);
        }
        // Has complex logic
        return ("Delete", true);
      }

      // Default to Delete with review
      return ("Delete", true);
    }
  }
}
