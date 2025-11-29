using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
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
        LineNumber: lineSpan.StartLinePosition.Line + 1
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
        LineNumber: lineSpan.StartLinePosition.Line + 1
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
        LineNumber: handleMethod?.GetLocation().GetLineSpan().StartLinePosition.Line + 1 ?? lineSpan.StartLinePosition.Line + 1
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
        LineNumber: lineSpan.StartLinePosition.Line + 1
    );
  }

  private static void GenerateMessageRegistry(
      SourceProductionContext context,
      (((ImmutableArray<MessageTypeInfo>, ImmutableArray<DispatcherLocationInfo>), ImmutableArray<ReceptorLocationInfo>), ImmutableArray<PerspectiveLocationInfo>) data) {

    var (((messages, dispatchers), receptors), perspectives) = data;

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

    var jsonWrapperSnippet = TemplateUtilities.ExtractSnippet(
        typeof(MessageRegistryGenerator).Assembly,
        "MessageRegistrySnippets.cs",
        "JSON_ARRAY_WRAPPER");

    // Collect all message type names from all sources
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

    // Group by message type - include all message types, even if not defined in this project
    var messageGroups = allMessageTypes
        .Select(typeName => {
          var msg = messages.FirstOrDefault(m => m.TypeName == typeName);
          return new {
            TypeName = typeName,
            Message = msg, // Might be null if message is from referenced assembly
            Dispatchers = dispatchers.Where(d => d.MessageType == typeName).ToList(),
            Receptors = receptors.Where(r => r.MessageType == typeName).ToList(),
            Perspectives = perspectives.Where(p => p.EventTypes.Contains(typeName)).ToList()
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
        messageHeader = messageHeader
            .Replace("__IS_COMMAND__", msg.IsCommand.ToString().ToLower())
            .Replace("__IS_EVENT__", msg.IsEvent.ToString().ToLower())
            .Replace("__FILE_PATH__", EscapeJson(msg.FilePath))
            .Replace("__LINE_NUMBER__", msg.LineNumber.ToString());
      } else {
        // Message is from a referenced assembly - infer type from handlers
        var isCommand = group.Receptors.Count > 0;
        var isEvent = group.Perspectives.Count > 0 || (group.Receptors.Count == 0 && group.Dispatchers.Count > 0);

        messageHeader = messageHeader
            .Replace("__IS_COMMAND__", isCommand.ToString().ToLower())
            .Replace("__IS_EVENT__", isEvent.ToString().ToLower())
            .Replace("__FILE_PATH__", "")
            .Replace("__LINE_NUMBER__", "0");
      }

      // Build dispatchers
      var dispatcherEntries = new StringBuilder();
      for (int j = 0; j < group.Dispatchers.Count; j++) {
        var d = group.Dispatchers[j];
        var entry = dispatcherSnippet
            .Replace("__CLASS_NAME__", EscapeJson(d.ClassName))
            .Replace("__METHOD_NAME__", EscapeJson(d.MethodName))
            .Replace("__FILE_PATH__", EscapeJson(d.FilePath))
            .Replace("__LINE_NUMBER__", d.LineNumber.ToString());

        dispatcherEntries.Append(entry);
        if (j < group.Dispatchers.Count - 1) {
          dispatcherEntries.AppendLine(",");
        }
      }

      // Build receptors
      var receptorEntries = new StringBuilder();
      for (int j = 0; j < group.Receptors.Count; j++) {
        var r = group.Receptors[j];
        var entry = receptorSnippet
            .Replace("__CLASS_NAME__", EscapeJson(r.ClassName))
            .Replace("__METHOD_NAME__", EscapeJson(r.MethodName))
            .Replace("__FILE_PATH__", EscapeJson(r.FilePath))
            .Replace("__LINE_NUMBER__", r.LineNumber.ToString());

        receptorEntries.Append(entry);
        if (j < group.Receptors.Count - 1) {
          receptorEntries.AppendLine(",");
        }
      }

      // Build perspectives
      var perspectiveEntries = new StringBuilder();
      for (int j = 0; j < group.Perspectives.Count; j++) {
        var p = group.Perspectives[j];
        var entry = perspectiveSnippet
            .Replace("__CLASS_NAME__", EscapeJson(p.ClassName))
            .Replace("__FILE_PATH__", EscapeJson(p.FilePath))
            .Replace("__LINE_NUMBER__", p.LineNumber.ToString());

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

  private static string EscapeJson(string value) {
    return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
  }
}

// Value types for captured information (registry-specific versions to avoid conflicts)
internal record MessageTypeInfo(string TypeName, bool IsCommand, bool IsEvent, string FilePath, int LineNumber);
internal record DispatcherLocationInfo(string MessageType, string ClassName, string MethodName, string FilePath, int LineNumber);
internal record ReceptorLocationInfo(string MessageType, string ClassName, string MethodName, string FilePath, int LineNumber);
internal record PerspectiveLocationInfo(string ClassName, string[] EventTypes, string FilePath, int LineNumber);
