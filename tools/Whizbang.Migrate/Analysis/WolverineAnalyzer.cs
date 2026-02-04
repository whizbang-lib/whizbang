using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Migrate.Analysis;

/// <summary>
/// Analyzes source code for Wolverine handler patterns that need migration.
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public sealed class WolverineAnalyzer : ICodeAnalyzer {
  /// <summary>
  /// Known Wolverine interfaces that should not generate warnings.
  /// </summary>
  private static readonly HashSet<string> _knownWolverineInterfaces = new(StringComparer.Ordinal) {
    "IHandle",
    "IMessageBus",
    "IMessageContext",
    "MessageContext"
  };

  /// <summary>
  /// Known Marten interfaces that should not generate warnings.
  /// </summary>
  private static readonly HashSet<string> _knownMartenInterfaces = new(StringComparer.Ordinal) {
    "IDocumentSession",
    "IQuerySession",
    "IDocumentStore"
  };

  /// <summary>
  /// Known standard types that should not generate warnings as parameters.
  /// </summary>
  private static readonly HashSet<string> _knownStandardTypes = new(StringComparer.Ordinal) {
    "CancellationToken",
    "ILogger",
    "IServiceProvider"
  };

  /// <summary>
  /// Base class patterns to ignore from CustomHandlerBaseClass warnings.
  /// These are non-Wolverine/Marten base classes that shouldn't generate warnings.
  /// </summary>
  private static readonly HashSet<string> _ignoredBaseClassPatterns = new(StringComparer.Ordinal) {
    "Endpoint",           // FastEndpoints - not Wolverine
    "EndpointBase",       // FastEndpoints base
    "EndpointWithoutRequest", // FastEndpoints
    "BaseEndpoint"        // Common endpoint base class pattern
  };

  /// <inheritdoc />
  public Task<AnalysisResult> AnalyzeAsync(
      string sourceCode,
      string filePath,
      CancellationToken ct = default) {
    var handlers = new List<HandlerInfo>();
    var warnings = new List<MigrationWarning>();

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

        // Check for custom base class
        var baseClassWarning = _checkForCustomBaseClass(classDecl, filePath, className, lineNumber);
        if (baseClassWarning != null) {
          warnings.Add(baseClassWarning);
        }

        // Check handle method parameters
        var handleMethod = _findHandleMethodSyntax(classDecl);
        if (handleMethod != null) {
          warnings.AddRange(_checkForUnknownParameters(handleMethod, filePath, className));
        }

        continue;
      }

      // Check for [WolverineHandler] attribute
      if (_hasWolverineHandlerAttribute(classDecl)) {
        var handleMethod = _findHandleMethod(classDecl);

        // Always count as handler if [WolverineHandler] attribute is present,
        // even if we can't find a standard Handle method (may use custom base class)
        var messageType = handleMethod?.MessageType ?? _inferMessageTypeFromBaseClass(classDecl) ?? "unknown";
        var returnType = handleMethod?.ReturnType;

        handlers.Add(new HandlerInfo(
            filePath,
            className,
            fullyQualifiedName,
            messageType,
            returnType,
            HandlerKind.WolverineAttribute,
            lineNumber));

        // Check for custom base class
        var baseClassWarning = _checkForCustomBaseClass(classDecl, filePath, className, lineNumber);
        if (baseClassWarning != null) {
          warnings.Add(baseClassWarning);
        }

        // Check handle method parameters
        var handleMethodSyntax = _findHandleMethodSyntax(classDecl);
        if (handleMethodSyntax != null) {
          warnings.AddRange(_checkForUnknownParameters(handleMethodSyntax, filePath, className));
        }

        continue;
      }

      // Check for convention-based handlers (public Handle/HandleAsync methods)
      var conventionHandlers = _findConventionBasedHandlers(classDecl, filePath, fullyQualifiedName);
      if (conventionHandlers.Count > 0) {
        handlers.AddRange(conventionHandlers);

        // Check for custom base class
        var baseClassWarning = _checkForCustomBaseClass(classDecl, filePath, className, lineNumber);
        if (baseClassWarning != null) {
          warnings.Add(baseClassWarning);
        }

        // Check handle method parameters for all convention-based handlers
        var handleMethods = classDecl.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text is "Handle" or "HandleAsync")
            .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword));

        foreach (var method in handleMethods) {
          warnings.AddRange(_checkForUnknownParameters(method, filePath, className));
        }
      }
    }

    return Task.FromResult(new AnalysisResult {
      Handlers = handlers,
      Projections = [],
      EventStoreUsages = [],
      DIRegistrations = [],
      Warnings = warnings
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
    var allWarnings = new List<MigrationWarning>();

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
      allWarnings.AddRange(result.Warnings);
    }

    return new AnalysisResult {
      Handlers = allHandlers,
      Projections = allProjections,
      EventStoreUsages = allEventStoreUsages,
      DIRegistrations = allDIRegistrations,
      Warnings = allWarnings
    };
  }

  private static AnalysisResult _createEmptyResult() {
    return new AnalysisResult {
      Handlers = [],
      Projections = [],
      EventStoreUsages = [],
      DIRegistrations = [],
      Warnings = []
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

  private static MethodDeclarationSyntax? _findHandleMethodSyntax(ClassDeclarationSyntax classDecl) {
    return classDecl.Members
        .OfType<MethodDeclarationSyntax>()
        .Where(m => m.Identifier.Text is "Handle" or "HandleAsync")
        .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword))
        .FirstOrDefault();
  }

  private static MigrationWarning? _checkForCustomBaseClass(
      ClassDeclarationSyntax classDecl,
      string filePath,
      string className,
      int lineNumber) {
    if (classDecl.BaseList == null) {
      return null;
    }

    foreach (var baseType in classDecl.BaseList.Types) {
      var typeName = baseType.Type.ToString();
      var baseTypeName = _getBaseTypeName(typeName);

      // Skip known Wolverine interfaces (e.g., IHandle<T>)
      if (_isKnownWolverineType(baseTypeName)) {
        continue;
      }

      // Skip known Marten interfaces
      if (_isKnownMartenType(baseTypeName)) {
        continue;
      }

      // Skip interfaces (they start with I and have uppercase second letter)
      if (_isInterface(baseTypeName)) {
        continue;
      }

      // Skip "object" base class
      if (baseTypeName is "object" or "Object") {
        continue;
      }

      // Skip ignored base class patterns (e.g., FastEndpoints)
      if (_ignoredBaseClassPatterns.Contains(baseTypeName)) {
        continue;
      }

      // This is a custom base class - generate warning
      return new MigrationWarning(
          filePath,
          className,
          MigrationWarningKind.CustomHandlerBaseClass,
          $"Handler '{className}' inherits from custom base class '{typeName}'. " +
          "This base class may contain Marten/Wolverine infrastructure that needs manual migration.",
          lineNumber,
          typeName);
    }

    return null;
  }

  private static List<MigrationWarning> _checkForUnknownParameters(
      MethodDeclarationSyntax method,
      string filePath,
      string className) {
    var warnings = new List<MigrationWarning>();
    var parameters = method.ParameterList.Parameters;

    // Skip first parameter (the message type)
    foreach (var param in parameters.Skip(1)) {
      var typeName = param.Type?.ToString() ?? "";
      var baseTypeName = _getBaseTypeName(typeName);
      var lineNumber = param.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

      // Skip known Wolverine types
      if (_isKnownWolverineType(baseTypeName)) {
        continue;
      }

      // Skip known Marten types
      if (_isKnownMartenType(baseTypeName)) {
        continue;
      }

      // Skip known standard types
      if (_isKnownStandardType(baseTypeName)) {
        continue;
      }

      // Check if it's an interface (starts with I and has uppercase second letter)
      if (_isInterface(baseTypeName)) {
        warnings.Add(new MigrationWarning(
            filePath,
            className,
            MigrationWarningKind.UnknownInterfaceParameter,
            $"Handler '{className}' has unknown interface parameter '{typeName}'. " +
            "This interface may wrap Marten/Wolverine infrastructure that needs migration.",
            lineNumber,
            baseTypeName));
        continue;
      }

      // Check if it's a custom context class (contains "Context" in name)
      if (baseTypeName.Contains("Context", StringComparison.OrdinalIgnoreCase)) {
        warnings.Add(new MigrationWarning(
            filePath,
            className,
            MigrationWarningKind.CustomContextParameter,
            $"Handler '{className}' has custom context parameter '{typeName}'. " +
            "This context class may wrap Marten/Wolverine infrastructure that needs migration.",
            lineNumber,
            baseTypeName));
      }
    }

    return warnings;
  }

  private static string _getBaseTypeName(string typeName) {
    // Remove generic arguments: IHandle<T> -> IHandle
    var genericIndex = typeName.IndexOf('<');
    if (genericIndex > 0) {
      return typeName.Substring(0, genericIndex);
    }

    return typeName;
  }

  private static bool _isKnownWolverineType(string typeName) {
    return _knownWolverineInterfaces.Contains(typeName) ||
           typeName.StartsWith("IHandle", StringComparison.Ordinal);
  }

  private static bool _isKnownMartenType(string typeName) {
    return _knownMartenInterfaces.Contains(typeName);
  }

  private static bool _isKnownStandardType(string typeName) {
    return _knownStandardTypes.Contains(typeName) ||
           typeName.StartsWith("ILogger<", StringComparison.Ordinal);
  }

  private static bool _isInterface(string typeName) {
    // Interface names start with 'I' followed by uppercase letter
    return typeName.Length >= 2 &&
           typeName[0] == 'I' &&
           char.IsUpper(typeName[1]);
  }

  /// <summary>
  /// Infers the message type from a generic base class.
  /// For example: BaseJdxMessageHandler&lt;WorkflowContracts.StepAssignedEvent&gt; returns "WorkflowContracts.StepAssignedEvent"
  /// </summary>
  private static string? _inferMessageTypeFromBaseClass(ClassDeclarationSyntax classDecl) {
    if (classDecl.BaseList == null) {
      return null;
    }

    foreach (var baseType in classDecl.BaseList.Types) {
      var typeName = baseType.Type.ToString();

      // Look for generic base classes that might contain message type
      var genericArgs = _extractGenericArguments(typeName);
      if (genericArgs.Count >= 1) {
        // Return first generic argument as message type
        return genericArgs[0];
      }
    }

    return null;
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
