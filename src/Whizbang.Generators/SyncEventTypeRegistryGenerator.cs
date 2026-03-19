using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Incremental source generator that discovers [AwaitPerspectiveSync] attributes
/// and generates the TrackedEventTypeRegistry for perspective sync tracking.
/// </summary>
/// <remarks>
/// <para>
/// This generator scans all receptor classes for [AwaitPerspectiveSync] attributes,
/// extracts the EventTypes and PerspectiveType, and builds a registry mapping
/// event types to perspective names.
/// </para>
/// <para>
/// The generated registry enables cross-scope perspective synchronization by
/// letting the SyncTrackingEventStoreDecorator know which event types to track.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync#type-registry</docs>
/// <tests>Whizbang.Generators.Tests/SyncEventTypeRegistryGeneratorTests.cs</tests>
[Generator]
public class SyncEventTypeRegistryGenerator : IIncrementalGenerator {
  private const string AWAIT_SYNC_ATTRIBUTE = "Whizbang.Core.Perspectives.Sync.AwaitPerspectiveSyncAttribute";
  private const string REGION_NAMESPACE = "NAMESPACE";
  private const string DEFAULT_NAMESPACE = "Whizbang.Core";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover classes with [AwaitPerspectiveSync] attribute
    var syncMappings = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
        transform: static (ctx, ct) => _extractSyncMappings(ctx, ct)
    ).Where(static mappings => mappings is not null && mappings.Length > 0);

    // Combine with compilation to get assembly name for namespace
    var compilationAndMappings = context.CompilationProvider.Combine(syncMappings.Collect());

    context.RegisterSourceOutput(
        compilationAndMappings,
        static (ctx, data) => {
          var compilation = data.Left;
          var allMappings = data.Right;
          _generateSyncEventTypeRegistry(ctx, compilation, allMappings!);
        }
    );
  }

  /// <summary>
  /// Extracts event type to perspective mappings from [AwaitPerspectiveSync] attributes.
  /// Returns null if no [AwaitPerspectiveSync] attributes are found.
  /// </summary>
  private static SyncTypeMapping[]? _extractSyncMappings(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    var classSymbol = RoslynGuards.GetClassSymbolOrThrow(classDeclaration, semanticModel, cancellationToken);

    var mappings = new List<SyncTypeMapping>();

    foreach (var attribute in classSymbol.GetAttributes()) {
      if (attribute.AttributeClass?.ToDisplayString() != AWAIT_SYNC_ATTRIBUTE) {
        continue;
      }

      // Extract PerspectiveType from constructor argument
      if (attribute.ConstructorArguments.Length == 0) {
        continue;
      }

      var perspectiveTypeArg = attribute.ConstructorArguments[0];
      if (perspectiveTypeArg.Value is not INamedTypeSymbol perspectiveTypeSymbol) {
        continue;
      }

      // Use CLR type name format to match database storage and PerspectiveSyncAwaiter
      // This produces "Namespace.Type" for top-level and "Namespace.Parent+Nested" for nested types
      var perspectiveType = TypeNameUtilities.BuildClrTypeName(perspectiveTypeSymbol);

      // Extract EventTypes from named argument (Type[]?)
      var eventTypesArg = attribute.NamedArguments.FirstOrDefault(na => na.Key == "EventTypes");
      if (eventTypesArg.Value.Kind == TypedConstantKind.Array && !eventTypesArg.Value.IsNull) {
        foreach (var typeConstant in eventTypesArg.Value.Values) {
          if (typeConstant.Value is INamedTypeSymbol eventTypeSymbol) {
            var eventType = eventTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            mappings.Add(new SyncTypeMapping(eventType, perspectiveType));
          }
        }
      }
    }

    return mappings.Count > 0 ? mappings.ToArray() : null;
  }

  /// <summary>
  /// Generates the SyncEventTypeRegistry auto-registration code.
  /// </summary>
  private static void _generateSyncEventTypeRegistry(
      SourceProductionContext context,
      Compilation compilation,
      ImmutableArray<SyncTypeMapping[]> allMappings) {

    var assemblyName = compilation.AssemblyName ?? DEFAULT_NAMESPACE;
    var namespaceName = $"{assemblyName}.Generated";

    // Flatten and group by event type
    var eventTypeToPerspectives = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

    foreach (var mappingArray in allMappings) {
      if (mappingArray is null) {
        continue;
      }

      foreach (var mapping in mappingArray) {
        if (!eventTypeToPerspectives.TryGetValue(mapping.EventType, out var perspectives)) {
          perspectives = new HashSet<string>(StringComparer.Ordinal);
          eventTypeToPerspectives[mapping.EventType] = perspectives;
        }
        perspectives.Add(mapping.PerspectiveType);
      }
    }

    // Load template
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(SyncEventTypeRegistryGenerator).Assembly,
        "SyncEventTypeRegistryTemplate.cs"
    );

    // Generate registration calls
    var registrationsCode = new StringBuilder();
    foreach (var kvp in eventTypeToPerspectives) {
      var eventType = kvp.Key;
      foreach (var perspectiveType in kvp.Value) {
        registrationsCode.AppendLine($"    global::Whizbang.Core.Perspectives.Sync.SyncEventTypeRegistrations.Register(typeof({eventType}), \"{perspectiveType}\");");
      }
    }

    // Replace template markers
    var result = template;
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(SyncEventTypeRegistryGenerator).Assembly, result);
    result = TemplateUtilities.ReplaceRegion(result, REGION_NAMESPACE, $"namespace {namespaceName};");
    result = result.Replace("{{EVENT_TYPE_COUNT}}", eventTypeToPerspectives.Count.ToString(CultureInfo.InvariantCulture));
    result = result.Replace("{{PERSPECTIVE_COUNT}}", eventTypeToPerspectives.SelectMany(kv => kv.Value).Distinct().Count().ToString(CultureInfo.InvariantCulture));
    result = TemplateUtilities.ReplaceRegion(result, "EVENT_TYPE_REGISTRATIONS", registrationsCode.ToString());

    context.AddSource("SyncEventTypeRegistry.g.cs", result);
  }
}

/// <summary>
/// Value type containing a single event type to perspective mapping.
/// </summary>
/// <param name="EventType">Fully qualified event type name.</param>
/// <param name="PerspectiveType">Fully qualified perspective type name.</param>
internal sealed record SyncTypeMapping(
    string EventType,
    string PerspectiveType
);
