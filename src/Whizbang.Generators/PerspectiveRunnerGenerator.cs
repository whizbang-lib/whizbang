using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Generates IPerspectiveRunner implementations for perspectives that implement IPerspectiveModel&lt;TModel&gt;.
/// Runners handle unit-of-work event replay with UUID7 ordering, configurable batching, and checkpoint management.
/// </summary>
[Generator]
public class PerspectiveRunnerGenerator : IIncrementalGenerator {
  private const string PERSPECTIVE_INTERFACE_NAME = "Whizbang.Core.IPerspectiveOf";
  private const string PERSPECTIVE_MODEL_INTERFACE_NAME = "Whizbang.Core.IPerspectiveModel";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Reuse the same discovery logic as PerspectiveDiscoveryGenerator
    var perspectiveCandidates = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => ExtractPerspectiveInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Combine with compilation to get assembly name
    var compilationAndPerspectives = context.CompilationProvider.Combine(perspectiveCandidates.Collect());

    context.RegisterSourceOutput(
        compilationAndPerspectives,
        static (ctx, data) => {
          var compilation = data.Left;
          var perspectives = data.Right;
          GeneratePerspectiveRunners(ctx, compilation, perspectives!);
        }
    );
  }

  /// <summary>
  /// Extracts perspective information from a class declaration.
  /// Returns null if the class doesn't implement IPerspectiveOf or IPerspectiveModel.
  /// </summary>
  private static PerspectiveInfo? ExtractPerspectiveInfo(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    var classSymbol = RoslynGuards.GetClassSymbolOrThrow(classDeclaration, semanticModel, cancellationToken);

    // Skip abstract classes
    if (classSymbol.IsAbstract) {
      return null;
    }

    // Look for IPerspectiveOf<TEvent> interfaces
    var perspectiveInterfaces = classSymbol.AllInterfaces
        .Where(i => i.OriginalDefinition.ToDisplayString() == PERSPECTIVE_INTERFACE_NAME + "<TEvent>"
                    && i.TypeArguments.Length == 1)
        .ToList();

    if (perspectiveInterfaces.Count == 0) {
      return null;
    }

    // Extract all event types
    var eventTypes = perspectiveInterfaces
        .Select(i => i.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
        .ToArray();

    // Extract model type from IPerspectiveModel<TModel> if implemented
    var modelInterface = classSymbol.AllInterfaces
        .FirstOrDefault(i => i.OriginalDefinition.ToDisplayString() == PERSPECTIVE_MODEL_INTERFACE_NAME + "<TModel>"
                             && i.TypeArguments.Length == 1);

    // ONLY generate runner for perspectives with IPerspectiveModel
    if (modelInterface is null) {
      return null;
    }

    var modelType = modelInterface.TypeArguments[0];
    var modelTypeName = modelType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    // Find property with [StreamKey] attribute
    string? streamKeyPropertyName = null;
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

    if (streamKeyPropertyName is null) {
      // Cannot generate runner without StreamKey - warn user
      return null;
    }

    return new PerspectiveInfo(
        ClassName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        EventTypes: eventTypes,
        ModelTypeName: modelTypeName,
        StreamKeyPropertyName: streamKeyPropertyName
    );
  }

  /// <summary>
  /// Generates IPerspectiveRunner implementations for all discovered perspectives with models.
  /// </summary>
  private static void GeneratePerspectiveRunners(
      SourceProductionContext context,
      Compilation compilation,
      ImmutableArray<PerspectiveInfo> perspectives) {

    if (perspectives.IsEmpty) {
      return;
    }

    // Generate a runner for each perspective
    foreach (var perspective in perspectives) {
      var runnerSource = GenerateRunnerSource(compilation, perspective);
      var runnerName = GetRunnerName(perspective.ClassName);
      context.AddSource($"{runnerName}.g.cs", runnerSource);

      // Report diagnostic
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.PerspectiveRunnerGenerated,
          Location.None,
          GetSimpleName(perspective.ClassName),
          runnerName
      ));
    }
  }

  /// <summary>
  /// Generates the C# source code for a perspective runner.
  /// Uses template-based generation with unit-of-work pattern.
  /// </summary>
  private static string GenerateRunnerSource(Compilation compilation, PerspectiveInfo perspective) {
    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";

    // Load template from embedded resource
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(PerspectiveRunnerGenerator).Assembly,
        "PerspectiveRunnerTemplate.cs"
    );

    var runnerName = GetRunnerName(perspective.ClassName);
    var perspectiveSimpleName = GetSimpleName(perspective.ClassName);

    // Replace template markers
    var result = template;
    result = TemplateUtilities.ReplaceRegion(result, "NAMESPACE", $"namespace {namespaceName};");
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(PerspectiveRunnerGenerator).Assembly, result);
    result = result.Replace("__RUNNER_CLASS_NAME__", runnerName);
    result = result.Replace("__PERSPECTIVE_CLASS_NAME__", perspective.ClassName);
    result = result.Replace("__MODEL_TYPE_NAME__", perspective.ModelTypeName!);
    result = result.Replace("__STREAM_KEY_PROPERTY__", perspective.StreamKeyPropertyName!);
    result = result.Replace("__PERSPECTIVE_SIMPLE_NAME__", perspectiveSimpleName);

    return result;
  }

  /// <summary>
  /// Gets the runner class name from a perspective class name.
  /// E.g., "MyApp.OrderPerspective" -> "OrderPerspectiveRunner"
  /// </summary>
  private static string GetRunnerName(string perspectiveClassName) {
    var simpleName = GetSimpleName(perspectiveClassName);
    return $"{simpleName}Runner";
  }

  /// <summary>
  /// Gets the simple name from a fully qualified type name.
  /// E.g., "global::MyApp.OrderPerspective" -> "OrderPerspective"
  /// </summary>
  private static string GetSimpleName(string fullyQualifiedName) {
    var lastDot = fullyQualifiedName.LastIndexOf('.');
    return lastDot >= 0 ? fullyQualifiedName[(lastDot + 1)..] : fullyQualifiedName;
  }
}
