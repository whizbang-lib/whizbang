using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Generates AOT-compatible event namespace registry for zero-reflection event subscription discovery.
/// Discovers event namespaces from:
/// - IPerspectiveFor&lt;TModel, TEvent1, ...&gt; implementations (event types projected by perspectives)
/// - IReceptor&lt;TEvent&gt; implementations where TEvent : IEvent (events handled by receptors)
/// </summary>
/// <remarks>
/// <para>
/// At transport startup, services use the generated EventNamespaceRegistry to auto-discover
/// which event topics to subscribe to, based on registered perspectives and receptors.
/// </para>
/// <para>
/// Combined with EventSubscriptionDiscovery service, this provides automatic event subscription
/// without requiring manual SubscribeTo() configuration in RoutingOptions.
/// </para>
/// </remarks>
/// <docs>core-concepts/routing#event-namespace-registry</docs>
[Generator]
public class EventNamespaceRegistryGenerator : IIncrementalGenerator {
  private const string IEVENT_INTERFACE = "global::Whizbang.Core.IEvent";
  private const string IRECEPTOR_INTERFACE_NAME = "global::Whizbang.Core.IReceptor";
  private const string PERSPECTIVE_INTERFACE_NAME = "global::Whizbang.Core.Perspectives.IPerspectiveFor";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Pipeline 1: Discover event namespaces from IPerspectiveFor implementations
    var perspectiveNamespaces = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractPerspectiveEventNamespaces(ctx, ct)
    ).Where(static info => info.HasValue)
     .SelectMany(static (namespaces, _) => namespaces!.Value);

    // Pipeline 2: Discover event namespaces from IReceptor<TEvent> implementations
    var receptorNamespaces = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractReceptorEventNamespace(ctx, ct)
    ).Where(static ns => ns is not null);

    // Combine both pipelines with compilation
    var allData = perspectiveNamespaces.Collect()
        .Combine(receptorNamespaces.Collect())
        .Combine(context.CompilationProvider);

    // Generate registry
    context.RegisterSourceOutput(
        allData,
        static (ctx, data) => {
          var perspectiveNs = data.Left.Left;
          var receptorNs = data.Left.Right;
          var compilation = data.Right;
          _generateEventNamespaceRegistry(ctx, compilation, perspectiveNs, receptorNs!);
        }
    );
  }

  /// <summary>
  /// Extracts event namespaces from a perspective class that implements IPerspectiveFor.
  /// </summary>
  private static ImmutableArray<string>? _extractPerspectiveEventNamespaces(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    var classSymbol = semanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken) as INamedTypeSymbol;
    if (classSymbol is null) {
      return null;
    }

    // Look for IPerspectiveFor<TModel, TEvent1, TEvent2, ...> interface
    // Use FullyQualifiedFormat to include global:: prefix which matches our constant
    var perspectiveInterface = classSymbol.AllInterfaces.FirstOrDefault(i =>
        i.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).StartsWith(PERSPECTIVE_INTERFACE_NAME + "<", StringComparison.Ordinal));

    if (perspectiveInterface is null) {
      return null;
    }

    // Extract event types from type arguments (skip first which is TModel)
    var eventNamespaces = new List<string>();
    var typeArguments = perspectiveInterface.TypeArguments;

    // First type argument is TModel, rest are event types
    for (var i = 1; i < typeArguments.Length; i++) {
      var eventType = typeArguments[i];

      // Verify it's an IEvent implementation
      // Use FullyQualifiedFormat to include global:: prefix which matches our constant
      if (!eventType.AllInterfaces.Any(iface => iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == IEVENT_INTERFACE)) {
        continue;
      }

      // Get the namespace
      var ns = eventType.ContainingNamespace?.ToDisplayString();
      if (!string.IsNullOrEmpty(ns)) {
        eventNamespaces.Add(ns!.ToLowerInvariant());
      }
    }

    if (eventNamespaces.Count == 0) {
      return null;
    }

    return eventNamespaces.Distinct().ToImmutableArray();
  }

  /// <summary>
  /// Extracts event namespace from a receptor class that implements IReceptor&lt;TEvent&gt;.
  /// Only extracts if TEvent implements IEvent.
  /// </summary>
  private static string? _extractReceptorEventNamespace(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    var classSymbol = semanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken) as INamedTypeSymbol;
    if (classSymbol is null) {
      return null;
    }

    // Skip generic open types
    if (classSymbol.IsGenericType && classSymbol.TypeParameters.Length > 0) {
      return null;
    }

    // Look for IReceptor<TMessage> or IReceptor<TMessage, TResponse> interface
    // Use FullyQualifiedFormat to include global:: prefix which matches our constant
    var receptorInterface = classSymbol.AllInterfaces.FirstOrDefault(i =>
        i.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).StartsWith(IRECEPTOR_INTERFACE_NAME + "<", StringComparison.Ordinal));

    if (receptorInterface is null) {
      return null;
    }

    // Get the message type (first type argument)
    var messageType = receptorInterface.TypeArguments[0];

    // Check if it's an IEvent implementation
    // CRITICAL: Use TypeNameHelper.GetFullyQualifiedName to match the global:: prefixed constant
    var isEvent = TypeNameHelper.ImplementsInterface(messageType, StandardInterfaceNames.I_EVENT);
    if (!isEvent) {
      return null;  // Not an event receptor
    }

    // Get the namespace
    var containingNamespace = messageType.ContainingNamespace;
    if (containingNamespace is null || containingNamespace.IsGlobalNamespace) {
      return null;
    }

    var ns = containingNamespace.ToDisplayString();
    return ns.ToLowerInvariant();
  }

  /// <summary>
  /// Generates the EventNamespaceSource class implementing IEventNamespaceSource
  /// and a ModuleInitializer that registers it with EventNamespaceRegistry.
  /// </summary>
  private static void _generateEventNamespaceRegistry(
      SourceProductionContext context,
      Compilation compilation,
      ImmutableArray<string> perspectiveNamespaces,
      ImmutableArray<string?> receptorNamespaces) {

    var assemblyName = compilation.AssemblyName ?? "UnknownAssembly";
    var namespaceName = $"{assemblyName}.Generated";

    // Filter out nulls and deduplicate namespaces (case-insensitive)
    var uniquePerspectiveNs = perspectiveNamespaces
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(ns => ns, StringComparer.Ordinal)
        .ToList();

    var uniqueReceptorNs = receptorNamespaces
        .Where(ns => ns is not null)
        .Select(ns => ns!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(ns => ns, StringComparer.Ordinal)
        .ToList();

    var allNamespaces = uniquePerspectiveNs
        .Union(uniqueReceptorNs, StringComparer.OrdinalIgnoreCase)
        .OrderBy(ns => ns, StringComparer.Ordinal)
        .ToList();

    var source = new StringBuilder();

    // File header
    source.AppendLine("// <auto-generated/>");
    source.AppendLine($"// Generated by EventNamespaceRegistryGenerator at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
    source.AppendLine("// DO NOT EDIT - Changes will be overwritten");
    source.AppendLine("#nullable enable");
    source.AppendLine();

    // Usings
    source.AppendLine("using System;");
    source.AppendLine("using System.Collections.Generic;");
    source.AppendLine("using System.Runtime.CompilerServices;");
    source.AppendLine("using Whizbang.Core.Routing;");
    source.AppendLine();

    source.AppendLine($"namespace {namespaceName};");
    source.AppendLine();

    // Module Initializer class
    source.AppendLine("/// <summary>");
    source.AppendLine("/// Auto-registration coordinator for event namespace discovery.");
    source.AppendLine("/// Uses [ModuleInitializer] to register EventNamespaceSource with the global EventNamespaceRegistry.");
    source.AppendLine("/// Runs before Main() - no explicit registration needed.");
    source.AppendLine("/// </summary>");
    source.AppendLine("internal static class EventNamespaceSourceInitializer {");
    source.AppendLine("  /// <summary>");
    source.AppendLine("  /// Registers this assembly's EventNamespaceSource with the global registry.");
    source.AppendLine("  /// </summary>");
    source.AppendLine("  // CA2255: Intentional use of ModuleInitializer for AOT-compatible event namespace registration");
    source.AppendLine("#pragma warning disable CA2255");
    source.AppendLine("  [ModuleInitializer]");
    source.AppendLine("#pragma warning restore CA2255");
    source.AppendLine("  public static void Initialize() {");
    source.AppendLine("    EventNamespaceRegistry.Register(EventNamespaceSource.Instance);");
    source.AppendLine("  }");
    source.AppendLine("}");
    source.AppendLine();

    // Source implementation class
    source.AppendLine("/// <summary>");
    source.AppendLine("/// Auto-generated source for event namespace discovery (AOT-compatible).");
    source.AppendLine($"/// Discovered {uniquePerspectiveNs.Count} perspective namespace(s) and {uniqueReceptorNs.Count} receptor namespace(s).");
    source.AppendLine("/// Implements IEventNamespaceSource for static EventNamespaceRegistry.");
    source.AppendLine("/// </summary>");
    source.AppendLine("internal sealed class EventNamespaceSource : IEventNamespaceSource {");
    source.AppendLine();

    // Singleton instance
    source.AppendLine("  /// <summary>Singleton instance for registration.</summary>");
    source.AppendLine("  public static readonly EventNamespaceSource Instance = new();");
    source.AppendLine();

    // Private constructor
    source.AppendLine("  private EventNamespaceSource() { }");
    source.AppendLine();

    // Static fields for the namespace sets
    source.AppendLine("  private static readonly HashSet<string> _perspectiveNamespaces = new(StringComparer.OrdinalIgnoreCase) {");
    foreach (var ns in uniquePerspectiveNs) {
      source.AppendLine($"    \"{ns}\",");
    }
    source.AppendLine("  };");
    source.AppendLine();

    source.AppendLine("  private static readonly HashSet<string> _receptorNamespaces = new(StringComparer.OrdinalIgnoreCase) {");
    foreach (var ns in uniqueReceptorNs) {
      source.AppendLine($"    \"{ns}\",");
    }
    source.AppendLine("  };");
    source.AppendLine();

    source.AppendLine("  private static readonly HashSet<string> _allNamespaces = new(StringComparer.OrdinalIgnoreCase) {");
    foreach (var ns in allNamespaces) {
      source.AppendLine($"    \"{ns}\",");
    }
    source.AppendLine("  };");
    source.AppendLine();

    // GetPerspectiveEventNamespaces()
    source.AppendLine("  /// <inheritdoc />");
    source.AppendLine("  public IReadOnlySet<string> GetPerspectiveEventNamespaces() => _perspectiveNamespaces;");
    source.AppendLine();

    // GetReceptorEventNamespaces()
    source.AppendLine("  /// <inheritdoc />");
    source.AppendLine("  public IReadOnlySet<string> GetReceptorEventNamespaces() => _receptorNamespaces;");
    source.AppendLine();

    // GetAllEventNamespaces()
    source.AppendLine("  /// <inheritdoc />");
    source.AppendLine("  public IReadOnlySet<string> GetAllEventNamespaces() => _allNamespaces;");
    source.AppendLine("}");

    context.AddSource("EventNamespaceSource.g.cs", source.ToString());
  }
}
