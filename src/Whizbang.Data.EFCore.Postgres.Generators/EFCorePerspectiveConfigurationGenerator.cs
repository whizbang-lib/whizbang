using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Data.EFCore.Postgres.Generators;

/// <summary>
/// Source generator that discovers Perspective implementations and generates EF Core ModelBuilder setup.
/// Generates a ConfigureWhizbang() extension method that configures:
/// - PerspectiveRow&lt;TModel&gt; entities (discovered from IPerspectiveFor&lt;TModel&gt; perspectives)
/// - InboxRecord, OutboxRecord, EventStoreRecord, ServiceInstanceRecord (fixed Whizbang entities)
/// Uses EF Core 10 ComplexProperty().ToJson() for JSONB columns (Postgres).
/// </summary>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedCode_ImplementsIDiagnosticsInterfaceAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_HasCorrectGeneratorNameAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_ReportsCorrectPerspectiveCountAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:LogDiscoveryDiagnostics_OutputsPerspectiveDetailsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_WithNoPerspectives_ReportsZeroAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_DeduplicatesPerspectivesAsync</tests>
[Generator]
public class EFCorePerspectiveConfigurationGenerator : IIncrementalGenerator {
  /// <summary>
  /// Initializes the incremental generator by discovering perspectives and registering source generation.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedCode_ImplementsIDiagnosticsInterfaceAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_HasCorrectGeneratorNameAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_ReportsCorrectPerspectiveCountAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:LogDiscoveryDiagnostics_OutputsPerspectiveDetailsAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_WithNoPerspectives_ReportsZeroAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_DeduplicatesPerspectivesAsync</tests>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover classes implementing IPerspectiveFor<TModel>
    var perspectiveClasses = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractPerspectiveInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Discover DbContexts with [WhizbangDbContext] attribute to extract schema
    var dbContextClasses = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0, BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractDbContextSchema(ctx, ct)
    ).Where(static schema => schema is not null);

    //  Combine perspectives and DbContext schema with compilation
    var perspectivesWithDbContextAndCompilation = perspectiveClasses.Collect()
        .Combine(dbContextClasses.Collect())
        .Combine(context.CompilationProvider);

    // Generate ModelBuilder extension method with all Whizbang entities
    context.RegisterSourceOutput(
        perspectivesWithDbContextAndCompilation,
        static (ctx, data) => {
          var perspectives = data.Left.Left;
          var dbContextSchemas = data.Left.Right;
          var compilation = data.Right;

          // Skip generation if this IS the library project itself
          // The library should not have this class baked in - only consuming projects should
          if (compilation.AssemblyName == "Whizbang.Data.EFCore.Postgres") {
            return;
          }

          // Extract schema from first DbContext (typically one per project)
          // If no DbContext found or no schema specified, defaults to null and generator will derive from namespace
          string? schema = dbContextSchemas.IsEmpty ? null : dbContextSchemas[0];

          _generateModelBuilderExtension(ctx, perspectives!, schema);
        }
    );
  }

  /// <summary>
  /// Extracts schema name from a DbContext class with [WhizbangDbContext] attribute.
  /// Returns the Schema property value if specified, otherwise derives from namespace.
  /// Returns null if the class doesn't have [WhizbangDbContext] attribute.
  /// </summary>
  private static string? _extractDbContextSchema(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    var classDecl = (ClassDeclarationSyntax)context.Node;
    var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl, ct) as INamedTypeSymbol;

    if (symbol is null) {
      return null;
    }

    // Check if class inherits from DbContext
    var baseType = symbol.BaseType;
    bool inheritsDbContext = false;
    while (baseType != null) {
      if (baseType.ToDisplayString() == "Microsoft.EntityFrameworkCore.DbContext") {
        inheritsDbContext = true;
        break;
      }
      baseType = baseType.BaseType;
    }

    if (!inheritsDbContext) {
      return null;
    }

    // Check for [WhizbangDbContext] attribute
    var attribute = symbol.GetAttributes()
        .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "Whizbang.Data.EFCore.Custom.WhizbangDbContextAttribute");

    if (attribute is null) {
      return null;  // No attribute = not discovered
    }

    // Extract Schema property from attribute
    var schemaProp = attribute.NamedArguments
        .FirstOrDefault(kvp => kvp.Key == "Schema");

    if (schemaProp.Key == "Schema" && schemaProp.Value.Value is string schemaValue) {
      return schemaValue;
    }

    // No Schema property set, derive from namespace
    var namespaceName = symbol.ContainingNamespace.ToDisplayString();
    return _deriveSchemaFromNamespace(namespaceName);
  }

  /// <summary>
  /// Derives PostgreSQL schema name from namespace.
  /// Examples:
  /// - "ECommerce.InventoryWorker" → "inventory"
  /// - "ECommerce.BFF.API" → "bff"
  /// - "MyApp.OrderService" → "order"
  /// </summary>
  private static string _deriveSchemaFromNamespace(string namespaceName) {
    if (string.IsNullOrEmpty(namespaceName)) {
      return "public"; // Default PostgreSQL schema
    }

    // Split namespace into segments
    var segments = namespaceName.Split('.');

    // Take the last segment (e.g., "InventoryWorker", "API")
    var lastSegment = segments[segments.Length - 1];

    // If last segment is generic (API, Service, etc.), take second-to-last
    if ((lastSegment.Equals("API", StringComparison.OrdinalIgnoreCase) ||
         lastSegment.Equals("Service", StringComparison.OrdinalIgnoreCase) ||
         lastSegment.Equals("Worker", StringComparison.OrdinalIgnoreCase)) &&
        segments.Length > 1) {
      lastSegment = segments[segments.Length - 2];
    }

    // Remove common suffixes (case-insensitive)
    // Use regex for case-insensitive replacement (netstandard2.0 doesn't have String.Replace with StringComparison)
    lastSegment = System.Text.RegularExpressions.Regex.Replace(lastSegment, "Worker", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    lastSegment = System.Text.RegularExpressions.Regex.Replace(lastSegment, "Service", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Convert to lowercase
    return lastSegment.ToLowerInvariant();
  }

  /// <summary>
  /// Extracts perspective information from a class implementing IPerspectiveFor.
  /// Discovers TModel type from IPerspectiveFor&lt;TModel&gt; base interface (first type argument).
  /// Returns null if the class doesn't implement the interface.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_ReportsCorrectPerspectiveCountAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:LogDiscoveryDiagnostics_OutputsPerspectiveDetailsAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_DeduplicatesPerspectivesAsync</tests>
  private static PerspectiveInfo? _extractPerspectiveInfo(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    var classDecl = (ClassDeclarationSyntax)context.Node;
    var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl, ct) as INamedTypeSymbol;

    if (symbol is null) {
      return null;
    }

    // Check if class implements IPerspectiveFor<TModel> base interface or any variant
    // IPerspectiveFor has multiple overloads:
    // - IPerspectiveFor<TModel> (base marker)
    // - IPerspectiveFor<TModel, TEvent1>
    // - IPerspectiveFor<TModel, TEvent1, TEvent2>
    // ... up to IPerspectiveFor<TModel, TEvent1, ..., TEvent5>
    var perspectiveForInterface = symbol.AllInterfaces.FirstOrDefault(i => {
      var originalDef = i.OriginalDefinition.ToDisplayString();
      return originalDef == "Whizbang.Core.Perspectives.IPerspectiveFor<TModel>" ||
             originalDef.StartsWith("Whizbang.Core.Perspectives.IPerspectiveFor<TModel,", StringComparison.Ordinal);
    });

    if (perspectiveForInterface is null) {
      return null; // Not a perspective
    }

    // Perspective discovered - extract TModel from first type argument
    var modelType = perspectiveForInterface.TypeArguments[0];
    var tableName = "wh_per_" + _toSnakeCase(modelType.Name);

    return new PerspectiveInfo(
        ModelTypeName: modelType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        TableName: tableName
    );
  }


  /// <summary>
  /// Converts PascalCase to snake_case.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:LogDiscoveryDiagnostics_OutputsPerspectiveDetailsAsync</tests>
  private static string _toSnakeCase(string input) {
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
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedCode_ImplementsIDiagnosticsInterfaceAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_HasCorrectGeneratorNameAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_ReportsCorrectPerspectiveCountAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:LogDiscoveryDiagnostics_OutputsPerspectiveDetailsAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_WithNoPerspectives_ReportsZeroAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_DeduplicatesPerspectivesAsync</tests>
  private static void _generateModelBuilderExtension(
      SourceProductionContext context,
      ImmutableArray<PerspectiveInfo> perspectives,
      string? schema) {

    // Report perspective discovery for diagnostics
    var debugDescriptor = new DiagnosticDescriptor(
        id: "WHIZ701",
        title: "EF Core Perspective Discovery",
        messageFormat: "Discovered {0} perspective(s) for EF Core ModelBuilder configuration",
        category: "Whizbang.Generator",
        defaultSeverity: DiagnosticSeverity.Info,
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
    template = template.Replace("__PERSPECTIVE_COUNT__", uniquePerspectives.Length.ToString(CultureInfo.InvariantCulture));

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
        // Use provided schema, or default to "public" if not specified
        var effectiveSchema = schema ?? "public";
        var config = snippet
            .Replace("__MODEL_TYPE__", perspective.ModelTypeName)
            .Replace("__TABLE_NAME__", perspective.TableName)
            .Replace("__SCHEMA__", effectiveSchema);

        perspectiveConfigs.AppendLine(TemplateUtilities.IndentCode(config, "  "));
        perspectiveConfigs.AppendLine();
      }
    }

    template = TemplateUtilities.ReplaceRegion(template, "PERSPECTIVE_CONFIGURATIONS", perspectiveConfigs.ToString());

    // Infrastructure configuration is now handled by static WhizbangModelBuilderExtensions.ConfigureWhizbangInfrastructure()
    // No need to extract and inject infrastructure snippets here

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
    var timestamp = System.DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    template = template.Replace("__TIMESTAMP__", timestamp);

    var totalEntityCount = uniquePerspectives.Length + 4; // perspectives + inbox + outbox + eventstore + serviceinstance
    template = template.Replace("__TOTAL_ENTITY_COUNT__", totalEntityCount.ToString(CultureInfo.InvariantCulture));

    // CRITICAL: Replace __SCHEMA__ placeholder for infrastructure configuration call
    // Without this, ConfigureWhizbangInfrastructure receives literal "__SCHEMA__" string
    template = template.Replace("__SCHEMA__", schema ?? "public");

    context.AddSource("WhizbangModelBuilderExtensions.g.cs", template);
  }
}
