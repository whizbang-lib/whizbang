using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Migrate.Transformers;

/// <summary>
/// Transforms Wolverine IHandle handlers to Whizbang IReceptor receptors.
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public sealed class HandlerToReceptorTransformer : ICodeTransformer {
  /// <inheritdoc />
  public Task<TransformationResult> TransformAsync(
      string sourceCode,
      string filePath,
      CancellationToken ct = default) {
    var changes = new List<CodeChange>();
    var warnings = new List<string>();

    var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: ct);
    var root = tree.GetRoot(ct);

    // Check if there are any handlers to transform
    var hasHandlers = _hasWolverineHandlers(root);
    if (!hasHandlers) {
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

    // 2. Transform class declarations (IHandle -> IReceptor)
    newRoot = _transformClasses(newRoot, changes);

    // 3. Transform method names (Handle -> ReceiveAsync)
    newRoot = _transformMethods(newRoot, changes);

    // 4. Remove [WolverineHandler] attributes
    newRoot = _removeWolverineAttributes(newRoot, changes);

    var transformedCode = newRoot.ToFullString();

    return Task.FromResult(new TransformationResult(
        sourceCode,
        transformedCode,
        changes,
        warnings));
  }

  private static bool _hasWolverineHandlers(SyntaxNode root) {
    var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

    foreach (var classDecl in classes) {
      // Check for IHandle interface
      if (classDecl.BaseList != null) {
        foreach (var baseType in classDecl.BaseList.Types) {
          var typeName = baseType.Type.ToString();
          if (typeName.StartsWith("IHandle<", StringComparison.Ordinal)) {
            return true;
          }
        }
      }

      // Check for [WolverineHandler] attribute
      if (classDecl.AttributeLists.SelectMany(al => al.Attributes)
          .Any(a => a.Name.ToString() is "WolverineHandler" or "WolverineHandlerAttribute")) {
        return true;
      }
    }

    return false;
  }

  private static SyntaxNode _transformUsings(SyntaxNode root, List<CodeChange> changes) {
    var compilationUnit = root as CompilationUnitSyntax;
    if (compilationUnit == null) {
      return root;
    }

    var hasWolverineUsing = compilationUnit.Usings
        .Any(u => u.Name?.ToString() == "Wolverine");

    if (!hasWolverineUsing) {
      return root;
    }

    // Remove Wolverine using and add Whizbang.Core
    var newUsings = new List<UsingDirectiveSyntax>();
    var addedWhizbang = false;

    foreach (var usingDirective in compilationUnit.Usings) {
      var name = usingDirective.Name?.ToString();

      if (name == "Wolverine") {
        // Replace with Whizbang.Core - preserve original formatting
        var whizbangUsing = usingDirective
            .WithName(SyntaxFactory.ParseName("Whizbang.Core")
                .WithLeadingTrivia(usingDirective.Name?.GetLeadingTrivia() ?? SyntaxFactory.TriviaList())
                .WithTrailingTrivia(usingDirective.Name?.GetTrailingTrivia() ?? SyntaxFactory.TriviaList()));
        newUsings.Add(whizbangUsing);
        addedWhizbang = true;

        changes.Add(new CodeChange(
            usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.UsingRemoved,
            "Replaced 'using Wolverine' with 'using Whizbang.Core'",
            "using Wolverine;",
            "using Whizbang.Core;"));
      } else {
        newUsings.Add(usingDirective);
      }
    }

    // If no Whizbang using was added (Wolverine wasn't found), add it
    if (!addedWhizbang) {
      var whizbangUsing = SyntaxFactory.UsingDirective(
          SyntaxFactory.ParseName("Whizbang.Core"))
          .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
      newUsings.Insert(0, whizbangUsing);

      changes.Add(new CodeChange(
          1,
          ChangeType.UsingAdded,
          "Added 'using Whizbang.Core'",
          "",
          "using Whizbang.Core;"));
    }

    return compilationUnit.WithUsings(SyntaxFactory.List(newUsings));
  }

  private static SyntaxNode _transformClasses(SyntaxNode root, List<CodeChange> changes) {
    var rewriter = new HandlerToReceptorRewriter(changes);
    return rewriter.Visit(root);
  }

  private static SyntaxNode _transformMethods(SyntaxNode root, List<CodeChange> changes) {
    var rewriter = new HandleMethodRewriter(changes);
    return rewriter.Visit(root);
  }

  private static SyntaxNode _removeWolverineAttributes(SyntaxNode root, List<CodeChange> changes) {
    var rewriter = new WolverineAttributeRemover(changes);
    return rewriter.Visit(root);
  }

  /// <summary>
  /// Rewriter that transforms IHandle to IReceptor in base lists.
  /// </summary>
  private sealed class HandlerToReceptorRewriter : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;

    public HandlerToReceptorRewriter(List<CodeChange> changes) {
      _changes = changes;
    }

    public override SyntaxNode? VisitSimpleBaseType(SimpleBaseTypeSyntax node) {
      var typeName = node.Type.ToString();

      if (typeName.StartsWith("IHandle<", StringComparison.Ordinal)) {
        // Extract generic arguments
        var newTypeName = typeName.Replace("IHandle<", "IReceptor<");
        var newType = SyntaxFactory.ParseTypeName(newTypeName)
            .WithLeadingTrivia(node.Type.GetLeadingTrivia())
            .WithTrailingTrivia(node.Type.GetTrailingTrivia());

        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.InterfaceReplacement,
            $"Replaced '{typeName}' with '{newTypeName}'",
            typeName,
            newTypeName));

        return node.WithType(newType);
      }

      return base.VisitSimpleBaseType(node);
    }
  }

  /// <summary>
  /// Rewriter that transforms Handle methods to ReceiveAsync.
  /// </summary>
  private sealed class HandleMethodRewriter : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;

    public HandleMethodRewriter(List<CodeChange> changes) {
      _changes = changes;
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) {
      var methodName = node.Identifier.Text;

      // Only transform Handle methods in classes that implement IReceptor
      if (methodName is "Handle" or "HandleAsync") {
        var classDecl = node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (classDecl?.BaseList != null) {
          var implementsReceptor = classDecl.BaseList.Types
              .Any(t => t.Type.ToString().StartsWith("IReceptor<", StringComparison.Ordinal) ||
                        t.Type.ToString().StartsWith("IHandle<", StringComparison.Ordinal));

          if (implementsReceptor) {
            var newName = "ReceiveAsync";
            var newIdentifier = SyntaxFactory.Identifier(newName)
                .WithLeadingTrivia(node.Identifier.LeadingTrivia)
                .WithTrailingTrivia(node.Identifier.TrailingTrivia);

            _changes.Add(new CodeChange(
                node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                ChangeType.MethodSignatureChange,
                $"Renamed method '{methodName}' to '{newName}'",
                methodName,
                newName));

            return node.WithIdentifier(newIdentifier);
          }
        }
      }

      return base.VisitMethodDeclaration(node);
    }
  }

  /// <summary>
  /// Rewriter that removes [WolverineHandler] attributes.
  /// </summary>
  private sealed class WolverineAttributeRemover : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;

    public WolverineAttributeRemover(List<CodeChange> changes) {
      _changes = changes;
    }

    public override SyntaxNode? VisitAttributeList(AttributeListSyntax node) {
      var newAttributes = new List<AttributeSyntax>();
      var removedAny = false;

      foreach (var attr in node.Attributes) {
        var attrName = attr.Name.ToString();
        if (attrName is "WolverineHandler" or "WolverineHandlerAttribute") {
          _changes.Add(new CodeChange(
              attr.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
              ChangeType.AttributeRemoved,
              $"Removed [{attrName}] attribute",
              $"[{attrName}]",
              ""));
          removedAny = true;
        } else {
          newAttributes.Add(attr);
        }
      }

      if (removedAny) {
        if (newAttributes.Count == 0) {
          // Remove the entire attribute list if empty
          return null;
        }

        return node.WithAttributes(SyntaxFactory.SeparatedList(newAttributes));
      }

      return base.VisitAttributeList(node);
    }
  }
}
