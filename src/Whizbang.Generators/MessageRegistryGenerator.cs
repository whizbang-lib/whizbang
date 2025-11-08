using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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

    INamedTypeSymbol? typeSymbol = typeDeclaration switch {
      RecordDeclarationSyntax record => semanticModel.GetDeclaredSymbol(record, cancellationToken) as INamedTypeSymbol,
      ClassDeclarationSyntax @class => semanticModel.GetDeclaredSymbol(@class, cancellationToken) as INamedTypeSymbol,
      _ => null
    };

    if (typeSymbol is null) {
      return null;
    }

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

    // Get the symbol info for the invocation
    var symbolInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
    if (symbolInfo.Symbol is not IMethodSymbol methodSymbol) {
      return null;
    }

    // Verify it's actually SendAsync or PublishAsync
    if (methodSymbol.Name != "SendAsync" && methodSymbol.Name != "PublishAsync") {
      return null;
    }

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
    var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();

    if (containingClass is null) {
      return null;
    }

    var classSymbol = semanticModel.GetDeclaredSymbol(containingClass, cancellationToken) as INamedTypeSymbol;
    if (classSymbol is null) {
      return null;
    }

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

    var classSymbol = semanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken) as INamedTypeSymbol;
    if (classSymbol is null) {
      return null;
    }

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
      // Regular receptor with response
      if (receptorInterface.TypeArguments.Length != 2) {
        return null;
      }
      messageType = receptorInterface.TypeArguments[0].ToDisplayString();
      responseType = receptorInterface.TypeArguments[1].ToDisplayString();
    } else {
      // Void receptor
      if (voidReceptorInterface!.TypeArguments.Length != 1) {
        return null;
      }
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

    var classSymbol = semanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken) as INamedTypeSymbol;
    if (classSymbol is null) {
      return null;
    }

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

    // Build message registry JSON
    var sb = new StringBuilder();
    sb.AppendLine("{");
    sb.AppendLine("  \"messages\": [");

    // Collect all message type names from all sources
    var allMessageTypes = new HashSet<string>();

    // Add explicitly defined message types
    foreach (var msg in messages) {
      allMessageTypes.Add(msg.TypeName);
    }

    // Add message types referenced by dispatchers
    foreach (var dispatcher in dispatchers) {
      allMessageTypes.Add(dispatcher.MessageType);
    }

    // Add message types referenced by receptors
    foreach (var receptor in receptors) {
      allMessageTypes.Add(receptor.MessageType);
    }

    // Add message types referenced by perspectives
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

    for (int i = 0; i < messageGroups.Count; i++) {
      var group = messageGroups[i];
      var msg = group.Message;

      sb.AppendLine("    {");
      sb.AppendLine($"      \"type\": \"{EscapeJson(group.TypeName)}\",");

      // If message is defined in this project, use its metadata; otherwise, infer from usage
      if (msg is not null) {
        sb.AppendLine($"      \"isCommand\": {msg.IsCommand.ToString().ToLower()},");
        sb.AppendLine($"      \"isEvent\": {msg.IsEvent.ToString().ToLower()},");
        sb.AppendLine($"      \"filePath\": \"{EscapeJson(msg.FilePath)}\",");
        sb.AppendLine($"      \"lineNumber\": {msg.LineNumber},");
      } else {
        // Message is from a referenced assembly - infer type from handlers
        var isCommand = group.Receptors.Count > 0; // Commands have receptors
        var isEvent = group.Perspectives.Count > 0 || (group.Receptors.Count == 0 && group.Dispatchers.Count > 0); // Events have perspectives or are published

        sb.AppendLine($"      \"isCommand\": {isCommand.ToString().ToLower()},");
        sb.AppendLine($"      \"isEvent\": {isEvent.ToString().ToLower()},");
        sb.AppendLine($"      \"filePath\": \"\","); // No file path for referenced types
        sb.AppendLine($"      \"lineNumber\": 0,"); // No line number for referenced types
      }

      // Dispatchers
      sb.AppendLine("      \"dispatchers\": [");
      for (int j = 0; j < group.Dispatchers.Count; j++) {
        var d = group.Dispatchers[j];
        sb.AppendLine("        {");
        sb.AppendLine($"          \"class\": \"{EscapeJson(d.ClassName)}\",");
        sb.AppendLine($"          \"method\": \"{EscapeJson(d.MethodName)}\",");
        sb.AppendLine($"          \"filePath\": \"{EscapeJson(d.FilePath)}\",");
        sb.Append($"          \"lineNumber\": {d.LineNumber}");
        sb.AppendLine();
        sb.Append(j < group.Dispatchers.Count - 1 ? "        }," : "        }");
        sb.AppendLine();
      }
      sb.AppendLine("      ],");

      // Receptors
      sb.AppendLine("      \"receptors\": [");
      for (int j = 0; j < group.Receptors.Count; j++) {
        var r = group.Receptors[j];
        sb.AppendLine("        {");
        sb.AppendLine($"          \"class\": \"{EscapeJson(r.ClassName)}\",");
        sb.AppendLine($"          \"method\": \"{EscapeJson(r.MethodName)}\",");
        sb.AppendLine($"          \"filePath\": \"{EscapeJson(r.FilePath)}\",");
        sb.Append($"          \"lineNumber\": {r.LineNumber}");
        sb.AppendLine();
        sb.Append(j < group.Receptors.Count - 1 ? "        }," : "        }");
        sb.AppendLine();
      }
      sb.AppendLine("      ],");

      // Perspectives
      sb.AppendLine("      \"perspectives\": [");
      for (int j = 0; j < group.Perspectives.Count; j++) {
        var p = group.Perspectives[j];
        sb.AppendLine("        {");
        sb.AppendLine($"          \"class\": \"{EscapeJson(p.ClassName)}\",");
        sb.AppendLine($"          \"method\": \"Update\",");
        sb.AppendLine($"          \"filePath\": \"{EscapeJson(p.FilePath)}\",");
        sb.Append($"          \"lineNumber\": {p.LineNumber}");
        sb.AppendLine();
        sb.Append(j < group.Perspectives.Count - 1 ? "        }," : "        }");
        sb.AppendLine();
      }
      sb.AppendLine("      ]");

      sb.Append(i < messageGroups.Count - 1 ? "    }," : "    }");
      sb.AppendLine();
    }

    sb.AppendLine("  ]");
    sb.AppendLine("}");

    // Generate as C# file with JSON content as a string constant
    // This allows MSBuild to extract and write it as a JSON file
    var csharpSource = $$"""
// <auto-generated/>
#nullable enable

namespace Whizbang.Generated {
  internal static class MessageRegistry {
    internal const string Json = @"{{sb.ToString().Replace("\"", "\"\"")}}";
  }
}
""";

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
