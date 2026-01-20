using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Migrate.Transformers;

/// <summary>
/// Transforms Guid.NewGuid() and Guid.CreateVersion7() calls to use IWhizbangIdProvider.
/// Handles:
/// - Guid.NewGuid() → _idProvider.NewGuid()
/// - Guid.CreateVersion7() → _idProvider.NewGuid()
/// - Adds IWhizbangIdProvider to primary constructor parameters
/// - Adds using Whizbang.Core; directive
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public sealed class GuidToIdProviderTransformer : ICodeTransformer {
  /// <inheritdoc />
  public Task<TransformationResult> TransformAsync(
      string sourceCode,
      string filePath,
      CancellationToken ct = default) {
    var changes = new List<CodeChange>();
    var warnings = new List<string>();

    var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: ct);
    var root = tree.GetRoot(ct);

    // Check if there are any Guid generation patterns to transform
    var hasGuidPatterns = _hasGuidGenerationPatterns(root);
    if (!hasGuidPatterns) {
      return Task.FromResult(new TransformationResult(
          sourceCode,
          sourceCode,
          changes,
          warnings));
    }

    // Apply transformations
    var newRoot = root;

    // 1. Add using directive for Whizbang.Core if not present
    newRoot = _ensureWhizbangCoreUsing(newRoot, changes);

    // 2. Add IWhizbangIdProvider to primary constructor parameters
    newRoot = _addIdProviderToConstructor(newRoot, changes, warnings);

    // 3. Transform Guid.NewGuid() and Guid.CreateVersion7() calls
    newRoot = _transformGuidCalls(newRoot, changes);

    var transformedCode = newRoot.ToFullString();

    return Task.FromResult(new TransformationResult(
        sourceCode,
        transformedCode,
        changes,
        warnings));
  }

  private static bool _hasGuidGenerationPatterns(SyntaxNode root) {
    return root.DescendantNodes()
        .OfType<InvocationExpressionSyntax>()
        .Any(inv => {
          var expr = inv.Expression.ToString();
          return expr == "Guid.NewGuid" ||
                 expr == "Guid.CreateVersion7" ||
                 expr == "System.Guid.NewGuid" ||
                 expr == "System.Guid.CreateVersion7";
        });
  }

  private static SyntaxNode _ensureWhizbangCoreUsing(SyntaxNode root, List<CodeChange> changes) {
    var compilationUnit = root as CompilationUnitSyntax;
    if (compilationUnit == null) {
      return root;
    }

    // Check if Whizbang.Core using already exists
    var hasWhizbangCore = compilationUnit.Usings
        .Any(u => u.Name?.ToString() == "Whizbang.Core");

    if (hasWhizbangCore) {
      return root;
    }

    // Find the best position to insert (after System usings, before others)
    var usings = compilationUnit.Usings.ToList();
    var insertIndex = usings.FindLastIndex(u =>
        u.Name?.ToString()?.StartsWith("System", StringComparison.Ordinal) == true);
    insertIndex = insertIndex >= 0 ? insertIndex + 1 : 0;

    var newUsing = SyntaxFactory.UsingDirective(
            SyntaxFactory.ParseName("Whizbang.Core"))
        .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

    usings.Insert(insertIndex, newUsing);

    changes.Add(new CodeChange(
        insertIndex + 1,
        ChangeType.UsingAdded,
        "Added 'using Whizbang.Core' for IWhizbangIdProvider",
        "",
        "using Whizbang.Core;"));

    return compilationUnit.WithUsings(SyntaxFactory.List(usings));
  }

  private static SyntaxNode _addIdProviderToConstructor(
      SyntaxNode root,
      List<CodeChange> changes,
      List<string> warnings) {
    var rewriter = new ConstructorParameterRewriter(changes, warnings);
    return rewriter.Visit(root);
  }

  private static SyntaxNode _transformGuidCalls(SyntaxNode root, List<CodeChange> changes) {
    var rewriter = new GuidCallRewriter(changes);
    return rewriter.Visit(root);
  }

  /// <summary>
  /// Rewriter that adds IWhizbangIdProvider to primary constructor parameters.
  /// </summary>
  private sealed class ConstructorParameterRewriter : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;
    private readonly List<string> _warnings;

    public ConstructorParameterRewriter(List<CodeChange> changes, List<string> warnings) {
      _changes = changes;
      _warnings = warnings;
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) {
      // Check if this class has Guid generation calls
      var hasGuidCalls = node.DescendantNodes()
          .OfType<InvocationExpressionSyntax>()
          .Any(inv => {
            var expr = inv.Expression.ToString();
            return expr == "Guid.NewGuid" ||
                   expr == "Guid.CreateVersion7" ||
                   expr == "System.Guid.NewGuid" ||
                   expr == "System.Guid.CreateVersion7";
          });

      if (!hasGuidCalls) {
        return base.VisitClassDeclaration(node);
      }

      // Check for primary constructor (parameter list after class name)
      if (node.ParameterList != null) {
        // Check if IWhizbangIdProvider already exists
        var hasIdProvider = node.ParameterList.Parameters
            .Any(p => p.Type?.ToString() == "IWhizbangIdProvider");

        if (!hasIdProvider) {
          // Add IWhizbangIdProvider parameter
          var newParam = SyntaxFactory.Parameter(
                  SyntaxFactory.Identifier("idProvider"))
              .WithType(SyntaxFactory.ParseTypeName("IWhizbangIdProvider ")
                  .WithTrailingTrivia(SyntaxFactory.Space));

          var newParams = node.ParameterList.Parameters.Add(newParam);
          var newParamList = node.ParameterList.WithParameters(newParams);

          _changes.Add(new CodeChange(
              node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
              ChangeType.MethodSignatureChange,
              "Added 'IWhizbangIdProvider idProvider' to primary constructor",
              node.ParameterList.ToString(),
              newParamList.ToString()));

          node = node.WithParameterList(newParamList);
        }
      } else {
        // No primary constructor - warn about manual injection needed
        _warnings.Add($"Line {node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: " +
            $"Class '{node.Identifier.Text}' uses Guid generation but has no primary constructor. " +
            "Manually add: private readonly IWhizbangIdProvider _idProvider; and inject via constructor.");
      }

      return base.VisitClassDeclaration(node);
    }

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) {
      // Check if this record has Guid generation calls
      var hasGuidCalls = node.DescendantNodes()
          .OfType<InvocationExpressionSyntax>()
          .Any(inv => {
            var expr = inv.Expression.ToString();
            return expr == "Guid.NewGuid" ||
                   expr == "Guid.CreateVersion7" ||
                   expr == "System.Guid.NewGuid" ||
                   expr == "System.Guid.CreateVersion7";
          });

      if (!hasGuidCalls) {
        return base.VisitRecordDeclaration(node);
      }

      // Check for primary constructor
      if (node.ParameterList != null) {
        var hasIdProvider = node.ParameterList.Parameters
            .Any(p => p.Type?.ToString() == "IWhizbangIdProvider");

        if (!hasIdProvider) {
          var newParam = SyntaxFactory.Parameter(
                  SyntaxFactory.Identifier("idProvider"))
              .WithType(SyntaxFactory.ParseTypeName("IWhizbangIdProvider ")
                  .WithTrailingTrivia(SyntaxFactory.Space));

          var newParams = node.ParameterList.Parameters.Add(newParam);
          var newParamList = node.ParameterList.WithParameters(newParams);

          _changes.Add(new CodeChange(
              node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
              ChangeType.MethodSignatureChange,
              "Added 'IWhizbangIdProvider idProvider' to primary constructor",
              node.ParameterList.ToString(),
              newParamList.ToString()));

          node = node.WithParameterList(newParamList);
        }
      }

      return base.VisitRecordDeclaration(node);
    }
  }

  /// <summary>
  /// Rewriter that transforms Guid.NewGuid()/Guid.CreateVersion7() to idProvider.NewGuid().
  /// </summary>
  private sealed class GuidCallRewriter : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;

    public GuidCallRewriter(List<CodeChange> changes) {
      _changes = changes;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node) {
      var expr = node.Expression.ToString();

      if (expr is "Guid.NewGuid" or "System.Guid.NewGuid" or
          "Guid.CreateVersion7" or "System.Guid.CreateVersion7") {
        // Replace with idProvider.NewGuid()
        var newExpression = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("idProvider"),
                    SyntaxFactory.IdentifierName("NewGuid")))
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia());

        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.MethodCallReplacement,
            $"Replaced '{expr}()' with 'idProvider.NewGuid()'",
            node.ToString(),
            "idProvider.NewGuid()"));

        return newExpression;
      }

      return base.VisitInvocationExpression(node);
    }
  }
}
