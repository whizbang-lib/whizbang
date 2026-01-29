using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Migrate.Transformers;

/// <summary>
/// Transforms Guid.NewGuid() and Guid.CreateVersion7() calls to use TrackedGuid.NewMedo().
/// This provides UUIDv7 with sub-millisecond precision via Medo.Uuid7.
/// Handles:
/// - Guid.NewGuid() → TrackedGuid.NewMedo()
/// - Guid.CreateVersion7() → TrackedGuid.NewMedo()
/// - System.Guid.NewGuid() → TrackedGuid.NewMedo()
/// - System.Guid.CreateVersion7() → TrackedGuid.NewMedo()
/// - CombGuidIdGeneration.NewGuid() → TrackedGuid.NewMedo() (Marten sequential GUIDs)
/// - Adds using Whizbang.Core.ValueObjects; directive
/// - Removes using Marten.Schema.Identity; directive (when CombGuid is transformed)
/// - Emits warnings for default StreamId check patterns (G03)
/// - Emits warnings for collision retry patterns (G04)
/// </summary>
/// <docs>migration-guide/automated-migration#guid-to-trackedguid</docs>
public sealed class GuidToTrackedGuidTransformer : ICodeTransformer {
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

    // 1. Add using directive for Whizbang.Core.ValueObjects if not present
    newRoot = _ensureWhizbangCoreValueObjectsUsing(newRoot, changes);

    // 2. Remove Marten.Schema.Identity using if present (for CombGuid migration)
    newRoot = _removeMartenSchemaIdentityUsing(newRoot, changes);

    // 3. Transform Guid.NewGuid(), Guid.CreateVersion7(), and CombGuidIdGeneration.NewGuid() calls
    newRoot = _transformGuidCalls(newRoot, changes);

    // Add warnings about type changes
    _addTypeChangeWarnings(root, warnings);

    // Add warnings for common patterns (G03: default check, G04: collision retry)
    _addMartenPatternWarnings(root, warnings);

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
                 expr == "System.Guid.CreateVersion7" ||
                 expr == "CombGuidIdGeneration.NewGuid" ||
                 expr == "Marten.Schema.Identity.CombGuidIdGeneration.NewGuid";
        });
  }

  private static SyntaxNode _ensureWhizbangCoreValueObjectsUsing(SyntaxNode root, List<CodeChange> changes) {
    var compilationUnit = root as CompilationUnitSyntax;
    if (compilationUnit == null) {
      return root;
    }

    // Check if Whizbang.Core.ValueObjects using already exists
    var hasWhizbangCoreValueObjects = compilationUnit.Usings
        .Any(u => u.Name?.ToString() == "Whizbang.Core.ValueObjects");

    if (hasWhizbangCoreValueObjects) {
      return root;
    }

    // Find the best position to insert (after System usings, before others)
    var usings = compilationUnit.Usings.ToList();
    var insertIndex = usings.FindLastIndex(u =>
        u.Name?.ToString()?.StartsWith("System", StringComparison.Ordinal) == true);
    insertIndex = insertIndex >= 0 ? insertIndex + 1 : 0;

    var newUsing = SyntaxFactory.UsingDirective(
            SyntaxFactory.ParseName("Whizbang.Core.ValueObjects"))
        .WithUsingKeyword(
            SyntaxFactory.Token(SyntaxKind.UsingKeyword)
                .WithTrailingTrivia(SyntaxFactory.Space))
        .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

    usings.Insert(insertIndex, newUsing);

    changes.Add(new CodeChange(
        insertIndex + 1,
        ChangeType.UsingAdded,
        "Added 'using Whizbang.Core.ValueObjects' for TrackedGuid",
        "",
        "using Whizbang.Core.ValueObjects;"));

    return compilationUnit.WithUsings(SyntaxFactory.List(usings));
  }

  private static SyntaxNode _removeMartenSchemaIdentityUsing(SyntaxNode root, List<CodeChange> changes) {
    var compilationUnit = root as CompilationUnitSyntax;
    if (compilationUnit == null) {
      return root;
    }

    // Check if Marten.Schema.Identity using exists
    var martenUsing = compilationUnit.Usings
        .FirstOrDefault(u => u.Name?.ToString() == "Marten.Schema.Identity");

    if (martenUsing == null) {
      return root;
    }

    var lineNumber = martenUsing.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    var newUsings = compilationUnit.Usings.Remove(martenUsing);

    changes.Add(new CodeChange(
        lineNumber,
        ChangeType.UsingRemoved,
        "Removed 'using Marten.Schema.Identity' - CombGuidIdGeneration replaced with TrackedGuid",
        "using Marten.Schema.Identity;",
        ""));

    return compilationUnit.WithUsings(newUsings);
  }

  private static SyntaxNode _transformGuidCalls(SyntaxNode root, List<CodeChange> changes) {
    var rewriter = new GuidCallRewriter(changes);
    return rewriter.Visit(root);
  }

  private static void _addTypeChangeWarnings(SyntaxNode root, List<string> warnings) {
    // Find methods that return Guid and contain Guid generation calls
    var methodsWithGuidReturn = root.DescendantNodes()
        .OfType<MethodDeclarationSyntax>()
        .Where(m => m.ReturnType.ToString() == "Guid" ||
                    m.ReturnType.ToString() == "System.Guid")
        .Where(m => m.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Any(inv => {
              var expr = inv.Expression.ToString();
              return expr == "Guid.NewGuid" ||
                     expr == "Guid.CreateVersion7" ||
                     expr == "System.Guid.NewGuid" ||
                     expr == "System.Guid.CreateVersion7";
            }))
        .ToList();

    foreach (var method in methodsWithGuidReturn) {
      var lineNumber = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
      warnings.Add($"Line {lineNumber}: Method '{method.Identifier.Text}' returns Guid but now " +
          "creates TrackedGuid. TrackedGuid is implicitly convertible to Guid, but consider " +
          "updating the return type to TrackedGuid or a strongly-typed [WhizbangId] type.");
    }

    // Also warn about properties with Guid initializers
    var propertiesWithGuidInit = root.DescendantNodes()
        .OfType<PropertyDeclarationSyntax>()
        .Where(p => p.Initializer != null)
        .Where(p => p.Type.ToString() == "Guid" || p.Type.ToString() == "System.Guid")
        .Where(p => {
          var initText = p.Initializer?.Value.ToString() ?? "";
          return initText.Contains("Guid.NewGuid") || initText.Contains("Guid.CreateVersion7");
        })
        .ToList();

    foreach (var prop in propertiesWithGuidInit) {
      var lineNumber = prop.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
      warnings.Add($"Line {lineNumber}: Property '{prop.Identifier.Text}' has type Guid but now " +
          "initializes with TrackedGuid. TrackedGuid is implicitly convertible to Guid.");
    }
  }

  private static void _addMartenPatternWarnings(SyntaxNode root, List<string> warnings) {
    // G03: Detect default StreamId check patterns (if (x.StreamId == default))
    var defaultChecks = root.DescendantNodes()
        .OfType<BinaryExpressionSyntax>()
        .Where(b => b.IsKind(SyntaxKind.EqualsExpression))
        .Where(b => {
          var text = b.ToString();
          return (text.Contains("StreamId") || text.Contains("streamId")) &&
                 (text.Contains("default") || text.Contains("Guid.Empty"));
        })
        .ToList();

    foreach (var check in defaultChecks) {
      var lineNumber = check.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
      warnings.Add($"Line {lineNumber}: Default StreamId check pattern detected. With TrackedGuid, " +
          "always generate IDs at creation time rather than checking for default values. " +
          "Consider removing this check and generating IDs upfront.");
    }

    // G04: Detect collision retry patterns (for loop with try/catch and Guid.NewGuid)
    var forStatements = root.DescendantNodes()
        .OfType<ForStatementSyntax>()
        .Where(f => {
          var bodyText = f.ToString();
          return bodyText.Contains("Guid.NewGuid") &&
                 (bodyText.Contains("duplicate key") ||
                  bodyText.Contains("collision") ||
                  bodyText.Contains("retry") ||
                  bodyText.Contains("Retry") ||
                  bodyText.Contains("attempt") ||
                  bodyText.Contains("Attempt"));
        })
        .ToList();

    foreach (var forLoop in forStatements) {
      var lineNumber = forLoop.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
      warnings.Add($"Line {lineNumber}: GUID collision retry pattern detected. TrackedGuid.NewMedo() " +
          "is virtually collision-free (timestamp + random), making retry logic typically unnecessary. " +
          "Consider simplifying to a single ID generation call.");
    }

    // Also check while loops for retry patterns
    var whileStatements = root.DescendantNodes()
        .OfType<WhileStatementSyntax>()
        .Where(w => {
          var bodyText = w.ToString();
          return bodyText.Contains("Guid.NewGuid") &&
                 (bodyText.Contains("duplicate key") ||
                  bodyText.Contains("collision") ||
                  bodyText.Contains("retry"));
        })
        .ToList();

    foreach (var whileLoop in whileStatements) {
      var lineNumber = whileLoop.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
      warnings.Add($"Line {lineNumber}: GUID collision retry pattern detected. TrackedGuid.NewMedo() " +
          "is virtually collision-free, making retry logic typically unnecessary.");
    }
  }

  /// <summary>
  /// Rewriter that transforms Guid.NewGuid()/Guid.CreateVersion7()/CombGuidIdGeneration.NewGuid() to TrackedGuid.NewMedo().
  /// </summary>
  private sealed class GuidCallRewriter : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;

    public GuidCallRewriter(List<CodeChange> changes) {
      _changes = changes;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node) {
      var expr = node.Expression.ToString();

      if (expr is "Guid.NewGuid" or "System.Guid.NewGuid" or
          "Guid.CreateVersion7" or "System.Guid.CreateVersion7" or
          "CombGuidIdGeneration.NewGuid" or "Marten.Schema.Identity.CombGuidIdGeneration.NewGuid") {
        // Replace with TrackedGuid.NewMedo()
        var newExpression = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("TrackedGuid"),
                    SyntaxFactory.IdentifierName("NewMedo")))
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia());

        var description = expr.Contains("CombGuid")
            ? $"Replaced CombGuidIdGeneration.NewGuid() with 'TrackedGuid.NewMedo()' - MEDO provides similar sequential GUID benefits"
            : $"Replaced '{expr}()' with 'TrackedGuid.NewMedo()'";

        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.MethodCallReplacement,
            description,
            node.ToString(),
            "TrackedGuid.NewMedo()"));

        return newExpression;
      }

      return base.VisitInvocationExpression(node);
    }
  }
}
