using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_EmptyCompilation_GeneratesNothingAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_SinglePerspectiveOneEvent_GeneratesRegistrationAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_SinglePerspectiveMultipleEvents_GeneratesMultipleRegistrationsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_MultiplePerspectives_GeneratesAllRegistrationsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_AbstractClass_IsIgnoredAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_GeneratesDiagnosticForDiscoveredPerspectiveAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_GeneratedCodeUsesCorrectNamespaceAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_UsesFullyQualifiedTypeNamesAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_ReturnsServiceCollectionForMethodChainingAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_ClassWithoutIPerspectiveOf_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_ArrayEventType_HandlesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_TypeInGlobalNamespace_HandlesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_NestedClass_DiscoversCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_InterfaceWithPerspectiveOf_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:Generator_WithArrayEventType_SimplifiesInDiagnosticAsync</tests>
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
    // Combine compilation with discovered perspectives to get assembly name for namespace
    var compilationAndPerspectives = context.CompilationProvider.Combine(perspectiveCandidates.Collect());

    context.RegisterSourceOutput(
        compilationAndPerspectives,
        static (ctx, data) => {
          var compilation = data.Left;
          var perspectives = data.Right;
          GeneratePerspectiveRegistrations(ctx, compilation, perspectives!);
        }
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

    // Extract model type from IPerspectiveModel<TModel> if implemented (optional)
    var modelInterface = classSymbol.AllInterfaces
        .FirstOrDefault(i => i.OriginalDefinition.ToDisplayString() == "Whizbang.Core.IPerspectiveModel<TModel>"
                             && i.TypeArguments.Length == 1);

    string? modelTypeName = null;
    string? streamKeyPropertyName = null;

    if (modelInterface is not null) {
      var modelType = modelInterface.TypeArguments[0];
      modelTypeName = modelType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

      // Find property with [StreamKey] attribute in the model type
      foreach (var member in modelType.GetMembers()) {
        if (member is IPropertySymbol property) {
          var hasStreamKeyAttribute = property.GetAttributes()
              .Any(a => a.AttributeClass?.ToDisplayString() == "Whizbang.Core.StreamKeyAttribute");

          if (hasStreamKeyAttribute) {
            streamKeyPropertyName = property.Name;
            break;
          }
        }
      }
    }

    // NOTE: ModelTypeName and StreamKeyPropertyName are optional
    // Perspectives may not implement IPerspectiveModel if they manage state differently

    return new PerspectiveInfo(
        ClassName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        EventTypes: eventTypes,
        ModelTypeName: modelTypeName,
        StreamKeyPropertyName: streamKeyPropertyName
    );
  }

  /// <summary>
  /// Generates the perspective registration code for all discovered perspectives.
  /// Creates an AddWhizbangPerspectives extension method that registers all perspectives as Scoped services.
  /// Uses assembly-specific namespace to avoid conflicts when multiple assemblies use Whizbang.
  /// </summary>
  private static void GeneratePerspectiveRegistrations(
      SourceProductionContext context,
      Compilation compilation,
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

    var registrationSource = GenerateRegistrationSource(compilation, perspectives);
    context.AddSource("PerspectiveRegistrations.g.cs", registrationSource);
  }

  /// <summary>
  /// Generates the C# source code for the registration extension method.
  /// Uses template-based generation for IDE support.
  /// Handles perspectives that implement multiple IPerspectiveOf&lt;TEvent&gt; interfaces.
  /// Uses assembly-specific namespace to avoid conflicts when multiple assemblies use Whizbang.
  /// </summary>
  private static string GenerateRegistrationSource(Compilation compilation, ImmutableArray<PerspectiveInfo> perspectives) {
    // Determine namespace from assembly name
    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";

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
    result = TemplateUtilities.ReplaceRegion(result, "NAMESPACE", $"namespace {namespaceName};");
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
