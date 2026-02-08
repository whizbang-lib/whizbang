using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Migrate.Transformers;

/// <summary>
/// Transforms Wolverine.Http patterns to FastEndpoints equivalents.
/// Note: Full endpoint conversion requires manual intervention as the patterns are fundamentally different.
/// This transformer handles using statements and flags methods that need manual conversion.
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public sealed class WolverineHttpTransformer : ICodeTransformer {
  /// <summary>
  /// Wolverine HTTP attributes that indicate methods need conversion.
  /// </summary>
  private static readonly HashSet<string> _wolverineHttpAttributes = new(StringComparer.Ordinal) {
    "WolverineGet",
    "WolverinePost",
    "WolverinePut",
    "WolverineDelete",
    "WolverinePatch",
    "WolverineHead",
    "WolverineOptions"
  };

  /// <summary>
  /// Using directives related to Wolverine HTTP.
  /// </summary>
  private static readonly HashSet<string> _wolverineHttpUsings = new(StringComparer.Ordinal) {
    "Wolverine.Http",
    "WolverineFx.Http"
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

    // Check if there are any Wolverine HTTP patterns to transform
    if (!_hasWolverineHttpPatterns(root)) {
      return Task.FromResult(new TransformationResult(
          sourceCode,
          sourceCode,
          changes,
          warnings));
    }

    var newRoot = root;

    // 1. Transform using directives
    newRoot = _transformUsings(newRoot, changes);

    // 2. Detect and warn about Wolverine HTTP attributes (requires manual conversion)
    _detectHttpAttributesAndWarn(newRoot, warnings, changes);

    // 3. Remove Wolverine HTTP attributes (they won't compile without the package)
    newRoot = _removeWolverineHttpAttributes(newRoot);

    var transformedCode = newRoot.ToFullString();

    return Task.FromResult(new TransformationResult(
        sourceCode,
        transformedCode,
        changes,
        warnings));
  }

  private static bool _hasWolverineHttpPatterns(SyntaxNode root) {
    // Check for using directives
    if (root is CompilationUnitSyntax compilationUnit) {
      foreach (var usingDirective in compilationUnit.Usings) {
        var name = usingDirective.Name?.ToString();
        if (name != null && _wolverineHttpUsings.Contains(name)) {
          return true;
        }
      }
    }

    // Check for Wolverine HTTP attributes
    var attributes = root.DescendantNodes().OfType<AttributeSyntax>();
    foreach (var attr in attributes) {
      var attrName = _getAttributeName(attr);
      if (attrName != null && _wolverineHttpAttributes.Contains(attrName)) {
        return true;
      }
    }

    return false;
  }

  private static string? _getAttributeName(AttributeSyntax attribute) {
    return attribute.Name switch {
      IdentifierNameSyntax id => id.Identifier.Text,
      QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
      _ => attribute.Name.ToString()
    };
  }

  private static SyntaxNode _transformUsings(SyntaxNode root, List<CodeChange> changes) {
    var compilationUnit = root as CompilationUnitSyntax;
    if (compilationUnit == null) {
      return root;
    }

    var hasWolverineHttpUsing = compilationUnit.Usings
        .Any(u => u.Name?.ToString() != null && _wolverineHttpUsings.Contains(u.Name.ToString()!));

    if (!hasWolverineHttpUsing) {
      return root;
    }

    var newUsings = new List<UsingDirectiveSyntax>();
    var addedFastEndpoints = false;
    var addedWhizbangFastEndpoints = false;

    foreach (var usingDirective in compilationUnit.Usings) {
      var name = usingDirective.Name?.ToString();

      if (name != null && _wolverineHttpUsings.Contains(name)) {
        // Add FastEndpoints using if not already added
        if (!addedFastEndpoints) {
          var fastEndpointsUsing = usingDirective
              .WithName(SyntaxFactory.ParseName("FastEndpoints")
                  .WithLeadingTrivia(usingDirective.Name?.GetLeadingTrivia() ?? SyntaxFactory.TriviaList())
                  .WithTrailingTrivia(usingDirective.Name?.GetTrailingTrivia() ?? SyntaxFactory.TriviaList()));
          newUsings.Add(fastEndpointsUsing);
          addedFastEndpoints = true;

          changes.Add(new CodeChange(
              usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
              ChangeType.UsingRemoved,
              $"Replaced 'using {name}' with 'using FastEndpoints'",
              $"using {name};",
              "using FastEndpoints;"));
        }

        // Add Whizbang FastEndpoints using
        if (!addedWhizbangFastEndpoints) {
          var whizbangUsing = SyntaxFactory.UsingDirective(
              SyntaxFactory.ParseName("Whizbang.Transports.FastEndpoints"))
              .WithLeadingTrivia(usingDirective.GetLeadingTrivia())
              .WithTrailingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.EndOfLine("\n")));
          newUsings.Add(whizbangUsing);
          addedWhizbangFastEndpoints = true;

          changes.Add(new CodeChange(
              usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
              ChangeType.UsingAdded,
              "Added 'using Whizbang.Transports.FastEndpoints' for FastEndpoints integration",
              "",
              "using Whizbang.Transports.FastEndpoints;"));
        }
      } else {
        newUsings.Add(usingDirective);
      }
    }

    return compilationUnit.WithUsings(SyntaxFactory.List(newUsings));
  }

  private static void _detectHttpAttributesAndWarn(
      SyntaxNode root,
      List<string> warnings,
      List<CodeChange> changes) {
    var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

    foreach (var method in methods) {
      _processMethodForHttpAttributes(method, warnings, changes);
    }
  }

  private static void _processMethodForHttpAttributes(
      MethodDeclarationSyntax method,
      List<string> warnings,
      List<CodeChange> changes) {
    var attributes = method.AttributeLists.SelectMany(al => al.Attributes);

    foreach (var attr in attributes) {
      var attrName = _getAttributeName(attr);
      if (attrName == null || !_wolverineHttpAttributes.Contains(attrName)) {
        continue;
      }

      _addHttpAttributeWarning(method, attr, attrName, warnings, changes);
    }
  }

  private static void _addHttpAttributeWarning(
      MethodDeclarationSyntax method,
      AttributeSyntax attr,
      string attrName,
      List<string> warnings,
      List<CodeChange> changes) {
    var httpMethod = attrName.Replace("Wolverine", "").ToUpperInvariant();
    var route = _extractRouteFromAttribute(attr);
    var methodName = method.Identifier.Text;
    var className = method.Parent is ClassDeclarationSyntax classDecl
        ? classDecl.Identifier.Text
        : "Unknown";

    var lineNumber = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    warnings.Add(
        $"MANUAL CONVERSION REQUIRED: {className}.{methodName}() has [{attrName}(\"{route}\")] - " +
        $"Convert to FastEndpoints Endpoint<TRequest, TResponse> class with Configure() and HandleAsync() methods.");

    changes.Add(new CodeChange(
        lineNumber,
        ChangeType.AttributeRemoved,
        $"[{attrName}] requires manual conversion to FastEndpoints pattern - " +
        $"Create new Endpoint<TRequest, TResponse> class with {httpMethod}(\"{route}\") in Configure()",
        $"[{attrName}(\"{route}\")]",
        "// TODO: Convert to FastEndpoints endpoint class"));
  }

  private static string _extractRouteFromAttribute(AttributeSyntax attr) {
    if (attr.ArgumentList?.Arguments.Count > 0) {
      var firstArg = attr.ArgumentList.Arguments[0];
      if (firstArg.Expression is LiteralExpressionSyntax literal) {
        return literal.Token.ValueText;
      }
      return firstArg.Expression.ToString().Trim('"');
    }
    return "/";
  }

  private static SyntaxNode _removeWolverineHttpAttributes(SyntaxNode root) {
    var rewriter = new WolverineHttpAttributeRemover();
    return rewriter.Visit(root);
  }

  /// <summary>
  /// Rewriter that removes Wolverine HTTP attributes and adds TODO comments.
  /// </summary>
  private sealed class WolverineHttpAttributeRemover : CSharpSyntaxRewriter {

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) {
      var hasWolverineHttpAttr = false;
      string? attrName = null;
      string? route = null;

      foreach (var attrList in node.AttributeLists) {
        foreach (var attr in attrList.Attributes) {
          var name = _getAttributeName(attr);
          if (name != null && _wolverineHttpAttributes.Contains(name)) {
            hasWolverineHttpAttr = true;
            attrName = name;
            route = _extractRouteFromAttribute(attr);
            break;
          }
        }
        if (hasWolverineHttpAttr) {
          break;
        }
      }

      if (!hasWolverineHttpAttr) {
        return base.VisitMethodDeclaration(node);
      }

      // Remove Wolverine HTTP attribute lists
      var newAttributeLists = new List<AttributeListSyntax>();
      foreach (var attrList in node.AttributeLists) {
        var newAttributes = attrList.Attributes
            .Where(attr => {
              var name = _getAttributeName(attr);
              return name == null || !_wolverineHttpAttributes.Contains(name);
            })
            .ToList();

        if (newAttributes.Count > 0) {
          newAttributeLists.Add(attrList.WithAttributes(SyntaxFactory.SeparatedList(newAttributes)));
        }
      }

      // Add TODO comment - attrName is guaranteed non-null when hasWolverineHttpAttr is true
      var httpMethod = attrName!.Replace("Wolverine", "").ToUpperInvariant();
      var todoComment = SyntaxFactory.Comment(
          $"// TODO: Convert to FastEndpoints - Create Endpoint<TRequest, TResponse> class with {httpMethod}(\"{route}\") in Configure()\n");

      var leadingTrivia = node.GetLeadingTrivia().Insert(0, todoComment);

      var newNode = node
          .WithAttributeLists(SyntaxFactory.List(newAttributeLists))
          .WithLeadingTrivia(leadingTrivia);

      return newNode;
    }
  }
}
