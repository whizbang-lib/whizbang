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

    // Check for handlers without async operations and suggest ISyncReceptor
    _addSyncReceptorSuggestions(root, warnings);

    // Add Wolverine-specific warnings (H02, H06, H07)
    _addWolverinePatternWarnings(root, warnings);

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

    // 5. Transform MessageContext → MessageEnvelope (H06)
    newRoot = _transformMessageContext(newRoot, changes);

    // 6. Transform LocalMessage patterns (H04)
    newRoot = _transformLocalMessagePatterns(newRoot, changes);

    var transformedCode = newRoot.ToFullString();

    // Post-process: Update comments that reference LocalMessage to reference LocalInvokeAsync
    if (transformedCode.Contains("LocalMessage<")) {
      transformedCode = transformedCode.Replace("LocalMessage<T>", "LocalInvokeAsync<T>");
      transformedCode = transformedCode.Replace("LocalMessage<", "LocalInvokeAsync<");
    }

    return Task.FromResult(new TransformationResult(
        sourceCode,
        transformedCode,
        changes,
        warnings));
  }

  private static bool _hasWolverineHandlers(SyntaxNode root) {
    var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

    foreach (var classDecl in classes) {
      // Check for IHandle interface (Wolverine handler interface)
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

    // Also check for IMessageBus or LocalMessage<T> patterns (Wolverine patterns)
    var hasMessageBus = root.DescendantNodes()
        .OfType<IdentifierNameSyntax>()
        .Any(id => id.Identifier.Text == "IMessageBus");

    if (hasMessageBus) {
      return true;
    }

    var hasLocalMessage = root.DescendantNodes()
        .OfType<GenericNameSyntax>()
        .Any(n => n.Identifier.Text == "LocalMessage");

    return hasLocalMessage;
  }

  private static void _addSyncReceptorSuggestions(SyntaxNode root, List<string> warnings) {
    var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

    foreach (var classDecl in classes) {
      // Check if this is a handler class
      var isHandler = classDecl.BaseList?.Types
          .Any(t => t.Type.ToString().StartsWith("IHandle<", StringComparison.Ordinal)) ?? false;

      if (!isHandler) {
        continue;
      }

      // Find the Handle method
      var handleMethod = classDecl.Members
          .OfType<MethodDeclarationSyntax>()
          .FirstOrDefault(m => m.Identifier.Text is "Handle" or "HandleAsync");

      if (handleMethod == null) {
        continue;
      }

      // Check if the method has any await expressions
      var hasAwait = handleMethod.DescendantNodes()
          .OfType<AwaitExpressionSyntax>()
          .Any();

      // Check if the method returns Task.CompletedTask or Task.FromResult
      var hasTaskReturn = handleMethod.DescendantNodes()
          .OfType<MemberAccessExpressionSyntax>()
          .Any(m => m.ToString() is "Task.CompletedTask"
              or "Task.FromResult"
              or "ValueTask.FromResult");

      // If no await and uses Task.CompletedTask/FromResult, suggest ISyncReceptor
      if (!hasAwait || hasTaskReturn) {
        var lineNumber = handleMethod.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var className = classDecl.Identifier.Text;
        warnings.Add(
            $"Line {lineNumber}: Handler '{className}' has no async operations. " +
            "Consider using ISyncReceptor<TMessage, TResponse> instead of IReceptor for cleaner code.");
      }
    }
  }

  private static void _addWolverinePatternWarnings(SyntaxNode root, List<string> warnings) {
    var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

    foreach (var classDecl in classes) {
      var className = classDecl.Identifier.Text;
      var lineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

      // H02: Check for nested handler classes (Wolverine IHandle<T>)
      var parentClass = classDecl.Parent as ClassDeclarationSyntax;
      if (parentClass != null) {
        var isHandler = classDecl.BaseList?.Types
            .Any(t => {
              var typeName = t.Type.ToString();
              return typeName.StartsWith("IHandle<", StringComparison.Ordinal) ||
                     typeName.StartsWith("IReceptor<", StringComparison.Ordinal);
            }) ?? false;

        if (isHandler) {
          warnings.Add($"Line {lineNumber}: Found nested handler class '{className}' inside '{parentClass.Identifier.Text}'. " +
              "Consider extracting to a top-level receptor class for better discoverability and testing.");
        }
      }

      // H06: Check for MessageContext usage (Wolverine pattern)
      var hasMessageContext = classDecl.DescendantNodes()
          .OfType<IdentifierNameSyntax>()
          .Any(id => id.Identifier.Text == "MessageContext");

      if (hasMessageContext) {
        var contextLine = classDecl.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(id => id.Identifier.Text == "MessageContext")
            .GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        warnings.Add($"Line {contextLine}: MessageContext usage detected in '{className}'. " +
            "In Whizbang, use MessageEnvelope to access correlation ID, tenant ID, and message metadata. " +
            "Inject IMessageEnvelope or access via the ReceiveAsync method signature.");
      }

      // H07: Check for Activity/telemetry usage
      var hasActivitySource = classDecl.DescendantNodes()
          .OfType<IdentifierNameSyntax>()
          .Any(id => id.Identifier.Text == "ActivitySource");

      if (hasActivitySource) {
        var activityLine = classDecl.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(id => id.Identifier.Text == "ActivitySource")
            .GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        warnings.Add($"Line {activityLine}: ActivitySource usage detected in '{className}'. " +
            "Whizbang provides built-in observability via IReceptorObserver. Consider using the observability " +
            "infrastructure instead of manual Activity tracing for consistent telemetry.");
      }
    }
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

    // If no Whizbang using was added, add it
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

  private static SyntaxNode _transformMessageContext(SyntaxNode root, List<CodeChange> changes) {
    var rewriter = new MessageContextRewriter(changes);
    return rewriter.Visit(root);
  }

  private static SyntaxNode _transformLocalMessagePatterns(SyntaxNode root, List<CodeChange> changes) {
    var rewriter = new LocalMessageRewriter(changes);
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

      // Transform Wolverine IHandle<T> to Whizbang IReceptor<T>
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

      // Only transform Handle/HandleAsync methods in classes that implement IReceptor or IHandle
      if (methodName is "Handle" or "HandleAsync") {
        var classDecl = node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (classDecl?.BaseList != null) {
          var implementsHandler = classDecl.BaseList.Types
              .Any(t => {
                var typeName = t.Type.ToString();
                return typeName.StartsWith("IReceptor<", StringComparison.Ordinal) ||
                       typeName.StartsWith("IHandle<", StringComparison.Ordinal);
              });

          if (implementsHandler) {
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

  /// <summary>
  /// Rewriter that transforms MessageContext to MessageEnvelope (H06).
  /// </summary>
  private sealed class MessageContextRewriter : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;

    public MessageContextRewriter(List<CodeChange> changes) {
      _changes = changes;
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) {
      if (node.Identifier.Text == "MessageContext") {
        var newNode = SyntaxFactory.IdentifierName("MessageEnvelope")
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia());

        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.TypeRename,
            "Replaced 'MessageContext' with 'MessageEnvelope'",
            "MessageContext",
            "MessageEnvelope"));

        return newNode;
      }

      return base.VisitIdentifierName(node);
    }
  }

  /// <summary>
  /// Rewriter that transforms LocalMessage patterns and IMessageBus to IDispatcher (H04).
  /// </summary>
  private sealed class LocalMessageRewriter : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;

    public LocalMessageRewriter(List<CodeChange> changes) {
      _changes = changes;
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) {
      // Transform IMessageBus to IDispatcher
      if (node.Identifier.Text == "IMessageBus") {
        var newNode = SyntaxFactory.IdentifierName("IDispatcher")
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia());

        _changes.Add(new CodeChange(
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.TypeRename,
            "Replaced 'IMessageBus' with 'IDispatcher'",
            "IMessageBus",
            "IDispatcher"));

        return newNode;
      }

      return base.VisitIdentifierName(node);
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node) {
      // Transform _bus.InvokeAsync(new LocalMessage<T>(...)) to _dispatcher.LocalInvokeAsync<T>(...)
      var exprText = node.Expression.ToString();

      // Check if it's an InvokeAsync call with a LocalMessage argument
      if (exprText.EndsWith(".InvokeAsync", StringComparison.Ordinal)) {
        var args = node.ArgumentList.Arguments;
        if (args.Count == 1) {
          var arg = args[0].Expression;
          if (arg is ObjectCreationExpressionSyntax creation &&
              creation.Type.ToString().StartsWith("LocalMessage<", StringComparison.Ordinal)) {
            // Extract the generic type from LocalMessage<T>
            var localMessageType = creation.Type.ToString();
            var start = localMessageType.IndexOf('<');
            var end = localMessageType.LastIndexOf('>');
            if (start >= 0 && end > start) {
              var innerType = localMessageType.Substring(start + 1, end - start - 1);

              // Get the inner argument (the actual message)
              var innerArgs = creation.ArgumentList?.Arguments;
              var newArgList = innerArgs.HasValue && innerArgs.Value.Count > 0
                  ? SyntaxFactory.ArgumentList(innerArgs.Value)
                  : SyntaxFactory.ArgumentList();

              // Build: _dispatcher.LocalInvokeAsync<T>(innerMessage)
              var memberAccess = node.Expression as MemberAccessExpressionSyntax;
              if (memberAccess != null) {
                var newExpression = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    memberAccess.Expression,
                    SyntaxFactory.GenericName(
                        SyntaxFactory.Identifier("LocalInvokeAsync"),
                        SyntaxFactory.TypeArgumentList(
                            SyntaxFactory.SeparatedList<TypeSyntax>(new[] {
                                SyntaxFactory.ParseTypeName(innerType)
                            }))));

                var newInvocation = SyntaxFactory.InvocationExpression(newExpression, newArgList)
                    .WithLeadingTrivia(node.GetLeadingTrivia())
                    .WithTrailingTrivia(node.GetTrailingTrivia());

                _changes.Add(new CodeChange(
                    node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    ChangeType.MethodCallReplacement,
                    $"Transformed LocalMessage<{innerType}> to LocalInvokeAsync<{innerType}>",
                    node.ToString(),
                    newInvocation.ToString()));

                return newInvocation;
              }
            }
          }
        }
      }

      return base.VisitInvocationExpression(node);
    }
  }
}
