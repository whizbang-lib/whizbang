// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
  private const string SCOPES_TYPE_NAME = "global::Whizbang.Transports.HotChocolate.GraphQLLensScopes";
  private const string SCOPE_RESOLVER_TYPE_NAME = "global::Whizbang.Transports.HotChocolate.GraphQLLensScopeResolver";
  private const string ROW_TYPE_SUFFIX = "LensRowType";
  private const string FILTER_TYPE_SUFFIX = "LensRowFilterInputType";
  private const string SORT_TYPE_SUFFIX = "LensRowSortInputType";

  // Each scope flag and the PerspectiveRow members it exposes, in the order the generated types bind them.
  private static readonly (string Flag, string[] Members)[] _rowParts = [
      ("Data", ["Data"]),
      ("Metadata", ["Metadata"]),
      ("Scope", ["Scope"]),
      ("SystemFields", ["Id", "CreatedAt", "UpdatedAt", "Version"])
  ];

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

    // The "Roslyn bound no named-type symbol" guard is folded into the attribute test rather than
    // standing alone: a declaration with no symbol exposes no attributes either, so both conditions
    // take the same exit and the symbol is still never dereferenced. Merged so the guard runs on
    // every call instead of sitting on a line no input can reach.
    // Check for [GraphQLLens] attribute
    if (context.SemanticModel.GetDeclaredSymbol(typeDeclaration, ct) is not INamedTypeSymbol symbol
        || symbol.GetAttributes()
             .FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, GRAPHQL_LENS_ATTRIBUTE_NAME))
           is not { } graphQLLensAttr) {
      return null;
    }

    // Find ILensQuery<TModel> interface to get the model type
    var lensQueryInterface = symbol.AllInterfaces
        .FirstOrDefault(i => TypeNameUtilities.Display(i.OriginalDefinition).StartsWith(LENS_QUERY_INTERFACE_NAME, StringComparison.Ordinal));

    if (lensQueryInterface is null || lensQueryInterface.TypeArguments.Length == 0) {
      return null;
    }

    var modelType = lensQueryInterface.TypeArguments[0];

    // Extract attribute properties
    var queryName = AttributeUtilities.GetStringValue(graphQLLensAttr, "QueryName")
                    ?? NamingConventionUtilities.ToDefaultQueryName(modelType.Name);
    var scope = AttributeUtilities.GetIntValue(graphQLLensAttr, "Scope", 0);
    var enableFiltering = AttributeUtilities.GetBoolValue(graphQLLensAttr, "EnableFiltering", true);
    var enableSorting = AttributeUtilities.GetBoolValue(graphQLLensAttr, "EnableSorting", true);
    var enablePaging = AttributeUtilities.GetBoolValue(graphQLLensAttr, "EnablePaging", true);
    var enableProjection = AttributeUtilities.GetBoolValue(graphQLLensAttr, "EnableProjection", true);
    var defaultPageSize = AttributeUtilities.GetIntValue(graphQLLensAttr, "DefaultPageSize", 10);
    var maxPageSize = AttributeUtilities.GetIntValue(graphQLLensAttr, "MaxPageSize", 100);

    return new GraphQLLensInfo(
        InterfaceName: TypeNameUtilities.FullyQualified(symbol),
        ModelTypeName: TypeNameUtilities.FullyQualified(modelType),
        QueryName: queryName,
        Scope: scope,
        EnableFiltering: enableFiltering,
        EnableSorting: enableSorting,
        EnablePaging: enablePaging,
        EnableProjection: enableProjection,
        DefaultPageSize: defaultPageSize,
        MaxPageSize: maxPageSize,
        Namespace: TypeNameUtilities.Display(symbol.ContainingNamespace)
    );
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
    var rowTypes = new StringBuilder();
    var lensInfoProps = new StringBuilder();

    foreach (var lens in validLenses) {
      // Generate query method
      var methodCode = _generateQueryMethod(lens);
      queryMethods.AppendLine(methodCode);
      queryMethods.AppendLine();

      // Generate the lens's own row, filter and sort types, which carry its scope into the schema
      rowTypes.AppendLine(_generateRowTypes(lens));

      // Generate info property
      var infoCode = _generateLensInfoProperty(lens);
      lensInfoProps.AppendLine(infoCode);
    }

    // Replace regions in template
    var result = template;

    // Replace header manually (we don't have DispatcherSnippets.cs in this generator)
    // Deterministic: wall-clock here would change the compiled output hash every build.
    var timestamp = TemplateUtilities.GetDeterministicBuildStamp(typeof(GraphQLLensTypeGenerator).Assembly);
    var header = $"// <auto-generated/>\n// Generated by GraphQLLensTypeGenerator at {timestamp}\n// DO NOT EDIT - Changes will be overwritten\n#nullable enable";
    result = TemplateUtilities.ReplaceRegion(result, "HEADER", header);

    result = result.Replace("__NAMESPACE__", targetNamespace);
    result = result.Replace("__LENS_COUNT__", validLenses.Length.ToString(CultureInfo.InvariantCulture));
    result = result.Replace("__TIMESTAMP__", timestamp);

    result = TemplateUtilities.ReplaceRegion(result, "LENS_QUERY_METHODS", queryMethods.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "LENS_ROW_TYPES", rowTypes.ToString());
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
    var pascalName = _pascalName(lens.QueryName);
    var methodName = "Get" + pascalName;

    sb.AppendLine("  /// <summary>");
    sb.AppendLine($"  /// Query field for {lens.QueryName}.");
    sb.AppendLine($"  /// Returns results from the {lens.InterfaceName} lens.");
    sb.AppendLine("  /// </summary>");

    // Add attributes based on configuration. Every attribute names the lens's own types, so the
    // schema exposes only the row parts its scope declares, in the output and in filter and sort inputs.
    var rowType = pascalName + ROW_TYPE_SUFFIX;
    if (lens.EnablePaging) {
      sb.AppendLine($"  [UsePaging(typeof({rowType}), DefaultPageSize = {lens.DefaultPageSize}, MaxPageSize = {lens.MaxPageSize})]");
    } else {
      sb.AppendLine($"  [GraphQLType(typeof(NonNullType<ListType<NonNullType<{rowType}>>>))]");
    }
    if (lens.EnableProjection) {
      sb.AppendLine("  [UseProjection]");
    }
    if (lens.EnableFiltering) {
      sb.AppendLine($"  [UseFiltering(typeof({pascalName}{FILTER_TYPE_SUFFIX}))]");
    }
    if (lens.EnableSorting) {
      sb.AppendLine($"  [UseSorting(typeof({pascalName}{SORT_TYPE_SUFFIX}))]");
    }

    sb.AppendLine($"  public global::System.Linq.IQueryable<PerspectiveRow<{lens.ModelTypeName}>> {methodName}(");
    sb.AppendLine($"      [Service] {lens.InterfaceName} lens) {{");
    sb.AppendLine("    return lens.Query;");
    sb.AppendLine("  }");

    return sb.ToString();
  }

  /// <summary>
  /// Generate the lens's own row type, plus its filter and sort input types when those are enabled. Each binds
  /// only the row parts its scope exposes, resolved against the system options when the schema is built. The
  /// declared scope is emitted as its integer value so an undefined value reaches the runtime resolution, which
  /// fails closed, instead of being dropped here.
  /// </summary>
  private static string _generateRowTypes(GraphQLLensInfo lens) {
    var pascalName = _pascalName(lens.QueryName);
    var sb = new StringBuilder();

    _appendRowType(sb, lens, pascalName + ROW_TYPE_SUFFIX, pascalName + "Row",
        "global::HotChocolate.Types.ObjectType", "global::HotChocolate.Types.IObjectTypeDescriptor", "ResolveForOutput");
    if (lens.EnableFiltering) {
      _appendRowType(sb, lens, pascalName + FILTER_TYPE_SUFFIX, pascalName + "RowFilterInput",
          "global::HotChocolate.Data.Filters.FilterInputType", "global::HotChocolate.Data.Filters.IFilterInputTypeDescriptor", "ResolveForInput");
    }
    if (lens.EnableSorting) {
      _appendRowType(sb, lens, pascalName + SORT_TYPE_SUFFIX, pascalName + "RowSortInput",
          "global::HotChocolate.Data.Sorting.SortInputType", "global::HotChocolate.Data.Sorting.ISortInputTypeDescriptor", "ResolveForInput");
    }

    return sb.ToString();
  }

  private static void _appendRowType(
      StringBuilder sb, GraphQLLensInfo lens, string className, string graphQLName,
      string baseType, string descriptorType, string resolveMethod) {
    var row = $"PerspectiveRow<{lens.ModelTypeName}>";
    var declaredScope = $"({SCOPES_TYPE_NAME})({lens.Scope.ToString(CultureInfo.InvariantCulture)})";

    sb.AppendLine("/// <summary>");
    sb.AppendLine($"/// GraphQL type {graphQLName} for the {lens.QueryName} lens, limited to the row parts its scope exposes.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine($"public sealed class {className} : {baseType}<{row}> {{");
    sb.AppendLine($"  protected override void Configure({descriptorType}<{row}> descriptor) {{");
    sb.AppendLine($"    descriptor.Name(\"{graphQLName}\");");
    sb.AppendLine("    descriptor.BindFieldsExplicitly();");
    sb.AppendLine($"    var scope = {SCOPE_RESOLVER_TYPE_NAME}.{resolveMethod}({declaredScope}, descriptor.Extend().Context);");
    foreach (var (flag, members) in _rowParts) {
      sb.AppendLine($"    if (scope.HasFlag({SCOPES_TYPE_NAME}.{flag})) {{");
      foreach (var member in members) {
        sb.AppendLine($"      descriptor.Field(row => row.{member});");
      }
      sb.AppendLine("    }");
    }
    sb.AppendLine("  }");
    sb.AppendLine("}");
    sb.AppendLine();
  }

  private static string _pascalName(string queryName) =>
      char.ToUpperInvariant(queryName[0]) + queryName[1..];

  /// <summary>
  /// Generate a lens info property for diagnostics.
  /// </summary>
  private static string _generateLensInfoProperty(GraphQLLensInfo lens) {
    var propName = _pascalName(lens.QueryName);
    return $"""
  /// <summary>
  /// Information about the {lens.QueryName} lens.
  /// </summary>
  public static (string QueryName, string InterfaceName, string ModelType) {propName}Info =>
      ("{lens.QueryName}", "{lens.InterfaceName}", "{lens.ModelTypeName}");
""";
  }
}
