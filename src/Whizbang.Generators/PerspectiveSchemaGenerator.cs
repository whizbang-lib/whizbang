using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Models;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs:Generator_WithPerspective_GeneratesSchemaAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs:Generator_WithAbstractPerspective_SkipsSchemaAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs:Generator_WithMultiplePerspectives_GeneratesAllSchemasAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs:Generator_WithLargePerspective_GeneratesSizeWarningAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs:Generator_WithNoPerspectives_GeneratesNoOutputAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs:Generator_WithPerspective_GeneratesJSONBColumnsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs:Generator_WithPerspective_GeneratesUniversalColumnsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs:Generator_WithPerspective_GeneratesCorrectTableNameAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs:Generator_WithClassNoBaseList_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs:Generator_WithStaticProperties_ExcludesFromCountAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs:Generator_WithOnlyStaticProperties_GeneratesSchemaAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs:Generator_WithMultipleIPerspectiveInterfaces_GeneratesSchemaAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs:PerspectiveSchemaGenerator_LowercaseClassName_GeneratesTableNameWithoutLeadingUnderscoreAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs:PerspectiveSchemaGenerator_PerspectiveAtExactThreshold_GeneratesWarningAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs:PerspectiveSchemaGenerator_ClassWithBaseListButNotPerspective_SkipsAsync</tests>
/// Incremental source generator that discovers IPerspectiveFor implementations
/// and generates PostgreSQL table schemas with 3-column JSONB pattern.
/// Schemas use universal columns (id, created_at, updated_at, version) + JSONB (model_data, metadata, scope).
/// </summary>
[Generator]
public class PerspectiveSchemaGenerator : IIncrementalGenerator {
  private const int SIZE_WARNING_THRESHOLD = 1500; // Warn before hitting 2KB compression threshold

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Filter for classes that have a base list (potential interface implementations)
    var perspectiveCandidates = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractPerspectiveSchemaInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Collect all perspectives and generate schemas
    context.RegisterSourceOutput(
        perspectiveCandidates.Collect(),
        static (ctx, perspectives) => _generatePerspectiveSchemas(ctx, perspectives!)
    );
  }

  /// <summary>
  /// Extracts perspective schema information from a class declaration.
  /// Returns null if the class doesn't implement IPerspectiveFor.
  /// </summary>
  private static PerspectiveSchemaInfo? _extractPerspectiveSchemaInfo(
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

    // Look for IPerspectiveFor<TModel, TEvent1, ...> interfaces (all variants)
    // Check if interface name contains "IPerspectiveFor" (case-sensitive)
    var perspectiveInterfaces = classSymbol.AllInterfaces
        .Where(i => {
          var originalDef = i.OriginalDefinition.ToDisplayString();
          // Simple contains check to match any perspective interface
          return originalDef.Contains("IPerspectiveFor");
        })
        .ToList();

    if (perspectiveInterfaces.Count == 0) {
      return null;
    }

    // Verify perspective handles at least one event (not just marker interface)
    var hasEvents = perspectiveInterfaces.Any(i => i.TypeArguments.Length > 1);
    if (!hasEvents) {
      return null;
    }

    // Extract class name and generate table name
    var className = classSymbol.Name;
    var tableName = _generateTableName(className);

    // Estimate size based on properties in the MODEL type (first type argument)
    // For IPerspectiveFor<TModel, TEvent>, TModel is at index 0
    var modelType = perspectiveInterfaces[0].TypeArguments[0];
    var modelClassName = modelType.Name;
    var modelProperties = modelType.GetMembers()
        .OfType<IPropertySymbol>()
        .Where(p => !p.IsStatic)
        .ToList();

    var propertyCount = modelProperties.Count;
    var estimatedSize = _estimateJsonSize(propertyCount);

    // Extract storage mode from [PerspectiveStorage] attribute on model
    var storageMode = _extractStorageMode(modelType);

    // Discover physical fields on model properties
    var physicalFields = _discoverPhysicalFields(modelProperties);

    return new PerspectiveSchemaInfo(
        ClassName: className,
        FullyQualifiedClassName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        ModelClassName: modelClassName,
        TableName: tableName,
        PropertyCount: propertyCount,
        EstimatedSizeBytes: estimatedSize,
        StorageMode: storageMode,
        PhysicalFields: physicalFields
    );
  }

  /// <summary>
  /// Extracts the FieldStorageMode from [PerspectiveStorage] attribute on the model type.
  /// </summary>
  private static GeneratorFieldStorageMode _extractStorageMode(ITypeSymbol modelType) {
    const string PERSPECTIVE_STORAGE_ATTRIBUTE = "Whizbang.Core.Perspectives.PerspectiveStorageAttribute";

    foreach (var attribute in modelType.GetAttributes()) {
      var attrClassName = attribute.AttributeClass?.ToDisplayString();
      if (attrClassName == PERSPECTIVE_STORAGE_ATTRIBUTE && attribute.ConstructorArguments.Length > 0) {
        var modeArg = attribute.ConstructorArguments[0];
        if (modeArg.Value is int modeValue) {
          return (GeneratorFieldStorageMode)modeValue;
        }
      }
    }

    return GeneratorFieldStorageMode.JsonOnly;
  }

  /// <summary>
  /// Discovers physical fields from [PhysicalField] and [VectorField] attributes on model properties.
  /// </summary>
  private static PhysicalFieldInfo[] _discoverPhysicalFields(System.Collections.Generic.List<IPropertySymbol> properties) {
    const string PHYSICAL_FIELD_ATTRIBUTE = "Whizbang.Core.Perspectives.PhysicalFieldAttribute";
    const string VECTOR_FIELD_ATTRIBUTE = "Whizbang.Core.Perspectives.VectorFieldAttribute";

    var physicalFields = new System.Collections.Generic.List<PhysicalFieldInfo>();

    foreach (var property in properties) {
      foreach (var attribute in property.GetAttributes()) {
        var attrClassName = attribute.AttributeClass?.ToDisplayString();

        if (attrClassName == PHYSICAL_FIELD_ATTRIBUTE) {
          var fieldInfo = _extractPhysicalFieldInfo(property, attribute);
          if (fieldInfo != null) {
            physicalFields.Add(fieldInfo);
          }
        } else if (attrClassName == VECTOR_FIELD_ATTRIBUTE) {
          var fieldInfo = _extractVectorFieldInfo(property, attribute);
          if (fieldInfo != null) {
            physicalFields.Add(fieldInfo);
          }
        }
      }
    }

    return physicalFields.ToArray();
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
          // Handle various numeric types - TypedConstant may return int, long, short, etc.
          // -1 or 0 means "not set" (unlimited TEXT)
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
    var finalColumnName = columnName ?? _generateTableName(propertyName);

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
    bool isIndexed = true; // Default
    int? indexLists = null;
    string? columnName = null;

    foreach (var namedArg in attribute.NamedArguments) {
      switch (namedArg.Key) {
        case "DistanceMetric":
          var metricVal = namedArg.Value.Value;
          if (metricVal != null) {
            distanceMetric = (GeneratorVectorDistanceMetric)System.Convert.ToInt32(metricVal, CultureInfo.InvariantCulture);
          }
          break;
        case "IndexType":
          var typeVal = namedArg.Value.Value;
          if (typeVal != null) {
            indexType = (GeneratorVectorIndexType)System.Convert.ToInt32(typeVal, CultureInfo.InvariantCulture);
          }
          break;
        case "Indexed":
          isIndexed = namedArg.Value.Value is true;
          break;
        case "IndexLists":
          var indexListsVal = namedArg.Value.Value;
          if (indexListsVal != null) {
            indexLists = System.Convert.ToInt32(indexListsVal, CultureInfo.InvariantCulture);
          }
          break;
        case "ColumnName":
          columnName = namedArg.Value.Value as string;
          break;
      }
    }

    // Default column name is snake_case of property name
    var finalColumnName = columnName ?? _generateTableName(propertyName);

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
  /// Generates PostgreSQL schema CREATE TABLE statements for all discovered perspectives.
  /// </summary>
  private static void _generatePerspectiveSchemas(
      SourceProductionContext context,
      ImmutableArray<PerspectiveSchemaInfo> perspectives) {

    if (perspectives.IsEmpty) {
      return;
    }

    // Load SQL snippets (SQL doesn't fit the C# template pattern, so we use snippets only)
    var createTableSnippet = TemplateUtilities.ExtractSnippet(
        typeof(PerspectiveSchemaGenerator).Assembly,
        "PerspectiveSchemaSnippets.sql",
        "CREATE_TABLE_SNIPPET");

    var createIndexesSnippet = TemplateUtilities.ExtractSnippet(
        typeof(PerspectiveSchemaGenerator).Assembly,
        "PerspectiveSchemaSnippets.sql",
        "CREATE_INDEXES_SNIPPET");

    // Build SQL content
    var sqlBuilder = new StringBuilder();
    sqlBuilder.AppendLine("-- Whizbang Perspective Tables - Auto-Generated");
    sqlBuilder.AppendLine("-- 3-Column JSONB Pattern: model_data (projection state), metadata (correlation/causation), scope (tenant/user)");
    sqlBuilder.AppendLine();

    foreach (var perspective in perspectives) {
      // Report size warning if estimated size is large
      if (perspective.EstimatedSizeBytes >= SIZE_WARNING_THRESHOLD) {
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.PerspectiveSizeWarning,
            Location.None,
            perspective.ClassName,
            perspective.EstimatedSizeBytes
        ));
      }

      // Report physical fields discovered
      if (perspective.PhysicalFields.Length > 0) {
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.PhysicalFieldsDiscovered,
            Location.None,
            perspective.ModelClassName,
            perspective.PhysicalFields.Length.ToString(CultureInfo.InvariantCulture),
            perspective.StorageMode.ToString()
        ));
      }

      // Generate physical column definitions
      var physicalColumnsSql = _generatePhysicalColumnsSql(perspective.PhysicalFields);

      // Generate CREATE TABLE from snippet
      var tableCode = createTableSnippet
          .Replace("__CLASS_NAME__", perspective.ClassName)
          .Replace("__ESTIMATED_SIZE__", perspective.EstimatedSizeBytes.ToString(CultureInfo.InvariantCulture))
          .Replace("__TABLE_NAME__", perspective.TableName);

      // Insert physical columns before closing parenthesis of CREATE TABLE
      if (!string.IsNullOrEmpty(physicalColumnsSql)) {
        // Find the position to insert: before the final ");"
        var insertPos = tableCode.LastIndexOf(");", StringComparison.Ordinal);
        if (insertPos > 0) {
          // Add physical columns with proper comma separation
          tableCode = tableCode.Substring(0, insertPos) + ",\n" + physicalColumnsSql + "\n" + tableCode.Substring(insertPos);
        }
      }

      sqlBuilder.AppendLine(tableCode);
      sqlBuilder.AppendLine();

      // Generate standard indexes from snippet
      var indexesCode = createIndexesSnippet
          .Replace("__TABLE_NAME__", perspective.TableName);

      sqlBuilder.AppendLine(indexesCode);

      // Generate physical field indexes
      var physicalIndexesSql = _generatePhysicalIndexesSql(perspective.TableName, perspective.PhysicalFields);
      if (!string.IsNullOrEmpty(physicalIndexesSql)) {
        sqlBuilder.AppendLine(physicalIndexesSql);
      }

      sqlBuilder.AppendLine();
    }

    // Wrap SQL in C# class with embedded resource
    var schemaBuilder = new StringBuilder();
    schemaBuilder.AppendLine("// <auto-generated/>");
    schemaBuilder.AppendLine("#nullable enable");
    schemaBuilder.AppendLine();
    schemaBuilder.AppendLine("namespace Whizbang.Generated;");
    schemaBuilder.AppendLine();
    schemaBuilder.AppendLine("/// <summary>");
    schemaBuilder.AppendLine("/// Generated PostgreSQL schemas for Whizbang perspectives.");
    schemaBuilder.AppendLine("/// </summary>");
    schemaBuilder.AppendLine("internal static class PerspectiveSchemas");
    schemaBuilder.AppendLine("{");
    schemaBuilder.AppendLine("    /// <summary>");
    schemaBuilder.AppendLine("    /// SQL DDL for creating perspective tables.");
    schemaBuilder.AppendLine("    /// </summary>");
    schemaBuilder.AppendLine("    public const string Sql = @\"");
    schemaBuilder.Append(sqlBuilder.ToString().Replace("\"", "\"\""));  // Escape quotes for verbatim string
    schemaBuilder.AppendLine("\";");
    schemaBuilder.AppendLine("}");

    // Add source file as C# code
    context.AddSource("PerspectiveSchemas.g.sql.cs", schemaBuilder.ToString());

    // Report summary
    context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.PerspectiveDiscovered,
        Location.None,
        perspectives.Length.ToString(CultureInfo.InvariantCulture),
        string.Join(", ", perspectives.Select(p => p.ClassName))
    ));
  }

  /// <summary>
  /// Generates SQL column definitions for physical fields.
  /// </summary>
  private static string _generatePhysicalColumnsSql(PhysicalFieldInfo[] physicalFields) {
    if (physicalFields.Length == 0) {
      return string.Empty;
    }

    var sb = new StringBuilder();
    for (int i = 0; i < physicalFields.Length; i++) {
      var field = physicalFields[i];
      var sqlType = _mapToPostgresType(field);

      sb.Append("  ");
      sb.Append(field.ColumnName);
      sb.Append(' ');
      sb.Append(sqlType);

      if (i < physicalFields.Length - 1) {
        sb.Append(',');
      }
      sb.AppendLine();
    }

    return sb.ToString().TrimEnd('\r', '\n');
  }

  /// <summary>
  /// Maps a physical field to its PostgreSQL column type.
  /// </summary>
  private static string _mapToPostgresType(PhysicalFieldInfo field) {
    if (field.IsVector && field.VectorDimensions.HasValue) {
      return $"vector({field.VectorDimensions.Value})";
    }

    // Normalize the type name by removing global:: and nullable markers
    var typeName = field.TypeName
        .Replace("global::", "")
        .TrimEnd('?');

    return typeName switch {
      "System.String" or "string" => field.MaxLength.HasValue
          ? $"VARCHAR({field.MaxLength.Value})"
          : "TEXT",
      "System.Int32" or "int" => "INTEGER",
      "System.Int64" or "long" => "BIGINT",
      "System.Int16" or "short" => "SMALLINT",
      "System.Decimal" or "decimal" => "DECIMAL",
      "System.Double" or "double" => "DOUBLE PRECISION",
      "System.Single" or "float" => "REAL",
      "System.Boolean" or "bool" => "BOOLEAN",
      "System.Guid" => "UUID",
      "System.DateTime" => "TIMESTAMP",
      "System.DateTimeOffset" => "TIMESTAMPTZ",
      "System.DateOnly" => "DATE",
      "System.TimeOnly" => "TIME",
      "System.Single[]" or "float[]" => "REAL[]", // fallback for float[] without VectorField
      _ => "TEXT" // Default fallback
    };
  }

  /// <summary>
  /// Generates SQL index definitions for physical fields.
  /// </summary>
  private static string _generatePhysicalIndexesSql(string tableName, PhysicalFieldInfo[] physicalFields) {
    var sb = new StringBuilder();

    foreach (var field in physicalFields) {
      if (!field.IsIndexed && !field.IsUnique) {
        continue;
      }

      if (field.IsVector) {
        // Generate vector index
        var indexSql = _generateVectorIndexSql(tableName, field);
        if (!string.IsNullOrEmpty(indexSql)) {
          sb.AppendLine(indexSql);
        }
      } else {
        // Generate standard B-tree index
        var indexName = $"ix_{tableName}_{field.ColumnName}";
        var uniqueClause = field.IsUnique ? "UNIQUE " : "";
        sb.AppendLine($"CREATE {uniqueClause}INDEX IF NOT EXISTS {indexName} ON {tableName}({field.ColumnName});");
      }
    }

    return sb.ToString().TrimEnd('\r', '\n');
  }

  /// <summary>
  /// Generates a pgvector index SQL statement.
  /// </summary>
  private static string _generateVectorIndexSql(string tableName, PhysicalFieldInfo field) {
    if (!field.IsVector || field.VectorIndexType == GeneratorVectorIndexType.None) {
      return string.Empty;
    }

    var indexName = $"ix_{tableName}_{field.ColumnName}_vec";
    var indexMethod = field.VectorIndexType switch {
      GeneratorVectorIndexType.HNSW => "hnsw",
      GeneratorVectorIndexType.IVFFlat => "ivfflat",
      _ => null
    };

    if (indexMethod == null) {
      return string.Empty;
    }

    var opsClass = field.VectorDistanceMetric switch {
      GeneratorVectorDistanceMetric.L2 => "vector_l2_ops",
      GeneratorVectorDistanceMetric.InnerProduct => "vector_ip_ops",
      GeneratorVectorDistanceMetric.Cosine => "vector_cosine_ops",
      _ => "vector_cosine_ops" // Default to cosine
    };

    // Build WITH clause for index parameters
    var withClause = "";
    if (field.VectorIndexType == GeneratorVectorIndexType.IVFFlat && field.VectorIndexLists.HasValue) {
      withClause = $" WITH (lists = {field.VectorIndexLists.Value})";
    }

    return $"CREATE INDEX IF NOT EXISTS {indexName} ON {tableName} USING {indexMethod} ({field.ColumnName} {opsClass}){withClause};";
  }

  /// <summary>
  /// Generates a snake_case table name from a PascalCase class name.
  /// Example: "OrderSummaryPerspective" -> "order_summary_perspective"
  /// </summary>
  private static string _generateTableName(string className) {
    var sb = new StringBuilder();
    for (int i = 0; i < className.Length; i++) {
      var c = className[i];
      if (i > 0 && char.IsUpper(c)) {
        sb.Append('_');
      }
      sb.Append(char.ToLowerInvariant(c));
    }
    return sb.ToString();
  }

  /// <summary>
  /// Estimates JSON size based on property count (rough heuristic).
  /// Assumes average property: {"propertyName": "averageValue"} ~= 40 bytes
  /// </summary>
  private static int _estimateJsonSize(int propertyCount) {
    const int BYTES_PER_PROPERTY = 40; // Rough average
    const int BASE_OVERHEAD = 20; // JSON object overhead
    return BASE_OVERHEAD + (propertyCount * BYTES_PER_PROPERTY);
  }
}

/// <summary>
/// Value type containing schema information about a discovered perspective.
/// Uses value equality for incremental generator caching.
/// </summary>
/// <param name="ClassName">Simple class name (e.g., "OrderSummaryPerspective")</param>
/// <param name="FullyQualifiedClassName">Fully qualified class name</param>
/// <param name="ModelClassName">Simple class name of the model type</param>
/// <param name="TableName">Generated PostgreSQL table name (e.g., "order_summary_perspective")</param>
/// <param name="PropertyCount">Number of properties for size estimation</param>
/// <param name="EstimatedSizeBytes">Estimated JSON size in bytes</param>
/// <param name="StorageMode">Field storage mode from [PerspectiveStorage] attribute</param>
/// <param name="PhysicalFields">Array of physical fields discovered on the model</param>
internal sealed record PerspectiveSchemaInfo(
    string ClassName,
    string FullyQualifiedClassName,
    string ModelClassName,
    string TableName,
    int PropertyCount,
    int EstimatedSizeBytes,
    GeneratorFieldStorageMode StorageMode,
    PhysicalFieldInfo[] PhysicalFields
);

/// <summary>
/// Field storage mode for physical fields in a perspective.
/// Mirrors Whizbang.Core.Perspectives.FieldStorageMode for generator use.
/// </summary>
public enum GeneratorFieldStorageMode {
  /// <summary>No physical columns - all data in JSONB (default, backwards compatible)</summary>
  JsonOnly = 0,

  /// <summary>JSONB contains full model; physical columns are indexed copies</summary>
  Extracted = 1,

  /// <summary>Physical columns contain marked fields; JSONB contains remainder only</summary>
  Split = 2
}
