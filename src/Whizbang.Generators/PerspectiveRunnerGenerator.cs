using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Generates IPerspectiveRunner implementations for perspectives that implement IPerspectiveFor&lt;TModel, TEvent&gt;.
/// Runners handle unit-of-work event replay with UUID7 ordering, configurable batching, and checkpoint management.
/// Supports both single-stream (IPerspectiveFor) and multi-stream (IGlobalPerspectiveFor) perspectives.
/// </summary>
[Generator]
public class PerspectiveRunnerGenerator : IIncrementalGenerator {
  private const string PERSPECTIVE_FOR_INTERFACE_NAME = "Whizbang.Core.Perspectives.IPerspectiveFor";
  private const string GLOBAL_PERSPECTIVE_FOR_INTERFACE_NAME = "Whizbang.Core.Perspectives.IGlobalPerspectiveFor";

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
  /// Returns null if the class doesn't implement IPerspectiveFor&lt;TModel, TEvent&gt; or IGlobalPerspectiveFor&lt;TModel, TPartitionKey, TEvent&gt;.
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

    // Look for IPerspectiveFor<TModel, TEvent1..3> interfaces (single-stream)
    // Format: Whizbang.Core.Perspectives.IPerspectiveFor<TModel, TEvent1> (2 type args)
    // Format: Whizbang.Core.Perspectives.IPerspectiveFor<TModel, TEvent1, TEvent2> (3 type args)
    // Format: Whizbang.Core.Perspectives.IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3> (4 type args)
    var singleStreamInterfaces = classSymbol.AllInterfaces
        .Where(i => {
          var originalDef = i.OriginalDefinition.ToDisplayString();
          return (originalDef == PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TEvent1>" ||
                  originalDef == PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TEvent1, TEvent2>" ||
                  originalDef == PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TEvent1, TEvent2, TEvent3>")
                 && i.TypeArguments.Length >= 2;
        })
        .ToList();

    // Look for IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1..3> interfaces (multi-stream)
    // Format: Whizbang.Core.Perspectives.IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1> (3 type args)
    var globalInterfaces = classSymbol.AllInterfaces
        .Where(i => {
          var originalDef = i.OriginalDefinition.ToDisplayString();
          return (originalDef == GLOBAL_PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TPartitionKey, TEvent1>" ||
                  originalDef == GLOBAL_PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TPartitionKey, TEvent1, TEvent2>" ||
                  originalDef == GLOBAL_PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3>")
                 && i.TypeArguments.Length >= 3;
        })
        .ToList();

    if (singleStreamInterfaces.Count == 0 && globalInterfaces.Count == 0) {
      return null;
    }

    // Extract model type (first type argument in both IPerspectiveFor and IGlobalPerspectiveFor)
    ITypeSymbol? modelType = null;
    if (singleStreamInterfaces.Count > 0) {
      modelType = singleStreamInterfaces[0].TypeArguments[0];
    } else if (globalInterfaces.Count > 0) {
      modelType = globalInterfaces[0].TypeArguments[0];
    }

    if (modelType is null) {
      return null;
    }

    var modelTypeName = modelType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    // Extract event types from all interfaces
    var eventTypes = new List<string>();

    // Extract from single-stream: skip TModel (index 0), all others are events
    foreach (var iface in singleStreamInterfaces) {
      for (int i = 1; i < iface.TypeArguments.Length; i++) {
        var eventType = iface.TypeArguments[i].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (!eventTypes.Contains(eventType)) {
          eventTypes.Add(eventType);
        }
      }
    }

    // Extract from global: skip TModel (index 0) and TPartitionKey (index 1), rest are events
    foreach (var iface in globalInterfaces) {
      for (int i = 2; i < iface.TypeArguments.Length; i++) {
        var eventType = iface.TypeArguments[i].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (!eventTypes.Contains(eventType)) {
          eventTypes.Add(eventType);
        }
      }
    }

    if (eventTypes.Count == 0) {
      return null;
    }

    // Find property with [StreamKey] attribute on the model
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
      // Cannot generate runner without StreamKey - skip silently
      return null;
    }

    return new PerspectiveInfo(
        ClassName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        EventTypes: eventTypes.ToArray(),
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
