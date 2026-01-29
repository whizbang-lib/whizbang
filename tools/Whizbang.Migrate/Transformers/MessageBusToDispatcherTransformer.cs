using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Migrate.Transformers;

/// <summary>
/// Transforms Wolverine IMessageBus patterns to Whizbang IDispatcher.
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public sealed class MessageBusToDispatcherTransformer : ICodeTransformer {
  /// <inheritdoc />
  public Task<TransformationResult> TransformAsync(
      string sourceCode,
      string filePath,
      CancellationToken ct = default) {
    var changes = new List<CodeChange>();
    var warnings = new List<string>();

    var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: ct);
    var root = tree.GetRoot(ct);

    // Check if there are any IMessageBus patterns to transform
    var hasMessageBus = _hasMessageBusPatterns(root);
    if (!hasMessageBus) {
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

    // 2. Transform IMessageBus → IDispatcher in types
    newRoot = _transformMessageBusTypes(newRoot, changes);

    // 3. Rename _messageBus fields to _dispatcher
    newRoot = _renameFields(newRoot, changes);

    // 4. Transform method calls (SendAsync, PublishAsync, InvokeAsync)
    newRoot = _transformMethodCalls(newRoot, changes, warnings);

    // 5. Add warnings about patterns
    _addPatternWarnings(newRoot, warnings, filePath);

    var transformedCode = newRoot.ToFullString();

    return Task.FromResult(new TransformationResult(
        sourceCode,
        transformedCode,
        changes,
        warnings));
  }

  private static bool _hasMessageBusPatterns(SyntaxNode root) {
    // Check for IMessageBus in types, fields, parameters
    var identifiers = root.DescendantNodes()
        .OfType<IdentifierNameSyntax>()
        .Select(i => i.Identifier.Text);

    return identifiers.Any(i => i == "IMessageBus");
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

    // If no Whizbang using was added, add it at the start
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

  private static SyntaxNode _transformMessageBusTypes(SyntaxNode root, List<CodeChange> changes) {
    var rewriter = new MessageBusTypeRewriter(changes);
    return rewriter.Visit(root);
  }

  private static SyntaxNode _renameFields(SyntaxNode root, List<CodeChange> changes) {
    var rewriter = new FieldRenameRewriter(changes);
    return rewriter.Visit(root);
  }

  private static SyntaxNode _transformMethodCalls(SyntaxNode root, List<CodeChange> changes, List<string> warnings) {
    var rewriter = new MethodCallRewriter(changes, warnings);
    return rewriter.Visit(root);
  }

  private static void _addPatternWarnings(SyntaxNode root, List<string> warnings, string filePath) {
    // Check if this is a receptor file based on naming
    var isReceptor = filePath.Contains("Receptor", StringComparison.OrdinalIgnoreCase);

    // Find all PublishAsync calls
    var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

    foreach (var invocation in invocations) {
      var expressionText = invocation.Expression.ToString();

      // Check for PublishAsync calls in receptors
      if (expressionText.Contains(".PublishAsync") && isReceptor) {
        var lineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        if (!warnings.Any(w => w.Contains("PublishAsync") && w.Contains($"Line {lineNumber}"))) {
          warnings.Add($"Line {lineNumber}: PublishAsync found in receptor. PREFERRED: Return the event " +
              "in a tuple instead (e.g., return (result, @event);) - the framework auto-publishes " +
              "any IEvent instances in the return value. This is cleaner and more declarative.");
        }
      }
    }
  }

  /// <summary>
  /// Rewriter that transforms IMessageBus to IDispatcher in type declarations.
  /// </summary>
  private sealed class MessageBusTypeRewriter : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;

    public MessageBusTypeRewriter(List<CodeChange> changes) {
      _changes = changes;
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) {
      if (node.Identifier.Text == "IMessageBus") {
        // Check if this is a type usage (not a member access)
        var parent = node.Parent;
        var isTypeUsage = parent is TypeSyntax ||
                          parent is QualifiedNameSyntax ||
                          parent is VariableDeclarationSyntax ||
                          parent is ParameterSyntax ||
                          parent is FieldDeclarationSyntax;

        // Also check grandparent for type argument lists
        if (!isTypeUsage && parent?.Parent is TypeArgumentListSyntax) {
          isTypeUsage = true;
        }

        if (isTypeUsage) {
          var newIdentifier = SyntaxFactory.IdentifierName("IDispatcher")
              .WithLeadingTrivia(node.GetLeadingTrivia())
              .WithTrailingTrivia(node.GetTrailingTrivia());

          _changes.Add(new CodeChange(
              node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
              ChangeType.InterfaceReplacement,
              "Replaced 'IMessageBus' with 'IDispatcher'",
              "IMessageBus",
              "IDispatcher"));

          return newIdentifier;
        }
      }

      return base.VisitIdentifierName(node);
    }
  }

  /// <summary>
  /// Rewriter that renames _messageBus fields to _dispatcher.
  /// </summary>
  private sealed class FieldRenameRewriter : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;
    private readonly HashSet<string> _renamedFields = [];

    public FieldRenameRewriter(List<CodeChange> changes) {
      _changes = changes;
    }

    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node) {
      var newVariables = new List<VariableDeclaratorSyntax>();
      var anyChanged = false;

      foreach (var variable in node.Declaration.Variables) {
        var fieldName = variable.Identifier.Text;

        if (fieldName.Contains("messageBus", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("MessageBus", StringComparison.Ordinal)) {
          var newName = _getDispatcherFieldName(fieldName);
          var newIdentifier = SyntaxFactory.Identifier(newName)
              .WithLeadingTrivia(variable.Identifier.LeadingTrivia)
              .WithTrailingTrivia(variable.Identifier.TrailingTrivia);

          _renamedFields.Add(fieldName);

          _changes.Add(new CodeChange(
              variable.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
              ChangeType.TypeRename,
              $"Renamed field '{fieldName}' to '{newName}'",
              fieldName,
              newName));

          newVariables.Add(variable.WithIdentifier(newIdentifier));
          anyChanged = true;
        } else {
          newVariables.Add(variable);
        }
      }

      if (anyChanged) {
        var newDeclaration = node.Declaration.WithVariables(SyntaxFactory.SeparatedList(newVariables));
        return node.WithDeclaration(newDeclaration);
      }

      return base.VisitFieldDeclaration(node);
    }

    public override SyntaxNode? VisitParameter(ParameterSyntax node) {
      var paramName = node.Identifier.Text;

      if (paramName.Contains("messageBus", StringComparison.OrdinalIgnoreCase) ||
          paramName.Contains("MessageBus", StringComparison.Ordinal)) {
        var newName = _getDispatcherParamName(paramName);
        var newIdentifier = SyntaxFactory.Identifier(newName)
            .WithLeadingTrivia(node.Identifier.LeadingTrivia)
            .WithTrailingTrivia(node.Identifier.TrailingTrivia);

        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.TypeRename,
            $"Renamed parameter '{paramName}' to '{newName}'",
            paramName,
            newName));

        return node.WithIdentifier(newIdentifier);
      }

      return base.VisitParameter(node);
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) {
      var name = node.Identifier.Text;

      // Rename usages of renamed fields
      if (name.Contains("messageBus", StringComparison.OrdinalIgnoreCase) ||
          name.Contains("MessageBus", StringComparison.Ordinal)) {
        // Check if this is in a member access context (not a type)
        var parent = node.Parent;
        var isMemberAccess = parent is MemberAccessExpressionSyntax ||
                            parent is AssignmentExpressionSyntax ||
                            parent is EqualsValueClauseSyntax ||
                            parent is ArgumentSyntax;

        if (isMemberAccess) {
          var newName = name.Contains('_') ? _getDispatcherFieldName(name) : _getDispatcherParamName(name);

          return SyntaxFactory.IdentifierName(newName)
              .WithLeadingTrivia(node.GetLeadingTrivia())
              .WithTrailingTrivia(node.GetTrailingTrivia());
        }
      }

      return base.VisitIdentifierName(node);
    }

    private static string _getDispatcherFieldName(string fieldName) {
      // _messageBus -> _dispatcher
      // _bus -> _dispatcher
      if (fieldName.StartsWith('_')) {
        return "_dispatcher";
      }
      return "dispatcher";
    }

    private static string _getDispatcherParamName(string paramName) {
      // messageBus -> dispatcher
      // bus -> dispatcher
      return "dispatcher";
    }
  }

  /// <summary>
  /// Rewriter that transforms method calls like InvokeAsync to LocalInvokeAsync.
  /// </summary>
  private sealed class MethodCallRewriter : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;
    private readonly List<string> _warnings;

    public MethodCallRewriter(List<CodeChange> changes, List<string> warnings) {
      _changes = changes;
      _warnings = warnings;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node) {
      // First visit children to apply other transformations
      var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

      var expressionText = visited.Expression.ToString();

      // Handle InvokeAsync -> LocalInvokeAsync
      if (expressionText.Contains(".InvokeAsync")) {
        var lineNumber = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        // Transform the member access expression
        if (visited.Expression is MemberAccessExpressionSyntax memberAccess) {
          var newMemberName = SyntaxFactory.IdentifierName("LocalInvokeAsync")
              .WithLeadingTrivia(memberAccess.Name.GetLeadingTrivia())
              .WithTrailingTrivia(memberAccess.Name.GetTrailingTrivia());

          // Preserve any generic type arguments
          SyntaxNode newName = memberAccess.Name switch {
            GenericNameSyntax genericName => SyntaxFactory.GenericName(
                SyntaxFactory.Identifier("LocalInvokeAsync"),
                genericName.TypeArgumentList)
                .WithLeadingTrivia(genericName.GetLeadingTrivia())
                .WithTrailingTrivia(genericName.GetTrailingTrivia()),
            _ => newMemberName
          };

          var newMemberAccess = memberAccess.WithName((SimpleNameSyntax)newName);
          visited = visited.WithExpression(newMemberAccess);

          _changes.Add(new CodeChange(
              lineNumber,
              ChangeType.MethodCallReplacement,
              "Replaced 'InvokeAsync' with 'LocalInvokeAsync'",
              "InvokeAsync",
              "LocalInvokeAsync"));

          _warnings.Add($"Line {lineNumber}: InvokeAsync converted to LocalInvokeAsync. " +
              "LocalInvokeAsync is for in-process RPC only. For cross-service calls, use SendAsync.");
        }
      }

      return visited;
    }
  }
}
