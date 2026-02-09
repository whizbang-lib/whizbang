using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Models;
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
    // Timeout added to prevent ReDoS attacks (S6444)
    lastSegment = System.Text.RegularExpressions.Regex.Replace(lastSegment, "Worker", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    lastSegment = System.Text.RegularExpressions.Regex.Replace(lastSegment, "Service", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

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

    // Extract physical fields from model type
    var physicalFields = _extractPhysicalFields(modelType as INamedTypeSymbol);

    return new PerspectiveInfo(
        ModelTypeName: modelType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        TableName: tableName,
        PhysicalFields: physicalFields
    );
  }

  private const string PHYSICAL_FIELD_ATTRIBUTE = "Whizbang.Core.Perspectives.PhysicalFieldAttribute";
  private const string VECTOR_FIELD_ATTRIBUTE = "Whizbang.Core.Perspectives.VectorFieldAttribute";

  /// <summary>
  /// Extracts physical field information from a model type.
  /// </summary>
  private static ImmutableArray<PhysicalFieldInfo> _extractPhysicalFields(INamedTypeSymbol? modelType) {
    if (modelType is null) {
      return ImmutableArray<PhysicalFieldInfo>.Empty;
    }

    var physicalFields = new List<PhysicalFieldInfo>();
    var properties = modelType.GetMembers()
        .OfType<IPropertySymbol>()
        .Where(p => !p.IsStatic);

    foreach (var property in properties) {
      var physicalFieldAttr = property.GetAttributes()
          .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == PHYSICAL_FIELD_ATTRIBUTE);

      var vectorFieldAttr = property.GetAttributes()
          .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == VECTOR_FIELD_ATTRIBUTE);

      if (physicalFieldAttr is not null) {
        var info = _extractPhysicalFieldInfo(property, physicalFieldAttr);
        if (info is not null) {
          physicalFields.Add(info);
        }
      } else if (vectorFieldAttr is not null) {
        var info = _extractVectorFieldInfo(property, vectorFieldAttr);
        if (info is not null) {
          physicalFields.Add(info);
        }
      }
    }

    return physicalFields.ToImmutableArray();
  }

  /// <summary>
  /// Extracts PhysicalFieldInfo from a [PhysicalField] attribute.
  /// </summary>
  private static PhysicalFieldInfo? _extractPhysicalFieldInfo(IPropertySymbol property, AttributeData attribute) {
    var propertyName = property.Name;
    var typeName = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    // Extract named arguments
    bool isIndexed = false;
    bool isUnique = false;
    int? maxLength = null;
    string? columnName = null;

    foreach (var namedArg in attribute.NamedArguments) {
      switch (namedArg.Key) {
        case "Indexed":
          isIndexed = namedArg.Value.Value is true;
          break;
        case "Unique":
          isUnique = namedArg.Value.Value is true;
          break;
        case "MaxLength":
          // Handle various numeric types - -1 or 0 means "not set" (unlimited TEXT)
          if (namedArg.Value.Kind == TypedConstantKind.Primitive && namedArg.Value.Value != null) {
            var maxLengthVal = System.Convert.ToInt32(namedArg.Value.Value, CultureInfo.InvariantCulture);
            if (maxLengthVal > 0) {
              maxLength = maxLengthVal;
            }
          }
          break;
        case "ColumnName":
          columnName = namedArg.Value.Value as string;
          break;
      }
    }

    // Default column name is snake_case of property name
    var finalColumnName = columnName ?? _toSnakeCase(propertyName);

    return new PhysicalFieldInfo(
        PropertyName: propertyName,
        ColumnName: finalColumnName,
        TypeName: typeName,
        IsIndexed: isIndexed,
        IsUnique: isUnique,
        MaxLength: maxLength,
        IsVector: false,
        VectorDimensions: null,
        VectorDistanceMetric: null,
        VectorIndexType: null,
        VectorIndexLists: null
    );
  }

  /// <summary>
  /// Extracts PhysicalFieldInfo from a [VectorField] attribute.
  /// </summary>
  private static PhysicalFieldInfo? _extractVectorFieldInfo(IPropertySymbol property, AttributeData attribute) {
    var propertyName = property.Name;
    var typeName = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    // Extract constructor argument (dimensions)
    int? dimensions = null;
    if (attribute.ConstructorArguments.Length > 0) {
      dimensions = attribute.ConstructorArguments[0].Value as int?;
    }

    // Extract named arguments
    var distanceMetric = GeneratorVectorDistanceMetric.Cosine; // Default
    var indexType = GeneratorVectorIndexType.IVFFlat; // Default
    var isIndexed = true; // Vectors are indexed by default
    string? columnName = null;
    int? indexLists = null;

    foreach (var namedArg in attribute.NamedArguments) {
      switch (namedArg.Key) {
        case "DistanceMetric":
          if (namedArg.Value.Value is int metricValue) {
            distanceMetric = (GeneratorVectorDistanceMetric)metricValue;
          }
          break;
        case "IndexType":
          if (namedArg.Value.Value is int indexTypeValue) {
            indexType = (GeneratorVectorIndexType)indexTypeValue;
          }
          break;
        case "Indexed":
          isIndexed = namedArg.Value.Value is true;
          break;
        case "ColumnName":
          columnName = namedArg.Value.Value as string;
          break;
        case "IndexLists":
          indexLists = namedArg.Value.Value as int?;
          break;
      }
    }

    // Default column name is snake_case of property name
    var finalColumnName = columnName ?? _toSnakeCase(propertyName);

    // If not indexed, set index type to None
    if (!isIndexed) {
      indexType = GeneratorVectorIndexType.None;
    }

    return new PhysicalFieldInfo(
        PropertyName: propertyName,
        ColumnName: finalColumnName,
        TypeName: typeName,
        IsIndexed: isIndexed,
        IsUnique: false, // Vectors are never unique
        MaxLength: null, // N/A for vectors
        IsVector: true,
        VectorDimensions: dimensions,
        VectorDistanceMetric: distanceMetric,
        VectorIndexType: indexType,
        VectorIndexLists: indexLists
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
  /// Generates EF Core shadow property configurations for physical fields.
  /// </summary>
  private static string _generatePhysicalFieldConfigurations(
      ImmutableArray<PhysicalFieldInfo> physicalFields,
      string tableName) {
    if (physicalFields.IsEmpty) {
      return string.Empty;
    }

    var sb = new StringBuilder();

    foreach (var field in physicalFields) {
      // Generate shadow property configuration
      var columnType = _getEFCoreColumnType(field);

      sb.AppendLine($"      // Physical field: {field.PropertyName}");
      sb.AppendLine($"      entity.Property<{_getCSharpType(field)}>(\"{field.ColumnName}\")");
      sb.AppendLine($"        .HasColumnName(\"{field.ColumnName}\")");
      sb.AppendLine($"        .HasColumnType(\"{columnType}\");");
      sb.AppendLine();

      // Generate index if configured
      if (field.IsIndexed && !field.IsVector) {
        var indexName = $"ix_{tableName}_{field.ColumnName}";
        if (field.IsUnique) {
          sb.AppendLine($"      entity.HasIndex(\"{field.ColumnName}\")");
          sb.AppendLine($"        .HasDatabaseName(\"{indexName}\")");
          sb.AppendLine($"        .IsUnique();");
        } else {
          sb.AppendLine($"      entity.HasIndex(\"{field.ColumnName}\")");
          sb.AppendLine($"        .HasDatabaseName(\"{indexName}\");");
        }
        sb.AppendLine();
      }

      // Generate vector index if configured
      if (field.IsVector && field.IsIndexed && field.VectorIndexType != GeneratorVectorIndexType.None) {
        var indexName = $"ix_{tableName}_{field.ColumnName}_vec";
        var indexMethod = field.VectorIndexType == GeneratorVectorIndexType.HNSW ? "hnsw" : "ivfflat";
        var opClass = _getVectorOperatorClass(field.VectorDistanceMetric);

        sb.AppendLine($"      entity.HasIndex(\"{field.ColumnName}\")");
        sb.AppendLine($"        .HasDatabaseName(\"{indexName}\")");
        sb.AppendLine($"        .HasMethod(\"{indexMethod}\")");
        sb.AppendLine($"        .HasOperators(\"{opClass}\");");
        sb.AppendLine();
      }
    }

    return sb.ToString();
  }

  /// <summary>
  /// Gets the EF Core column type for a physical field.
  /// </summary>
  private static string _getEFCoreColumnType(PhysicalFieldInfo field) {
    if (field.IsVector && field.VectorDimensions.HasValue) {
      return $"vector({field.VectorDimensions.Value})";
    }

    // Normalize the type name
    var typeName = field.TypeName
        .Replace("global::", "")
        .TrimEnd('?');

    return typeName switch {
      "System.String" or "string" => field.MaxLength.HasValue
          ? $"varchar({field.MaxLength.Value})"
          : "text",
      "System.Int32" or "int" => "integer",
      "System.Int64" or "long" => "bigint",
      "System.Int16" or "short" => "smallint",
      "System.Decimal" or "decimal" => "decimal",
      "System.Double" or "double" => "double precision",
      "System.Single" or "float" => "real",
      "System.Boolean" or "bool" => "boolean",
      "System.Guid" => "uuid",
      "System.DateTime" => "timestamp",
      "System.DateTimeOffset" => "timestamptz",
      "System.DateOnly" => "date",
      "System.TimeOnly" => "time",
      _ => "text" // Default fallback
    };
  }

  /// <summary>
  /// Gets the C# type for a physical field shadow property.
  /// </summary>
  private static string _getCSharpType(PhysicalFieldInfo field) {
    if (field.IsVector) {
      return "Pgvector.Vector";
    }

    // Return the normalized type
    var typeName = field.TypeName
        .Replace("global::", "");

    // Handle nullable types
    if (typeName.EndsWith("?", StringComparison.Ordinal)) {
      return typeName;
    }

    // Non-nullable value types that could be null in database
    return typeName switch {
      "System.Int32" or "int" => "int?",
      "System.Int64" or "long" => "long?",
      "System.Int16" or "short" => "short?",
      "System.Decimal" or "decimal" => "decimal?",
      "System.Double" or "double" => "double?",
      "System.Single" or "float" => "float?",
      "System.Boolean" or "bool" => "bool?",
      "System.Guid" => "System.Guid?",
      "System.DateTime" => "System.DateTime?",
      "System.DateTimeOffset" => "System.DateTimeOffset?",
      "System.DateOnly" => "System.DateOnly?",
      "System.TimeOnly" => "System.TimeOnly?",
      "System.String" or "string" => "string?",
      _ => $"{typeName}?"
    };
  }

  /// <summary>
  /// Gets the pgvector operator class for a distance metric.
  /// </summary>
  private static string _getVectorOperatorClass(GeneratorVectorDistanceMetric? metric) {
    return metric switch {
      GeneratorVectorDistanceMetric.L2 => "vector_l2_ops",
      GeneratorVectorDistanceMetric.InnerProduct => "vector_ip_ops",
      GeneratorVectorDistanceMetric.Cosine => "vector_cosine_ops",
      _ => "vector_cosine_ops" // Default to cosine
    };
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

        // Generate physical field configurations
        var physicalFieldConfigs = _generatePhysicalFieldConfigurations(perspective.PhysicalFields, perspective.TableName);

        var config = snippet
            .Replace("__MODEL_TYPE__", perspective.ModelTypeName)
            .Replace("__TABLE_NAME__", perspective.TableName)
            .Replace("__SCHEMA__", effectiveSchema)
            .Replace("__PHYSICAL_FIELD_CONFIGS__", physicalFieldConfigs);

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
