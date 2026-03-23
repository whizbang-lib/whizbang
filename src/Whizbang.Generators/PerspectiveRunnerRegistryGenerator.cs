using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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
        transform: static (ctx, ct) => _extractPerspectiveInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Combine with compilation to get assembly name
    var compilationAndPerspectives = context.CompilationProvider.Combine(perspectiveCandidates.Collect());

    context.RegisterSourceOutput(
        compilationAndPerspectives,
        static (ctx, data) => {
          var compilation = data.Left;
          var perspectives = data.Right;
          _generatePerspectiveRunnerRegistry(ctx, compilation, perspectives!);
        }
    );
  }

  /// <summary>
  /// Extracts perspective information from a class declaration.
  /// Returns null if the class doesn't implement IPerspectiveFor&lt;TModel, TEvent&gt; or IGlobalPerspectiveFor&lt;TModel, TPartitionKey, TEvent&gt;.
  /// </summary>
  private static PerspectiveRegistryInfo? _extractPerspectiveInfo(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    var classSymbol = RoslynGuards.GetClassSymbolOrThrow(classDeclaration, semanticModel, cancellationToken);

    // Skip abstract classes
    if (classSymbol.IsAbstract) {
      return null;
    }

    // Look for IPerspectiveFor<TModel, TEvent1..50> interfaces (single-stream)
    var singleStreamInterfaces = classSymbol.AllInterfaces
        .Where(i => {
          var originalDef = i.OriginalDefinition.ToDisplayString();
          // Match IPerspectiveFor<TModel, TEvent1, ...> with any number of event types (1-50)
          return originalDef.StartsWith(PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TEvent", StringComparison.Ordinal)
                 && i.TypeArguments.Length >= 2;
        })
        .ToList();

    // Look for IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1..50> interfaces (multi-stream)
    var globalInterfaces = classSymbol.AllInterfaces
        .Where(i => {
          var originalDef = i.OriginalDefinition.ToDisplayString();
          // Match IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, ...> with any number of event types (1-50)
          return originalDef.StartsWith(GLOBAL_PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TPartitionKey, TEvent", StringComparison.Ordinal)
                 && i.TypeArguments.Length >= 3;
        })
        .ToList();

    if (singleStreamInterfaces.Count == 0 && globalInterfaces.Count == 0) {
      return null;
    }

    // Extract model type (first type argument in both IPerspectiveFor and IGlobalPerspectiveFor)
    // At this point, at least one of singleStreamInterfaces or globalInterfaces has count > 0
    // (we returned null above if both were empty), so modelType is always assigned.
    var modelType = singleStreamInterfaces.Count > 0
        ? singleStreamInterfaces[0].TypeArguments[0]
        : globalInterfaces[0].TypeArguments[0];

    // Find property with [StreamId] attribute on the model
    var hasStreamIdAttribute = false;
    foreach (var member in modelType.GetMembers()) {
      if (member is IPropertySymbol property) {
        hasStreamIdAttribute = property.GetAttributes()
            .Any(a => a.AttributeClass?.ToDisplayString() == "Whizbang.Core.StreamIdAttribute");

        if (hasStreamIdAttribute) {
          break;
        }
      }
    }

    if (!hasStreamIdAttribute) {
      // Cannot generate runner without StreamId - skip silently
      return null;
    }

    var className = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    var simpleName = TypeNameUtilities.GetSimpleName(classSymbol);
    var clrTypeName = TypeNameUtilities.BuildClrTypeName(classSymbol);

    // Extract event types in TWO formats:
    // 1. Runtime format ("FullName, AssemblyName") for PerspectiveRegistrationInfo.EventTypes string matching
    // 2. Code-gen format ("global::FullName") for typeof() in generated code
    // The runtime format matches TypeNameFormatter.Format() — critical for _isEventWithoutPerspectives.
    var eventTypes = new List<string>();
    var eventTypesCodeGen = new List<string>();
    if (singleStreamInterfaces.Count > 0) {
      // IPerspectiveFor<TModel, TEvent1, TEvent2, ...> - events start at index 1
      foreach (var iface in singleStreamInterfaces) {
        for (var i = 1; i < iface.TypeArguments.Length; i++) {
          eventTypes.Add(TypeNameUtilities.FormatTypeNameForRuntime(iface.TypeArguments[i]));
          eventTypesCodeGen.Add(iface.TypeArguments[i].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }
      }
    } else {
      // globalInterfaces.Count is guaranteed > 0 here (we returned null above if both were empty)
      // IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, ...> - events start at index 2
      foreach (var iface in globalInterfaces) {
        for (var i = 2; i < iface.TypeArguments.Length; i++) {
          eventTypes.Add(TypeNameUtilities.FormatTypeNameForRuntime(iface.TypeArguments[i]));
          eventTypesCodeGen.Add(iface.TypeArguments[i].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }
      }
    }

    return new PerspectiveRegistryInfo(
        ClassName: className,
        SimpleName: simpleName,
        ClrTypeName: clrTypeName,
        RunnerName: $"{simpleName.Replace(".", "")}Runner",
        ModelType: modelType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        EventTypes: [.. eventTypes.Distinct()],
        EventTypesCodeGen: [.. eventTypesCodeGen.Distinct()]
    );
  }

  /// <summary>
  /// Generates the static registry class with GetRunner() and AddPerspectiveRunners() methods.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveRunnerRegistryGeneratorTests.cs:Generator_WithDuplicateNames_EmitsCollisionErrorAsync</tests>
  private static void _generatePerspectiveRunnerRegistry(
      SourceProductionContext context,
      Compilation compilation,
      ImmutableArray<PerspectiveRegistryInfo> perspectives) {

    if (perspectives.IsEmpty) {
      return;
    }

    // Check for name collisions before generating
    if (_reportNameCollisions(context, perspectives)) {
      return;
    }

    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";

    var source = new StringBuilder();

    _appendFileHeader(source, namespaceName, perspectives.Length);
    _appendRegistryClass(source, perspectives);
    _appendExtensionsClass(source, perspectives);
    _appendModuleInitializerClass(source, perspectives.Length);

    context.AddSource("PerspectiveRunnerRegistry.g.cs", source.ToString());

    // Report diagnostic
    context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.PerspectiveRunnerRegistryGenerated,
        Location.None,
        perspectives.Length
    ));
  }

  /// <summary>
  /// Reports name collision diagnostics. Returns true if collisions found (should skip generation).
  /// </summary>
  private static bool _reportNameCollisions(SourceProductionContext context, ImmutableArray<PerspectiveRegistryInfo> perspectives) {
    var nameGroups = perspectives.GroupBy(p => p.SimpleName).Where(g => g.Count() > 1).ToList();
    if (nameGroups.Count == 0) {
      return false;
    }

    foreach (var group in nameGroups) {
      var classNames = string.Join(", ", group.Select(p => p.ClassName));
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.PerspectiveNameCollision,
          Location.None,
          group.Key,
          classNames
      ));
    }
    return true;
  }

  /// <summary>
  /// Appends file header with usings and namespace.
  /// </summary>
  private static void _appendFileHeader(StringBuilder source, string namespaceName, int perspectiveCount) {
    source.AppendLine("// <auto-generated/>");
    source.AppendLine($"// Generated by {nameof(PerspectiveRunnerRegistryGenerator)} at {System.DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
    source.AppendLine("// DO NOT EDIT - Changes will be overwritten");
    source.AppendLine("#nullable enable");
    source.AppendLine();
    source.AppendLine("using System;");
    source.AppendLine("using System.Collections.Generic;");
    source.AppendLine("using System.Runtime.CompilerServices;");
    source.AppendLine("using Microsoft.Extensions.DependencyInjection;");
    source.AppendLine("using Whizbang.Core.Messaging;");
    source.AppendLine("using Whizbang.Core.Perspectives;");
    source.AppendLine();
    source.AppendLine($"namespace {namespaceName};");
    source.AppendLine();
  }

  /// <summary>
  /// Appends the PerspectiveRunnerRegistry class with GetRunner, GetRegisteredPerspectives, and GetEventTypes.
  /// </summary>
  private static void _appendRegistryClass(StringBuilder source, ImmutableArray<PerspectiveRegistryInfo> perspectives) {
    source.AppendLine("/// <summary>");
    source.AppendLine($"/// Auto-generated registry for {perspectives.Length} perspective runner(s).");
    source.AppendLine("/// Provides zero-reflection lookup for PerspectiveWorker (AOT-compatible).");
    source.AppendLine("/// Implements IPerspectiveRunnerRegistry for dependency injection.");
    source.AppendLine("/// </summary>");
    source.AppendLine("public sealed class PerspectiveRunnerRegistry : IPerspectiveRunnerRegistry {");
    source.AppendLine();

    // GetRunner() method
    source.AppendLine("  /// <summary>");
    source.AppendLine("  /// Gets a perspective runner by perspective type name (zero reflection).");
    source.AppendLine("  /// Returns null if no runner found for the given perspective name.");
    source.AppendLine("  /// </summary>");
    source.AppendLine("  /// <param name=\"perspectiveName\">CLR format type name (e.g., \"MyApp.Perspectives.OrderPerspective\" or \"MyApp.Parent+Nested\")</param>");
    source.AppendLine("  /// <param name=\"serviceProvider\">Service provider to resolve runner dependencies</param>");
    source.AppendLine("  /// <returns>IPerspectiveRunner instance or null if not found</returns>");
    source.AppendLine("  public IPerspectiveRunner? GetRunner(");
    source.AppendLine("      string perspectiveName,");
    source.AppendLine("      IServiceProvider serviceProvider) {");
    source.AppendLine();
    source.AppendLine("    return perspectiveName switch {");
    foreach (var perspective in perspectives.OrderBy(p => p.ClrTypeName)) {
      source.AppendLine($"      \"{perspective.ClrTypeName}\" => serviceProvider.GetRequiredService<{perspective.RunnerName}>(),");
    }
    source.AppendLine("      _ => null");
    source.AppendLine("    };");
    source.AppendLine("  }");
    source.AppendLine();

    // GetRegisteredPerspectives()
    source.AppendLine("  private static readonly PerspectiveRegistrationInfo[] _registeredPerspectives = [");
    foreach (var perspective in perspectives.OrderBy(p => p.ClrTypeName)) {
      var eventTypesArray = string.Join(", ", perspective.EventTypes.Select(e => $"\"{e}\""));
      source.AppendLine("    new PerspectiveRegistrationInfo(");
      source.AppendLine($"      \"{perspective.ClrTypeName}\",");
      source.AppendLine($"      \"{perspective.ClassName}\",");
      source.AppendLine($"      \"{perspective.ModelType}\",");
      source.AppendLine($"      [{eventTypesArray}]");
      source.AppendLine("    ),");
    }
    source.AppendLine("  ];");
    source.AppendLine();
    source.AppendLine("  /// <summary>");
    source.AppendLine("  /// Gets information about all registered perspectives (zero reflection).");
    source.AppendLine("  /// Useful for diagnostic messages when runner lookup fails.");
    source.AppendLine("  /// </summary>");
    source.AppendLine("  public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => _registeredPerspectives;");
    source.AppendLine();

    // GetEventTypes()
    var allEventTypes = perspectives
        .SelectMany(p => p.EventTypesCodeGen)
        .Distinct()
        .OrderBy(e => e, StringComparer.Ordinal)
        .ToList();

    source.AppendLine("  // All unique event types for IEventTypeProvider (lifecycle receptor polymorphic deserialization)");
    source.AppendLine("  private static readonly Type[] _allEventTypes = [");
    foreach (var eventType in allEventTypes) {
      source.AppendLine($"    typeof({eventType}),");
    }
    source.AppendLine("  ];");
    source.AppendLine();
    source.AppendLine("  /// <summary>");
    source.AppendLine("  /// Gets all unique event types across all perspectives.");
    source.AppendLine("  /// Used by PerspectiveWorker for lifecycle receptor invocation (AOT-compatible polymorphic deserialization).");
    source.AppendLine("  /// </summary>");
    source.AppendLine("  public IReadOnlyList<Type> GetEventTypes() => _allEventTypes;");
    source.AppendLine("}");
    source.AppendLine();
  }

  /// <summary>
  /// Appends the PerspectiveRunnerRegistryExtensions class with AddPerspectiveRunners.
  /// </summary>
  private static void _appendExtensionsClass(StringBuilder source, ImmutableArray<PerspectiveRegistryInfo> perspectives) {
    source.AppendLine("/// <summary>");
    source.AppendLine("/// Extension methods for registering perspective runners.");
    source.AppendLine("/// </summary>");
    source.AppendLine("public static class PerspectiveRunnerRegistryExtensions {");
    source.AppendLine("  /// <summary>");
    source.AppendLine($"  /// Registers all {perspectives.Length} perspective runner(s) as scoped services.");
    source.AppendLine("  /// Also registers PerspectiveRunnerRegistry as IPerspectiveRunnerRegistry and IEventTypeProvider singletons.");
    source.AppendLine("  /// Call this method in your service registration (e.g., Startup.cs or Program.cs).");
    source.AppendLine("  /// </summary>");
    source.AppendLine("  public static IServiceCollection AddPerspectiveRunners(");
    source.AppendLine("      this IServiceCollection services) {");
    source.AppendLine();
    source.AppendLine("    // Register the registry as singleton (implements both IPerspectiveRunnerRegistry and IEventTypeProvider)");
    source.AppendLine("    services.AddSingleton<PerspectiveRunnerRegistry>();");
    source.AppendLine("    services.AddSingleton<IPerspectiveRunnerRegistry>(sp => sp.GetRequiredService<PerspectiveRunnerRegistry>());");
    source.AppendLine("    services.AddSingleton<IEventTypeProvider>(sp => sp.GetRequiredService<PerspectiveRunnerRegistry>());");
    source.AppendLine();

    foreach (var perspective in perspectives.OrderBy(p => p.SimpleName)) {
      source.AppendLine($"    services.AddScoped<{perspective.ClassName}>();");
      source.AppendLine($"    services.AddScoped<{perspective.RunnerName}>();");
    }

    source.AppendLine();
    source.AppendLine("    // TURNKEY: Automatically register PerspectiveWorker as hosted service");
    source.AppendLine("    // This ensures perspectives are processed without requiring manual registration");
    source.AppendLine("    services.AddHostedService<global::Whizbang.Core.Workers.PerspectiveWorker>();");
    source.AppendLine();
    source.AppendLine("    return services;");
    source.AppendLine("  }");
    source.AppendLine("}");
    source.AppendLine();
  }

  /// <summary>
  /// Appends the PerspectiveRunnerInitializer module initializer class.
  /// </summary>
  private static void _appendModuleInitializerClass(StringBuilder source, int perspectiveCount) {
    source.AppendLine("/// <summary>");
    source.AppendLine($"/// Auto-generated module initializer for registering {perspectiveCount} perspective runner(s).");
    source.AppendLine("/// Runs at module load time and registers with PerspectiveRunnerCallbackRegistry (AOT-compatible).");
    source.AppendLine("/// For test assemblies where ModuleInitializers may not run reliably, call Initialize() explicitly.");
    source.AppendLine("/// </summary>");
    source.AppendLine("public static class PerspectiveRunnerInitializer {");
    source.AppendLine("  /// <summary>");
    source.AppendLine("  /// Module initializer that registers the perspective runner registration callback.");
    source.AppendLine("  /// This runs automatically when the assembly is loaded (no reflection required).");
    source.AppendLine("  /// For test assemblies, you can call this method explicitly in test setup.");
    source.AppendLine("  /// </summary>");
    source.AppendLine("  [ModuleInitializer]");
    source.AppendLine("  public static void Initialize() {");
    source.AppendLine("    // Register callback with the library's registry");
    source.AppendLine("    // When .WithDriver.Postgres (or similar) is called, this callback will be invoked");
    source.AppendLine("    // Wrap in lambda because AddPerspectiveRunners returns IServiceCollection (fluent API)");
    source.AppendLine("    PerspectiveRunnerCallbackRegistry.RegisterCallback(services => {");
    source.AppendLine("      _ = PerspectiveRunnerRegistryExtensions.AddPerspectiveRunners(services);");
    source.AppendLine("    });");
    source.AppendLine("  }");
    source.AppendLine("}");
  }

}

/// <summary>
/// Registry information for a discovered perspective.
/// </summary>
/// <param name="ClassName">Fully qualified class name (with global:: prefix for code generation)</param>
/// <param name="SimpleName">Simple class name (e.g., "InventoryLevelsPerspective")</param>
/// <param name="ClrTypeName">CLR format type name for database storage (e.g., "Namespace.Parent+Child")</param>
/// <param name="RunnerName">Generated runner name (e.g., "InventoryLevelsPerspectiveRunner")</param>
/// <param name="ModelType">Fully qualified model type from IPerspectiveFor&lt;TModel, TEvent&gt;</param>
/// <param name="EventTypes">Runtime-format event types ("FullName, AssemblyName") for string matching in PerspectiveRegistrationInfo</param>
/// <param name="EventTypesCodeGen">Code-generation-format event types ("global::FullName") for typeof() in generated code</param>
internal sealed record PerspectiveRegistryInfo(
    string ClassName,
    string SimpleName,
    string ClrTypeName,
    string RunnerName,
    string ModelType,
    string[] EventTypes,
    string[] EventTypesCodeGen
);
