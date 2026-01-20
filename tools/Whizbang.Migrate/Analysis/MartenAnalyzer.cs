using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Migrate.Analysis;

/// <summary>
/// Analyzes source code for Marten projection and event store patterns that need migration.
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public sealed class MartenAnalyzer : ICodeAnalyzer {
  /// <inheritdoc />
  public Task<AnalysisResult> AnalyzeAsync(
      string sourceCode,
      string filePath,
      CancellationToken ct = default) {
    var projections = new List<ProjectionInfo>();
    var eventStoreUsages = new List<EventStoreUsageInfo>();
    var diRegistrations = new List<DIRegistrationInfo>();

    if (string.IsNullOrWhiteSpace(sourceCode)) {
      return Task.FromResult(_createEmptyResult());
    }

    var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: ct);
    var root = tree.GetRoot(ct);

    // Find projections
    var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
    foreach (var classDecl in classDeclarations) {
      ct.ThrowIfCancellationRequested();

      var projectionInfo = _analyzeProjection(classDecl, filePath);
      if (projectionInfo != null) {
        projections.Add(projectionInfo);
      }

      // Check for IDocumentStore/IDocumentSession injection
      var storeUsages = _analyzeEventStoreUsages(classDecl, filePath);
      eventStoreUsages.AddRange(storeUsages);
    }

    // Find DI registrations
    var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
    foreach (var invocation in invocations) {
      ct.ThrowIfCancellationRequested();

      var registration = _analyzeDIRegistration(invocation, filePath);
      if (registration != null) {
        diRegistrations.Add(registration);
      }
    }

    return Task.FromResult(new AnalysisResult {
      Handlers = [],
      Projections = projections,
      EventStoreUsages = eventStoreUsages,
      DIRegistrations = diRegistrations
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

  private static ProjectionInfo? _analyzeProjection(ClassDeclarationSyntax classDecl, string filePath) {
    if (classDecl.BaseList == null) {
      return null;
    }

    foreach (var baseType in classDecl.BaseList.Types) {
      var typeName = baseType.Type.ToString();

      ProjectionKind? kind = null;
      string? aggregateType = null;

      if (typeName.StartsWith("SingleStreamProjection<", StringComparison.Ordinal)) {
        kind = ProjectionKind.SingleStream;
        var genericArgs = _extractGenericArguments(typeName);
        aggregateType = genericArgs.Count > 0 ? genericArgs[0] : "unknown";
      } else if (typeName.StartsWith("MultiStreamProjection<", StringComparison.Ordinal)) {
        kind = ProjectionKind.MultiStream;
        var genericArgs = _extractGenericArguments(typeName);
        aggregateType = genericArgs.Count > 0 ? genericArgs[0] : "unknown";
      }

      if (kind != null && aggregateType != null) {
        var className = classDecl.Identifier.Text;
        var namespaceName = _getNamespace(classDecl);
        var fullyQualifiedName = string.IsNullOrEmpty(namespaceName)
            ? className
            : $"{namespaceName}.{className}";
        var lineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var eventTypes = _extractEventTypes(classDecl);

        return new ProjectionInfo(
            filePath,
            className,
            fullyQualifiedName,
            aggregateType,
            eventTypes,
            kind.Value,
            lineNumber);
      }
    }

    return null;
  }

  private static List<string> _extractEventTypes(ClassDeclarationSyntax classDecl) {
    var eventTypes = new List<string>();

    // Find all Apply methods
    var applyMethods = classDecl.Members
        .OfType<MethodDeclarationSyntax>()
        .Where(m => m.Identifier.Text == "Apply");

    foreach (var method in applyMethods) {
      var parameters = method.ParameterList.Parameters;
      if (parameters.Count >= 1) {
        var eventType = parameters[0].Type?.ToString();
        if (!string.IsNullOrEmpty(eventType)) {
          eventTypes.Add(eventType);
        }
      }
    }

    return eventTypes;
  }

  private static List<EventStoreUsageInfo> _analyzeEventStoreUsages(
      ClassDeclarationSyntax classDecl,
      string filePath) {
    var usages = new List<EventStoreUsageInfo>();
    var className = classDecl.Identifier.Text;

    // Check fields
    var fields = classDecl.Members.OfType<FieldDeclarationSyntax>();
    foreach (var field in fields) {
      var typeName = field.Declaration.Type.ToString();
      if (typeName == "IDocumentStore") {
        var lineNumber = field.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        usages.Add(new EventStoreUsageInfo(
            filePath,
            className,
            EventStoreUsageKind.DocumentStoreInjection,
            lineNumber));
      } else if (typeName == "IDocumentSession") {
        var lineNumber = field.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        usages.Add(new EventStoreUsageInfo(
            filePath,
            className,
            EventStoreUsageKind.DocumentSessionUsage,
            lineNumber));
      }
    }

    // Check constructor parameters
    var constructors = classDecl.Members.OfType<ConstructorDeclarationSyntax>();
    foreach (var ctor in constructors) {
      foreach (var param in ctor.ParameterList.Parameters) {
        var typeName = param.Type?.ToString();
        if (typeName == "IDocumentStore") {
          var lineNumber = param.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
          usages.Add(new EventStoreUsageInfo(
              filePath,
              className,
              EventStoreUsageKind.DocumentStoreInjection,
              lineNumber));
        } else if (typeName == "IDocumentSession") {
          var lineNumber = param.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
          usages.Add(new EventStoreUsageInfo(
              filePath,
              className,
              EventStoreUsageKind.DocumentSessionUsage,
              lineNumber));
        }
      }
    }

    return usages;
  }

  private static DIRegistrationInfo? _analyzeDIRegistration(
      InvocationExpressionSyntax invocation,
      string filePath) {
    var methodName = _getMethodName(invocation);

    DIRegistrationKind? kind = methodName switch {
      "AddMarten" => DIRegistrationKind.AddMarten,
      "UseWolverine" => DIRegistrationKind.UseWolverine,
      _ => null
    };

    if (kind == null) {
      return null;
    }

    var lineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    var originalCode = invocation.ToString();

    return new DIRegistrationInfo(
        filePath,
        kind.Value,
        lineNumber,
        originalCode);
  }

  private static string? _getMethodName(InvocationExpressionSyntax invocation) {
    return invocation.Expression switch {
      MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
      IdentifierNameSyntax identifier => identifier.Identifier.Text,
      _ => null
    };
  }

  private static List<string> _extractGenericArguments(string typeName) {
    var result = new List<string>();
    var start = typeName.IndexOf('<');
    var end = typeName.LastIndexOf('>');

    if (start < 0 || end < 0 || end <= start) {
      return result;
    }

    var args = typeName.Substring(start + 1, end - start - 1);

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

  private static string _getNamespace(ClassDeclarationSyntax classDecl) {
    var fileScopedNamespace = classDecl.Ancestors()
        .OfType<FileScopedNamespaceDeclarationSyntax>()
        .FirstOrDefault();
    if (fileScopedNamespace != null) {
      return fileScopedNamespace.Name.ToString();
    }

    var blockNamespace = classDecl.Ancestors()
        .OfType<NamespaceDeclarationSyntax>()
        .FirstOrDefault();
    if (blockNamespace != null) {
      return blockNamespace.Name.ToString();
    }

    return string.Empty;
  }
}
