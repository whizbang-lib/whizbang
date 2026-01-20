using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Migrate.Analysis;

/// <summary>
/// Analyzes source code for Wolverine handler patterns that need migration.
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public sealed class WolverineAnalyzer : ICodeAnalyzer {
  /// <inheritdoc />
  public Task<AnalysisResult> AnalyzeAsync(
      string sourceCode,
      string filePath,
      CancellationToken ct = default) {
    var handlers = new List<HandlerInfo>();

    if (string.IsNullOrWhiteSpace(sourceCode)) {
      return Task.FromResult(_createEmptyResult());
    }

    var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: ct);
    var root = tree.GetRoot(ct);

    // Find all class declarations
    var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

    foreach (var classDecl in classDeclarations) {
      ct.ThrowIfCancellationRequested();

      var className = classDecl.Identifier.Text;
      var namespaceName = _getNamespace(classDecl);
      var fullyQualifiedName = string.IsNullOrEmpty(namespaceName)
          ? className
          : $"{namespaceName}.{className}";
      var lineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

      // Check for IHandle<T> or IHandle<T,TResult> interface
      var iHandleInfo = _findIHandleInterface(classDecl);
      if (iHandleInfo != null) {
        handlers.Add(new HandlerInfo(
            filePath,
            className,
            fullyQualifiedName,
            iHandleInfo.Value.MessageType,
            iHandleInfo.Value.ReturnType,
            HandlerKind.IHandleInterface,
            lineNumber));
        continue;
      }

      // Check for [WolverineHandler] attribute
      if (_hasWolverineHandlerAttribute(classDecl)) {
        var handleMethod = _findHandleMethod(classDecl);
        if (handleMethod != null) {
          handlers.Add(new HandlerInfo(
              filePath,
              className,
              fullyQualifiedName,
              handleMethod.Value.MessageType,
              handleMethod.Value.ReturnType,
              HandlerKind.WolverineAttribute,
              lineNumber));
        }

        continue;
      }

      // Check for convention-based handlers (public Handle/HandleAsync methods)
      var conventionHandlers = _findConventionBasedHandlers(classDecl, filePath, fullyQualifiedName);
      handlers.AddRange(conventionHandlers);
    }

    return Task.FromResult(new AnalysisResult {
      Handlers = handlers,
      Projections = [],
      EventStoreUsages = [],
      DIRegistrations = []
    });
  }

  /// <inheritdoc />
  public async Task<AnalysisResult> AnalyzeProjectAsync(
      string projectPath,
      CancellationToken ct = default) {
    var allHandlers = new List<HandlerInfo>();
    var allProjections = new List<ProjectionInfo>();
    var allEventStoreUsages = new List<EventStoreUsageInfo>();
    var allDIRegistrations = new List<DIRegistrationInfo>();

    var projectDir = Path.GetDirectoryName(projectPath) ?? ".";
    var csFiles = Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories);

    foreach (var file in csFiles) {
      ct.ThrowIfCancellationRequested();

      var sourceCode = await File.ReadAllTextAsync(file, ct);
      var result = await AnalyzeAsync(sourceCode, file, ct);

      allHandlers.AddRange(result.Handlers);
      allProjections.AddRange(result.Projections);
      allEventStoreUsages.AddRange(result.EventStoreUsages);
      allDIRegistrations.AddRange(result.DIRegistrations);
    }

    return new AnalysisResult {
      Handlers = allHandlers,
      Projections = allProjections,
      EventStoreUsages = allEventStoreUsages,
      DIRegistrations = allDIRegistrations
    };
  }

  private static AnalysisResult _createEmptyResult() {
    return new AnalysisResult {
      Handlers = [],
      Projections = [],
      EventStoreUsages = [],
      DIRegistrations = []
    };
  }

  private static string _getNamespace(ClassDeclarationSyntax classDecl) {
    // Check for file-scoped namespace
    var fileScopedNamespace = classDecl.Ancestors()
        .OfType<FileScopedNamespaceDeclarationSyntax>()
        .FirstOrDefault();
    if (fileScopedNamespace != null) {
      return fileScopedNamespace.Name.ToString();
    }

    // Check for block-scoped namespace
    var blockNamespace = classDecl.Ancestors()
        .OfType<NamespaceDeclarationSyntax>()
        .FirstOrDefault();
    if (blockNamespace != null) {
      return blockNamespace.Name.ToString();
    }

    return string.Empty;
  }

  private static (string MessageType, string? ReturnType)? _findIHandleInterface(ClassDeclarationSyntax classDecl) {
    if (classDecl.BaseList == null) {
      return null;
    }

    foreach (var baseType in classDecl.BaseList.Types) {
      var typeName = baseType.Type.ToString();

      // Match IHandle<T> or IHandle<T,TResult>
      if (typeName.StartsWith("IHandle<", StringComparison.Ordinal)) {
        var genericArgs = _extractGenericArguments(typeName);
        if (genericArgs.Count >= 1) {
          return (genericArgs[0], genericArgs.Count > 1 ? genericArgs[1] : null);
        }
      }
    }

    return null;
  }

  private static List<string> _extractGenericArguments(string typeName) {
    var result = new List<string>();
    var start = typeName.IndexOf('<');
    var end = typeName.LastIndexOf('>');

    if (start < 0 || end < 0 || end <= start) {
      return result;
    }

    var args = typeName.Substring(start + 1, end - start - 1);

    // Simple split for non-nested generics
    var depth = 0;
    var currentArg = new System.Text.StringBuilder();

    foreach (var c in args) {
      if (c == '<') {
        depth++;
        currentArg.Append(c);
      } else if (c == '>') {
        depth--;
        currentArg.Append(c);
      } else if (c == ',' && depth == 0) {
        result.Add(currentArg.ToString().Trim());
        currentArg.Clear();
      } else {
        currentArg.Append(c);
      }
    }

    if (currentArg.Length > 0) {
      result.Add(currentArg.ToString().Trim());
    }

    return result;
  }

  private static bool _hasWolverineHandlerAttribute(ClassDeclarationSyntax classDecl) {
    return classDecl.AttributeLists
        .SelectMany(al => al.Attributes)
        .Any(attr => {
          var name = attr.Name.ToString();
          return name is "WolverineHandler" or "WolverineHandlerAttribute";
        });
  }

  private static (string MessageType, string? ReturnType)? _findHandleMethod(ClassDeclarationSyntax classDecl) {
    var handleMethods = classDecl.Members
        .OfType<MethodDeclarationSyntax>()
        .Where(m => m.Identifier.Text is "Handle" or "HandleAsync")
        .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword))
        .ToList();

    if (handleMethods.Count == 0) {
      return null;
    }

    var method = handleMethods[0];
    var parameters = method.ParameterList.Parameters;

    if (parameters.Count == 0) {
      return null;
    }

    var messageType = parameters[0].Type?.ToString() ?? "unknown";
    var returnType = _extractReturnType(method.ReturnType);

    return (messageType, returnType);
  }

  private static string? _extractReturnType(TypeSyntax returnType) {
    var returnTypeStr = returnType.ToString();

    // void or Task returns null
    if (returnTypeStr is "void" or "Task") {
      return null;
    }

    // Task<T> returns T
    if (returnTypeStr.StartsWith("Task<", StringComparison.Ordinal)) {
      var genericArgs = _extractGenericArguments(returnTypeStr);
      return genericArgs.Count > 0 ? genericArgs[0] : null;
    }

    // ValueTask<T> returns T
    if (returnTypeStr.StartsWith("ValueTask<", StringComparison.Ordinal)) {
      var genericArgs = _extractGenericArguments(returnTypeStr);
      return genericArgs.Count > 0 ? genericArgs[0] : null;
    }

    return returnTypeStr;
  }

  private static List<HandlerInfo> _findConventionBasedHandlers(
      ClassDeclarationSyntax classDecl,
      string filePath,
      string fullyQualifiedName) {
    var handlers = new List<HandlerInfo>();
    var className = classDecl.Identifier.Text;

    var handleMethods = classDecl.Members
        .OfType<MethodDeclarationSyntax>()
        .Where(m => m.Identifier.Text is "Handle" or "HandleAsync")
        .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword))
        .Where(m => !m.Modifiers.Any(SyntaxKind.PrivateKeyword))
        .ToList();

    foreach (var method in handleMethods) {
      var parameters = method.ParameterList.Parameters;
      if (parameters.Count == 0) {
        continue;
      }

      var messageType = parameters[0].Type?.ToString() ?? "unknown";
      var returnType = _extractReturnType(method.ReturnType);
      var lineNumber = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

      handlers.Add(new HandlerInfo(
          filePath,
          className,
          fullyQualifiedName,
          messageType,
          returnType,
          HandlerKind.ConventionBased,
          lineNumber));
    }

    return handlers;
  }
}
