using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Generators;

/// <summary>
/// Incremental source generator that discovers IPerspectiveOf implementations
/// and generates PostgreSQL table schemas with 3-column JSONB pattern.
/// Schemas use universal columns (id, created_at, updated_at, version) + JSONB (model_data, metadata, scope).
/// </summary>
[Generator]
public class PerspectiveSchemaGenerator : IIncrementalGenerator {
  private const string PERSPECTIVE_INTERFACE_NAME = "Whizbang.Core.IPerspectiveOf";
  private const int SIZE_WARNING_THRESHOLD = 1500; // Warn before hitting 2KB compression threshold

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Filter for classes that have a base list (potential interface implementations)
    var perspectiveCandidates = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => ExtractPerspectiveSchemaInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Collect all perspectives and generate schemas
    context.RegisterSourceOutput(
        perspectiveCandidates.Collect(),
        static (ctx, perspectives) => GeneratePerspectiveSchemas(ctx, perspectives!)
    );
  }

  /// <summary>
  /// Extracts perspective schema information from a class declaration.
  /// Returns null if the class doesn't implement IPerspectiveOf.
  /// </summary>
  private static PerspectiveSchemaInfo? ExtractPerspectiveSchemaInfo(
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

    // Look for IPerspectiveOf<TEvent> interface
    var perspectiveInterfaces = classSymbol.AllInterfaces
        .Where(i => i.OriginalDefinition.ToDisplayString() == PERSPECTIVE_INTERFACE_NAME + "<TEvent>"
                    && i.TypeArguments.Length == 1)
        .ToList();

    if (perspectiveInterfaces.Count == 0) {
      return null;
    }

    // Extract class name and generate table name
    var className = classSymbol.Name;
    var tableName = GenerateTableName(className);

    // Estimate size based on properties (rough heuristic)
    var propertyCount = classSymbol.GetMembers()
        .OfType<IPropertySymbol>()
        .Count(p => !p.IsStatic);

    var estimatedSize = EstimateJsonSize(propertyCount);

    return new PerspectiveSchemaInfo(
        ClassName: className,
        FullyQualifiedClassName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        TableName: tableName,
        PropertyCount: propertyCount,
        EstimatedSizeBytes: estimatedSize
    );
  }

  /// <summary>
  /// Generates PostgreSQL schema CREATE TABLE statements for all discovered perspectives.
  /// </summary>
  private static void GeneratePerspectiveSchemas(
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

      // Generate CREATE TABLE from snippet
      var tableCode = createTableSnippet
          .Replace("__CLASS_NAME__", perspective.ClassName)
          .Replace("__ESTIMATED_SIZE__", perspective.EstimatedSizeBytes.ToString())
          .Replace("__TABLE_NAME__", perspective.TableName);

      sqlBuilder.AppendLine(tableCode);
      sqlBuilder.AppendLine();

      // Generate indexes from snippet
      var indexesCode = createIndexesSnippet
          .Replace("__TABLE_NAME__", perspective.TableName);

      sqlBuilder.AppendLine(indexesCode);
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
        perspectives.Length.ToString(),
        string.Join(", ", perspectives.Select(p => p.ClassName))
    ));
  }

  /// <summary>
  /// Generates a snake_case table name from a PascalCase class name.
  /// Example: "OrderSummaryPerspective" -> "order_summary_perspective"
  /// </summary>
  private static string GenerateTableName(string className) {
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
  private static int EstimateJsonSize(int propertyCount) {
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
/// <param name="TableName">Generated PostgreSQL table name (e.g., "order_summary_perspective")</param>
/// <param name="PropertyCount">Number of properties for size estimation</param>
/// <param name="EstimatedSizeBytes">Estimated JSON size in bytes</param>
internal sealed record PerspectiveSchemaInfo(
    string ClassName,
    string FullyQualifiedClassName,
    string TableName,
    int PropertyCount,
    int EstimatedSizeBytes
);
