using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Data.EFCore.Postgres.Generators;

/// <summary>
/// Source generator that discovers Perspective implementations and generates EF Core ModelBuilder setup.
/// Generates a ConfigureWhizbang() extension method that configures:
/// - PerspectiveRow&lt;TModel&gt; entities (discovered from IPerspectiveOf implementations with IPerspectiveStore&lt;TModel&gt; dependencies)
/// - InboxRecord, OutboxRecord, EventStoreRecord, ServiceInstanceRecord (fixed Whizbang entities)
/// Uses EF Core 10 ComplexProperty().ToJson() for JSONB columns (Postgres).
/// </summary>
[Generator]
public class EFCorePerspectiveConfigurationGenerator : IIncrementalGenerator {
  private const string IPERSPECTIVE_OF_TYPE = "Whizbang.Core.IPerspectiveOf<TEvent>";
  private const string IPERSPECTIVE_STORE_TYPE = "Whizbang.Core.Lenses.IPerspectiveStore<TModel>";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover classes implementing IPerspectiveOf<TEvent> that use IPerspectiveStore<TModel>
    var perspectiveClasses = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => ExtractPerspectiveInfo(ctx, ct)
    ).Where(static info => info is not null);

    //  Combine with compilation to check assembly name
    var perspectivesWithCompilation = perspectiveClasses.Collect()
        .Combine(context.CompilationProvider);

    // Generate ModelBuilder extension method with all Whizbang entities
    context.RegisterSourceOutput(
        perspectivesWithCompilation,
        static (ctx, data) => {
          var perspectives = data.Left;
          var compilation = data.Right;

          // Skip generation if this IS the library project itself
          // The library should not have this class baked in - only consuming projects should
          if (compilation.AssemblyName == "Whizbang.Data.EFCore.Postgres") {
            return;
          }

          GenerateModelBuilderExtension(ctx, perspectives!);
        }
    );
  }

  /// <summary>
  /// Extracts perspective information from a class implementing IPerspectiveOf.
  /// Discovers TModel type from IPerspectiveStore&lt;TModel&gt; constructor parameter.
  /// Returns null if the class doesn't implement IPerspectiveOf or doesn't have IPerspectiveStore dependency.
  /// </summary>
  private static PerspectiveInfo? ExtractPerspectiveInfo(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    var classDecl = (ClassDeclarationSyntax)context.Node;
    var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl, ct) as INamedTypeSymbol;

    if (symbol is null) {
      return null;
    }

    // DEBUG: Check all interfaces the class implements
    var allInterfaceNames = string.Join(", ",
        symbol.AllInterfaces.Select(i => i.OriginalDefinition.ToDisplayString()));

    // Check if class implements IPerspectiveOf<TEvent>
    // Note: IPerspectiveOf is generic with ONE type parameter (TEvent)
    bool implementsIPerspectiveOf = symbol.AllInterfaces.Any(i => {
      var originalDef = i.OriginalDefinition.ToDisplayString();
      // IPerspectiveOf<TEvent> has full name "Whizbang.Core.IPerspectiveOf<TEvent>"
      return originalDef.StartsWith("Whizbang.Core.IPerspectiveOf<");
    });

    if (!implementsIPerspectiveOf) {
      return null;
    }

    // Find IPerspectiveStore<TModel> in constructor parameters
    var constructor = symbol.Constructors.FirstOrDefault();
    if (constructor is null) {
      return null;
    }

    foreach (var parameter in constructor.Parameters) {
      if (parameter.Type is INamedTypeSymbol parameterType) {
        var originalDef = parameterType.OriginalDefinition.ToDisplayString();

        // IPerspectiveStore<TModel> has full name "Whizbang.Core.Perspectives.IPerspectiveStore<TModel>"
        if (originalDef.StartsWith("Whizbang.Core.Perspectives.IPerspectiveStore<")) {
          // Get TModel from IPerspectiveStore<TModel>
          var modelType = parameterType.TypeArguments[0];
          var tableName = "wh_per_" + ToSnakeCase(modelType.Name);

          return new PerspectiveInfo(
              ModelTypeName: modelType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
              TableName: tableName
          );
        }
      }
    }

    return null; // No IPerspectiveStore<TModel> found in constructor
  }


  /// <summary>
  /// Converts PascalCase to snake_case.
  /// </summary>
  private static string ToSnakeCase(string input) {
    if (string.IsNullOrEmpty(input)) {
      return input;
    }

    var sb = new StringBuilder();
    sb.Append(char.ToLowerInvariant(input[0]));

    for (int i = 1; i < input.Length; i++) {
      char c = input[i];
      if (char.IsUpper(c)) {
        sb.Append('_');
        sb.Append(char.ToLowerInvariant(c));
      } else {
        sb.Append(c);
      }
    }

    return sb.ToString();
  }

  /// <summary>
  /// Generates the ModelBuilder extension method with EF Core configuration for all Whizbang entities.
  /// Includes: discovered PerspectiveRow&lt;TModel&gt; entities + fixed entities (Inbox, Outbox, EventStore).
  /// Uses template system for code generation.
  /// </summary>
  private static void GenerateModelBuilderExtension(
      SourceProductionContext context,
      ImmutableArray<PerspectiveInfo> perspectives) {

    // DEBUG: Report that this method is running
    var debugDescriptor = new DiagnosticDescriptor(
        id: "EFCORE001",
        title: "DEBUG: GenerateModelBuilderExtension Running",
        messageFormat: "DEBUG: GenerateModelBuilderExtension called with {0} raw perspective(s)",
        category: "Whizbang.Generator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
    context.ReportDiagnostic(Diagnostic.Create(debugDescriptor, Location.None, perspectives.Length));

    // Deduplicate perspectives by ModelTypeName (multiple perspectives might use same model type)
    var uniquePerspectives = perspectives
        .GroupBy(p => p.ModelTypeName)
        .Select(g => g.First())
        .ToImmutableArray();

    // Report diagnostic about discovery
    var runningDescriptor = new DiagnosticDescriptor(
        id: "EFCORE000",
        title: "EF Core Configuration Generator Executed",
        messageFormat: "Whizbang EF Core generator discovered {0} unique model type(s) from {1} perspective(s) + 4 fixed entities (Inbox, Outbox, EventStore, ServiceInstance)",
        category: "Whizbang.Generator",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    context.ReportDiagnostic(Diagnostic.Create(runningDescriptor, Location.None, uniquePerspectives.Length, perspectives.Length));

    // Load main template
    var assembly = typeof(EFCorePerspectiveConfigurationGenerator).Assembly;
    var template = TemplateUtilities.GetEmbeddedTemplate(
        assembly,
        "EFCoreConfigurationTemplate.cs",
        "Whizbang.Data.EFCore.Postgres.Generators.Templates"
    );

    // Replace header with timestamp
    template = TemplateUtilities.ReplaceRegion(
        template,
        "HEADER",
        $"// <auto-generated/>\n// Generated by Whizbang.Data.EFCore.Postgres.Generators.EFCorePerspectiveConfigurationGenerator at {System.DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n// DO NOT EDIT - Changes will be overwritten\n#nullable enable"
    );

    // Replace __PERSPECTIVE_COUNT__ placeholder
    template = template.Replace("__PERSPECTIVE_COUNT__", uniquePerspectives.Length.ToString());

    // Generate perspective configurations
    var perspectiveConfigs = new StringBuilder();
    if (uniquePerspectives.Length > 0) {
      perspectiveConfigs.AppendLine("  // ===== Discovered Perspective Entities =====");
      perspectiveConfigs.AppendLine();

      foreach (var perspective in uniquePerspectives) {
        // Extract perspective entity config snippet
        var snippet = TemplateUtilities.ExtractSnippet(
            assembly,
            "EFCoreSnippets.cs",
            "PERSPECTIVE_ENTITY_CONFIG_SNIPPET",
            "Whizbang.Data.EFCore.Postgres.Generators.Templates.Snippets"
        );

        // Replace placeholders
        var config = snippet
            .Replace("__MODEL_TYPE__", perspective.ModelTypeName)
            .Replace("__TABLE_NAME__", perspective.TableName);

        perspectiveConfigs.AppendLine(TemplateUtilities.IndentCode(config, "  "));
        perspectiveConfigs.AppendLine();
      }
    }

    template = TemplateUtilities.ReplaceRegion(template, "PERSPECTIVE_CONFIGURATIONS", perspectiveConfigs.ToString());

    // Generate inbox configuration
    var inboxSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        "EFCoreSnippets.cs",
        "INBOX_ENTITY_CONFIG_SNIPPET",
        "Whizbang.Data.EFCore.Postgres.Generators.Templates.Snippets"
    );
    template = TemplateUtilities.ReplaceRegion(template, "INBOX_CONFIGURATION", inboxSnippet);

    // Generate outbox configuration
    var outboxSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        "EFCoreSnippets.cs",
        "OUTBOX_ENTITY_CONFIG_SNIPPET",
        "Whizbang.Data.EFCore.Postgres.Generators.Templates.Snippets"
    );
    template = TemplateUtilities.ReplaceRegion(template, "OUTBOX_CONFIGURATION", outboxSnippet);

    // Generate event store configuration
    var eventStoreSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        "EFCoreSnippets.cs",
        "EVENTSTORE_ENTITY_CONFIG_SNIPPET",
        "Whizbang.Data.EFCore.Postgres.Generators.Templates.Snippets"
    );
    template = TemplateUtilities.ReplaceRegion(template, "EVENTSTORE_CONFIGURATION", eventStoreSnippet);

    // Generate service instance configuration
    var serviceInstanceSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        "EFCoreSnippets.cs",
        "SERVICE_INSTANCE_ENTITY_CONFIG_SNIPPET",
        "Whizbang.Data.EFCore.Postgres.Generators.Templates.Snippets"
    );
    template = TemplateUtilities.ReplaceRegion(template, "SERVICE_INSTANCE_CONFIGURATION", serviceInstanceSnippet);

    // Generate diagnostic perspective list
    var diagnosticList = new StringBuilder();
    if (uniquePerspectives.Length > 0) {
      if (uniquePerspectives.Length == perspectives.Length) {
        diagnosticList.AppendLine($"    logger.LogInformation(\"Discovered Perspectives: {uniquePerspectives.Length} perspective(s)\");");
      } else {
        diagnosticList.AppendLine($"    logger.LogInformation(\"Discovered Perspectives: {uniquePerspectives.Length} unique model type(s) from {perspectives.Length} perspective(s)\");");
      }
      diagnosticList.AppendLine("    logger.LogInformation(\"\");");

      foreach (var perspective in uniquePerspectives) {
        diagnosticList.AppendLine($"    logger.LogInformation(\"  - {perspective.ModelTypeName} (table: {perspective.TableName})\");");
      }
    } else {
      diagnosticList.AppendLine("    logger.LogInformation(\"Discovered Perspectives: 0 perspective(s)\");");
    }

    template = TemplateUtilities.ReplaceRegion(template, "DIAGNOSTIC_PERSPECTIVE_LIST", diagnosticList.ToString());

    // Replace diagnostic placeholders
    var timestamp = System.DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
    template = template.Replace("__TIMESTAMP__", timestamp);

    var totalEntityCount = uniquePerspectives.Length + 4; // perspectives + inbox + outbox + eventstore + serviceinstance
    template = template.Replace("__TOTAL_ENTITY_COUNT__", totalEntityCount.ToString());

    context.AddSource("WhizbangModelBuilderExtensions.g.cs", template);
  }
}
