using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Generators;

/// <summary>
/// Generates AOT-compatible topic registry for zero-reflection topic routing.
/// Discovers [Topic] attributes on IEvent/ICommand types and generates GetBaseTopic() method.
/// Falls back to convention-based routing for types without explicit attributes.
/// </summary>
[Generator]
public class TopicRegistryGenerator : IIncrementalGenerator {
  private const string IEVENT_INTERFACE = "Whizbang.Core.IEvent";
  private const string ICOMMAND_INTERFACE = "Whizbang.Core.ICommand";
  private const string TOPIC_ATTRIBUTE = "Whizbang.Core.Attributes.TopicAttribute";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Pipeline: Discover event/command types with [Topic] attribute or convention-based routing
    var messageTypes = context.SyntaxProvider.CreateSyntaxProvider(
        // Syntactic filtering: classes/records (cheap, filters ~99% of nodes)
        predicate: static (node, _) =>
            node is ClassDeclarationSyntax or RecordDeclarationSyntax,

        // Semantic analysis: Extract topic info (expensive, only on filtered nodes)
        transform: static (ctx, ct) => _extractTopicInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Combine with compilation to get assembly name for namespace
    var compilationAndTopics = context.CompilationProvider.Combine(messageTypes.Collect());

    // Generate registry
    context.RegisterSourceOutput(
        compilationAndTopics,
        static (ctx, data) => {
          var compilation = data.Left;
          var topics = data.Right;
          _generateTopicRegistry(ctx, compilation, topics!);
        }
    );
  }

  /// <summary>
  /// Extracts topic information from a type declaration.
  /// Returns null if type doesn't implement IEvent or ICommand.
  /// </summary>
  private static TopicInfo? _extractTopicInfo(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var typeDeclaration = (TypeDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    // Get type symbol
    var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) as INamedTypeSymbol;
    if (typeSymbol is null || typeSymbol.IsAbstract) {
      return null;  // Early exit - Roslyn returned null or abstract type
    }

    // Skip non-public types (private, internal, etc.)
    if (typeSymbol.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public) {
      return null;  // Early exit - not public
    }

    // Check if implements IEvent or ICommand
    var isEvent = typeSymbol.AllInterfaces.Any(i =>
        i.ToDisplayString() == IEVENT_INTERFACE);
    var isCommand = typeSymbol.AllInterfaces.Any(i =>
        i.ToDisplayString() == ICOMMAND_INTERFACE);

    if (!isEvent && !isCommand) {
      return null;  // Early exit - not a message type
    }

    // Get fully qualified type name
    var fullTypeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    // Check for [Topic] attribute
    var topicAttribute = typeSymbol.GetAttributes()
        .FirstOrDefault(attr => attr.AttributeClass?.ToDisplayString() == TOPIC_ATTRIBUTE);

    string? baseTopic = null;

    if (topicAttribute is not null) {
      // Extract topic name from attribute
      if (topicAttribute.ConstructorArguments.Length > 0) {
        var topicNameArg = topicAttribute.ConstructorArguments[0];
        if (topicNameArg.Value is string topicName) {
          baseTopic = topicName;
        }
      }
    } else {
      // Fall back to convention-based routing
      var typeName = typeSymbol.Name;
      if (typeName.StartsWith("Product", System.StringComparison.Ordinal)) {
        baseTopic = "products";
      } else if (typeName.StartsWith("Inventory", System.StringComparison.Ordinal)) {
        baseTopic = "inventory";
      } else if (typeName.StartsWith("Order", System.StringComparison.Ordinal)) {
        baseTopic = "orders";
      } else {
        // Default: remove "Event"/"Command" suffix and lowercase
        baseTopic = typeName
            .Replace("Event", "")
            .Replace("Command", "")
            .ToLowerInvariant();
      }
    }

    if (baseTopic is null) {
      return null;  // Couldn't determine topic
    }

    return new TopicInfo(
        TypeName: fullTypeName,
        BaseTopic: baseTopic
    );
  }

  /// <summary>
  /// Generates the TopicRegistry class and extension methods.
  /// </summary>
  private static void _generateTopicRegistry(
      SourceProductionContext context,
      Compilation compilation,
      ImmutableArray<TopicInfo> topics) {

    if (topics.IsEmpty) {
      return;  // No topics to register
    }

    var assemblyName = compilation.AssemblyName ?? "UnknownAssembly";
    var namespaceName = $"{assemblyName}.Generated";

    var source = new StringBuilder();

    // File header
    source.AppendLine("// <auto-generated/>");
    source.AppendLine($"// Generated by TopicRegistryGenerator at {System.DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
    source.AppendLine("// DO NOT EDIT - Changes will be overwritten");
    source.AppendLine("#nullable enable");
    source.AppendLine();

    // Usings
    source.AppendLine("using System;");
    source.AppendLine("using Microsoft.Extensions.DependencyInjection;");
    source.AppendLine("using Whizbang.Core.Routing;");
    source.AppendLine();
    source.AppendLine($"namespace {namespaceName};");
    source.AppendLine();

    // Registry implementation class
    source.AppendLine("/// <summary>");
    source.AppendLine($"/// Auto-generated registry for {topics.Length} message type(s).");
    source.AppendLine("/// Provides zero-reflection topic lookup for Dispatcher (AOT-compatible).");
    source.AppendLine("/// Implements ITopicRegistry for dependency injection.");
    source.AppendLine("/// </summary>");
    source.AppendLine("public sealed class TopicRegistry : ITopicRegistry {");
    source.AppendLine();

    // GetBaseTopic() method (implements ITopicRegistry)
    source.AppendLine("  /// <summary>");
    source.AppendLine("  /// Gets the base topic name for a message type (zero reflection).");
    source.AppendLine("  /// Returns null if no topic is configured for the given type.");
    source.AppendLine("  /// </summary>");
    source.AppendLine("  /// <param name=\"messageType\">The event or command type</param>");
    source.AppendLine("  /// <returns>The base topic name, or null if not configured</returns>");
    source.AppendLine("  public string? GetBaseTopic(Type messageType) {");
    source.AppendLine();

    // Generate if-else chain for type matching (can't use switch with Type)
    var orderedTopics = topics.OrderBy(t => t.TypeName).ToList();
    foreach (var topic in orderedTopics) {
      source.AppendLine($"    if (messageType == typeof({topic.TypeName})) {{");
      source.AppendLine($"      return \"{topic.BaseTopic}\";");
      source.AppendLine("    }");
      source.AppendLine();
    }

    source.AppendLine("    return null;");
    source.AppendLine("  }");
    source.AppendLine("}");
    source.AppendLine();

    // Extension class for AddTopicRegistry() (must be static for extension methods)
    source.AppendLine("/// <summary>");
    source.AppendLine("/// Extension methods for registering topic registry.");
    source.AppendLine("/// </summary>");
    source.AppendLine("public static class TopicRegistryExtensions {");
    source.AppendLine("  /// <summary>");
    source.AppendLine($"  /// Registers TopicRegistry with {topics.Length} message type(s) as a singleton.");
    source.AppendLine("  /// Call this method in your service registration (e.g., Startup.cs or Program.cs).");
    source.AppendLine("  /// </summary>");
    source.AppendLine("  public static IServiceCollection AddTopicRegistry(");
    source.AppendLine("      this IServiceCollection services) {");
    source.AppendLine();
    source.AppendLine("    // Register the registry as singleton");
    source.AppendLine("    services.AddSingleton<ITopicRegistry, TopicRegistry>();");
    source.AppendLine();
    source.AppendLine("    return services;");
    source.AppendLine("  }");
    source.AppendLine("}");

    context.AddSource("TopicRegistry.g.cs", source.ToString());
  }
}

/// <summary>
/// Value type containing information about a discovered message type and its topic.
/// Uses value equality for incremental generator caching.
/// </summary>
/// <param name="TypeName">Fully qualified type name</param>
/// <param name="BaseTopic">Base topic name (e.g., "products", "inventory")</param>
internal sealed record TopicInfo(
    string TypeName,
    string BaseTopic
);
