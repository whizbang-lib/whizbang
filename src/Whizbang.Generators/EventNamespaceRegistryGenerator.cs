// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;
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
/// <docs>fundamentals/dispatcher/routing#event-namespace-registry</docs>
[Generator]
public class EventNamespaceRegistryGenerator : IIncrementalGenerator {
  private const string IEVENT_INTERFACE = "global::Whizbang.Core.IEvent";
  private const string IRECEPTOR_INTERFACE_NAME = "global::Whizbang.Core.IReceptor";
  private const string ISYNCRECEPTOR_INTERFACE_NAME = "global::Whizbang.Core.ISyncReceptor";
  private const string ICOLLECTIVE_EVENT_INTERFACE = "global::Whizbang.Core.Messaging.ICollectiveEvent";
  private const string NOTIFICATION_TAG_ATTRIBUTE = "Whizbang.Core.NotificationTagAttribute";
  private const string NOTIFICATION_ID_TAG_ATTRIBUTE = "Whizbang.Core.NotificationIdTagAttribute";

  /// <summary>Syntax predicate for notification-tag consumers: an attributed class or record. Shares the receptor
  /// registry generator's test, so the two discover the same candidates.</summary>
  /// <param name="node">The syntax node.</param>
  /// <returns><see langword="true"/> for an attributed class or record.</returns>
  /// <tests>tests/Whizbang.Generators.Component.Tests/BuildCost/SyntaxCandidateGuardTests.cs</tests>
  public static bool IsAttributedClassOrRecordCandidate(SyntaxNode node) =>
    ReceptorRegistryQueryGenerator.IsAttributedClassOrRecordCandidate(node);

  /// <summary>Syntax predicate for sync receptors and collective events: a class or record with a base list. Shares the
  /// receptor registry generator's test, so the two discover the same candidates.</summary>
  /// <param name="node">The syntax node.</param>
  /// <returns><see langword="true"/> for a class or record with a base list.</returns>
  /// <tests>tests/Whizbang.Generators.Component.Tests/BuildCost/SyntaxCandidateGuardTests.cs</tests>
  public static bool IsClassOrRecordWithBaseListCandidate(SyntaxNode node) =>
    ReceptorRegistryQueryGenerator.IsClassOrRecordWithBaseListCandidate(node);

  /// <summary>
  /// Syntax predicate: admits a class with a base list, so it can implement the interfaces the transform looks for.
  /// </summary>
  /// <remarks>Public so the build-cost guard can run it over ordinary code (#1650).</remarks>
  /// <tests>tests/Whizbang.Generators.Component.Tests/BuildCost/SyntaxCandidateGuardTests.cs</tests>
  public static bool IsClassWithBaseListCandidate(SyntaxNode node) =>
      node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 };

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Pipeline 1: Discover event namespaces from IPerspectiveFor implementations
    var perspectiveNamespaces = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => IsClassWithBaseListCandidate(node),
        transform: static (ctx, ct) => _extractPerspectiveEventNamespaces(ctx, ct)
    ).Where(static info => info.HasValue)
     .SelectMany(static (namespaces, _) => namespaces!.Value);

    // Pipeline 2: Discover event namespaces from IReceptor<TEvent> implementations
    var receptorNamespaces = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => IsClassWithBaseListCandidate(node),
        transform: static (ctx, ct) => _extractReceptorEventNamespace(ctx, ct)
    ).Where(static ns => ns is not null);

    // Pipeline 3 (#2065): events a notification tag or the collective sink consumes. They have no receptor or
    // perspective of their own, and an event consumed in a namespace with no subscription is never delivered.
    var taggedNamespaces = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => IsAttributedClassOrRecordCandidate(node),
        transform: static (ctx, ct) => _extractConsumedEventNamespace(ctx, collective: false, ct)
    ).Where(static ns => ns is not null);
    var collectiveNamespaces = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => IsClassOrRecordWithBaseListCandidate(node),
        transform: static (ctx, ct) => _extractConsumedEventNamespace(ctx, collective: true, ct)
    ).Where(static ns => ns is not null);

    // Combine the pipelines with the assembly name, the one fact read from the compilation. Combining the
    // compilation itself would make every edit regenerate the registry, since every edit is a new compilation.
    var allData = perspectiveNamespaces.Collect()
        .Combine(receptorNamespaces.Collect())
        .Combine(taggedNamespaces.Collect())
        .Combine(collectiveNamespaces.Collect())
        .Combine(context.CompilationProvider.Select(static (compilation, _) => compilation.AssemblyName));

    // Generate registry
    context.RegisterSourceOutput(
        allData,
        static (ctx, data) => {
          var perspectiveNs = data.Left.Left.Left.Left;
          // Tag and collective consumers are receptor-like: something here other than a perspective consumes the event.
          var receptorNs = data.Left.Left.Left.Right.AddRange(data.Left.Left.Right).AddRange(data.Left.Right);
          var assemblyName = data.Right;
          _generateEventNamespaceRegistry(ctx, assemblyName, perspectiveNs, receptorNs!);
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

    // Use shared discovery helper for consistent perspective interface scanning. The bind guard
    // shares the "no events" exit: a declaration Roslyn bound no symbol for applies no events either.
    if (semanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken) is not INamedTypeSymbol classSymbol
        || Utilities.PerspectiveDiscoveryHelper.ExtractEventTypes(classSymbol) is not { Count: > 0 } eventTypes) {
      return null;
    }

    // Extract event namespaces
    var eventNamespaces = new List<string>();

    foreach (var eventType in eventTypes) {

      // Verify it's an IEvent implementation
      // Use FullyQualifiedFormat to include global:: prefix which matches our constant
      if (!eventType.AllInterfaces.Any(iface => TypeNameUtilities.FullyQualified(iface) == IEVENT_INTERFACE)) {
        continue;
      }

      // An event in the global namespace has none to register, as on the receptor path (#1177): displayed, the
      // global namespace reads "<global namespace>", which is not a namespace.
      if (!TypeNameUtilities.NamespaceOf(eventType).IsGlobalNamespace) {
        eventNamespaces.Add(TypeNameUtilities.Display(TypeNameUtilities.NamespaceOf(eventType)).ToLowerInvariant());
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

    // Skip generic open types. The bind guard shares that exit: a declaration Roslyn bound no symbol
    // for names no closed receptor type the registry can list.
    if (semanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken) is not INamedTypeSymbol classSymbol
        || (classSymbol.IsGenericType && classSymbol.TypeParameters.Length > 0)) {
      return null;
    }

    // Look for IReceptor<TMessage> or IReceptor<TMessage, TResponse> interface
    // Use FullyQualifiedFormat to include global:: prefix which matches our constant
    // A synchronous receptor consumes its event the same way (#2065).
    var receptorInterface = classSymbol.AllInterfaces.FirstOrDefault(i => {
      var name = TypeNameUtilities.FullyQualified(i.OriginalDefinition);
      return name.StartsWith(IRECEPTOR_INTERFACE_NAME + "<", StringComparison.Ordinal)
          || name.StartsWith(ISYNCRECEPTOR_INTERFACE_NAME + "<", StringComparison.Ordinal);
    });

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
    // A type that implements IEvent is a named type or a type parameter, and both sit in a namespace.
    var containingNamespace = TypeNameUtilities.NamespaceOf(messageType);
    if (containingNamespace.IsGlobalNamespace) {
      return null;
    }

    var ns = TypeNameUtilities.Display(containingNamespace);
    return ns.ToLowerInvariant();
  }

  /// <summary>
  /// The namespace of a concrete event a notification tag (<paramref name="collective"/> false) or the collective sink
  /// (true) consumes, lowercase; null for any other type (#2065).
  /// </summary>
  private static string? _extractConsumedEventNamespace(
      GeneratorSyntaxContext context, bool collective, System.Threading.CancellationToken cancellationToken) {
    if (context.SemanticModel.GetDeclaredSymbol(context.Node, cancellationToken) is not INamedTypeSymbol symbol
        || symbol.IsAbstract
        || TypeNameUtilities.NamespaceOf(symbol).IsGlobalNamespace
        || !symbol.AllInterfaces.Any(i => TypeNameUtilities.FullyQualified(i) == IEVENT_INTERFACE)) {
      return null;
    }
    var consumed = collective
      ? symbol.AllInterfaces.Any(i => TypeNameUtilities.FullyQualified(i) == ICOLLECTIVE_EVENT_INTERFACE)
      : symbol.GetAttributes().Any(a =>
          TypeNameUtilities.IsNamed(a.AttributeClass, NOTIFICATION_TAG_ATTRIBUTE)
          || TypeNameUtilities.IsNamed(a.AttributeClass, NOTIFICATION_ID_TAG_ATTRIBUTE));
    return consumed ? TypeNameUtilities.Display(TypeNameUtilities.NamespaceOf(symbol)).ToLowerInvariant() : null;
  }

  /// <summary>
  /// Generates the EventNamespaceSource class implementing IEventNamespaceSource
  /// and a ModuleInitializer that registers it with EventNamespaceRegistry.
  /// </summary>
  private static void _generateEventNamespaceRegistry(
      SourceProductionContext context,
      string? compilationAssemblyName,
      ImmutableArray<string> perspectiveNamespaces,
      ImmutableArray<string?> receptorNamespaces) {

    var assemblyName = compilationAssemblyName ?? "UnknownAssembly";
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
    source.AppendLine($"// Generated by EventNamespaceRegistryGenerator at {Whizbang.Generators.Shared.Utilities.TemplateUtilities.DeterministicBuildStamp}");
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
