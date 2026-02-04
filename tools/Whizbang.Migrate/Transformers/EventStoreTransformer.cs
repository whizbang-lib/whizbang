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

    // Add Marten-specific pattern warnings
    _addMartenPatternWarnings(root, warnings);

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

  private static void _addMartenPatternWarnings(SyntaxNode root, List<string> warnings) {
    var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

    foreach (var invocation in invocations) {
      var expressionText = invocation.Expression.ToString();
      var lineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

      // E03: AppendExclusive detection
      if (expressionText.Contains("AppendExclusive")) {
        warnings.Add($"Line {lineNumber}: AppendExclusive() found. In Whizbang, exclusive locking is handled " +
            "differently. Consider using optimistic concurrency with expectedSequence parameter on AppendAsync, " +
            "or use distributed locking if exclusive access is truly required.");
      }

      // E04: AppendOptimistic detection
      if (expressionText.Contains("AppendOptimistic")) {
        warnings.Add($"Line {lineNumber}: AppendOptimistic() found. In Whizbang, use AppendAsync with the " +
            "expectedSequence parameter for optimistic concurrency: " +
            "await _eventStore.AppendAsync(streamId, events, expectedSequence, ct);");
      }

      // E05: CombGuidIdGeneration detection
      if (expressionText.Contains("CombGuidIdGeneration")) {
        warnings.Add($"Line {lineNumber}: CombGuidIdGeneration found. In Whizbang, use TrackedGuid.NewMedo() " +
            "for sequential GUIDs with sub-millisecond precision. This provides similar benefits to Marten's " +
            "CombGuid but with UUIDv7 compliance.");
      }
    }

    // E06: Collision retry pattern (for loop with Guid.NewGuid)
    var forStatements = root.DescendantNodes().OfType<ForStatementSyntax>();
    foreach (var forLoop in forStatements) {
      var bodyText = forLoop.ToString();
      if ((bodyText.Contains("Guid.NewGuid") || bodyText.Contains("StreamId")) &&
          (bodyText.Contains("retry") || bodyText.Contains("Retry") ||
           bodyText.Contains("attempt") || bodyText.Contains("collision") ||
           bodyText.Contains("duplicate key"))) {
        var lineNumber = forLoop.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        warnings.Add($"Line {lineNumber}: GUID collision retry pattern detected. TrackedGuid.NewMedo() " +
            "uses timestamp-based UUIDs that are virtually collision-free, making retry logic typically " +
            "unnecessary. Consider simplifying to a single ID generation call.");
      }
    }

    // E07: Multiple consecutive Append calls (batch pattern)
    var statements = root.DescendantNodes().OfType<BlockSyntax>()
        .SelectMany(b => b.Statements);
    var consecutiveAppends = 0;
    ExpressionStatementSyntax? firstAppend = null;
    foreach (var statement in statements) {
      if (statement is ExpressionStatementSyntax expr &&
          expr.Expression.ToString().Contains(".Events.Append")) {
        consecutiveAppends++;
        firstAppend ??= expr;
      } else if (consecutiveAppends > 1 && firstAppend != null) {
        var lineNumber = firstAppend.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        warnings.Add($"Line {lineNumber}: Multiple consecutive Append calls detected. Consider using " +
            "batch append with AppendBatchAsync for better performance, or use IDispatcher.PublishAsync " +
            "for each event to leverage the built-in outbox.");
        consecutiveAppends = 0;
        firstAppend = null;
      } else {
        consecutiveAppends = 0;
        firstAppend = null;
      }
    }
    // Check final batch
    if (consecutiveAppends > 1 && firstAppend != null) {
      var lineNumber = firstAppend.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
      warnings.Add($"Line {lineNumber}: Multiple consecutive Append calls detected. Consider using " +
          "batch append with AppendBatchAsync for better performance.");
    }

    // E08: WorkCoordinator or batch processing patterns (foreach with StartStream)
    var foreachStatements = root.DescendantNodes().OfType<ForEachStatementSyntax>();
    foreach (var foreachLoop in foreachStatements) {
      var bodyText = foreachLoop.ToString();
      if (bodyText.Contains("StartStream") || bodyText.Contains(".Events.Append")) {
        var lineNumber = foreachLoop.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        warnings.Add($"Line {lineNumber}: Batch processing with multiple stream operations detected. " +
            "In Whizbang, use IWorkCoordinator with Perspectives for cross-stream or multi-stream " +
            "processing. Each perspective handles its own stream automatically.");
      }
    }

    // Also check for explicit WorkCoordinator or BatchProcessor references
    var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
    foreach (var classDecl in classDeclarations) {
      var classText = classDecl.ToString();
      if (classText.Contains("WorkCoordinator") || classText.Contains("BatchProcessor")) {
        var lineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        warnings.Add($"Line {lineNumber}: WorkCoordinator or BatchProcessor pattern detected. " +
            "In Whizbang, use IWorkCoordinator for cross-stream coordination.");
      }
    }

    // E09: Tenant-scoped sessions (LightweightSession with tenant parameter or ForTenant)
    var sessionInvocations = root.DescendantNodes()
        .OfType<InvocationExpressionSyntax>()
        .Where(inv => {
          var text = inv.Expression.ToString();
          return text.Contains("ForTenant") ||
                 text.Contains("SetTenantId") ||
                 (text.Contains("LightweightSession") && inv.ArgumentList.Arguments.Count > 0);
        });

    foreach (var tenantInvocation in sessionInvocations) {
      var lineNumber = tenantInvocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
      warnings.Add($"Line {lineNumber}: Tenant-scoped session detected. In Whizbang, multi-tenancy " +
          "is handled via scoped IEventStore registration and tenant context. Configure tenant " +
          "isolation in your DI setup rather than per-operation tenant switching.");
    }
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

      // Skip global using aliases - they're handled by GlobalUsingAliasTransformer
      if (usingDirective.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword) &&
          usingDirective.Alias != null) {
        newUsings.Add(usingDirective);
        continue;
      }

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

      // session.Events.Append(streamId, event) → consider IDispatcher.PublishAsync vs IEventStore.AppendAsync
      if (expressionText.Contains(".Events.Append") && !expressionText.Contains("AppendAsync")) {
        _warnings.Add($"Line {node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: " +
            "Events.Append() found. In Whizbang, consider: " +
            "(1) Use _dispatcher.PublishAsync(@event) for most cases (handles perspectives + outbox), or " +
            "(2) Use _eventStore.AppendAsync() ONLY for direct persistence without immediate perspective updates. " +
            "Note: Remove the corresponding SaveChangesAsync() call.");

        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.MethodCallReplacement,
            "Events.Append - consider IDispatcher.PublishAsync vs IEventStore.AppendAsync",
            node.ToString(),
            "/* TODO: await _dispatcher.PublishAsync(@event, ct); // or _eventStore.AppendAsync for direct persistence */"));
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
