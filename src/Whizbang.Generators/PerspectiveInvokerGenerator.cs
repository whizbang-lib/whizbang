using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Incremental source generator that discovers IPerspectiveFor implementations
/// and generates runtime routing logic for the GeneratedPerspectiveInvoker.
/// Creates type-safe routing from events to perspective implementations.
/// </summary>
/// <tests>No tests found</tests>
[Generator]
public class PerspectiveInvokerGenerator : IIncrementalGenerator {
  private const string PERSPECTIVE_INTERFACE_NAME = "Whizbang.Core.Perspectives.IPerspectiveFor";

  /// <summary>
  /// Initializes the incremental generator by discovering perspective implementations and registering source generation.
  /// </summary>
  /// <tests>No tests found</tests>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover IPerspectiveFor implementations
    var perspectiveCandidates = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractPerspectiveInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Generate perspective invoker with routing logic
    // Combine compilation with discovered perspectives to get assembly name for namespace
    var compilationAndPerspectives = context.CompilationProvider.Combine(perspectiveCandidates.Collect());

    context.RegisterSourceOutput(
        compilationAndPerspectives,
        static (ctx, data) => {
          var compilation = data.Left;
          var perspectives = data.Right;
          _generatePerspectiveInvoker(ctx, compilation, perspectives!);
        }
    );
  }

  /// <summary>
  /// Extracts perspective information from a class declaration.
  /// Returns null if the class doesn't implement IPerspectiveFor.
  /// A class can implement multiple IPerspectiveFor&lt;TModel, TEvent&gt; interfaces.
  /// </summary>
  /// <tests>No tests found</tests>
  private static PerspectiveInfo? _extractPerspectiveInfo(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    // Defensive guard: throws if Roslyn returns null (indicates compiler bug)
    // See RoslynGuards.cs for rationale - no branch created, eliminates coverage gap
    var classSymbol = RoslynGuards.GetClassSymbolOrThrow(classDeclaration, semanticModel, cancellationToken);

    // Skip abstract classes - they can't be instantiated
    if (classSymbol.IsAbstract) {
      return null;
    }

    // Look for all IPerspectiveFor<TModel, TEvent1, ...> interfaces (all variants)
    // Check if interface name contains "IPerspectiveFor" (case-sensitive)
    var perspectiveInterfaces = classSymbol.AllInterfaces
        .Where(i => {
          var originalDef = i.OriginalDefinition.ToDisplayString();
          // Simple contains check to match any perspective interface
          return originalDef.Contains("IPerspectiveFor");
        })
        .ToList();

    if (perspectiveInterfaces.Count == 0) {
      return null;
    }

    // Get the actual IPerspectiveFor<TModel, TEvent...> interface (not the marker base)
    // Skip the base marker interface (has only 1 type argument - just TModel)
    var perspectiveInterface = perspectiveInterfaces.FirstOrDefault(i => i.TypeArguments.Length > 1);

    if (perspectiveInterface is null) {
      // Only implements marker interface - skip
      return null;
    }

    // Extract all type arguments: [TModel, TEvent1, TEvent2, ...]
    // Use FullyQualifiedFormat for CODE GENERATION (includes global:: prefix)
    var typeArguments = perspectiveInterface.TypeArguments
        .Select(t => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
        .ToArray();

    // Extract event types (all except TModel at index 0) for diagnostics
    var eventTypes = typeArguments.Skip(1).ToArray();

    // Calculate DATABASE FORMAT (TypeName, AssemblyName - no global:: prefix)
    // This generator doesn't use database registration, but we need to provide the parameter
    var eventTypeSymbols = perspectiveInterface.TypeArguments.Skip(1).ToArray();
    var messageTypeNames = eventTypeSymbols
        .Select(t => {
          var typeName = t.ToDisplayString(new SymbolDisplayFormat(
              typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
              genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
          ));
          var assemblyName = t.ContainingAssembly.Name;
          return $"{typeName}, {assemblyName}";
        })
        .ToArray();

    return new PerspectiveInfo(
        ClassName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        InterfaceTypeArguments: typeArguments,
        EventTypes: eventTypes,
        MessageTypeNames: messageTypeNames
    );
  }

  /// <summary>
  /// Generates the GeneratedPerspectiveInvoker class with runtime routing logic.
  /// Groups perspectives by event type for efficient lookup.
  /// Uses assembly-specific namespace to avoid conflicts when multiple assemblies use Whizbang.
  /// </summary>
  /// <tests>No tests found</tests>
  private static void _generatePerspectiveInvoker(
      SourceProductionContext context,
      Compilation compilation,
      ImmutableArray<PerspectiveInfo> perspectives) {

    if (perspectives.IsEmpty) {
      // No perspectives found - generate empty invoker
      _generateEmptyInvoker(context, compilation);
      return;
    }

    // Determine namespace from assembly name
    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";

    // Report each discovered perspective for routing
    foreach (var perspective in perspectives) {
      var eventNames = string.Join(", ", perspective.EventTypes.Select(_getSimpleName));
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.PerspectiveInvokerGenerated,
          Location.None,
          _getSimpleName(perspective.ClassName),
          eventNames
      ));
    }

    // Flatten perspectives: one entry per (ClassName, EventType) pair
    var flatPerspectives = perspectives
        .SelectMany(p => p.EventTypes.Select(et => (p.ClassName, EventType: et)))
        .ToList();

    // Group by event type for routing generation
    var perspectivesByEvent = flatPerspectives
        .GroupBy(p => p.EventType)
        .OrderBy(g => g.Key);

    // Load template
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(PerspectiveInvokerGenerator).Assembly,
        "PerspectiveInvokerTemplate.cs"
    );

    // Load routing snippet
    var routingSnippet = TemplateUtilities.ExtractSnippet(
        typeof(PerspectiveInvokerGenerator).Assembly,
        "PerspectiveInvokerSnippets.cs",
        "PERSPECTIVE_ROUTING_SNIPPET"
    );

    // Generate routing code
    var routingCode = new StringBuilder();

    foreach (var group in perspectivesByEvent) {
      var eventType = group.Key;
      var generatedCode = routingSnippet
          .Replace("__EVENT_TYPE__", eventType)
          .Replace("__PERSPECTIVE_INTERFACE__", PERSPECTIVE_INTERFACE_NAME);

      routingCode.AppendLine(TemplateUtilities.IndentCode(generatedCode, "    "));
    }

    // Replace template regions
    var result = template;
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(PerspectiveInvokerGenerator).Assembly, result);
    result = TemplateUtilities.ReplaceRegion(result, "NAMESPACE", $"namespace {namespaceName};");
    result = TemplateUtilities.ReplaceRegion(result, "PERSPECTIVE_ROUTING", routingCode.ToString());

    context.AddSource("PerspectiveInvoker.g.cs", result);
  }

  /// <summary>
  /// Generates an empty invoker when no perspectives are discovered.
  /// This ensures the build doesn't fail when IPerspectiveInvoker is injected but no perspectives exist.
  /// Uses assembly-specific namespace to avoid conflicts when multiple assemblies use Whizbang.
  /// </summary>
  /// <tests>No tests found</tests>
  private static void _generateEmptyInvoker(SourceProductionContext context, Compilation compilation) {
    // Determine namespace from assembly name
    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";

    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(PerspectiveInvokerGenerator).Assembly,
        "PerspectiveInvokerTemplate.cs"
    );

    var result = template;
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(PerspectiveInvokerGenerator).Assembly, result);
    result = TemplateUtilities.ReplaceRegion(result, "NAMESPACE", $"namespace {namespaceName};");
    result = TemplateUtilities.ReplaceRegion(result, "PERSPECTIVE_ROUTING", "// No perspectives discovered");

    context.AddSource("PerspectiveInvoker.g.cs", result);
  }

  /// <summary>
  /// Gets the simple name from a fully qualified type name.
  /// E.g., "global::MyApp.Events.OrderCreatedEvent" -> "OrderCreatedEvent"
  /// </summary>
  /// <tests>No tests found</tests>
  private static string _getSimpleName(string fullyQualifiedName) {
    var lastDot = fullyQualifiedName.LastIndexOf('.');
    return lastDot >= 0 ? fullyQualifiedName[(lastDot + 1)..] : fullyQualifiedName;
  }
}
