// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs:Generator_WithPerspective_GeneratesEntriesArrayAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs:Generator_WithMultiplePerspectives_GeneratesEntriesForEachAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs:Generator_EntriesSqlMatchesConcatenatedSqlAsync</tests>
/// Incremental source generator that discovers IPerspectiveFor implementations
/// and generates PostgreSQL table schemas with 3-column JSONB pattern.
/// Schemas use universal columns (id, created_at, updated_at, version) + JSONB (data, metadata, scope).
/// Table names are configurable via MSBuild properties:
/// - WhizbangStripTableNameSuffixes (default: true) - Strip common suffixes like Model, Projection, Dto
/// - WhizbangTableNameSuffixesToStrip (default: ReadModel,Model,Projection,Dto,View) - Suffixes to strip
/// </summary>
[Generator]
public class PerspectiveSchemaGenerator : IIncrementalGenerator {
  private const int SIZE_WARNING_THRESHOLD = 1500; // Warn before hitting 2KB compression threshold

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Read table name configuration from MSBuild properties
    var tableNameConfig = context.AnalyzerConfigOptionsProvider.Select(
        ConfigurationUtilities.SelectTableNameConfig
    );

    // Filter for classes that have a base list (potential interface implementations)
    var perspectiveCandidates = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractPerspectiveCandidate(ctx, ct)
    ).Where(static info => info is not null);

    // Combine perspective candidates with table name configuration
    var perspectivesWithConfig = perspectiveCandidates.Collect().Combine(tableNameConfig);

    // Collect all perspectives and generate schemas
    context.RegisterSourceOutput(
        perspectivesWithConfig,
        static (ctx, data) => {
          var (candidates, config) = data;
          var perspectives = candidates
              .Where(c => c is not null)
              .Select(c => _buildPerspectiveSchemaInfo(c!, config))
              .ToImmutableArray();
          _generatePerspectiveSchemas(ctx, perspectives);
        }
    );
  }

  /// <summary>
  /// Extracts perspective candidate information from a class declaration.
  /// Returns null if the class doesn't implement IPerspectiveFor.
  /// Does not apply table name configuration - that happens in _buildPerspectiveSchemaInfo.
  /// </summary>
  private static PerspectiveCandidate? _extractPerspectiveCandidate(
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
          var originalDef = TypeNameUtilities.Display(i.OriginalDefinition);
          // Match IPerspectiveBase — unified marker for all perspective types
          return originalDef.Contains("IPerspectiveBase");
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

    // Extract class name and table base name
    // Use CLR type name to handle nested classes correctly (e.g., "Activity+Projection")
    var clrTypeName = TypeNameUtilities.BuildClrTypeName(classSymbol);
    // Extract simple name for display (last part after last + or .)
    var className = clrTypeName.Contains('+')
        ? clrTypeName[(clrTypeName.LastIndexOf('+') + 1)..]
        : clrTypeName[(clrTypeName.LastIndexOf('.') + 1)..];
    // Extract table base name from CLR name (remove + to merge nested names)
    // This ensures nested classes get unique table names: Activity+Projection → ActivityProjection
    var tableBaseName = clrTypeName[(clrTypeName.LastIndexOf('.') + 1)..].Replace("+", "");

    // Estimate size based on properties in the MODEL type (first type argument)
    // For IPerspectiveFor<TModel, TEvent>, TModel is at index 0
    var modelType = perspectiveInterfaces[0].TypeArguments[0];
    var modelClassName = modelType.Name;
    // Use shared utility to include inherited properties from base model classes. A model type that
    // is NOT a named type — an array (IPerspectiveFor<TModel> only constrains TModel to `class`, which
    // an array satisfies), a type parameter — declares no member symbols at all in Roslyn, so there is
    // nothing to enumerate and the row is sized at the base JSON overhead alone.
    var modelProperties = modelType is INamedTypeSymbol namedModelType
        ? namedModelType.GetAllProperties().ToList()
        : [];

    var propertyCount = modelProperties.Count;
    var estimatedSize = _estimateJsonSize(propertyCount);

    // Extract storage mode from [PerspectiveStorage] attribute on model
    var storageMode = _extractStorageMode(modelType);

    // Discover physical fields on model properties
    var physicalFields = _discoverPhysicalFields(modelProperties);

    return new PerspectiveCandidate(
        ClassName: className,
        FullyQualifiedClassName: TypeNameUtilities.FullyQualified(classSymbol),
        ModelClassName: modelClassName,
        TableBaseName: tableBaseName,
        PropertyCount: propertyCount,
        EstimatedSizeBytes: estimatedSize,
        StorageMode: storageMode,
        PhysicalFields: physicalFields,
        DocumentProperties: [.. DocumentPropertyDiscovery.From(modelType as INamedTypeSymbol)],
        BuildsMetadataIndex: PerspectiveQueriesDiscovery.From(modelType as INamedTypeSymbol).BuildsMetadataIndex,
        TableStorage: TableStorageInfo.From(modelType)
    );
  }

  /// <summary>
  /// Builds the final PerspectiveSchemaInfo from a candidate by applying table name configuration.
  /// </summary>
  private static PerspectiveSchemaInfo _buildPerspectiveSchemaInfo(
      PerspectiveCandidate candidate,
      TableNameConfig config) {

    // Generate table name using shared utility with configurable suffix stripping
    var tableName = NamingConventionUtilities.GenerateTableName(candidate.TableBaseName, config);

    return new PerspectiveSchemaInfo(
        ClassName: candidate.ClassName,
        FullyQualifiedClassName: candidate.FullyQualifiedClassName,
        ModelClassName: candidate.ModelClassName,
        TableName: tableName,
        PropertyCount: candidate.PropertyCount,
        EstimatedSizeBytes: candidate.EstimatedSizeBytes,
        StorageMode: candidate.StorageMode,
        PhysicalFields: candidate.PhysicalFields,
        DocumentProperties: candidate.DocumentProperties,
        BuildsMetadataIndex: candidate.BuildsMetadataIndex,
        TableStorage: candidate.TableStorage
    );
  }

  /// <summary>
  /// Extracts the FieldStorageMode from [PerspectiveStorage] attribute on the model type.
  /// </summary>
  private static GeneratorFieldStorageMode _extractStorageMode(ITypeSymbol modelType) {
    const string PERSPECTIVE_STORAGE_ATTRIBUTE = "Whizbang.Core.Perspectives.PerspectiveStorageAttribute";

    foreach (var attribute in modelType.GetAttributes()) {
      var attrClassName = TypeNameUtilities.DisplayOrNull(attribute.AttributeClass);
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
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Reads both field attributes with every optional argument in one pass over the properties.")]
  private static PhysicalFieldInfo[] _discoverPhysicalFields(System.Collections.Generic.List<IPropertySymbol> properties) {
    const string PHYSICAL_FIELD_ATTRIBUTE = "Whizbang.Core.Perspectives.PhysicalFieldAttribute";
    const string VECTOR_FIELD_ATTRIBUTE = "Whizbang.Core.Perspectives.VectorFieldAttribute";

    var physicalFields = new System.Collections.Generic.List<PhysicalFieldInfo>();

    foreach (var property in properties) {
      foreach (var attribute in property.GetAttributes()) {
        var attrClassName = TypeNameUtilities.DisplayOrNull(attribute.AttributeClass);

        if (attrClassName == PHYSICAL_FIELD_ATTRIBUTE) {
          var fieldInfo = _extractPhysicalFieldInfo(property, attribute);
          if (fieldInfo != null) {
            physicalFields.Add(ColumnStorageSql.WithStorage(fieldInfo, attribute));
          }
        } else if (attrClassName == VECTOR_FIELD_ATTRIBUTE) {
          var fieldInfo = _extractVectorFieldInfo(property, attribute);
          if (fieldInfo != null) {
            physicalFields.Add(fieldInfo);
          }
        }
      }
    }

    return [.. physicalFields];
  }

  /// <summary>
  /// Extracts PhysicalFieldInfo from a [PhysicalField] attribute.
  /// </summary>
  private static PhysicalFieldInfo? _extractPhysicalFieldInfo(IPropertySymbol property, AttributeData attribute) {
    var propertyName = property.Name;
    var typeName = TypeNameUtilities.FullyQualified(property.Type);

    // Extract named arguments
    // [Indexed] is how any field asks for an index, promoted or not, so this reads it rather than a
    // flag on the promotion attribute.
    // Containment is a GIN index of its own over a jsonb column, never a btree.
    var declaredKind = JsonIndexDiscovery.DeclaredKind(property) ?? 0;
    bool isIndexed = JsonIndexDiscovery.WithoutContainment(declaredKind) > 0;
    bool isUnique = false;
    int? maxLength = null;
    string? columnName = null;
    string? columnType = null;

    foreach (var namedArg in attribute.NamedArguments) {
      switch (namedArg.Key) {
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
        case "ColumnType":
          // Verbatim: the set of types a server might have is open, so there is nothing to
          // validate against that would not refuse the cases this exists for.
          columnType = namedArg.Value.Value as string;
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
        VectorIndexLists: null,
        // An object, a collection or a dictionary is a jsonb column unless the author declared otherwise.
        ColumnType: columnType ?? PhysicalFieldScalar.DefaultColumnType(property.Type),
        EnumScalarType: PhysicalFieldScalar.EnumColumnScalar(property.Type),
        EnumMembers: PhysicalFieldScalar.EnumMembers(property.Type),
        EnumIsFlags: PhysicalFieldScalar.IsFlagsEnum(property.Type)
,
        IsContainmentIndexed: PhysicalFieldScalar.IsContainmentIndexed(
          declaredKind, columnType ?? PhysicalFieldScalar.DefaultColumnType(property.Type)));
  }

  /// <summary>
  /// Extracts PhysicalFieldInfo from a [VectorField] attribute.
  /// </summary>
  private static PhysicalFieldInfo? _extractVectorFieldInfo(IPropertySymbol property, AttributeData attribute) {
    var propertyName = property.Name;
    var typeName = TypeNameUtilities.FullyQualified(property.Type);

    // Extract constructor argument (dimensions)
    int? dimensions = null;
    if (attribute.ConstructorArguments.Length > 0) {
      dimensions = attribute.ConstructorArguments[0].Value as int?;
    }

    // Extract named arguments
    var distanceMetric = GeneratorVectorDistanceMetric.Cosine; // Default
    var indexType = GeneratorVectorIndexType.IVFFlat; // Default
    bool isIndexed = JsonIndexDiscovery.DeclaredKind(property) is > 0; // [Indexed] is how a vector asks for its index, like any other field
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

    // Build SQL content — collect per-perspective SQL for both concatenated Sql and individual Entries[]
    var sqlBuilder = new StringBuilder();
    sqlBuilder.AppendLine("-- Whizbang Perspective Tables - Auto-Generated");
    sqlBuilder.AppendLine("-- 3-Column JSONB Pattern: data (projection state), metadata (correlation/causation), scope (tenant/user)");
    sqlBuilder.AppendLine();

    var perspectiveEntries = new System.Collections.Generic.List<(string Name, string Sql)>();
    // The objects each table's DDL builds, declared to the managed-object ledger (#1252), read from that DDL.
    var managedObjects = new System.Collections.Generic.List<(string Table, string Kind, string Name, string DeclaredBy)>();

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
          tableCode = tableCode[..insertPos] + ",\n" + physicalColumnsSql + "\n" + tableCode[insertPos..];
        }
      }

      // Build per-perspective SQL (moves into existing columns, table, indexes)
      var perspectiveSqlBuilder = new StringBuilder();
      _appendPreTableSql(perspectiveSqlBuilder, perspective);
      perspectiveSqlBuilder.AppendLine(tableCode);
      perspectiveSqlBuilder.AppendLine();

      _appendPostTableSql(perspectiveSqlBuilder, perspective, createIndexesSnippet);

      // Collect per-perspective entry
      perspectiveEntries.Add((perspective.ClassName, perspectiveSqlBuilder.ToString()));
      foreach (var (kind, name) in ManagedObjectNames.Extract(perspectiveSqlBuilder.ToString())) {
        managedObjects.Add((perspective.TableName, kind, name, perspective.ModelClassName));
      }

      // Append to concatenated SQL (backward compat)
      sqlBuilder.Append(perspectiveSqlBuilder);
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
    schemaBuilder.AppendLine();
    schemaBuilder.AppendLine("    /// <summary>");
    schemaBuilder.AppendLine("    /// Per-perspective SQL entries for individual hash tracking.");
    schemaBuilder.AppendLine("    /// Each entry maps a perspective name to its DDL (CREATE TABLE + indexes).");
    schemaBuilder.AppendLine("    /// </summary>");
    schemaBuilder.AppendLine("    public static readonly System.Collections.Generic.KeyValuePair<string, string>[] Entries = new System.Collections.Generic.KeyValuePair<string, string>[] {");
    foreach (var (name, sql) in perspectiveEntries) {
      schemaBuilder.Append("        new System.Collections.Generic.KeyValuePair<string, string>(\"");
      schemaBuilder.Append(name);
      schemaBuilder.Append("\", @\"");
      schemaBuilder.Append(sql.Replace("\"", "\"\""));
      schemaBuilder.AppendLine("\"),");
    }
    schemaBuilder.AppendLine("    };");
    schemaBuilder.AppendLine();
    schemaBuilder.AppendLine("    /// <summary>");
    schemaBuilder.AppendLine("    /// The indexes and constraints the entries build, as (table, kind, name, declared by): what the");
    schemaBuilder.AppendLine("    /// managed-object reconcile keeps, and the Whizbang-built objects missing from it are retired.");
    schemaBuilder.AppendLine("    /// </summary>");
    schemaBuilder.AppendLine("    public static readonly (string Table, string Kind, string Name, string DeclaredBy)[] ManagedObjects = new (string, string, string, string)[] {");
    foreach (var (table, kind, name, declaredBy) in managedObjects) {
      schemaBuilder.AppendLine($"        (\"{table}\", \"{kind}\", \"{name.Replace("\"", "\\\"")}\", \"{declaredBy}\"),");
    }
    schemaBuilder.AppendLine("    };");
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
  /// <summary>
  /// The DDL that follows a perspective's table: its standard and physical-field indexes, its length
  /// constraints, and the backfill of each physical column from the document. Post-table, so a column-copy
  /// migration runs it against the swapped-in table.
  /// </summary>
  private static void _appendPostTableSql(
      StringBuilder perspectiveSqlBuilder, PerspectiveSchemaInfo perspective, string createIndexesSnippet) {
    // Generate standard indexes from snippet. The metadata index follows [PerspectiveQueries] as it
    // does on the other driver: nothing the framework runs matches on metadata, so it is built only
    // for a model whose own queries do.
    var metadataIndex = perspective.BuildsMetadataIndex
        ? "CREATE INDEX IF NOT EXISTS ix___TABLE_NAME___metadata_gin ON __TABLE_NAME__ USING GIN (metadata jsonb_path_ops);"
        : "-- No metadata index: the model does not declare [PerspectiveQueries(MatchOnMetadata = true)].";
    var indexesCode = createIndexesSnippet
        .Replace("__METADATA_GIN_INDEX__", metadataIndex)
        .Replace("__TABLE_NAME__", perspective.TableName);

    perspectiveSqlBuilder.AppendLine(indexesCode);

    // Generate physical field indexes
    var physicalIndexesSql = _generatePhysicalIndexesSql(perspective.TableName, perspective.PhysicalFields);
    if (!string.IsNullOrEmpty(physicalIndexesSql)) {
      perspectiveSqlBuilder.AppendLine(physicalIndexesSql);
    }

    var lengthConstraintsSql = _generateLengthConstraintsSql(perspective.TableName, perspective.PhysicalFields);
    if (!string.IsNullOrEmpty(lengthConstraintsSql)) {
      perspectiveSqlBuilder.AppendLine(lengthConstraintsSql);
    }

    // The declared storage options of the table and its promoted columns, the same statements the EF Core
    // schema emits. Each alters only what differs from the catalog, so a re-apply changes nothing.
    foreach (var statement in ColumnStorageSql.ForTable(perspective.TableName, perspective.TableStorage)) {
      perspectiveSqlBuilder.AppendLine(statement);
    }
    foreach (var field in perspective.PhysicalFields) {
      foreach (var statement in ColumnStorageSql.ForColumn(perspective.TableName, perspective.TableName, field)) {
        perspectiveSqlBuilder.AppendLine(statement);
      }
    }

    // Fill each physical column from the document for rows written before it existed. Post-table DDL, so
    // a column-copy migration runs it against the swapped-in table; idempotent, so a re-apply finds
    // nothing to do. The same statements the EF Core schema emits, so both drivers agree. A Split model's
    // sync triggers go on first, on the table that is now in place, so a write that lands after the fill is
    // synced. Then the fields the model keeps only in the document are offered for demotion (#1022).
    var fields = _withStorageMode(perspective);
    // Each promoted column recorded as the framework's, on the table now in place: the pre-table arm cannot
    // see a table that does not exist yet, and a later demotion moves only a recorded column (#1022).
    foreach (var field in fields) {
      perspectiveSqlBuilder.AppendLine(PhysicalColumnSql.Arm(perspective.TableName, field));
    }
    if (fields.Any(f => f.IsSplit)) {
      perspectiveSqlBuilder.AppendLine(PhysicalColumnSql.SyncMoves(perspective.TableName));
    }
    foreach (var field in fields) {
      var backfill = PhysicalColumnSql.Backfill(perspective.TableName, field);
      if (backfill is not null) {
        perspectiveSqlBuilder.AppendLine(backfill);
      }
    }
    var demote = PhysicalColumnSql.Demote(perspective.TableName, perspective.DocumentProperties, fields);
    if (demote is not null) {
      perspectiveSqlBuilder.AppendLine(demote);
    }
  }

  /// <summary>
  /// The statements that run before the table's <c>CREATE TABLE</c>, against the table as an earlier release
  /// left it: each promoted field is armed while its column is still missing, then the column is added.
  /// </summary>
  /// <remarks>
  /// A column-copy migration builds the new table under another name and swaps it in, so the column exists
  /// on the swapped-in table before any post-table statement runs, and an arm there would never see it
  /// missing (issue #1010). Run first, the arm sees the table as the previous release wrote it, exactly as the
  /// EF Core schema's does, and the column it adds is copied across by the swap. A table that does not exist
  /// yet arms and adds nothing: <c>CREATE TABLE</c> then creates it with every column and no rows.
  /// </remarks>
  private static void _appendPreTableSql(StringBuilder perspectiveSqlBuilder, PerspectiveSchemaInfo perspective) {
    foreach (var field in _withStorageMode(perspective)) {
      perspectiveSqlBuilder.AppendLine(PhysicalColumnSql.Arm(perspective.TableName, field));
      perspectiveSqlBuilder.AppendLine(
        $"ALTER TABLE IF EXISTS {perspective.TableName} ADD COLUMN IF NOT EXISTS {field.ColumnName} {_mapToPostgresType(field)};");
    }
  }

  /// <summary>The model's promoted fields, each marked Split when the model is (vectors excepted).</summary>
  private static PhysicalFieldInfo[] _withStorageMode(PerspectiveSchemaInfo perspective) {
    var isSplit = perspective.StorageMode == GeneratorFieldStorageMode.Split;
    return [.. perspective.PhysicalFields.Select(f => f with { IsSplit = isSplit && !f.IsVector })];
  }

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
  /// <summary>
  /// The declared length of a promoted text column, as a constraint rather than as the column type.
  /// </summary>
  /// <remarks>
  /// In PostgreSQL a length-limited type is a constraint and nothing else, storage and performance
  /// being identical. This schema is applied to databases that already exist as well as to new ones,
  /// and only ever adds, so a limited type would constrain the new while leaving the established
  /// alone. A check constraint is additive and reaches both; NOT VALID leaves the rows already there
  /// unscanned, so there is no rewrite and no long lock, and every row written from then on is
  /// checked. A null is not a violation, because length(NULL) is NULL and a check passes unless it
  /// is false.
  /// </remarks>
  /// <param name="tableName">The perspective's table.</param>
  /// <param name="fields">The promoted fields.</param>
  /// <returns>The constraint statements, or an empty string when no length was declared.</returns>
  private static string _generateLengthConstraintsSql(
      string tableName, IReadOnlyList<PhysicalFieldInfo> fields) {
    var sb = new StringBuilder();

    foreach (var field in fields) {
      if (field.MaxLength is not { } maxLength || field.IsVector || !string.IsNullOrWhiteSpace(field.ColumnType)) {
        continue;
      }

      // The field's type name is rendered fully qualified, which writes a special type as its keyword.
      if (field.TypeName.TrimEnd('?') != "string") {
        continue;
      }

      // PostgreSQL has no ADD CONSTRAINT IF NOT EXISTS, and this script is applied on every start,
      // so re-running it has to be silent.
      var name = $"ck_{tableName}_{field.ColumnName}_len";
      sb.AppendLine("DO $$ BEGIN");
      sb.AppendLine($"  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = '{name}'");
      sb.AppendLine($"    AND conrelid = '{tableName}'::regclass) THEN");
      sb.AppendLine($"    ALTER TABLE {tableName} ADD CONSTRAINT {name}");
      sb.AppendLine($"      CHECK (length({field.ColumnName}) <= {maxLength}) NOT VALID;");
      sb.AppendLine("  END IF;");
      sb.AppendLine("END $$;");
    }

    return sb.ToString();
  }

  private static string _mapToPostgresType(PhysicalFieldInfo field) {
    if (field.IsVector && field.VectorDimensions.HasValue) {
      return $"vector({field.VectorDimensions.Value})";
    }

    // The author's own type wins over the derived one, and is consulted BEFORE the mapping table
    // rather than as its default arm. That arm is TEXT, so a native array, a domain or a type from
    // an extension would otherwise be silently downgraded -- and storing an array as text is the
    // exact defect the option exists to remove.
    if (!string.IsNullOrWhiteSpace(field.ColumnType)) {
      return field.ColumnType!;
    }

    // Normalize the type name by removing global:: and nullable markers
    // An enumeration is stored as its underlying number, so its column is typed from that scalar.
    var typeName = (field.EnumScalarType ?? field.TypeName)
        .Replace("global::", "")
        .TrimEnd('?');

    return PostgresTypeFor(typeName);
  }

  /// <summary>
  /// The PostgreSQL column type the perspective table DDL gives a physical field whose CLR type renders as
  /// <paramref name="typeName"/> (no <c>global::</c> prefix, no nullable suffix). The match is
  /// exact and ordinal: a name that is not one of the mapped types, however close, is
  /// <c>TEXT</c>.
  /// </summary>
  /// <remarks>
  /// Public rather than private so a test can pin the whole table, including the names the
  /// generator rarely or never renders (most CLR spellings of the keyword types) and the near misses that
  /// must fall through to the default. Not internal because InternalsVisibleTo on this assembly
  /// collides with the test project's polyfills (see AssemblyInfo.cs). This assembly ships as an analyzer, so its public
  /// surface is not a consumer API.
  /// </remarks>
  /// <tests>tests/Whizbang.Generators.Tests/GeneratorColumnTypeTableTests.cs:SchemaColumnType_MapsEveryKnownNameAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/GeneratorColumnTypeTableTests.cs:SchemaColumnType_NearMissNames_FallBackToDefaultAsync</tests>
  public static string PostgresTypeFor(string typeName) {
    return typeName switch {
      // A declared length is carried by a check constraint rather than by the column's type, so the
      // column is text here as it is in the table. Claiming a limited type while the table holds
      // text is what made the model and the database describe different columns.
      "System.String" or "string" => "TEXT",
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
      if (field.IsContainmentIndexed) {
        sb.AppendLine(PhysicalColumnSql.ContainmentIndex($"ix_{tableName}_{field.ColumnName}_gin", tableName, field.ColumnName));
      }

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
/// <param name="DocumentProperties">The model's properties kept only in the document, whose columns an earlier release may have left behind (#1022)</param>
/// <param name="BuildsMetadataIndex">Whether the model's [PerspectiveQueries] asks for the metadata index</param>
/// <param name="TableStorage">The table's storage options from <c>[PerspectiveTableStorage]</c>, if any</param>
internal sealed record PerspectiveSchemaInfo(
    string ClassName,
    string FullyQualifiedClassName,
    string ModelClassName,
    string TableName,
    int PropertyCount,
    int EstimatedSizeBytes,
    GeneratorFieldStorageMode StorageMode,
    PhysicalFieldInfo[] PhysicalFields,
    string[] DocumentProperties,
    bool BuildsMetadataIndex,
    TableStorageInfo? TableStorage = null
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

/// <summary>
/// Intermediate value type for perspective discovery before table name config is applied.
/// Separates syntax/semantic extraction from configuration-dependent table name generation.
/// </summary>
/// <param name="ClassName">Simple class name (e.g., "OrderSummaryProjection")</param>
/// <param name="FullyQualifiedClassName">Fully qualified class name</param>
/// <param name="ModelClassName">Simple class name of the model type</param>
/// <param name="TableBaseName">Base name for table generation (nested classes merged, e.g., "ActivityProjection")</param>
/// <param name="PropertyCount">Number of properties for size estimation</param>
/// <param name="EstimatedSizeBytes">Estimated JSON size in bytes</param>
/// <param name="StorageMode">Field storage mode from [PerspectiveStorage] attribute</param>
/// <param name="PhysicalFields">Array of physical fields discovered on the model</param>
/// <param name="DocumentProperties">The model's properties kept only in the document, whose columns an earlier release may have left behind (#1022)</param>
/// <param name="BuildsMetadataIndex">Whether the model's [PerspectiveQueries] asks for the metadata index</param>
/// <param name="TableStorage">The table's storage options from <c>[PerspectiveTableStorage]</c>, if any</param>
internal sealed record PerspectiveCandidate(
    string ClassName,
    string FullyQualifiedClassName,
    string ModelClassName,
    string TableBaseName,
    int PropertyCount,
    int EstimatedSizeBytes,
    GeneratorFieldStorageMode StorageMode,
    PhysicalFieldInfo[] PhysicalFields,
    string[] DocumentProperties,
    bool BuildsMetadataIndex,
    TableStorageInfo? TableStorage = null
);
