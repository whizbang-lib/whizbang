using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Generators;

/// <summary>
/// Incremental source generator that discovers IPerspectiveOf implementations
/// and generates runtime routing logic for the GeneratedPerspectiveInvoker.
/// Creates type-safe routing from events to perspective implementations.
/// </summary>
[Generator]
public class PerspectiveInvokerGenerator : IIncrementalGenerator {
  private const string PERSPECTIVE_INTERFACE_NAME = "Whizbang.Core.IPerspectiveOf";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover IPerspectiveOf implementations
    var perspectiveCandidates = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => ExtractPerspectiveInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Generate perspective invoker with routing logic
    context.RegisterSourceOutput(
        perspectiveCandidates.Collect(),
        static (ctx, perspectives) => GeneratePerspectiveInvoker(ctx, perspectives!)
    );
  }

  /// <summary>
  /// Extracts perspective information from a class declaration.
  /// Returns null if the class doesn't implement IPerspectiveOf.
  /// A class can implement multiple IPerspectiveOf&lt;TEvent&gt; interfaces.
  /// </summary>
  private static PerspectiveInfo? ExtractPerspectiveInfo(
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

    // Look for all IPerspectiveOf<TEvent> interfaces this class implements
    var perspectiveInterfaces = classSymbol.AllInterfaces
        .Where(i => i.OriginalDefinition.ToDisplayString() == PERSPECTIVE_INTERFACE_NAME + "<TEvent>"
                    && i.TypeArguments.Length == 1)
        .ToList();

    if (perspectiveInterfaces.Count == 0) {
      return null;
    }

    // Extract all event types this perspective listens to
    var eventTypes = perspectiveInterfaces
        .Select(i => i.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
        .ToArray();

    return new PerspectiveInfo(
        ClassName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        EventTypes: eventTypes
    );
  }

  /// <summary>
  /// Generates the GeneratedPerspectiveInvoker class with runtime routing logic.
  /// Groups perspectives by event type for efficient lookup.
  /// </summary>
  private static void GeneratePerspectiveInvoker(
      SourceProductionContext context,
      ImmutableArray<PerspectiveInfo> perspectives) {

    if (perspectives.IsEmpty) {
      // No perspectives found - generate empty invoker
      GenerateEmptyInvoker(context);
      return;
    }

    // Report each discovered perspective for routing
    foreach (var perspective in perspectives) {
      var eventNames = string.Join(", ", perspective.EventTypes.Select(GetSimpleName));
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.PerspectiveInvokerGenerated,
          Location.None,
          GetSimpleName(perspective.ClassName),
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
    result = TemplateUtilities.ReplaceRegion(result, "PERSPECTIVE_ROUTING", routingCode.ToString());

    context.AddSource("PerspectiveInvoker.g.cs", result);
  }

  /// <summary>
  /// Generates an empty invoker when no perspectives are discovered.
  /// This ensures the build doesn't fail when IPerspectiveInvoker is injected but no perspectives exist.
  /// </summary>
  private static void GenerateEmptyInvoker(SourceProductionContext context) {
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(PerspectiveInvokerGenerator).Assembly,
        "PerspectiveInvokerTemplate.cs"
    );

    var result = template;
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(PerspectiveInvokerGenerator).Assembly, result);
    result = TemplateUtilities.ReplaceRegion(result, "PERSPECTIVE_ROUTING", "// No perspectives discovered");

    context.AddSource("PerspectiveInvoker.g.cs", result);
  }

  /// <summary>
  /// Gets the simple name from a fully qualified type name.
  /// E.g., "global::MyApp.Events.OrderCreatedEvent" -> "OrderCreatedEvent"
  /// </summary>
  private static string GetSimpleName(string fullyQualifiedName) {
    var lastDot = fullyQualifiedName.LastIndexOf('.');
    return lastDot >= 0 ? fullyQualifiedName[(lastDot + 1)..] : fullyQualifiedName;
  }
}
