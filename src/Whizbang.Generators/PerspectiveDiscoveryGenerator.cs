using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Incremental source generator that discovers IPerspectiveOf implementations
/// and generates DI registration code.
/// Perspectives are registered as Scoped services and updated via Event Store.
/// </summary>
[Generator]
public class PerspectiveDiscoveryGenerator : IIncrementalGenerator {
  private const string PERSPECTIVE_INTERFACE_NAME = "Whizbang.Core.IPerspectiveOf";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Filter for classes that have a base list (potential interface implementations)
    var perspectiveCandidates = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => ExtractPerspectiveInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Collect all perspectives and generate registration code
    context.RegisterSourceOutput(
        perspectiveCandidates.Collect(),
        static (ctx, perspectives) => GeneratePerspectiveRegistrations(ctx, perspectives!)
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
  /// Generates the perspective registration code for all discovered perspectives.
  /// Creates an AddWhizbangPerspectives extension method that registers all perspectives as Scoped services.
  /// </summary>
  private static void GeneratePerspectiveRegistrations(
      SourceProductionContext context,
      ImmutableArray<PerspectiveInfo> perspectives) {

    if (perspectives.IsEmpty) {
      // No perspectives found - skip code generation (WHIZ002 already handles this in ReceptorDiscoveryGenerator)
      return;
    }

    // Report each discovered perspective
    foreach (var perspective in perspectives) {
      var eventNames = string.Join(", ", perspective.EventTypes.Select(GetSimpleName));
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.PerspectiveDiscovered,
          Location.None,
          GetSimpleName(perspective.ClassName),
          eventNames
      ));
    }

    var registrationSource = GenerateRegistrationSource(perspectives);
    context.AddSource("PerspectiveRegistrations.g.cs", registrationSource);
  }

  /// <summary>
  /// Generates the C# source code for the registration extension method.
  /// Uses template-based generation for IDE support.
  /// Handles perspectives that implement multiple IPerspectiveOf&lt;TEvent&gt; interfaces.
  /// </summary>
  private static string GenerateRegistrationSource(ImmutableArray<PerspectiveInfo> perspectives) {
    // Load template from embedded resource
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(PerspectiveDiscoveryGenerator).Assembly,
        "PerspectiveRegistrationsTemplate.cs"
    );

    // Load registration snippet
    var registrationSnippet = TemplateUtilities.ExtractSnippet(
        typeof(PerspectiveDiscoveryGenerator).Assembly,
        "PerspectiveSnippets.cs",
        "PERSPECTIVE_REGISTRATION_SNIPPET"
    );

    // Generate registration calls for each perspective/event combination
    var registrations = new StringBuilder();
    int totalRegistrations = 0;

    foreach (var perspective in perspectives) {
      foreach (var eventType in perspective.EventTypes) {
        var generatedCode = registrationSnippet
            .Replace("__PERSPECTIVE_INTERFACE__", PERSPECTIVE_INTERFACE_NAME)
            .Replace("__EVENT_TYPE__", eventType)
            .Replace("__PERSPECTIVE_CLASS__", perspective.ClassName);

        registrations.AppendLine(TemplateUtilities.IndentCode(generatedCode, "            "));
        totalRegistrations++;
      }
    }

    // Replace template markers
    var result = template;
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(PerspectiveDiscoveryGenerator).Assembly, result);
    result = result.Replace("{{PERSPECTIVE_CLASS_COUNT}}", perspectives.Length.ToString());
    result = result.Replace("{{REGISTRATION_COUNT}}", totalRegistrations.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "PERSPECTIVE_REGISTRATIONS", registrations.ToString());

    return result;
  }

  /// <summary>
  /// Gets the simple name from a fully qualified type name.
  /// Handles tuples, arrays, and nested types.
  /// E.g., "global::MyApp.Events.OrderCreatedEvent" -> "OrderCreatedEvent"
  /// </summary>
  private static string GetSimpleName(string fullyQualifiedName) {
    // Handle arrays: Type[]
    if (fullyQualifiedName.EndsWith("[]")) {
      var baseType = fullyQualifiedName[..^2];
      return GetSimpleName(baseType) + "[]";
    }

    // Handle simple types
    var lastDot = fullyQualifiedName.LastIndexOf('.');
    return lastDot >= 0 ? fullyQualifiedName[(lastDot + 1)..] : fullyQualifiedName;
  }
}
