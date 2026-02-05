using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Transports.HotChocolate.Generators;

/// <summary>
/// Source generator that discovers [GraphQLLens] attributes and generates
/// HotChocolate query type extensions for Whizbang Lenses.
/// </summary>
[Generator]
public sealed class GraphQLLensTypeGenerator : IIncrementalGenerator {
  private const string GRAPHQL_LENS_ATTRIBUTE_NAME = "Whizbang.Transports.HotChocolate.GraphQLLensAttribute";
  private const string LENS_QUERY_INTERFACE_NAME = "Whizbang.Core.Lenses.ILensQuery";

  /// <inheritdoc />
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover types with [GraphQLLens] attribute
    // Syntactic filtering: only look at interfaces/classes with attributes
    var lenses = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => _isPotentialGraphQLLens(node),
        transform: static (ctx, ct) => _extractLensInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Generate code from discovered lenses
    context.RegisterSourceOutput(
        lenses.Collect(),
        static (ctx, lenses) => _generateLensCode(ctx, lenses!)
    );
  }

  /// <summary>
  /// Syntactic predicate: quickly filter to types that might have [GraphQLLens].
  /// </summary>
  private static bool _isPotentialGraphQLLens(SyntaxNode node) {
    // Look for interfaces or classes with at least one attribute
    return node switch {
      InterfaceDeclarationSyntax { AttributeLists.Count: > 0 } => true,
      ClassDeclarationSyntax { AttributeLists.Count: > 0 } => true,
      _ => false
    };
  }

  /// <summary>
  /// Semantic transform: extract lens information if type has [GraphQLLens].
  /// </summary>
  private static GraphQLLensInfo? _extractLensInfo(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    var typeDeclaration = (TypeDeclarationSyntax)context.Node;
    var symbol = context.SemanticModel.GetDeclaredSymbol(typeDeclaration, ct) as INamedTypeSymbol;

    if (symbol is null) {
      return null;
    }

    // Check for [GraphQLLens] attribute
    var graphQLLensAttr = symbol.GetAttributes()
        .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == GRAPHQL_LENS_ATTRIBUTE_NAME);

    if (graphQLLensAttr is null) {
      return null;
    }

    // Find ILensQuery<TModel> interface to get the model type
    var lensQueryInterface = symbol.AllInterfaces
        .FirstOrDefault(i => i.OriginalDefinition.ToDisplayString().StartsWith(LENS_QUERY_INTERFACE_NAME, StringComparison.Ordinal));

    if (lensQueryInterface is null || lensQueryInterface.TypeArguments.Length == 0) {
      return null;
    }

    var modelType = lensQueryInterface.TypeArguments[0];

    // Extract attribute properties
    var queryName = _getAttributeStringValue(graphQLLensAttr, "QueryName")
                    ?? _getDefaultQueryName(modelType.Name);
    var scope = _getAttributeIntValue(graphQLLensAttr, "Scope", 0);
    var enableFiltering = _getAttributeBoolValue(graphQLLensAttr, "EnableFiltering", true);
    var enableSorting = _getAttributeBoolValue(graphQLLensAttr, "EnableSorting", true);
    var enablePaging = _getAttributeBoolValue(graphQLLensAttr, "EnablePaging", true);
    var enableProjection = _getAttributeBoolValue(graphQLLensAttr, "EnableProjection", true);
    var defaultPageSize = _getAttributeIntValue(graphQLLensAttr, "DefaultPageSize", 10);
    var maxPageSize = _getAttributeIntValue(graphQLLensAttr, "MaxPageSize", 100);

    return new GraphQLLensInfo(
        InterfaceName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        ModelTypeName: modelType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        QueryName: queryName,
        Scope: scope,
        EnableFiltering: enableFiltering,
        EnableSorting: enableSorting,
        EnablePaging: enablePaging,
        EnableProjection: enableProjection,
        DefaultPageSize: defaultPageSize,
        MaxPageSize: maxPageSize,
        Namespace: symbol.ContainingNamespace.ToDisplayString()
    );
  }

  /// <summary>
  /// Get a string property value from an attribute.
  /// </summary>
  private static string? _getAttributeStringValue(AttributeData attribute, string propertyName) {
    var namedArg = attribute.NamedArguments
        .FirstOrDefault(a => a.Key == propertyName);

    if (namedArg.Key is null || namedArg.Value.Value is not string value) {
      return null;
    }

    return value;
  }

  /// <summary>
  /// Get a boolean property value from an attribute.
  /// </summary>
  private static bool _getAttributeBoolValue(AttributeData attribute, string propertyName, bool defaultValue) {
    var namedArg = attribute.NamedArguments
        .FirstOrDefault(a => a.Key == propertyName);

    if (namedArg.Key is null || namedArg.Value.Value is not bool value) {
      return defaultValue;
    }

    return value;
  }

  /// <summary>
  /// Get an integer property value from an attribute.
  /// </summary>
  private static int _getAttributeIntValue(AttributeData attribute, string propertyName, int defaultValue) {
    var namedArg = attribute.NamedArguments
        .FirstOrDefault(a => a.Key == propertyName);

    if (namedArg.Key is null || namedArg.Value.Value is not int value) {
      return defaultValue;
    }

    return value;
  }

  /// <summary>
  /// Generate default query name from model type name.
  /// E.g., "OrderReadModel" -> "orders"
  /// </summary>
  private static string _getDefaultQueryName(string modelTypeName) {
    // Remove common suffixes
    var name = modelTypeName;
    if (name.EndsWith("ReadModel", StringComparison.Ordinal)) {
      name = name.Substring(0, name.Length - 9);
    } else if (name.EndsWith("Model", StringComparison.Ordinal)) {
      name = name.Substring(0, name.Length - 5);
    } else if (name.EndsWith("Dto", StringComparison.Ordinal)) {
      name = name.Substring(0, name.Length - 3);
    }

    // Simple pluralization and lowercase
    var pluralized = name.EndsWith("s", StringComparison.Ordinal) ? name : name + "s";
    return char.ToLowerInvariant(pluralized[0]) + pluralized.Substring(1);
  }

  /// <summary>
  /// Generate code from discovered lenses.
  /// </summary>
  private static void _generateLensCode(
      SourceProductionContext context,
      ImmutableArray<GraphQLLensInfo?> lenses) {

    // Filter nulls to ensure type safety
    var validLenses = lenses.Where(l => l is not null).Select(l => l!).ToImmutableArray();

    if (validLenses.IsEmpty) {
      return;
    }

    // Load template
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(GraphQLLensTypeGenerator).Assembly,
        "GraphQLLensQueryTypeTemplate.cs",
        "Whizbang.Transports.HotChocolate.Generators.Templates"
    );

    // Determine namespace (use first lens's namespace or default)
    var targetNamespace = validLenses[0].Namespace + ".Generated";

    // Generate query methods
    var queryMethods = new StringBuilder();
    var lensInfoProps = new StringBuilder();

    foreach (var lens in validLenses) {
      // Generate query method
      var methodCode = _generateQueryMethod(lens);
      queryMethods.AppendLine(methodCode);
      queryMethods.AppendLine();

      // Generate info property
      var infoCode = _generateLensInfoProperty(lens);
      lensInfoProps.AppendLine(infoCode);
    }

    // Replace regions in template
    var result = template;

    // Replace header manually (we don't have DispatcherSnippets.cs in this generator)
    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC", CultureInfo.InvariantCulture);
    var header = $"// <auto-generated/>\n// Generated by GraphQLLensTypeGenerator at {timestamp}\n// DO NOT EDIT - Changes will be overwritten\n#nullable enable";
    result = TemplateUtilities.ReplaceRegion(result, "HEADER", header);

    result = result.Replace("__NAMESPACE__", targetNamespace);
    result = result.Replace("__LENS_COUNT__", validLenses.Length.ToString(CultureInfo.InvariantCulture));
    result = result.Replace("__TIMESTAMP__", timestamp);

    result = TemplateUtilities.ReplaceRegion(result, "LENS_QUERY_METHODS", queryMethods.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "LENS_INFO_PROPERTIES", lensInfoProps.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "LENS_TYPE_REGISTRATIONS", "// No additional registrations needed");

    // Add source
    context.AddSource("WhizbangLensQueries.g.cs", result);
  }

  /// <summary>
  /// Generate a query method for a lens based on its configuration.
  /// </summary>
  private static string _generateQueryMethod(GraphQLLensInfo lens) {
    var sb = new StringBuilder();
    var methodName = "Get" + char.ToUpperInvariant(lens.QueryName[0]) + lens.QueryName.Substring(1);

    sb.AppendLine($"  /// <summary>");
    sb.AppendLine($"  /// Query field for {lens.QueryName}.");
    sb.AppendLine($"  /// Returns results from the {lens.InterfaceName} lens.");
    sb.AppendLine($"  /// </summary>");

    // Add attributes based on configuration
    if (lens.EnablePaging) {
      sb.AppendLine($"  [UsePaging(DefaultPageSize = {lens.DefaultPageSize}, MaxPageSize = {lens.MaxPageSize})]");
    }
    if (lens.EnableProjection) {
      sb.AppendLine($"  [UseProjection]");
    }
    if (lens.EnableFiltering) {
      sb.AppendLine($"  [UseFiltering]");
    }
    if (lens.EnableSorting) {
      sb.AppendLine($"  [UseSorting]");
    }

    sb.AppendLine($"  public IQueryable<PerspectiveRow<{lens.ModelTypeName}>> {methodName}(");
    sb.AppendLine($"      [Service] {lens.InterfaceName} lens) {{");
    sb.AppendLine($"    return lens.Query;");
    sb.AppendLine($"  }}");

    return sb.ToString();
  }

  /// <summary>
  /// Generate a lens info property for diagnostics.
  /// </summary>
  private static string _generateLensInfoProperty(GraphQLLensInfo lens) {
    var propName = char.ToUpperInvariant(lens.QueryName[0]) + lens.QueryName.Substring(1);
    return $@"  /// <summary>
  /// Information about the {lens.QueryName} lens.
  /// </summary>
  public static (string QueryName, string InterfaceName, string ModelType) {propName}Info =>
      (""{lens.QueryName}"", ""{lens.InterfaceName}"", ""{lens.ModelTypeName}"");";
  }
}
