using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Data.EFCore.Postgres.Generators.Limits;
using Whizbang.Generators.Shared.Limits;
using Whizbang.Generators.Shared.Models;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Data.EFCore.Postgres.Generators;

/// <summary>
/// Source generator that discovers Perspective implementations and generates EF Core ModelBuilder setup.
/// Generates a ConfigureWhizbang() extension method that configures:
/// - PerspectiveRow&lt;TModel&gt; entities (discovered from IPerspectiveFor&lt;TModel&gt; perspectives)
/// - InboxRecord, OutboxRecord, EventStoreRecord, ServiceInstanceRecord (fixed Whizbang entities)
/// Uses EF Core 10 ComplexProperty().ToJson() for JSONB columns (Postgres).
/// Table names are configurable via MSBuild properties:
/// - WhizbangStripTableNameSuffixes (default: true) - Strip common suffixes like Model, Projection, Dto
/// - WhizbangTableNameSuffixesToStrip (default: ReadModel,Model,Projection,Dto,View) - Suffixes to strip
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
    // Read table name configuration from MSBuild properties
    var tableNameConfig = context.AnalyzerConfigOptionsProvider.Select(
        ConfigurationUtilities.SelectTableNameConfig
    );

    // Read optional max identifier length override from MSBuild properties
    var maxIdLengthOverride = context.AnalyzerConfigOptionsProvider.Select(
        ConfigurationUtilities.SelectMaxIdentifierLengthOverride
    );

    // Discover classes implementing IPerspectiveFor<TModel>
    var perspectiveCandidates = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractPerspectiveCandidate(ctx, ct)
    ).Where(static info => info is not null);

    // Discover DbContexts with [WhizbangDbContext] attribute to extract schema
    var dbContextClasses = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0, BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractDbContextSchema(ctx, ct)
    ).Where(static schema => schema is not null);

    // Combine perspective candidates with table name configuration and max identifier length override
    var perspectivesWithConfig = perspectiveCandidates.Collect()
        .Combine(tableNameConfig)
        .Combine(maxIdLengthOverride);

    //  Combine with DbContext schema and compilation
    var allData = perspectivesWithConfig
        .Combine(dbContextClasses.Collect())
        .Combine(context.CompilationProvider);

    // Generate ModelBuilder extension method with all Whizbang entities
    context.RegisterSourceOutput(
        allData,
        static (ctx, data) => {
          var candidates = data.Left.Left.Left.Left;
          var config = data.Left.Left.Left.Right;
          var maxIdOverride = data.Left.Left.Right;
          var dbContextSchemas = data.Left.Right;
          var compilation = data.Right;

          // Skip generation if this IS the library project itself
          // The library should not have this class baked in - only consuming projects should
          if (compilation.AssemblyName == "Whizbang.Data.EFCore.Postgres") {
            return;
          }

          // Get provider limits - use override if configured, otherwise PostgreSQL defaults
          IDbProviderLimits limits = maxIdOverride.HasValue
              ? new OverriddenPostgresLimits(maxIdOverride.Value)
              : PostgresLimits.Instance;

          // Build PerspectiveInfo with table names using config
          var allPerspectives = candidates
              .Where(c => c is not null)
              .Select(c => _buildPerspectiveInfo(c!, config))
              .ToList();

          // Validate identifier lengths and report diagnostics
          var validPerspectives = new List<PerspectiveInfo>();
          foreach (var perspective in allPerspectives) {
            var hasError = false;

            // Validate table name
            var tableError = IdentifierValidation.ValidateTableName(perspective.TableName, limits);
            if (tableError is not null) {
              ctx.ReportDiagnostic(Diagnostic.Create(
                  DiagnosticDescriptors.TableNameExceedsLimit,
                  Location.None,
                  perspective.ModelTypeName,
                  perspective.TableName,
                  IdentifierValidation.GetByteCount(perspective.TableName),
                  limits.ProviderName,
                  limits.MaxTableNameBytes
              ));
              hasError = true;
            }

            // Validate physical field column and index names
            foreach (var field in perspective.PhysicalFields) {
              var columnError = IdentifierValidation.ValidateColumnName(field.ColumnName, limits);
              if (columnError is not null) {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.ColumnNameExceedsLimit,
                    Location.None,
                    field.PropertyName,
                    perspective.ModelTypeName,
                    field.ColumnName,
                    IdentifierValidation.GetByteCount(field.ColumnName),
                    limits.ProviderName,
                    limits.MaxColumnNameBytes
                ));
                hasError = true;
              }

              // Validate index name if indexed
              if (field.IsIndexed || field.IsUnique) {
                var indexName = $"ix_{perspective.TableName}_{field.ColumnName}";
                var indexError = IdentifierValidation.ValidateIndexName(indexName, limits);
                if (indexError is not null) {
                  ctx.ReportDiagnostic(Diagnostic.Create(
                      DiagnosticDescriptors.IndexNameExceedsLimit,
                      Location.None,
                      indexName,
                      field.PropertyName,
                      perspective.ModelTypeName,
                      IdentifierValidation.GetByteCount(indexName),
                      limits.ProviderName,
                      limits.MaxIndexNameBytes
                  ));
                  hasError = true;
                }
              }
            }

            if (!hasError) {
              validPerspectives.Add(perspective);
            }
          }

          var perspectives = validPerspectives.ToImmutableArray();

          // Extract schema from first DbContext (typically one per project)
          // If no DbContext found or no schema specified, defaults to null and generator will derive from namespace
          string? schema = dbContextSchemas.IsEmpty ? null : dbContextSchemas[0];

          _generateModelBuilderExtension(ctx, perspectives, schema);
        }
    );
  }

  /// <summary>
  /// Helper class for overriding PostgreSQL limits via MSBuild property.
  /// </summary>
  private sealed class OverriddenPostgresLimits : IDbProviderLimits {
    private readonly int _maxLength;

    public OverriddenPostgresLimits(int maxLength) {
      _maxLength = maxLength;
    }

    public int MaxTableNameBytes => _maxLength;
    public int MaxColumnNameBytes => _maxLength;
    public int MaxIndexNameBytes => _maxLength;
    public string ProviderName => $"PostgreSQL (override: {_maxLength})";
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
  /// Extracts perspective candidate information from a class implementing IPerspectiveFor.
  /// Discovers TModel type from IPerspectiveFor&lt;TModel&gt; base interface (first type argument).
  /// Returns null if the class doesn't implement the interface.
  /// Does not apply table name configuration - that happens in _buildPerspectiveInfo.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_ReportsCorrectPerspectiveCountAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:LogDiscoveryDiagnostics_OutputsPerspectiveDetailsAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:GeneratedDiagnostics_DeduplicatesPerspectivesAsync</tests>
  private static PerspectiveCandidate? _extractPerspectiveCandidate(
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
    // Use GetTableBaseName to handle nested types correctly (e.g., ActiveJobTemplate.Model -> ActiveJobTemplateModel)
    var tableBaseName = TypeNameUtilities.GetTableBaseName(modelType);

    // Extract physical fields from model type
    var physicalFields = _extractPhysicalFields(modelType as INamedTypeSymbol);

    // Detect polymorphic properties in model type
    var hasPolymorphicProperties = _hasPolymorphicProperties(modelType as INamedTypeSymbol);

    return new PerspectiveCandidate(
        ModelTypeName: modelType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        TableBaseName: tableBaseName,
        PhysicalFields: physicalFields,
        HasPolymorphicProperties: hasPolymorphicProperties
    );
  }

  /// <summary>
  /// Builds the final PerspectiveInfo from a candidate by applying table name configuration.
  /// </summary>
  private static PerspectiveInfo _buildPerspectiveInfo(
      PerspectiveCandidate candidate,
      TableNameConfig config) {

    // Generate table name using shared utility with configurable suffix stripping
    var tableName = NamingConventionUtilities.GenerateTableName(candidate.TableBaseName, config);

    return new PerspectiveInfo(
        ModelTypeName: candidate.ModelTypeName,
        TableName: tableName,
        PhysicalFields: candidate.PhysicalFields,
        HasPolymorphicProperties: candidate.HasPolymorphicProperties
    );
  }

  private const string PHYSICAL_FIELD_ATTRIBUTE = "Whizbang.Core.Perspectives.PhysicalFieldAttribute";
  private const string VECTOR_FIELD_ATTRIBUTE = "Whizbang.Core.Perspectives.VectorFieldAttribute";
  private const string POLYMORPHIC_DISCRIMINATOR_ATTRIBUTE = "Whizbang.Core.Perspectives.PolymorphicDiscriminatorAttribute";
  private const string JSON_POLYMORPHIC_ATTRIBUTE = "System.Text.Json.Serialization.JsonPolymorphicAttribute";

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

      var polymorphicDiscriminatorAttr = property.GetAttributes()
          .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == POLYMORPHIC_DISCRIMINATOR_ATTRIBUTE);

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
      } else if (polymorphicDiscriminatorAttr is not null) {
        var info = _extractPolymorphicDiscriminatorInfo(property, polymorphicDiscriminatorAttr);
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
    var finalColumnName = columnName ?? NamingConventionUtilities.ToSnakeCase(propertyName);

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
    var finalColumnName = columnName ?? NamingConventionUtilities.ToSnakeCase(propertyName);

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
  /// Extracts PhysicalFieldInfo from a [PolymorphicDiscriminator] attribute.
  /// Polymorphic discriminators are always string columns with an index for efficient queries.
  /// </summary>
  private static PhysicalFieldInfo? _extractPolymorphicDiscriminatorInfo(
      IPropertySymbol property,
      AttributeData attribute) {
    var propertyName = property.Name;

    // Polymorphic discriminators are always strings (type name discriminator)
    const string typeName = "global::System.String";

    // Extract named arguments
    string? columnName = null;

    foreach (var namedArg in attribute.NamedArguments) {
      if (namedArg.Key == "ColumnName") {
        columnName = namedArg.Value.Value as string;
      }
    }

    // Default column name is snake_case of property name
    var finalColumnName = columnName ?? NamingConventionUtilities.ToSnakeCase(propertyName);

    return new PhysicalFieldInfo(
        PropertyName: propertyName,
        ColumnName: finalColumnName,
        TypeName: typeName,
        IsIndexed: true, // Discriminators are always indexed for efficient queries
        IsUnique: false, // Discriminators are not unique (many rows can have same type)
        MaxLength: null, // No max length - TEXT type for full type names
        IsVector: false,
        VectorDimensions: null,
        VectorDistanceMetric: null,
        VectorIndexType: null,
        VectorIndexLists: null
    );
  }

  /// <summary>
  /// Checks if a model type contains any polymorphic properties (abstract types or [JsonPolymorphic] types).
  /// </summary>
  private static bool _hasPolymorphicProperties(INamedTypeSymbol? modelType) {
    if (modelType is null) {
      return false;
    }

    var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
    return _checkForPolymorphicTypes(modelType, visited);
  }

  /// <summary>
  /// Recursively checks if a type or its nested types contain polymorphic properties.
  /// </summary>
  private static bool _checkForPolymorphicTypes(INamedTypeSymbol type, HashSet<INamedTypeSymbol> visited) {
    // Cycle detection
    if (!visited.Add(type)) {
      return false;
    }

    // Skip system types (except System.Collections)
    var ns = type.ContainingNamespace?.ToDisplayString();
    if (ns != null && ns.StartsWith("System", StringComparison.Ordinal) &&
        !ns.StartsWith("System.Collections", StringComparison.Ordinal)) {
      return false;
    }

    var properties = type.GetMembers()
        .OfType<IPropertySymbol>()
        .Where(p => !p.IsStatic && !p.IsIndexer && !p.IsWriteOnly);

    foreach (var property in properties) {
      // Skip ignored properties
      if (_isPropertyIgnored(property)) {
        continue;
      }

      var propType = property.Type as INamedTypeSymbol;
      if (propType == null) {
        continue;
      }

      // Get the element type if this is a collection
      var elementType = _getCollectionElementType(propType);
      var typeToCheck = elementType ?? propType;

      // Check if this type is polymorphic
      if (_isPolymorphicType(typeToCheck)) {
        return true;
      }

      // Recursively check nested types
      if ((typeToCheck.TypeKind == TypeKind.Class || typeToCheck.TypeKind == TypeKind.Struct) &&
          !_isSystemPrimitiveType(typeToCheck) &&
          _checkForPolymorphicTypes(typeToCheck, visited)) {
        return true;
      }

      // Check generic type arguments
      foreach (var typeArg in propType.TypeArguments.OfType<INamedTypeSymbol>()) {
        if (_isPolymorphicType(typeArg)) {
          return true;
        }
        if (!_isSystemPrimitiveType(typeArg) && _checkForPolymorphicTypes(typeArg, visited)) {
          return true;
        }
      }
    }

    return false;
  }

  /// <summary>
  /// Checks if a type is polymorphic (abstract class or has [JsonPolymorphic] attribute).
  /// </summary>
  private static bool _isPolymorphicType(INamedTypeSymbol type) {
    // Check if type is an abstract class
    if (type.IsAbstract && type.TypeKind == TypeKind.Class) {
      return true;
    }

    // Check for [JsonPolymorphic] attribute
    foreach (var attr in type.GetAttributes()) {
      if (attr.AttributeClass?.ToDisplayString() == JSON_POLYMORPHIC_ATTRIBUTE) {
        return true;
      }
    }

    return false;
  }

  /// <summary>
  /// Checks if a property is marked as ignored by EF Core or JSON serialization.
  /// </summary>
  private static bool _isPropertyIgnored(IPropertySymbol property) {
    foreach (var attr in property.GetAttributes()) {
      var attrName = attr.AttributeClass?.ToDisplayString();
      if (attrName == "System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute" ||
          attrName == "System.Text.Json.Serialization.JsonIgnoreAttribute" ||
          attrName == "Newtonsoft.Json.JsonIgnoreAttribute") {
        return true;
      }
    }
    return false;
  }

  /// <summary>
  /// Gets the element type if the type is a collection (List, IEnumerable, array, etc.).
  /// </summary>
  private static INamedTypeSymbol? _getCollectionElementType(INamedTypeSymbol type) {
    // Check for generic collection types
    if (!type.IsGenericType || type.TypeArguments.Length == 0) {
      return null;
    }

    var originalDef = type.ConstructedFrom.ToDisplayString();

    // Common collection interfaces and types
    if (originalDef.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Generic.IList<", StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Generic.ICollection<", StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Generic.IEnumerable<", StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Generic.IReadOnlyList<", StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Generic.IReadOnlyCollection<", StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Immutable.ImmutableList<", StringComparison.Ordinal) ||
        originalDef.StartsWith("System.Collections.Immutable.ImmutableArray<", StringComparison.Ordinal)) {
      return type.TypeArguments[0] as INamedTypeSymbol;
    }

    return null;
  }

  /// <summary>
  /// Checks if a type is a system primitive type that won't contain polymorphic properties.
  /// </summary>
  private static bool _isSystemPrimitiveType(INamedTypeSymbol type) {
    var ns = type.ContainingNamespace?.ToDisplayString();
    if (ns == "System") {
      var name = type.Name;
      return name is "String" or "DateTime" or "DateTimeOffset" or "TimeSpan" or
             "Guid" or "Decimal" or "Uri" or "Version" or "DateOnly" or "TimeOnly";
    }
    return false;
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
          sb.AppendLine("        .IsUnique();");
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
        // Extract perspective entity config snippet - use polymorphic version if model has polymorphic types
        var snippetName = perspective.HasPolymorphicProperties
            ? "PERSPECTIVE_ENTITY_CONFIG_POLYMORPHIC_SNIPPET"
            : "PERSPECTIVE_ENTITY_CONFIG_SNIPPET";
        var snippet = TemplateUtilities.ExtractSnippet(
            assembly,
            "EFCoreSnippets.cs",
            snippetName,
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

    // Check if any perspective has vector fields - if so, generate HasPostgresExtension("vector")
    // This ensures the pgvector extension is automatically created in the database
    var hasVectorFields = uniquePerspectives.Any(p => p.PhysicalFields.Any(f => f.IsVector));
    var vectorExtensionConfig = hasVectorFields
        ? "    // Auto-configured: pgvector extension required for [VectorField] columns\n    modelBuilder.HasPostgresExtension(\"vector\");\n"
        : "// No vector fields detected - pgvector extension not required\n";
    template = TemplateUtilities.ReplaceRegion(template, "VECTOR_EXTENSION_CONFIG", vectorExtensionConfig);

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
