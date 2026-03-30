using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Transports.FastEndpoints.Generators;

/// <summary>
/// Source generator that discovers [RestLens] attributes and generates
/// FastEndpoints endpoint classes for Whizbang Lenses.
/// </summary>
[Generator]
public sealed class RestLensEndpointGenerator : IIncrementalGenerator {
  private const string REST_LENS_ATTRIBUTE_NAME = "Whizbang.Transports.FastEndpoints.RestLensAttribute";
  private const string LENS_QUERY_INTERFACE_NAME = "Whizbang.Core.Lenses.ILensQuery";

  /// <inheritdoc />
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover types with [RestLens] attribute
    // Syntactic filtering: only look at interfaces/classes with attributes
    var lenses = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => _isPotentialRestLens(node),
        transform: static (ctx, ct) => _extractLensInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Generate code from discovered lenses
    context.RegisterSourceOutput(
        lenses.Collect(),
        static (ctx, lenses) => _generateLensCode(ctx, lenses!)
    );
  }

  /// <summary>
  /// Syntactic predicate: quickly filter to types that might have [RestLens].
  /// </summary>
  private static bool _isPotentialRestLens(SyntaxNode node) {
    // Look for interfaces or classes with at least one attribute
    return node switch {
      InterfaceDeclarationSyntax { AttributeLists.Count: > 0 } => true,
      ClassDeclarationSyntax { AttributeLists.Count: > 0 } => true,
      _ => false
    };
  }

  /// <summary>
  /// Semantic transform: extract lens information if type has [RestLens].
  /// </summary>
  private static RestLensInfo? _extractLensInfo(
      GeneratorSyntaxContext context,
      CancellationToken ct) {
    var typeDeclaration = (TypeDeclarationSyntax)context.Node;

    if (context.SemanticModel.GetDeclaredSymbol(typeDeclaration, ct) is not INamedTypeSymbol symbol) {
      return null;
    }

    // Check for [RestLens] attribute
    var restLensAttr = symbol.GetAttributes()
        .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == REST_LENS_ATTRIBUTE_NAME);

    if (restLensAttr is null) {
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
    var route = AttributeUtilities.GetStringValue(restLensAttr, "Route")
                ?? NamingConventionUtilities.ToDefaultRouteName(modelType.Name);
    var enableFiltering = AttributeUtilities.GetBoolValue(restLensAttr, "EnableFiltering", true);
    var enableSorting = AttributeUtilities.GetBoolValue(restLensAttr, "EnableSorting", true);
    var enablePaging = AttributeUtilities.GetBoolValue(restLensAttr, "EnablePaging", true);
    var defaultPageSize = AttributeUtilities.GetIntValue(restLensAttr, "DefaultPageSize", 10);
    var maxPageSize = AttributeUtilities.GetIntValue(restLensAttr, "MaxPageSize", 100);

    // Generate endpoint class name from interface name
    var endpointClassName = _getEndpointClassName(symbol.Name);

    return new RestLensInfo(
        InterfaceName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        ModelTypeName: modelType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        Route: route,
        EnableFiltering: enableFiltering,
        EnableSorting: enableSorting,
        EnablePaging: enablePaging,
        DefaultPageSize: defaultPageSize,
        MaxPageSize: maxPageSize,
        Namespace: symbol.ContainingNamespace.ToDisplayString(),
        EndpointClassName: endpointClassName
    );
  }

  /// <summary>
  /// Generate endpoint class name from interface name and model name.
  /// E.g., "IOrderLens" + "OrderReadModel" -> "OrderLensEndpoint"
  /// </summary>
  private static string _getEndpointClassName(string interfaceName) {
    // Remove 'I' prefix if present
    var baseName = interfaceName.StartsWith("I", StringComparison.Ordinal)
        ? interfaceName[1..]
        : interfaceName;

    // Ensure it ends with "Endpoint"
    if (!baseName.EndsWith("Endpoint", StringComparison.Ordinal)) {
      baseName += "Endpoint";
    }

    return baseName;
  }

  /// <summary>
  /// Generate code from discovered lenses.
  /// </summary>
  private static void _generateLensCode(
      SourceProductionContext context,
      ImmutableArray<RestLensInfo?> lenses) {

    // Filter nulls to ensure type safety
    var validLenses = lenses.Where(l => l is not null).Select(l => l!).ToImmutableArray();

    if (validLenses.IsEmpty) {
      return;
    }

    // Load template
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(RestLensEndpointGenerator).Assembly,
        "RestLensEndpointTemplate.cs",
        "Whizbang.Transports.FastEndpoints.Generators.Templates"
    );

    // Determine namespace (use first lens's namespace or default)
    var targetNamespace = validLenses[0].Namespace + ".Generated";

    // Generate endpoint classes
    var endpointClasses = new StringBuilder();
    var lensInfoProps = new StringBuilder();

    foreach (var lens in validLenses) {
      // Generate endpoint class
      var endpointCode = _generateEndpointClass(lens);
      endpointClasses.AppendLine(endpointCode);
      endpointClasses.AppendLine();

      // Generate info property
      var infoCode = _generateLensInfoProperty(lens);
      lensInfoProps.AppendLine(infoCode);
    }

    // Replace regions in template
    var result = template;

    // Replace header
    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC", CultureInfo.InvariantCulture);
    var header = $"// <auto-generated/>\n// Generated by RestLensEndpointGenerator at {timestamp}\n// DO NOT EDIT - Changes will be overwritten\n#nullable enable";
    result = TemplateUtilities.ReplaceRegion(result, "HEADER", header);

    result = result.Replace("__NAMESPACE__", targetNamespace);
    result = result.Replace("__LENS_COUNT__", validLenses.Length.ToString(CultureInfo.InvariantCulture));
    result = result.Replace("__TIMESTAMP__", timestamp);

    result = TemplateUtilities.ReplaceRegion(result, "ENDPOINT_CLASSES", endpointClasses.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "LENS_INFO_PROPERTIES", lensInfoProps.ToString());

    // Add source
    context.AddSource("WhizbangRestLensEndpoints.g.cs", result);
  }

  /// <summary>
  /// Generate an endpoint class for a lens based on its configuration.
  /// </summary>
  private static string _generateEndpointClass(RestLensInfo lens) {
    var sb = new StringBuilder();

    sb.AppendLine("/// <summary>");
    sb.AppendLine($"/// Generated REST endpoint for {lens.InterfaceName}.");
    sb.AppendLine($"/// Route: {lens.Route}");
    sb.AppendLine("/// </summary>");
    sb.AppendLine($"public partial class {lens.EndpointClassName} : Endpoint<LensRequest, LensResponse<{lens.ModelTypeName}>> {{");
    sb.AppendLine($"  private readonly {lens.InterfaceName} _lens;");
    sb.AppendLine();
    sb.AppendLine("  /// <summary>");
    sb.AppendLine($"  /// Creates a new instance of {lens.EndpointClassName}.");
    sb.AppendLine("  /// </summary>");
    sb.AppendLine($"  public {lens.EndpointClassName}({lens.InterfaceName} lens) {{");
    sb.AppendLine("    _lens = lens;");
    sb.AppendLine("  }");
    sb.AppendLine();
    sb.AppendLine("  /// <inheritdoc />");
    sb.AppendLine("  public override void Configure() {");
    sb.AppendLine($"    Get(\"{lens.Route}\");");
    sb.AppendLine("    AllowAnonymous();");
    sb.AppendLine("  }");
    sb.AppendLine();
    sb.AppendLine("  /// <inheritdoc />");
    sb.AppendLine("  public override async Task HandleAsync(LensRequest req, CancellationToken ct) {");
    sb.AppendLine("    // Calculate paging");
    sb.AppendLine("    var page = Math.Max(1, req.Page);");
    sb.AppendLine($"    var pageSize = req.PageSize ?? {lens.DefaultPageSize};");
    sb.AppendLine($"    pageSize = Math.Min(pageSize, {lens.MaxPageSize});");
    sb.AppendLine("    pageSize = Math.Max(1, pageSize);");
    sb.AppendLine("    var skip = (page - 1) * pageSize;");
    sb.AppendLine();
    sb.AppendLine("    // Build query with default ordering by Id for consistent pagination");
    sb.AppendLine("    var query = _lens.Query.Select(r => r.Data).OrderBy(x => x.Id);");
    sb.AppendLine();
    sb.AppendLine("    // TODO: Apply filtering based on req.Filter");
    sb.AppendLine("    // TODO: Apply sorting based on req.Sort (should override default OrderBy)");
    sb.AppendLine();
    sb.AppendLine("    // Get total count before paging");
    sb.AppendLine("    var totalCount = await query.CountAsync(ct);");
    sb.AppendLine();
    sb.AppendLine("    // Apply paging");
    sb.AppendLine("    var items = await query.Skip(skip).Take(pageSize).ToListAsync(ct);");
    sb.AppendLine();
    sb.AppendLine("    // Build response");
    sb.AppendLine($"    var response = new LensResponse<{lens.ModelTypeName}> {{");
    sb.AppendLine("      Data = items,");
    sb.AppendLine("      TotalCount = totalCount,");
    sb.AppendLine("      Page = page,");
    sb.AppendLine("      PageSize = pageSize");
    sb.AppendLine("    };");
    sb.AppendLine();
    sb.AppendLine("    await SendAsync(response, cancellation: ct);");
    sb.AppendLine("  }");
    sb.AppendLine("}");

    return sb.ToString();
  }

  /// <summary>
  /// Generate a lens info property for diagnostics.
  /// </summary>
  private static string _generateLensInfoProperty(RestLensInfo lens) {
    return $"""
  /// <summary>
  /// Information about the {lens.EndpointClassName} lens endpoint.
  /// </summary>
  public static (string Route, string InterfaceName, string ModelType) {lens.EndpointClassName}Info =>
      ("{lens.Route}", "{lens.InterfaceName}", "{lens.ModelTypeName}");
""";
  }
}
