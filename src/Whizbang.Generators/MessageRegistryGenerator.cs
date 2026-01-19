using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_EmptyCompilation_GeneratesEmptyRegistryAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_SingleCommand_DiscoversCommandAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_SingleEvent_DiscoversEventAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_CommandWithDispatcher_DiscoversDispatcherAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_CommandWithReceptor_DiscoversReceptorAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_EventWithPerspective_DiscoversPerspectiveAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_MultipleMessages_DiscoversAllAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_GeneratedJson_ContainsFilePathsAndLineNumbersAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_PerspectiveWithMultipleEvents_DiscoversAllEventsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_NoCompilationErrors_GeneratesValidCodeAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_VoidReceptor_DiscoversReceptorAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_MixedReceptorTypes_DiscoversAllAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_MultipleVoidReceptors_DiscoversAllAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_PublishAsyncWithGeneric_DiscoversDispatcherAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_MultipleDispatchesInSameMethod_DiscoversAllAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_ConditionalDispatch_DiscoversDispatcherAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_DispatcherVariableName_DiscoversDispatcherAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_StructMessageType_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_ClassWithoutMessageInterface_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_WrongMethodName_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_SendAsyncWithNoArguments_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_ReceptorWithWrongTypeArguments_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_ReferencedAssemblyMessage_InfersTypeAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_OnlyDispatcherForMessage_InfersEventAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_DispatcherInTopLevelStatement_HandlesGracefullyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_ReceptorWithoutHandleAsync_DiscoversWithFallbackLineAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_PublishAsyncWithDefaultExpression_InfersFromGenericAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_NonMethodInvocation_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_SendAsyncWithStringArgument_DiscoversDispatcherAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_ReceptorWithoutHandleAsyncMethod_UsesClassLineNumberAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_MessageOnlyDispatchedNoDefinition_InfersTypeAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_PerspectiveOnly_InfersEventTypeAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_EmptyProject_GeneratesEmptyRegistryAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_SendAsyncWithZeroArguments_ReturnsNullAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_NonGenericMethodWithoutValidArguments_ReturnsNullAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_GenericMethodWithZeroTypeArguments_ReturnsNullAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_ReceptorWithMalformedInterface_ReturnsNullAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_MessageWithBothReceptorAndPerspective_MarksAsBothCommandAndEventAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_EventWithOnlyPerspectives_InfersAsEventAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_ReceptorImplementsBothInterfaces_UsesRegularNotVoidAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_MessageWithMultipleDispatchers_FormatsJsonCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_MessageWithMultipleReceptors_FormatsJsonCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_MessageWithMultiplePerspectives_FormatsJsonCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_EventWithOnlyDispatchers_InfersAsEventAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_DispatcherInFieldInitializer_HandlesNullContainingMethodAsync</tests>
/// Incremental source generator that discovers messages, dispatchers, receptors, and perspectives,
/// and generates a message-registry.json file for VSCode extension tooling.
/// </summary>
[Generator]
public class MessageRegistryGenerator : IIncrementalGenerator {
  private const string I_COMMAND = "Whizbang.Core.ICommand";
  private const string I_EVENT = "Whizbang.Core.IEvent";
  private const string I_RECEPTOR = "Whizbang.Core.IReceptor";
  private const string I_PERSPECTIVE_FOR = "Whizbang.Core.Perspectives.IPerspectiveFor";

  // Template and placeholder constants
  private const string TEMPLATE_SNIPPET_FILE = "MessageRegistrySnippets.cs";
  private const string PLACEHOLDER_MESSAGE_TYPE = "__MESSAGE_TYPE__";
  private const string PLACEHOLDER_IS_COMMAND = "__IS_COMMAND__";
  private const string PLACEHOLDER_IS_EVENT = "__IS_EVENT__";
  private const string PLACEHOLDER_FILE_PATH = "__FILE_PATH__";
  private const string PLACEHOLDER_LINE_NUMBER = "__LINE_NUMBER__";
  private const string PLACEHOLDER_DOCS_URL = "__DOCS_URL__";
  private const string PLACEHOLDER_TESTS = "__TESTS__";
  private const string PLACEHOLDER_CLASS_NAME = "__CLASS_NAME__";
  private const string PLACEHOLDER_METHOD_NAME = "__METHOD_NAME__";
  private const string PLACEHOLDER_DISPATCHERS = "__DISPATCHERS__";
  private const string PLACEHOLDER_RECEPTORS = "__RECEPTORS__";
  private const string PLACEHOLDER_PERSPECTIVES = "__PERSPECTIVES__";
  private const string PLACEHOLDER_MESSAGES = "__MESSAGES__";
  private const string PLACEHOLDER_JSON = "__JSON__";
  private const string PLACEHOLDER_TEST_FILE = "__TEST_FILE__";
  private const string PLACEHOLDER_TEST_METHOD = "__TEST_METHOD__";
  private const string PLACEHOLDER_TEST_LINE = "__TEST_LINE__";
  private const string PLACEHOLDER_TEST_CLASS = "__TEST_CLASS__";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover message types (ICommand, IEvent)
    // Where() filters nulls, Select() unwraps nullable for incremental generator caching
    var messageTypes = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is RecordDeclarationSyntax { BaseList.Types.Count: > 0 } ||
                                       node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractMessageType(ctx, ct)
    ).Where(static info => info is not null)
     .Select(static (info, _) => info!);

    // Discover dispatchers (SendAsync and PublishAsync calls)
    // Where() filters nulls, Select() unwraps nullable for incremental generator caching
    var dispatchers = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is InvocationExpressionSyntax invocation &&
                                       invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                                       (memberAccess.Name.Identifier.Text == "SendAsync" ||
                                        memberAccess.Name.Identifier.Text == "PublishAsync"),
        transform: static (ctx, ct) => _extractDispatcher(ctx, ct)
    ).Where(static info => info is not null)
     .Select(static (info, _) => info!);

    // Discover receptors (IReceptor implementations)
    // Where() filters nulls, Select() unwraps nullable for incremental generator caching
    var receptors = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractReceptor(ctx, ct)
    ).Where(static info => info is not null)
     .Select(static (info, _) => info!);

    // Discover perspectives (IPerspectiveFor<TEvent> implementations)
    // Where() filters nulls, Select() unwraps nullable for incremental generator caching
    var perspectives = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractPerspective(ctx, ct)
    ).Where(static info => info is not null)
     .Select(static (info, _) => info!);

    // Combine all discoveries and generate JSON
    var allData = messageTypes.Collect()
        .Combine(dispatchers.Collect())
        .Combine(receptors.Collect())
        .Combine(perspectives.Collect());

    context.RegisterSourceOutput(
        allData,
        static (ctx, data) => _generateMessageRegistry(ctx, data)
    );
  }

  private static MessageTypeInfo? _extractMessageType(
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

  private static DispatcherLocationInfo? _extractDispatcher(
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

  private static ReceptorLocationInfo? _extractReceptor(
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

    // Determine message type
    string messageType;

    if (receptorInterface is not null) {
      // Regular receptor with response - defensive guard
      RoslynGuards.ValidateTypeArgumentCount(receptorInterface, 2, "IReceptor<TMessage, TResponse>");
      messageType = receptorInterface.TypeArguments[0].ToDisplayString();
    } else {
      // Void receptor - defensive guard
      RoslynGuards.ValidateTypeArgumentCount(voidReceptorInterface!, 1, "IReceptor<TMessage>");
      messageType = voidReceptorInterface!.TypeArguments[0].ToDisplayString();
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

  private static PerspectiveLocationInfo? _extractPerspective(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    // Defensive guard: throws if Roslyn returns null (indicates compiler bug)
    // See RoslynGuards.cs for rationale - no branch created, eliminates coverage gap
    var classSymbol = RoslynGuards.GetClassSymbolOrThrow(classDeclaration, semanticModel, cancellationToken);

    // Look for IPerspectiveFor<TModel, TEvent1, ...> interfaces (all variants)
    // Must match the base marker interface or any event-handling variant
    var perspectiveInterfaces = classSymbol.AllInterfaces
        .Where(i => {
          var originalDef = i.OriginalDefinition.ToDisplayString();
          // Check for base marker or event-handling variants (with or without space after comma)
          return originalDef == I_PERSPECTIVE_FOR + "<TModel>" ||
                 originalDef.StartsWith(I_PERSPECTIVE_FOR + "<TModel,", StringComparison.Ordinal) ||
                 originalDef.StartsWith(I_PERSPECTIVE_FOR + "<TModel, ", StringComparison.Ordinal);
        })
        .ToList();

    if (perspectiveInterfaces.Count == 0) {
      return null;
    }

    var location = classDeclaration.GetLocation();
    var lineSpan = location.GetLineSpan();

    // A perspective can handle multiple events
    // For IPerspectiveFor<TModel, TEvent1, TEvent2, ...>, event types start at index 1
    // Index 0 is always TModel (the read model type)
    // Skip the base marker interface (has only 1 type argument - just TModel)
    var events = perspectiveInterfaces
        .Where(i => i.TypeArguments.Length > 1) // Only event-handling variants, not base marker
        .SelectMany(i => i.TypeArguments.Skip(1)) // Skip TModel, take all event types
        .Select(t => t.ToDisplayString())
        .Distinct()
        .ToArray();

    // Perspectives must handle at least one event - skip marker-only implementations
    if (events.Length == 0) {
      return null;
    }

    return new PerspectiveLocationInfo(
        ClassName: classSymbol.ToDisplayString(),
        EventTypes: events,
        FilePath: lineSpan.Path,
        LineNumber: lineSpan.StartLinePosition.Line + 1,
        DocsUrl: null,    // Enriched later in GenerateMessageRegistry
        Tests: []         // Enriched later in GenerateMessageRegistry
    );
  }

  private static void _generateMessageRegistry(
      SourceProductionContext context,
      (((ImmutableArray<MessageTypeInfo>, ImmutableArray<DispatcherLocationInfo>), ImmutableArray<ReceptorLocationInfo>), ImmutableArray<PerspectiveLocationInfo>) data) {

    var (((messages, dispatchers), receptors), perspectives) = data;

    // Load documentation and test mappings for VSCode tooling enhancement
    var docsMap = _loadCodeDocsMap(context);
    var testsMap = _loadCodeTestsMap(context);

    // Enrich all data with documentation URLs and test counts
    var enrichedMessages = messages.Select(m => _enrichMessageInfo(m, docsMap, testsMap)).ToImmutableArray();
    var enrichedDispatchers = dispatchers.Select(d => _enrichDispatcherInfo(d, docsMap, testsMap)).ToImmutableArray();
    var enrichedReceptors = receptors.Select(r => _enrichReceptorInfo(r, docsMap, testsMap)).ToImmutableArray();
    var enrichedPerspectives = perspectives.Select(p => _enrichPerspectiveInfo(p, docsMap, testsMap)).ToImmutableArray();

    // Load snippets
    var messageHeaderSnippet = TemplateUtilities.ExtractSnippet(
        typeof(MessageRegistryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "MESSAGE_ENTRY_HEADER");

    var messageFooterSnippet = TemplateUtilities.ExtractSnippet(
        typeof(MessageRegistryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "MESSAGE_ENTRY_FOOTER");

    var dispatcherSnippet = TemplateUtilities.ExtractSnippet(
        typeof(MessageRegistryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "DISPATCHER_ENTRY");

    var receptorSnippet = TemplateUtilities.ExtractSnippet(
        typeof(MessageRegistryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "RECEPTOR_ENTRY");

    var perspectiveSnippet = TemplateUtilities.ExtractSnippet(
        typeof(MessageRegistryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "PERSPECTIVE_ENTRY");

    var testSnippet = TemplateUtilities.ExtractSnippet(
        typeof(MessageRegistryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "TEST_ENTRY");

    var jsonWrapperSnippet = TemplateUtilities.ExtractSnippet(
        typeof(MessageRegistryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "JSON_ARRAY_WRAPPER");

    // Collect all message type names from all sources
    var allMessageTypes = _collectAllMessageTypes(enrichedMessages, enrichedDispatchers, enrichedReceptors, enrichedPerspectives);

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
          .Replace(PLACEHOLDER_MESSAGE_TYPE, _escapeJson(group.TypeName));

      // If message is defined in this project, use its metadata; otherwise, infer from usage
      if (msg is not null) {
        var testEntries = _buildTestEntries(msg.Tests, testSnippet);
        messageHeader = messageHeader
            .Replace(PLACEHOLDER_IS_COMMAND, msg.IsCommand.ToString().ToLowerInvariant())
            .Replace(PLACEHOLDER_IS_EVENT, msg.IsEvent.ToString().ToLowerInvariant())
            .Replace(PLACEHOLDER_FILE_PATH, _escapeJson(msg.FilePath))
            .Replace(PLACEHOLDER_LINE_NUMBER, msg.LineNumber.ToString(CultureInfo.InvariantCulture))
            .Replace(PLACEHOLDER_DOCS_URL, msg.DocsUrl ?? "")
            .Replace(PLACEHOLDER_TESTS, testEntries);
      } else {
        // Message is from a referenced assembly - infer type from handlers
        var isCommand = group.Receptors.Count > 0;
        var isEvent = group.Perspectives.Count > 0 || (group.Receptors.Count == 0 && group.Dispatchers.Count > 0);

        messageHeader = messageHeader
            .Replace(PLACEHOLDER_IS_COMMAND, isCommand.ToString().ToLowerInvariant())
            .Replace(PLACEHOLDER_IS_EVENT, isEvent.ToString().ToLowerInvariant())
            .Replace(PLACEHOLDER_FILE_PATH, "")
            .Replace(PLACEHOLDER_LINE_NUMBER, "0")
            .Replace(PLACEHOLDER_DOCS_URL, "")
            .Replace(PLACEHOLDER_TESTS, "");
      }

      // Build dispatchers, receptors, and perspectives using extracted helper methods
      var dispatcherEntries = _buildDispatchersList(group.Dispatchers, dispatcherSnippet, testSnippet);
      var receptorEntries = _buildReceptorsList(group.Receptors, receptorSnippet, testSnippet);
      var perspectiveEntries = _buildPerspectivesList(group.Perspectives, perspectiveSnippet, testSnippet);

      // Build message footer
      var messageFooter = messageFooterSnippet
          .Replace(PLACEHOLDER_DISPATCHERS, dispatcherEntries)
          .Replace(PLACEHOLDER_RECEPTORS, receptorEntries)
          .Replace(PLACEHOLDER_PERSPECTIVES, perspectiveEntries);

      // Combine header and footer
      messageEntries.Append(messageHeader);
      messageEntries.Append(messageFooter);

      if (i < messageGroups.Count - 1) {
        messageEntries.AppendLine(",");
      }
    }

    // Build final JSON
    var json = jsonWrapperSnippet.Replace(PLACEHOLDER_MESSAGES, messageEntries.ToString());

    // Generate C# wrapper with embedded JSON using snippet
    var wrapperSnippet = TemplateUtilities.ExtractSnippet(
        typeof(MessageRegistryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "CSHARP_WRAPPER");

    var csharpSource = wrapperSnippet.Replace(PLACEHOLDER_JSON, json.Replace("\"", "\"\""));

    context.AddSource("MessageRegistry.g.cs", csharpSource);
  }

  // ========================================
  // Mapping File Loaders for VSCode Tooling Enhancement
  // ========================================

  /// <summary>
  /// Loads code-docs-map.json from documentation repository.
  /// Returns mapping: symbol name → documentation URL
  /// </summary>
  private static Dictionary<string, string> _loadCodeDocsMap(SourceProductionContext context) {
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
  private static Dictionary<string, TestInfo[]> _loadCodeTestsMap(SourceProductionContext context) {
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
  private static string _extractSimpleTypeName(string fullName) {
    var lastDot = fullName.LastIndexOf('.');
    return lastDot >= 0 ? fullName.Substring(lastDot + 1) : fullName;
  }

  /// <summary>
  /// Enriches a MessageTypeInfo with documentation URL and test information.
  /// </summary>
  private static MessageTypeInfo _enrichMessageInfo(
      MessageTypeInfo info,
      Dictionary<string, string> docsMap,
      Dictionary<string, TestInfo[]> testsMap) {

    var simpleName = _extractSimpleTypeName(info.TypeName);
    var docsUrl = docsMap.TryGetValue(simpleName, out var docs) ? docs : null;
    var tests = testsMap.TryGetValue(simpleName, out var t) ? t : [];

    return info with { DocsUrl = docsUrl, Tests = tests };
  }

  /// <summary>
  /// Enriches a DispatcherLocationInfo with documentation URL and test information.
  /// </summary>
  private static DispatcherLocationInfo _enrichDispatcherInfo(
      DispatcherLocationInfo info,
      Dictionary<string, string> docsMap,
      Dictionary<string, TestInfo[]> testsMap) {

    var simpleName = _extractSimpleTypeName(info.ClassName);
    var docsUrl = docsMap.TryGetValue(simpleName, out var docs) ? docs : null;
    var tests = testsMap.TryGetValue(simpleName, out var t) ? t : [];

    return info with { DocsUrl = docsUrl, Tests = tests };
  }

  /// <summary>
  /// Enriches a ReceptorLocationInfo with documentation URL and test information.
  /// </summary>
  private static ReceptorLocationInfo _enrichReceptorInfo(
      ReceptorLocationInfo info,
      Dictionary<string, string> docsMap,
      Dictionary<string, TestInfo[]> testsMap) {

    var simpleName = _extractSimpleTypeName(info.ClassName);
    var docsUrl = docsMap.TryGetValue(simpleName, out var docs) ? docs : null;
    var tests = testsMap.TryGetValue(simpleName, out var t) ? t : [];

    return info with { DocsUrl = docsUrl, Tests = tests };
  }

  /// <summary>
  /// Enriches a PerspectiveLocationInfo with documentation URL and test information.
  /// </summary>
  private static PerspectiveLocationInfo _enrichPerspectiveInfo(
      PerspectiveLocationInfo info,
      Dictionary<string, string> docsMap,
      Dictionary<string, TestInfo[]> testsMap) {

    var simpleName = _extractSimpleTypeName(info.ClassName);
    var docsUrl = docsMap.TryGetValue(simpleName, out var docs) ? docs : null;
    var tests = testsMap.TryGetValue(simpleName, out var t) ? t : [];

    return info with { DocsUrl = docsUrl, Tests = tests };
  }

  private static string _escapeJson(string value) {
    return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
  }

  /// <summary>
  /// Builds JSON test entries from test information array.
  /// </summary>
  private static string _buildTestEntries(TestInfo[] tests, string testSnippet) {
    if (tests.Length == 0) {
      return "";
    }

    var entries = new StringBuilder();
    for (int i = 0; i < tests.Length; i++) {
      var test = tests[i];
      var entry = testSnippet
          .Replace(PLACEHOLDER_TEST_FILE, _escapeJson(test.TestFile))
          .Replace(PLACEHOLDER_TEST_METHOD, _escapeJson(test.TestMethod))
          .Replace(PLACEHOLDER_TEST_LINE, test.TestLine.ToString(CultureInfo.InvariantCulture))
          .Replace(PLACEHOLDER_TEST_CLASS, _escapeJson(test.TestClass));

      entries.Append(entry);
      if (i < tests.Length - 1) {
        entries.AppendLine(",");
      }
    }

    return entries.ToString();
  }

  // ========================================
  // Helper Methods for _generateMessageRegistry Complexity Reduction
  // ========================================

  /// <summary>
  /// Collects all unique message type names from messages, dispatchers, receptors, and perspectives.
  /// </summary>
  private static HashSet<string> _collectAllMessageTypes(
      ImmutableArray<MessageTypeInfo> messages,
      ImmutableArray<DispatcherLocationInfo> dispatchers,
      ImmutableArray<ReceptorLocationInfo> receptors,
      ImmutableArray<PerspectiveLocationInfo> perspectives) {

    var allMessageTypes = new HashSet<string>();

    foreach (var msg in messages) {
      allMessageTypes.Add(msg.TypeName);
    }

    foreach (var dispatcher in dispatchers) {
      allMessageTypes.Add(dispatcher.MessageType);
    }

    foreach (var receptor in receptors) {
      allMessageTypes.Add(receptor.MessageType);
    }

    foreach (var perspective in perspectives) {
      foreach (var eventType in perspective.EventTypes) {
        allMessageTypes.Add(eventType);
      }
    }

    return allMessageTypes;
  }

  /// <summary>
  /// Builds JSON entries for a list of dispatchers.
  /// </summary>
  private static string _buildDispatchersList(
      List<DispatcherLocationInfo> dispatchers,
      string dispatcherSnippet,
      string testSnippet) {

    var entries = new StringBuilder();
    for (int j = 0; j < dispatchers.Count; j++) {
      var d = dispatchers[j];
      var testEntries = _buildTestEntries(d.Tests, testSnippet);
      var entry = dispatcherSnippet
          .Replace(PLACEHOLDER_CLASS_NAME, _escapeJson(d.ClassName))
          .Replace(PLACEHOLDER_METHOD_NAME, _escapeJson(d.MethodName))
          .Replace(PLACEHOLDER_FILE_PATH, _escapeJson(d.FilePath))
          .Replace(PLACEHOLDER_LINE_NUMBER, d.LineNumber.ToString(CultureInfo.InvariantCulture))
          .Replace(PLACEHOLDER_DOCS_URL, d.DocsUrl ?? "")
          .Replace(PLACEHOLDER_TESTS, testEntries);

      entries.Append(entry);
      if (j < dispatchers.Count - 1) {
        entries.AppendLine(",");
      }
    }
    return entries.ToString();
  }

  /// <summary>
  /// Builds JSON entries for a list of receptors.
  /// </summary>
  private static string _buildReceptorsList(
      List<ReceptorLocationInfo> receptors,
      string receptorSnippet,
      string testSnippet) {

    var entries = new StringBuilder();
    for (int j = 0; j < receptors.Count; j++) {
      var r = receptors[j];
      var testEntries = _buildTestEntries(r.Tests, testSnippet);
      var entry = receptorSnippet
          .Replace(PLACEHOLDER_CLASS_NAME, _escapeJson(r.ClassName))
          .Replace(PLACEHOLDER_METHOD_NAME, _escapeJson(r.MethodName))
          .Replace(PLACEHOLDER_FILE_PATH, _escapeJson(r.FilePath))
          .Replace(PLACEHOLDER_LINE_NUMBER, r.LineNumber.ToString(CultureInfo.InvariantCulture))
          .Replace(PLACEHOLDER_DOCS_URL, r.DocsUrl ?? "")
          .Replace(PLACEHOLDER_TESTS, testEntries);

      entries.Append(entry);
      if (j < receptors.Count - 1) {
        entries.AppendLine(",");
      }
    }
    return entries.ToString();
  }

  /// <summary>
  /// Builds JSON entries for a list of perspectives.
  /// </summary>
  private static string _buildPerspectivesList(
      List<PerspectiveLocationInfo> perspectives,
      string perspectiveSnippet,
      string testSnippet) {

    var entries = new StringBuilder();
    for (int j = 0; j < perspectives.Count; j++) {
      var p = perspectives[j];
      var testEntries = _buildTestEntries(p.Tests, testSnippet);
      var entry = perspectiveSnippet
          .Replace(PLACEHOLDER_CLASS_NAME, _escapeJson(p.ClassName))
          .Replace(PLACEHOLDER_FILE_PATH, _escapeJson(p.FilePath))
          .Replace(PLACEHOLDER_LINE_NUMBER, p.LineNumber.ToString(CultureInfo.InvariantCulture))
          .Replace(PLACEHOLDER_DOCS_URL, p.DocsUrl ?? "")
          .Replace(PLACEHOLDER_TESTS, testEntries);

      entries.Append(entry);
      if (j < perspectives.Count - 1) {
        entries.AppendLine(",");
      }
    }
    return entries.ToString();
  }

  // Helper classes for JSON deserialization
  private sealed class CodeDocsEntry {
    public string File { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Docs { get; set; } = "";
  }

  private sealed class CodeTestsMapData {
#pragma warning disable S1144, S3459 // Used by JSON deserialization
    public Dictionary<string, TestLinkMapping[]>? CodeToTests { get; init; }
#pragma warning restore S1144, S3459
  }

  private sealed class TestLinkMapping {
    public string TestFile { get; set; } = "";
    public string TestMethod { get; set; } = "";
#pragma warning disable S1144, S3459 // Used by JSON deserialization
    public int TestLine { get; set; }
#pragma warning restore S1144, S3459
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

