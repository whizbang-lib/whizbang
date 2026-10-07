// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

    // The "Roslyn bound no named-type symbol" guard is folded into the attribute test rather than
    // standing alone: a declaration with no symbol exposes no attributes either, so both conditions
    // take the same exit and the symbol is still never dereferenced. Merged so the guard runs on
    // every call instead of sitting on a line no input can reach.
    // Check for [RestLens] attribute
    if (context.SemanticModel.GetDeclaredSymbol(typeDeclaration, ct) is not INamedTypeSymbol symbol
        || symbol.GetAttributes()
             .FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, REST_LENS_ATTRIBUTE_NAME))
           is not { } restLensAttr) {
      return null;
    }

    // Find ILensQuery<TModel> interface to get the model type
    var lensQueryInterface = symbol.AllInterfaces
        .FirstOrDefault(i => TypeNameUtilities.Display(i.OriginalDefinition).StartsWith(LENS_QUERY_INTERFACE_NAME, StringComparison.Ordinal));

    if (lensQueryInterface is null || lensQueryInterface.TypeArguments.Length == 0) {
      return null;
    }

    var modelType = lensQueryInterface.TypeArguments[0];
    var fields = _shapedFieldsOf(modelType);

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
        InterfaceName: TypeNameUtilities.FullyQualified(symbol),
        ModelTypeName: TypeNameUtilities.FullyQualified(modelType),
        Route: route,
        EnableFiltering: enableFiltering,
        EnableSorting: enableSorting,
        EnablePaging: enablePaging,
        DefaultPageSize: defaultPageSize,
        MaxPageSize: maxPageSize,
        Namespace: TypeNameUtilities.Display(symbol.ContainingNamespace),
        EndpointClassName: endpointClassName,
        FilterCases: _renderFilterCases(fields),
        SortCases: _renderSortCases(fields)
    );
  }

  /// <summary>How a filter value is read as a property's type.</summary>
  private enum FieldKind {
    /// <summary>Compared as the text given.</summary>
    Text,

    /// <summary>Read through the type's own <c>IParsable</c> implementation.</summary>
    Parsable,

    /// <summary>Read as a defined member of the enumeration.</summary>
    Enumeration,
  }

  /// <summary>A model property a request can filter and sort by.</summary>
  private readonly record struct ShapedField(string Name, string Identifier, string TypeName, FieldKind Kind);

  // The value types whose BCL implementation of IParsable reads them back from text, by special type
  // and, for the types that have none, by name.
  private static readonly HashSet<SpecialType> _parsableSpecialTypes = [
    SpecialType.System_Boolean, SpecialType.System_Byte, SpecialType.System_SByte,
    SpecialType.System_Int16, SpecialType.System_UInt16, SpecialType.System_Int32, SpecialType.System_UInt32,
    SpecialType.System_Int64, SpecialType.System_UInt64, SpecialType.System_Single, SpecialType.System_Double,
    SpecialType.System_Decimal, SpecialType.System_DateTime,
  ];

  private static readonly HashSet<string> _parsableTypeNames = new(StringComparer.Ordinal) {
    "System.Guid", "System.DateTimeOffset", "System.DateOnly", "System.TimeOnly", "System.TimeSpan",
  };

  /// <summary>
  /// The model's readable, public, instance properties whose value a request can compare and order
  /// by, most-derived first. A name that differs only in case from one already taken is skipped,
  /// since request field names match ignoring case.
  /// </summary>
  private static List<ShapedField> _shapedFieldsOf(ITypeSymbol model) {
    var fields = new List<ShapedField>();
    var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    for (var type = model; type is not null; type = type.BaseType) {
      foreach (var property in type.GetMembers().OfType<IPropertySymbol>()) {
        if (property.IsStatic || property.IsIndexer || property.GetMethod is null
            || property.DeclaredAccessibility != Accessibility.Public
            || _kindOf(property.Type) is not { } kind
            || !taken.Add(property.Name)) {
          continue;
        }

        fields.Add(new ShapedField(
          property.Name,
          SyntaxFacts.GetKeywordKind(property.Name) == SyntaxKind.None ? property.Name : "@" + property.Name,
          TypeNameUtilities.FullyQualified(_withoutNullable(property.Type)),
          kind));
      }
    }

    return fields;
  }

  private static ITypeSymbol _withoutNullable(ITypeSymbol type) =>
    type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
      ? nullable.TypeArguments[0]
      : type;

  private static FieldKind? _kindOf(ITypeSymbol type) {
    var bare = _withoutNullable(type);
    if (bare.SpecialType == SpecialType.System_String) {
      return FieldKind.Text;
    }

    if (bare.TypeKind == TypeKind.Enum) {
      return FieldKind.Enumeration;
    }

    return _parsableSpecialTypes.Contains(bare.SpecialType) || _parsableTypeNames.Contains(TypeNameUtilities.Display(bare))
      ? FieldKind.Parsable
      : null;
  }

  /// <summary>
  /// One <c>case</c> per field: text compares as given, anything else is first read as the field's
  /// type, so a value that is not one is a 400 rather than a comparison that matches nothing.
  /// </summary>
  private static string _renderFilterCases(List<ShapedField> fields) {
    var sb = new StringBuilder();
    foreach (var field in fields) {
      var key = field.Name.ToUpperInvariant();
      if (field.Kind == FieldKind.Text) {
        sb.AppendLine($"      case \"{key}\": return query.Where(x => x.{field.Identifier} == value);");
        continue;
      }

      var parse = field.Kind == FieldKind.Enumeration ? "ParseEnum" : "Parse";
      sb.AppendLine($"      case \"{key}\": {{");
      sb.AppendLine($"        var parsed = LensQueryShaping.{parse}<{field.TypeName}>(field, value);");
      sb.AppendLine($"        return query.Where(x => x.{field.Identifier} == parsed);");
      sb.AppendLine("      }");
    }

    return sb.ToString();
  }

  /// <summary>One switch arm per field, each adding that field to the ordering.</summary>
  private static string _renderSortCases(List<ShapedField> fields) {
    var sb = new StringBuilder();
    foreach (var field in fields) {
      sb.AppendLine($"        \"{field.Name.ToUpperInvariant()}\" => LensQueryShaping.ThenOrderBy(query, ordered, x => x.{field.Identifier}, sort.Descending),");
    }

    return sb.ToString();
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
    // Deterministic: wall-clock here would change the compiled output hash every build.
    var timestamp = TemplateUtilities.GetDeterministicBuildStamp(typeof(RestLensEndpointGenerator).Assembly);
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
    // The default scope, not the unscoped legacy query: a public endpoint must return only the rows
    // the caller's scope allows.
    sb.AppendLine($"    IQueryable<{lens.ModelTypeName}> query = _lens.DefaultScope.Query.Select(r => r.Data);");
    sb.AppendLine($"    IQueryable<{lens.ModelTypeName}> ordered;");
    sb.AppendLine("    try {");
    if (lens.EnableFiltering) {
      sb.AppendLine("      // Every filter[field]=value narrows the rows; together they are AND'd.");
      sb.AppendLine("      if (req.Filter is not null) {");
      sb.AppendLine("        foreach (var filter in req.Filter) {");
      sb.AppendLine("          query = _filter(query, filter.Key, filter.Value);");
      sb.AppendLine("        }");
      sb.AppendLine("      }");
    }

    if (lens.EnableSorting) {
      sb.AppendLine("      ordered = _sort(query, LensQueryShaping.ParseSort(req.Sort));");
    } else {
      sb.AppendLine("      // Sorting is off for this lens: a stable order by Id keeps pages consistent.");
      sb.AppendLine("      ordered = query.OrderBy(x => x.Id);");
    }

    sb.AppendLine("    } catch (InvalidLensRequestException ex) {");
    sb.AppendLine("      ThrowError(ex.Message);");
    sb.AppendLine("      return;");
    sb.AppendLine("    }");
    sb.AppendLine();

    if (lens.EnablePaging) {
      sb.AppendLine("    var page = Math.Max(1, req.Page);");
      sb.AppendLine($"    var pageSize = req.PageSize ?? {lens.DefaultPageSize};");
      sb.AppendLine($"    pageSize = Math.Min(pageSize, {lens.MaxPageSize});");
      sb.AppendLine("    pageSize = Math.Max(1, pageSize);");
      sb.AppendLine("    var skip = (page - 1) * pageSize;");
      sb.AppendLine();
      sb.AppendLine("    var totalCount = await ordered.CountAsync(ct);");
      sb.AppendLine("    var items = await ordered.Skip(skip).Take(pageSize).ToListAsync(ct);");
      sb.AppendLine();
      sb.AppendLine($"    var response = new LensResponse<{lens.ModelTypeName}> {{");
      sb.AppendLine("      Data = items,");
      sb.AppendLine("      TotalCount = totalCount,");
      sb.AppendLine("      Page = page,");
      sb.AppendLine("      PageSize = pageSize");
      sb.AppendLine("    };");
    } else {
      sb.AppendLine("    // Paging is off for this lens: every matching row is one page.");
      sb.AppendLine("    var items = await ordered.ToListAsync(ct);");
      sb.AppendLine($"    var response = new LensResponse<{lens.ModelTypeName}> {{");
      sb.AppendLine("      Data = items,");
      sb.AppendLine("      TotalCount = items.Count,");
      sb.AppendLine("      Page = 1,");
      sb.AppendLine("      PageSize = items.Count");
      sb.AppendLine("    };");
    }

    sb.AppendLine();
    sb.AppendLine("    await Send.OkAsync(response, ct);");
    sb.AppendLine("  }");

    if (lens.EnableFiltering) {
      sb.AppendLine();
      sb.AppendLine($"  private static IQueryable<{lens.ModelTypeName}> _filter(IQueryable<{lens.ModelTypeName}> query, string field, string value) {{");
      sb.AppendLine("    switch (field.ToUpperInvariant()) {");
      sb.Append(lens.FilterCases);
      sb.AppendLine("      default:");
      sb.AppendLine("        throw InvalidLensRequestException.UnknownField(\"filter\", field);");
      sb.AppendLine("    }");
      sb.AppendLine("  }");
    }

    if (lens.EnableSorting) {
      sb.AppendLine();
      sb.AppendLine($"  private static IQueryable<{lens.ModelTypeName}> _sort(IQueryable<{lens.ModelTypeName}> query, IReadOnlyList<SortExpression> sorts) {{");
      sb.AppendLine($"    IOrderedQueryable<{lens.ModelTypeName}>? ordered = null;");
      sb.AppendLine("    foreach (var sort in sorts) {");
      sb.AppendLine("      ordered = sort.Field.ToUpperInvariant() switch {");
      sb.Append(lens.SortCases);
      sb.AppendLine("        _ => throw InvalidLensRequestException.UnknownField(\"sort\", sort.Field),");
      sb.AppendLine("      };");
      sb.AppendLine("    }");
      sb.AppendLine();
      sb.AppendLine("    // Id last, so rows the requested keys leave tied still page in a stable order.");
      sb.AppendLine("    return LensQueryShaping.ThenOrderBy(query, ordered, x => x.Id, false);");
      sb.AppendLine("  }");
    }

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
