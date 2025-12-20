using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Generates a static registry for zero-reflection perspective runner lookup.
/// Replaces runtime reflection in PerspectiveWorker with compile-time code generation.
/// Discovers perspectives implementing IPerspectiveFor and generates GetRunner() and AddPerspectiveRunners() methods.
/// </summary>
[Generator]
public class PerspectiveRunnerRegistryGenerator : IIncrementalGenerator {
  private const string PERSPECTIVE_FOR_INTERFACE_NAME = "Whizbang.Core.Perspectives.IPerspectiveFor";
  private const string GLOBAL_PERSPECTIVE_FOR_INTERFACE_NAME = "Whizbang.Core.Perspectives.IGlobalPerspectiveFor";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Reuse the same discovery logic as PerspectiveRunnerGenerator
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
          GeneratePerspectiveRunnerRegistry(ctx, compilation, perspectives!);
        }
    );
  }

  /// <summary>
  /// Extracts perspective information from a class declaration.
  /// Returns null if the class doesn't implement IPerspectiveFor&lt;TModel, TEvent&gt; or IGlobalPerspectiveFor&lt;TModel, TPartitionKey, TEvent&gt;.
  /// </summary>
  private static PerspectiveRegistryInfo? ExtractPerspectiveInfo(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    var classSymbol = RoslynGuards.GetClassSymbolOrThrow(classDeclaration, semanticModel, cancellationToken);

    // Skip abstract classes
    if (classSymbol.IsAbstract) {
      return null;
    }

    // Look for IPerspectiveFor<TModel, TEvent1..5> interfaces (single-stream)
    var singleStreamInterfaces = classSymbol.AllInterfaces
        .Where(i => {
          var originalDef = i.OriginalDefinition.ToDisplayString();
          return (originalDef == PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TEvent1>" ||
                  originalDef == PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TEvent1, TEvent2>" ||
                  originalDef == PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TEvent1, TEvent2, TEvent3>" ||
                  originalDef == PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TEvent1, TEvent2, TEvent3, TEvent4>" ||
                  originalDef == PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5>")
                 && i.TypeArguments.Length >= 2;
        })
        .ToList();

    // Look for IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1..3> interfaces (multi-stream)
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

    // Find property with [StreamKey] attribute on the model
    var hasStreamKeyAttribute = false;
    foreach (var member in modelType.GetMembers()) {
      if (member is IPropertySymbol property) {
        hasStreamKeyAttribute = property.GetAttributes()
            .Any(a => a.AttributeClass?.ToDisplayString() == "Whizbang.Core.StreamKeyAttribute");

        if (hasStreamKeyAttribute) {
          break;
        }
      }
    }

    if (!hasStreamKeyAttribute) {
      // Cannot generate runner without StreamKey - skip silently
      return null;
    }

    var className = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    var simpleName = GetSimpleName(className);

    return new PerspectiveRegistryInfo(
        ClassName: className,
        SimpleName: simpleName,
        RunnerName: $"{simpleName}Runner"
    );
  }

  /// <summary>
  /// Generates the static registry class with GetRunner() and AddPerspectiveRunners() methods.
  /// </summary>
  private static void GeneratePerspectiveRunnerRegistry(
      SourceProductionContext context,
      Compilation compilation,
      ImmutableArray<PerspectiveRegistryInfo> perspectives) {

    if (perspectives.IsEmpty) {
      return;
    }

    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";

    var source = new StringBuilder();

    // File header
    source.AppendLine("// <auto-generated/>");
    source.AppendLine($"// Generated by {nameof(PerspectiveRunnerRegistryGenerator)} at {System.DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
    source.AppendLine("// DO NOT EDIT - Changes will be overwritten");
    source.AppendLine("#nullable enable");
    source.AppendLine();
    source.AppendLine("using System;");
    source.AppendLine("using Microsoft.Extensions.DependencyInjection;");
    source.AppendLine("using Whizbang.Core.Perspectives;");
    source.AppendLine();
    source.AppendLine($"namespace {namespaceName};");
    source.AppendLine();

    // Registry implementation class
    source.AppendLine("/// <summary>");
    source.AppendLine($"/// Auto-generated registry for {perspectives.Length} perspective runner(s).");
    source.AppendLine("/// Provides zero-reflection lookup for PerspectiveWorker (AOT-compatible).");
    source.AppendLine("/// Implements IPerspectiveRunnerRegistry for dependency injection.");
    source.AppendLine("/// </summary>");
    source.AppendLine("public sealed class PerspectiveRunnerRegistry : IPerspectiveRunnerRegistry {");
    source.AppendLine();

    // GetRunner() method (implements IPerspectiveRunnerRegistry)
    source.AppendLine("  /// <summary>");
    source.AppendLine("  /// Gets a perspective runner by perspective type name (zero reflection).");
    source.AppendLine("  /// Returns null if no runner found for the given perspective name.");
    source.AppendLine("  /// </summary>");
    source.AppendLine("  /// <param name=\"perspectiveName\">Simple name of the perspective class (e.g., \"InventoryLevelsPerspective\")</param>");
    source.AppendLine("  /// <param name=\"serviceProvider\">Service provider to resolve runner dependencies</param>");
    source.AppendLine("  /// <returns>IPerspectiveRunner instance or null if not found</returns>");
    source.AppendLine("  public IPerspectiveRunner? GetRunner(");
    source.AppendLine("      string perspectiveName,");
    source.AppendLine("      IServiceProvider serviceProvider) {");
    source.AppendLine();
    source.AppendLine("    return perspectiveName switch {");

    // Generate switch cases for each perspective
    foreach (var perspective in perspectives.OrderBy(p => p.SimpleName)) {
      source.AppendLine($"      \"{perspective.SimpleName}\" => serviceProvider.GetRequiredService<{perspective.RunnerName}>(),");
    }

    source.AppendLine("      _ => null");
    source.AppendLine("    };");
    source.AppendLine("  }");
    source.AppendLine("}");
    source.AppendLine();

    // Extension class for AddPerspectiveRunners() (must be static for extension methods)
    source.AppendLine("/// <summary>");
    source.AppendLine("/// Extension methods for registering perspective runners.");
    source.AppendLine("/// </summary>");
    source.AppendLine("public static class PerspectiveRunnerRegistryExtensions {");
    source.AppendLine("  /// <summary>");
    source.AppendLine($"  /// Registers all {perspectives.Length} perspective runner(s) as scoped services.");
    source.AppendLine("  /// Also registers the PerspectiveRunnerRegistry as the IPerspectiveRunnerRegistry singleton.");
    source.AppendLine("  /// Call this method in your service registration (e.g., Startup.cs or Program.cs).");
    source.AppendLine("  /// </summary>");
    source.AppendLine("  public static IServiceCollection AddPerspectiveRunners(");
    source.AppendLine("      this IServiceCollection services) {");
    source.AppendLine();
    source.AppendLine("    // Register the registry as singleton");
    source.AppendLine("    services.AddSingleton<IPerspectiveRunnerRegistry, PerspectiveRunnerRegistry>();");
    source.AppendLine();

    // Register each runner
    foreach (var perspective in perspectives.OrderBy(p => p.SimpleName)) {
      source.AppendLine($"    services.AddScoped<{perspective.RunnerName}>();");
    }

    source.AppendLine();
    source.AppendLine("    return services;");
    source.AppendLine("  }");
    source.AppendLine("}");

    context.AddSource("PerspectiveRunnerRegistry.g.cs", source.ToString());

    // Report diagnostic
    context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.PerspectiveRunnerRegistryGenerated,
        Location.None,
        perspectives.Length
    ));
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

/// <summary>
/// Registry information for a discovered perspective.
/// </summary>
/// <param name="ClassName">Fully qualified class name</param>
/// <param name="SimpleName">Simple class name (e.g., "InventoryLevelsPerspective")</param>
/// <param name="RunnerName">Generated runner name (e.g., "InventoryLevelsPerspectiveRunner")</param>
internal sealed record PerspectiveRegistryInfo(
    string ClassName,
    string SimpleName,
    string RunnerName
);
