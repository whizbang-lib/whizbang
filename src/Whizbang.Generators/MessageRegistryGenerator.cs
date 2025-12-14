using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Incremental source generator that discovers messages, dispatchers, receptors, and perspectives,
/// and generates a message-registry.json file for VSCode extension tooling.
/// </summary>
[Generator]
public class MessageRegistryGenerator : IIncrementalGenerator {
  private const string I_COMMAND = "Whizbang.Core.ICommand";
  private const string I_EVENT = "Whizbang.Core.IEvent";
  private const string I_RECEPTOR = "Whizbang.Core.IReceptor";
  private const string I_PERSPECTIVE_OF = "Whizbang.Core.IPerspectiveOf";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover message types (ICommand, IEvent)
    var messageTypes = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is RecordDeclarationSyntax { BaseList.Types.Count: > 0 } ||
                                       node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => ExtractMessageType(ctx, ct)
    ).Where(static info => info is not null);

    // Discover dispatchers (SendAsync and PublishAsync calls)
    var dispatchers = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is InvocationExpressionSyntax invocation &&
                                       invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                                       (memberAccess.Name.Identifier.Text == "SendAsync" ||
                                        memberAccess.Name.Identifier.Text == "PublishAsync"),
        transform: static (ctx, ct) => ExtractDispatcher(ctx, ct)
    ).Where(static info => info is not null);

    // Discover receptors (IReceptor implementations)
    var receptors = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => ExtractReceptor(ctx, ct)
    ).Where(static info => info is not null);

    // Discover perspectives (IPerspectiveOf<TEvent> implementations)
    var perspectives = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => ExtractPerspective(ctx, ct)
    ).Where(static info => info is not null);

    // Combine all discoveries and generate JSON
    var allData = messageTypes.Collect()
        .Combine(dispatchers.Collect())
        .Combine(receptors.Collect())
        .Combine(perspectives.Collect());

    context.RegisterSourceOutput(
        allData,
        static (ctx, data) => GenerateMessageRegistry(ctx, data)
    );
  }

  private static MessageTypeInfo? ExtractMessageType(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var typeDeclaration = context.Node;
    var semanticModel = context.SemanticModel;

    // Predicate guarantees node is RecordDeclarationSyntax or ClassDeclarationSyntax
    // Defensive guard: throws if Roslyn returns null (indicates compiler bug)
    // See RoslynGuards.cs for rationale - no branch created, eliminates coverage gap
    var typeSymbol = RoslynGuards.GetTypeSymbolFromNode(typeDeclaration, semanticModel, cancellationToken);

    // Check if implements ICommand or IEvent
    var isCommand = typeSymbol.AllInterfaces.Any(i => i.ToDisplayString() == I_COMMAND);
    var isEvent = typeSymbol.AllInterfaces.Any(i => i.ToDisplayString() == I_EVENT);

    if (!isCommand && !isEvent) {
      return null;
    }

    var location = typeDeclaration.GetLocation();
    var lineSpan = location.GetLineSpan();

    return new MessageTypeInfo(
        TypeName: typeSymbol.ToDisplayString(),
        IsCommand: isCommand,
        IsEvent: isEvent,
        FilePath: lineSpan.Path,
        LineNumber: lineSpan.StartLinePosition.Line + 1,
        DocsUrl: null,    // Enriched later in GenerateMessageRegistry
        Tests: []         // Enriched later in GenerateMessageRegistry
    );
  }

  private static DispatcherLocationInfo? ExtractDispatcher(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var invocation = (InvocationExpressionSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    // Defensive guard: throws if Roslyn returns null (indicates compiler bug)
    // See RoslynGuards.cs for rationale - no branch created, eliminates coverage gap
    var methodSymbol = RoslynGuards.GetMethodSymbolOrThrow(invocation, semanticModel, cancellationToken);

    // Predicate already filtered for SendAsync/PublishAsync method names

    // Extract the message type from the actual argument expression
    ITypeSymbol? messageType = null;

    // For SendAsync and PublishAsync, look at the first argument
    if (invocation.ArgumentList.Arguments.Count > 0) {
      var firstArg = invocation.ArgumentList.Arguments[0].Expression;
      var argTypeInfo = semanticModel.GetTypeInfo(firstArg, cancellationToken);
      messageType = argTypeInfo.Type;
    }

    // Fallback to generic type argument for PublishAsync<TEvent>
    if (messageType is null && methodSymbol.IsGenericMethod && methodSymbol.TypeArguments.Length > 0) {
      messageType = methodSymbol.TypeArguments[0];
    }

    if (messageType is null) {
      return null;
    }

    // Find the containing method/class
    var containingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

    // Defensive guard: invocations must be in a class (valid C# requirement)
    var containingClass = RoslynGuards.GetContainingClassOrThrow(invocation);

    // Defensive guard: throws if Roslyn returns null (indicates compiler bug)
    // See RoslynGuards.cs for rationale - no branch created, eliminates coverage gap
    var classSymbol = RoslynGuards.GetClassSymbolOrThrow(containingClass, semanticModel, cancellationToken);

    var location = invocation.GetLocation();
    var lineSpan = location.GetLineSpan();

    return new DispatcherLocationInfo(
        MessageType: messageType.ToDisplayString(),
        ClassName: classSymbol.ToDisplayString(),
        MethodName: containingMethod?.Identifier.Text ?? "<unknown>",
        FilePath: lineSpan.Path,
        LineNumber: lineSpan.StartLinePosition.Line + 1,
        DocsUrl: null,    // Enriched later in GenerateMessageRegistry
        Tests: []         // Enriched later in GenerateMessageRegistry
    );
  }

  private static ReceptorLocationInfo? ExtractReceptor(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    // Defensive guard: throws if Roslyn returns null (indicates compiler bug)
    // See RoslynGuards.cs for rationale - no branch created, eliminates coverage gap
    var classSymbol = RoslynGuards.GetClassSymbolOrThrow(classDeclaration, semanticModel, cancellationToken);

    // Look for IReceptor<TMessage, TResponse> interface (regular receptor)
    var receptorInterface = classSymbol.AllInterfaces.FirstOrDefault(i =>
        i.OriginalDefinition.ToDisplayString() == I_RECEPTOR + "<TMessage, TResponse>");

    // If not found, look for IReceptor<TMessage> interface (void receptor)
    var voidReceptorInterface = receptorInterface is null
        ? classSymbol.AllInterfaces.FirstOrDefault(i =>
            i.OriginalDefinition.ToDisplayString() == I_RECEPTOR + "<TMessage>")
        : null;

    // Return null if neither interface is implemented
    if (receptorInterface is null && voidReceptorInterface is null) {
      return null;
    }

    // Determine message type and response type
    string messageType;
    string responseType;

    if (receptorInterface is not null) {
      // Regular receptor with response - defensive guard
      RoslynGuards.ValidateTypeArgumentCount(receptorInterface, 2, "IReceptor<TMessage, TResponse>");
      messageType = receptorInterface.TypeArguments[0].ToDisplayString();
      responseType = receptorInterface.TypeArguments[1].ToDisplayString();
    } else {
      // Void receptor - defensive guard
      RoslynGuards.ValidateTypeArgumentCount(voidReceptorInterface!, 1, "IReceptor<TMessage>");
      messageType = voidReceptorInterface.TypeArguments[0].ToDisplayString();
      responseType = "void";
    }

    // Find the HandleAsync method
    var handleMethod = classDeclaration.Members
        .OfType<MethodDeclarationSyntax>()
        .FirstOrDefault(m => m.Identifier.Text == "HandleAsync");

    var location = classDeclaration.GetLocation();
    var lineSpan = location.GetLineSpan();

    return new ReceptorLocationInfo(
        MessageType: messageType,
        ClassName: classSymbol.ToDisplayString(),
        MethodName: "HandleAsync",
        FilePath: lineSpan.Path,
        LineNumber: handleMethod?.GetLocation().GetLineSpan().StartLinePosition.Line + 1 ?? lineSpan.StartLinePosition.Line + 1,
        DocsUrl: null,    // Enriched later in GenerateMessageRegistry
        Tests: []         // Enriched later in GenerateMessageRegistry
    );
  }

  private static PerspectiveLocationInfo? ExtractPerspective(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    // Defensive guard: throws if Roslyn returns null (indicates compiler bug)
    // See RoslynGuards.cs for rationale - no branch created, eliminates coverage gap
    var classSymbol = RoslynGuards.GetClassSymbolOrThrow(classDeclaration, semanticModel, cancellationToken);

    // Look for IPerspectiveOf<TEvent> interfaces
    var perspectiveInterfaces = classSymbol.AllInterfaces
        .Where(i => i.OriginalDefinition.ToDisplayString() == I_PERSPECTIVE_OF + "<TEvent>")
        .ToList();

    if (!perspectiveInterfaces.Any()) {
      return null;
    }

    var location = classDeclaration.GetLocation();
    var lineSpan = location.GetLineSpan();

    // A perspective can handle multiple events
    var events = perspectiveInterfaces
        .Select(i => i.TypeArguments[0].ToDisplayString())
        .ToArray();

    return new PerspectiveLocationInfo(
        ClassName: classSymbol.ToDisplayString(),
        EventTypes: events,
        FilePath: lineSpan.Path,
        LineNumber: lineSpan.StartLinePosition.Line + 1,
        DocsUrl: null,    // Enriched later in GenerateMessageRegistry
        Tests: []         // Enriched later in GenerateMessageRegistry
    );
  }

  private static void GenerateMessageRegistry(
      SourceProductionContext context,
      (((ImmutableArray<MessageTypeInfo>, ImmutableArray<DispatcherLocationInfo>), ImmutableArray<ReceptorLocationInfo>), ImmutableArray<PerspectiveLocationInfo>) data) {

    var (((messages, dispatchers), receptors), perspectives) = data;

    // Load documentation and test mappings for VSCode tooling enhancement
    var docsMap = LoadCodeDocsMap(context);
    var testsMap = LoadCodeTestsMap(context);

    // Enrich all data with documentation URLs and test counts
    var enrichedMessages = messages.Select(m => EnrichMessageInfo(m, docsMap, testsMap)).ToImmutableArray();
    var enrichedDispatchers = dispatchers.Select(d => EnrichDispatcherInfo(d, docsMap, testsMap)).ToImmutableArray();
    var enrichedReceptors = receptors.Select(r => EnrichReceptorInfo(r, docsMap, testsMap)).ToImmutableArray();
    var enrichedPerspectives = perspectives.Select(p => EnrichPerspectiveInfo(p, docsMap, testsMap)).ToImmutableArray();

    // Load snippets
    var messageHeaderSnippet = TemplateUtilities.ExtractSnippet(
        typeof(MessageRegistryGenerator).Assembly,
        "MessageRegistrySnippets.cs",
        "MESSAGE_ENTRY_HEADER");

    var messageFooterSnippet = TemplateUtilities.ExtractSnippet(
        typeof(MessageRegistryGenerator).Assembly,
        "MessageRegistrySnippets.cs",
        "MESSAGE_ENTRY_FOOTER");

    var dispatcherSnippet = TemplateUtilities.ExtractSnippet(
        typeof(MessageRegistryGenerator).Assembly,
        "MessageRegistrySnippets.cs",
        "DISPATCHER_ENTRY");

    var receptorSnippet = TemplateUtilities.ExtractSnippet(
        typeof(MessageRegistryGenerator).Assembly,
        "MessageRegistrySnippets.cs",
        "RECEPTOR_ENTRY");

    var perspectiveSnippet = TemplateUtilities.ExtractSnippet(
        typeof(MessageRegistryGenerator).Assembly,
        "MessageRegistrySnippets.cs",
        "PERSPECTIVE_ENTRY");

    var testSnippet = TemplateUtilities.ExtractSnippet(
        typeof(MessageRegistryGenerator).Assembly,
        "MessageRegistrySnippets.cs",
        "TEST_ENTRY");

    var jsonWrapperSnippet = TemplateUtilities.ExtractSnippet(
        typeof(MessageRegistryGenerator).Assembly,
        "MessageRegistrySnippets.cs",
        "JSON_ARRAY_WRAPPER");

    // Collect all message type names from all sources
    var allMessageTypes = new HashSet<string>();

    foreach (var msg in enrichedMessages) {
      allMessageTypes.Add(msg.TypeName);
    }

    foreach (var dispatcher in enrichedDispatchers) {
      allMessageTypes.Add(dispatcher.MessageType);
    }

    foreach (var receptor in enrichedReceptors) {
      allMessageTypes.Add(receptor.MessageType);
    }

    foreach (var perspective in enrichedPerspectives) {
      foreach (var eventType in perspective.EventTypes) {
        allMessageTypes.Add(eventType);
      }
    }

    // Group by message type - include all message types, even if not defined in this project
    var messageGroups = allMessageTypes
        .Select(typeName => {
          var msg = enrichedMessages.FirstOrDefault(m => m.TypeName == typeName);
          return new {
            TypeName = typeName,
            Message = msg, // Might be null if message is from referenced assembly
            Dispatchers = enrichedDispatchers.Where(d => d.MessageType == typeName).ToList(),
            Receptors = enrichedReceptors.Where(r => r.MessageType == typeName).ToList(),
            Perspectives = enrichedPerspectives.Where(p => p.EventTypes.Contains(typeName)).ToList()
          };
        })
        .Where(g => g.Message is not null || g.Dispatchers.Count > 0 || g.Receptors.Count > 0 || g.Perspectives.Count > 0)
        .ToList();

    // Build message entries
    var messageEntries = new StringBuilder();

    for (int i = 0; i < messageGroups.Count; i++) {
      var group = messageGroups[i];
      var msg = group.Message;

      // Build message header
      var messageHeader = messageHeaderSnippet
          .Replace("__MESSAGE_TYPE__", EscapeJson(group.TypeName));

      // If message is defined in this project, use its metadata; otherwise, infer from usage
      if (msg is not null) {
        var testEntries = BuildTestEntries(msg.Tests, testSnippet);
        messageHeader = messageHeader
            .Replace("__IS_COMMAND__", msg.IsCommand.ToString().ToLower())
            .Replace("__IS_EVENT__", msg.IsEvent.ToString().ToLower())
            .Replace("__FILE_PATH__", EscapeJson(msg.FilePath))
            .Replace("__LINE_NUMBER__", msg.LineNumber.ToString())
            .Replace("__DOCS_URL__", msg.DocsUrl ?? "")
            .Replace("__TESTS__", testEntries);
      } else {
        // Message is from a referenced assembly - infer type from handlers
        var isCommand = group.Receptors.Count > 0;
        var isEvent = group.Perspectives.Count > 0 || (group.Receptors.Count == 0 && group.Dispatchers.Count > 0);

        messageHeader = messageHeader
            .Replace("__IS_COMMAND__", isCommand.ToString().ToLower())
            .Replace("__IS_EVENT__", isEvent.ToString().ToLower())
            .Replace("__FILE_PATH__", "")
            .Replace("__LINE_NUMBER__", "0")
            .Replace("__DOCS_URL__", "")
            .Replace("__TESTS__", "");
      }

      // Build dispatchers
      var dispatcherEntries = new StringBuilder();
      for (int j = 0; j < group.Dispatchers.Count; j++) {
        var d = group.Dispatchers[j];
        var dispatcherTestEntries = BuildTestEntries(d.Tests, testSnippet);
        var entry = dispatcherSnippet
            .Replace("__CLASS_NAME__", EscapeJson(d.ClassName))
            .Replace("__METHOD_NAME__", EscapeJson(d.MethodName))
            .Replace("__FILE_PATH__", EscapeJson(d.FilePath))
            .Replace("__LINE_NUMBER__", d.LineNumber.ToString())
            .Replace("__DOCS_URL__", d.DocsUrl ?? "")
            .Replace("__TESTS__", dispatcherTestEntries);

        dispatcherEntries.Append(entry);
        if (j < group.Dispatchers.Count - 1) {
          dispatcherEntries.AppendLine(",");
        }
      }

      // Build receptors
      var receptorEntries = new StringBuilder();
      for (int j = 0; j < group.Receptors.Count; j++) {
        var r = group.Receptors[j];
        var receptorTestEntries = BuildTestEntries(r.Tests, testSnippet);
        var entry = receptorSnippet
            .Replace("__CLASS_NAME__", EscapeJson(r.ClassName))
            .Replace("__METHOD_NAME__", EscapeJson(r.MethodName))
            .Replace("__FILE_PATH__", EscapeJson(r.FilePath))
            .Replace("__LINE_NUMBER__", r.LineNumber.ToString())
            .Replace("__DOCS_URL__", r.DocsUrl ?? "")
            .Replace("__TESTS__", receptorTestEntries);

        receptorEntries.Append(entry);
        if (j < group.Receptors.Count - 1) {
          receptorEntries.AppendLine(",");
        }
      }

      // Build perspectives
      var perspectiveEntries = new StringBuilder();
      for (int j = 0; j < group.Perspectives.Count; j++) {
        var p = group.Perspectives[j];
        var perspectiveTestEntries = BuildTestEntries(p.Tests, testSnippet);
        var entry = perspectiveSnippet
            .Replace("__CLASS_NAME__", EscapeJson(p.ClassName))
            .Replace("__FILE_PATH__", EscapeJson(p.FilePath))
            .Replace("__LINE_NUMBER__", p.LineNumber.ToString())
            .Replace("__DOCS_URL__", p.DocsUrl ?? "")
            .Replace("__TESTS__", perspectiveTestEntries);

        perspectiveEntries.Append(entry);
        if (j < group.Perspectives.Count - 1) {
          perspectiveEntries.AppendLine(",");
        }
      }

      // Build message footer
      var messageFooter = messageFooterSnippet
          .Replace("__DISPATCHERS__", dispatcherEntries.ToString())
          .Replace("__RECEPTORS__", receptorEntries.ToString())
          .Replace("__PERSPECTIVES__", perspectiveEntries.ToString());

      // Combine header and footer
      messageEntries.Append(messageHeader);
      messageEntries.Append(messageFooter);

      if (i < messageGroups.Count - 1) {
        messageEntries.AppendLine(",");
      }
    }

    // Build final JSON
    var json = jsonWrapperSnippet.Replace("__MESSAGES__", messageEntries.ToString());

    // Generate C# wrapper with embedded JSON using snippet
    var wrapperSnippet = TemplateUtilities.ExtractSnippet(
        typeof(MessageRegistryGenerator).Assembly,
        "MessageRegistrySnippets.cs",
        "CSHARP_WRAPPER");

    var csharpSource = wrapperSnippet.Replace("__JSON__", json.Replace("\"", "\"\""));

    context.AddSource("MessageRegistry.g.cs", csharpSource);
  }

  // ========================================
  // Mapping File Loaders for VSCode Tooling Enhancement
  // ========================================

  /// <summary>
  /// Loads code-docs-map.json from documentation repository.
  /// Returns mapping: symbol name → documentation URL
  /// </summary>
  private static Dictionary<string, string> LoadCodeDocsMap(SourceProductionContext context) {
    var docsPath = PathResolver.FindDocsRepositoryPath();
    if (docsPath == null) {
      return new Dictionary<string, string>();
    }

    var mapPath = Path.Combine(docsPath, "src", "assets", "code-docs-map.json");
    if (!File.Exists(mapPath)) {
      return new Dictionary<string, string>();
    }

    try {
      var json = File.ReadAllText(mapPath);
      var map = JsonSerializer.Deserialize<Dictionary<string, CodeDocsEntry>>(json);
      return map?.ToDictionary(
        kvp => kvp.Key,
        kvp => kvp.Value.Docs
      ) ?? new Dictionary<string, string>();
    } catch (Exception ex) {
      // Log diagnostic but don't fail generation
      context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.FailedToLoadDocsMap,
        Location.None,
        ex.Message
      ));
      return new Dictionary<string, string>();
    }
  }

  /// <summary>
  /// Loads code-tests-map.json from documentation repository.
  /// Returns mapping: symbol name → test information array
  /// </summary>
  private static Dictionary<string, TestInfo[]> LoadCodeTestsMap(SourceProductionContext context) {
    var docsPath = PathResolver.FindDocsRepositoryPath();
    if (docsPath == null) {
      return new Dictionary<string, TestInfo[]>();
    }

    var mapPath = Path.Combine(docsPath, "src", "assets", "code-tests-map.json");
    if (!File.Exists(mapPath)) {
      return new Dictionary<string, TestInfo[]>();
    }

    try {
      var json = File.ReadAllText(mapPath);
      var map = JsonSerializer.Deserialize<CodeTestsMapData>(json);
      return map?.CodeToTests?.ToDictionary(
        kvp => kvp.Key,
        kvp => kvp.Value?.Select(t => new TestInfo(
          TestFile: t.TestFile ?? "",
          TestMethod: t.TestMethod ?? "",
          TestLine: t.TestLine,
          TestClass: t.TestClass ?? ""
        )).ToArray() ?? []
      ) ?? new Dictionary<string, TestInfo[]>();
    } catch (Exception ex) {
      // Log diagnostic but don't fail generation
      context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.FailedToLoadTestsMap,
        Location.None,
        ex.Message
      ));
      return new Dictionary<string, TestInfo[]>();
    }
  }

  /// <summary>
  /// Extracts simple type name for mapping lookup.
  /// "Whizbang.Core.Dispatcher" → "Dispatcher"
  /// "IDispatcher" → "IDispatcher" (keeps interface prefix)
  /// </summary>
  private static string ExtractSimpleTypeName(string fullName) {
    var lastDot = fullName.LastIndexOf('.');
    return lastDot >= 0 ? fullName.Substring(lastDot + 1) : fullName;
  }

  /// <summary>
  /// Enriches a MessageTypeInfo with documentation URL and test information.
  /// </summary>
  private static MessageTypeInfo EnrichMessageInfo(
      MessageTypeInfo info,
      Dictionary<string, string> docsMap,
      Dictionary<string, TestInfo[]> testsMap) {

    var simpleName = ExtractSimpleTypeName(info.TypeName);
    var docsUrl = docsMap.TryGetValue(simpleName, out var docs) ? docs : null;
    var tests = testsMap.TryGetValue(simpleName, out var t) ? t : [];

    return info with { DocsUrl = docsUrl, Tests = tests };
  }

  /// <summary>
  /// Enriches a DispatcherLocationInfo with documentation URL and test information.
  /// </summary>
  private static DispatcherLocationInfo EnrichDispatcherInfo(
      DispatcherLocationInfo info,
      Dictionary<string, string> docsMap,
      Dictionary<string, TestInfo[]> testsMap) {

    var simpleName = ExtractSimpleTypeName(info.ClassName);
    var docsUrl = docsMap.TryGetValue(simpleName, out var docs) ? docs : null;
    var tests = testsMap.TryGetValue(simpleName, out var t) ? t : [];

    return info with { DocsUrl = docsUrl, Tests = tests };
  }

  /// <summary>
  /// Enriches a ReceptorLocationInfo with documentation URL and test information.
  /// </summary>
  private static ReceptorLocationInfo EnrichReceptorInfo(
      ReceptorLocationInfo info,
      Dictionary<string, string> docsMap,
      Dictionary<string, TestInfo[]> testsMap) {

    var simpleName = ExtractSimpleTypeName(info.ClassName);
    var docsUrl = docsMap.TryGetValue(simpleName, out var docs) ? docs : null;
    var tests = testsMap.TryGetValue(simpleName, out var t) ? t : [];

    return info with { DocsUrl = docsUrl, Tests = tests };
  }

  /// <summary>
  /// Enriches a PerspectiveLocationInfo with documentation URL and test information.
  /// </summary>
  private static PerspectiveLocationInfo EnrichPerspectiveInfo(
      PerspectiveLocationInfo info,
      Dictionary<string, string> docsMap,
      Dictionary<string, TestInfo[]> testsMap) {

    var simpleName = ExtractSimpleTypeName(info.ClassName);
    var docsUrl = docsMap.TryGetValue(simpleName, out var docs) ? docs : null;
    var tests = testsMap.TryGetValue(simpleName, out var t) ? t : [];

    return info with { DocsUrl = docsUrl, Tests = tests };
  }

  private static string EscapeJson(string value) {
    return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
  }

  /// <summary>
  /// Builds JSON test entries from test information array.
  /// </summary>
  private static string BuildTestEntries(TestInfo[] tests, string testSnippet) {
    if (tests.Length == 0) {
      return "";
    }

    var entries = new StringBuilder();
    for (int i = 0; i < tests.Length; i++) {
      var test = tests[i];
      var entry = testSnippet
          .Replace("__TEST_FILE__", EscapeJson(test.TestFile))
          .Replace("__TEST_METHOD__", EscapeJson(test.TestMethod))
          .Replace("__TEST_LINE__", test.TestLine.ToString())
          .Replace("__TEST_CLASS__", EscapeJson(test.TestClass));

      entries.Append(entry);
      if (i < tests.Length - 1) {
        entries.AppendLine(",");
      }
    }

    return entries.ToString();
  }

  // Helper classes for JSON deserialization
  private class CodeDocsEntry {
    public string File { get; set; } = "";
    public int Line { get; set; }
    public string Symbol { get; set; } = "";
    public string Docs { get; set; } = "";
  }

  private class CodeTestsMapData {
    public Dictionary<string, TestLinkMapping[]>? CodeToTests { get; set; }
  }

  private class TestLinkMapping {
    public string TestFile { get; set; } = "";
    public string TestMethod { get; set; } = "";
    public int TestLine { get; set; }
    public string TestClass { get; set; } = "";
  }
}

// Value types for captured information (registry-specific versions to avoid conflicts)
internal sealed record MessageTypeInfo(
  string TypeName,
  bool IsCommand,
  bool IsEvent,
  string FilePath,
  int LineNumber,
  string? DocsUrl,     // Documentation URL from code-docs-map.json
  TestInfo[] Tests     // Test information from code-tests-map.json
);

internal sealed record DispatcherLocationInfo(
  string MessageType,
  string ClassName,
  string MethodName,
  string FilePath,
  int LineNumber,
  string? DocsUrl,     // Documentation URL for dispatcher class
  TestInfo[] Tests     // Test information for dispatcher class
);

internal sealed record ReceptorLocationInfo(
  string MessageType,
  string ClassName,
  string MethodName,
  string FilePath,
  int LineNumber,
  string? DocsUrl,     // Documentation URL for receptor class
  TestInfo[] Tests     // Test information for receptor class
);

internal sealed record PerspectiveLocationInfo(
  string ClassName,
  string[] EventTypes,
  string FilePath,
  int LineNumber,
  string? DocsUrl,     // Documentation URL for perspective class
  TestInfo[] Tests     // Test information for perspective class
);

internal sealed record TestInfo(
  string TestFile,
  string TestMethod,
  int TestLine,
  string TestClass
);
