using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Migrate.Transformers;

/// <summary>
/// Transforms Marten IDocumentStore event sourcing patterns to Whizbang IEventStore.
/// Handles:
/// - IDocumentStore → IEventStore
/// - session.Events.StartStream() → eventStore.AppendAsync()
/// - session.Events.Append() + SaveChangesAsync() → eventStore.AppendAsync()
/// - session.Events.FetchStreamAsync() → eventStore.ReadAsync()
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public sealed class EventStoreTransformer : ICodeTransformer {
  /// <inheritdoc />
  public Task<TransformationResult> TransformAsync(
      string sourceCode,
      string filePath,
      CancellationToken ct = default) {
    var changes = new List<CodeChange>();
    var warnings = new List<string>();

    var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: ct);
    var root = tree.GetRoot(ct);

    // Check if there are any Marten event store patterns to transform
    var hasMartenPatterns = _hasMartenEventStorePatterns(root);
    if (!hasMartenPatterns) {
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

    // 2. Transform IDocumentStore to IEventStore in fields/parameters
    newRoot = _transformDocumentStoreTypes(newRoot, changes, warnings);

    // 3. Transform session-based event operations
    newRoot = _transformEventOperations(newRoot, changes, warnings);

    // 4. Remove session variable declarations and using statements
    newRoot = _removeSessionDeclarations(newRoot, changes);

    // 5. Remove SaveChangesAsync calls (each AppendAsync is atomic)
    newRoot = _removeSaveChangesAsync(newRoot, changes);

    var transformedCode = newRoot.ToFullString();

    return Task.FromResult(new TransformationResult(
        sourceCode,
        transformedCode,
        changes,
        warnings));
  }

  private static bool _hasMartenEventStorePatterns(SyntaxNode root) {
    // Check for IDocumentStore type
    var hasDocumentStore = root.DescendantNodes()
        .OfType<IdentifierNameSyntax>()
        .Any(id => id.Identifier.Text == "IDocumentStore");

    // Check for session.Events usage
    var hasSessionEvents = root.DescendantNodes()
        .OfType<MemberAccessExpressionSyntax>()
        .Any(ma => ma.Name.Identifier.Text == "Events");

    // Check for Marten using directive
    var hasMartenUsing = root.DescendantNodes()
        .OfType<UsingDirectiveSyntax>()
        .Any(u => u.Name?.ToString()?.StartsWith("Marten", StringComparison.Ordinal) == true);

    return hasDocumentStore || hasSessionEvents || hasMartenUsing;
  }

  private static SyntaxNode _transformUsings(SyntaxNode root, List<CodeChange> changes) {
    var compilationUnit = root as CompilationUnitSyntax;
    if (compilationUnit == null) {
      return root;
    }

    var newUsings = new List<UsingDirectiveSyntax>();
    var addedWhizbangMessaging = false;
    var removedMarten = false;

    foreach (var usingDirective in compilationUnit.Usings) {
      var name = usingDirective.Name?.ToString();

      if (name != null && name.StartsWith("Marten", StringComparison.Ordinal)) {
        // Replace first Marten using with Whizbang.Core.Messaging
        if (!addedWhizbangMessaging) {
          var whizbangUsing = usingDirective
              .WithName(SyntaxFactory.ParseName("Whizbang.Core.Messaging")
                  .WithLeadingTrivia(usingDirective.Name?.GetLeadingTrivia() ?? SyntaxFactory.TriviaList())
                  .WithTrailingTrivia(usingDirective.Name?.GetTrailingTrivia() ?? SyntaxFactory.TriviaList()));
          newUsings.Add(whizbangUsing);
          addedWhizbangMessaging = true;

          changes.Add(new CodeChange(
              usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
              ChangeType.UsingRemoved,
              $"Replaced 'using {name}' with 'using Whizbang.Core.Messaging'",
              $"using {name};",
              "using Whizbang.Core.Messaging;"));
        } else {
          // Remove additional Marten usings
          changes.Add(new CodeChange(
              usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
              ChangeType.UsingRemoved,
              $"Removed 'using {name}'",
              $"using {name};",
              ""));
        }
        removedMarten = true;
      } else {
        newUsings.Add(usingDirective);
      }
    }

    // If no Marten using was found but we have patterns, add Whizbang.Core.Messaging
    if (!removedMarten && !addedWhizbangMessaging) {
      var whizbangUsing = SyntaxFactory.UsingDirective(
          SyntaxFactory.ParseName("Whizbang.Core.Messaging"))
          .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
      newUsings.Insert(0, whizbangUsing);

      changes.Add(new CodeChange(
          1,
          ChangeType.UsingAdded,
          "Added 'using Whizbang.Core.Messaging'",
          "",
          "using Whizbang.Core.Messaging;"));
    }

    return compilationUnit.WithUsings(SyntaxFactory.List(newUsings));
  }

  private static SyntaxNode _transformDocumentStoreTypes(SyntaxNode root, List<CodeChange> changes, List<string> warnings) {
    var rewriter = new DocumentStoreTypeRewriter(changes, warnings);
    return rewriter.Visit(root);
  }

  private static SyntaxNode _transformEventOperations(SyntaxNode root, List<CodeChange> changes, List<string> warnings) {
    var rewriter = new EventOperationRewriter(changes, warnings);
    return rewriter.Visit(root);
  }

  private static SyntaxNode _removeSessionDeclarations(SyntaxNode root, List<CodeChange> changes) {
    var rewriter = new SessionDeclarationRemover(changes);
    return rewriter.Visit(root);
  }

  private static SyntaxNode _removeSaveChangesAsync(SyntaxNode root, List<CodeChange> changes) {
    var rewriter = new SaveChangesRemover(changes);
    return rewriter.Visit(root);
  }

  /// <summary>
  /// Rewriter that transforms IDocumentStore to IEventStore.
  /// </summary>
  private sealed class DocumentStoreTypeRewriter : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;
    private readonly List<string> _warnings;

    public DocumentStoreTypeRewriter(List<CodeChange> changes, List<string> warnings) {
      _changes = changes;
      _warnings = warnings;
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) {
      if (node.Identifier.Text == "IDocumentStore") {
        var newNode = SyntaxFactory.IdentifierName("IEventStore")
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia());

        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.InterfaceReplacement,
            "Replaced 'IDocumentStore' with 'IEventStore'",
            "IDocumentStore",
            "IEventStore"));

        return newNode;
      }

      // Warn about IDocumentSession - requires manual refactoring
      if (node.Identifier.Text == "IDocumentSession") {
        _warnings.Add($"Line {node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: " +
            "IDocumentSession found. Whizbang uses direct IEventStore methods instead of sessions. " +
            "Remove session usage and call IEventStore methods directly.");
      }

      return base.VisitIdentifierName(node);
    }

    public override SyntaxNode? VisitParameter(ParameterSyntax node) {
      // Transform parameter names: store → eventStore, documentStore → eventStore
      var paramName = node.Identifier.Text;
      if (paramName is "store" or "documentStore" or "_store" or "_documentStore") {
        var newName = paramName.StartsWith('_') ? "_eventStore" : "eventStore";
        var newNode = node.WithIdentifier(
            SyntaxFactory.Identifier(newName)
                .WithLeadingTrivia(node.Identifier.LeadingTrivia)
                .WithTrailingTrivia(node.Identifier.TrailingTrivia));

        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.TypeRename,
            $"Renamed parameter '{paramName}' to '{newName}'",
            paramName,
            newName));

        return base.VisitParameter(newNode);
      }

      return base.VisitParameter(node);
    }
  }

  /// <summary>
  /// Rewriter that transforms Marten event operations to Whizbang patterns.
  /// </summary>
  private sealed class EventOperationRewriter : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;
    private readonly List<string> _warnings;

    public EventOperationRewriter(List<CodeChange> changes, List<string> warnings) {
      _changes = changes;
      _warnings = warnings;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node) {
      var expressionText = node.Expression.ToString();

      // session.Events.StartStream<T>(event) → _eventStore.AppendAsync(streamId, event, ct)
      if (expressionText.Contains(".Events.StartStream")) {
        _warnings.Add($"Line {node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: " +
            "StartStream<T>() found. Convert to: var streamId = Guid.CreateVersion7(); " +
            "await _eventStore.AppendAsync(streamId, @event, ct);");

        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.MethodCallReplacement,
            "StartStream requires manual conversion to AppendAsync with new stream ID",
            node.ToString(),
            "/* TODO: var streamId = Guid.CreateVersion7(); await _eventStore.AppendAsync(streamId, @event, ct); */"));
      }

      // session.Events.Append(streamId, event) → await _eventStore.AppendAsync(streamId, event, ct)
      if (expressionText.Contains(".Events.Append") && !expressionText.Contains("AppendAsync")) {
        _warnings.Add($"Line {node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: " +
            "Events.Append() found. Convert to: await _eventStore.AppendAsync(streamId, @event, ct); " +
            "Note: Remove the corresponding SaveChangesAsync() call.");

        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.MethodCallReplacement,
            "Events.Append requires conversion to AppendAsync (remove SaveChangesAsync)",
            node.ToString(),
            "/* TODO: await _eventStore.AppendAsync(streamId, @event, ct); */"));
      }

      // session.Events.FetchStreamAsync(streamId) → _eventStore.ReadAsync<IEvent>(streamId, 0, ct)
      if (expressionText.Contains(".Events.FetchStreamAsync")) {
        _warnings.Add($"Line {node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: " +
            "FetchStreamAsync() found. Convert to: _eventStore.ReadAsync<IEvent>(streamId, 0, ct) " +
            "and iterate with await foreach.");

        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.MethodCallReplacement,
            "FetchStreamAsync requires conversion to ReadAsync with await foreach",
            node.ToString(),
            "/* TODO: await foreach (var env in _eventStore.ReadAsync<IEvent>(streamId, 0, ct)) { ... } */"));
      }

      // session.Events.AggregateStreamAsync<T>(streamId) → perspective or manual rehydration
      if (expressionText.Contains(".Events.AggregateStreamAsync")) {
        _warnings.Add($"Line {node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: " +
            "AggregateStreamAsync<T>() found. In Whizbang, use either: " +
            "(1) IPerspectiveFor<T> for read models, or " +
            "(2) Manual rehydration by reading events and applying them.");

        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.MethodCallReplacement,
            "AggregateStreamAsync requires manual conversion to Perspective or rehydration pattern",
            node.ToString(),
            "/* TODO: Use IPerspectiveFor<T> or manual rehydration */"));
      }

      return base.VisitInvocationExpression(node);
    }
  }

  /// <summary>
  /// Rewriter that removes session variable declarations.
  /// </summary>
  private sealed class SessionDeclarationRemover : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;

    public SessionDeclarationRemover(List<CodeChange> changes) {
      _changes = changes;
    }

    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node) {
      var declarationText = node.Declaration.ToString();

      // Remove: await using var session = _store.LightweightSession();
      // Remove: await using var session = _store.QuerySession();
      if (declarationText.Contains("LightweightSession") ||
          declarationText.Contains("QuerySession") ||
          declarationText.Contains("OpenSession")) {
        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.MethodCallReplacement,
            "Removed session declaration (Whizbang uses direct IEventStore methods)",
            node.ToString().Trim(),
            ""));

        // Return null to remove the node entirely
        return null;
      }

      return base.VisitLocalDeclarationStatement(node);
    }
  }

  /// <summary>
  /// Rewriter that removes SaveChangesAsync calls.
  /// </summary>
  private sealed class SaveChangesRemover : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;

    public SaveChangesRemover(List<CodeChange> changes) {
      _changes = changes;
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node) {
      var expressionText = node.Expression.ToString();

      // Remove: await session.SaveChangesAsync();
      // Remove: await session.SaveChangesAsync(ct);
      if (expressionText.Contains("SaveChangesAsync")) {
        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.MethodCallReplacement,
            "Removed SaveChangesAsync (each AppendAsync is atomic in Whizbang)",
            node.ToString().Trim(),
            ""));

        // Return null to remove the node entirely
        return null;
      }

      return base.VisitExpressionStatement(node);
    }
  }
}
