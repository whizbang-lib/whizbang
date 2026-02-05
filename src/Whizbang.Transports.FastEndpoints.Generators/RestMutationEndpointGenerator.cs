using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Transports.FastEndpoints.Generators;

/// <summary>
/// Source generator that discovers [CommandEndpoint] attributes with RestRoute
/// and generates FastEndpoints mutation endpoint classes for Whizbang commands.
/// </summary>
[Generator]
public sealed class RestMutationEndpointGenerator : IIncrementalGenerator {
  private const string COMMAND_ENDPOINT_ATTRIBUTE_PREFIX = "Whizbang.Transports.Mutations.CommandEndpointAttribute";

  /// <inheritdoc />
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover types with [CommandEndpoint] attribute that have RestRoute set
    // Syntactic filtering: only look at classes with attributes
    var mutations = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => _isPotentialCommandEndpoint(node),
        transform: static (ctx, ct) => _extractMutationInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Generate code from discovered mutations
    context.RegisterSourceOutput(
        mutations.Collect(),
        static (ctx, mutations) => _generateMutationCode(ctx, mutations!)
    );
  }

  /// <summary>
  /// Syntactic predicate: quickly filter to types that might have [CommandEndpoint].
  /// </summary>
  private static bool _isPotentialCommandEndpoint(SyntaxNode node) {
    // Look for classes with at least one attribute
    return node is ClassDeclarationSyntax { AttributeLists.Count: > 0 };
  }

  /// <summary>
  /// Semantic transform: extract mutation information if type has [CommandEndpoint] with RestRoute.
  /// </summary>
  private static RestMutationInfo? _extractMutationInfo(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var symbol = context.SemanticModel.GetDeclaredSymbol(classDeclaration, ct) as INamedTypeSymbol;

    if (symbol is null) {
      return null;
    }

    // Check for [CommandEndpoint<TCommand, TResult>] attribute
    var commandEndpointAttr = symbol.GetAttributes()
        .FirstOrDefault(a => a.AttributeClass?.ToDisplayString()
            .StartsWith(COMMAND_ENDPOINT_ATTRIBUTE_PREFIX, StringComparison.Ordinal) == true);

    if (commandEndpointAttr?.AttributeClass is null) {
      return null;
    }

    // Get the RestRoute from the attribute
    var restRoute = _getAttributeStringValue(commandEndpointAttr, "RestRoute");

    // If no RestRoute, skip this command (might be GraphQL-only)
    if (string.IsNullOrEmpty(restRoute)) {
      return null;
    }

    // Extract type arguments from generic attribute: CommandEndpointAttribute<TCommand, TResult>
    var attrClass = commandEndpointAttr.AttributeClass;
    if (!attrClass.IsGenericType || attrClass.TypeArguments.Length < 2) {
      return null;
    }

    var commandType = attrClass.TypeArguments[0];
    var resultType = attrClass.TypeArguments[1];

    // Get optional RequestType
    var requestTypeName = _getAttributeTypeValue(commandEndpointAttr, "RequestType");

    // Generate endpoint class name from command name
    var endpointClassName = symbol.Name + "Endpoint";

    return new RestMutationInfo(
        CommandTypeName: commandType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        CommandTypeNameShort: commandType.Name,
        ResultTypeName: resultType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        ResultTypeNameShort: resultType.Name,
        RestRoute: restRoute!,
        RequestTypeName: requestTypeName,
        Namespace: symbol.ContainingNamespace.ToDisplayString(),
        EndpointClassName: endpointClassName
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
  /// Get a Type property value from an attribute (as fully qualified string).
  /// </summary>
  private static string? _getAttributeTypeValue(AttributeData attribute, string propertyName) {
    var namedArg = attribute.NamedArguments
        .FirstOrDefault(a => a.Key == propertyName);

    if (namedArg.Key is null || namedArg.Value.Value is not ITypeSymbol typeSymbol) {
      return null;
    }

    return typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
  }

  /// <summary>
  /// Generate code from discovered mutations.
  /// </summary>
  private static void _generateMutationCode(
      SourceProductionContext context,
      ImmutableArray<RestMutationInfo?> mutations) {

    // Filter nulls to ensure type safety
    var validMutations = mutations.Where(m => m is not null).Select(m => m!).ToImmutableArray();

    if (validMutations.IsEmpty) {
      return;
    }

    // Load template
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(RestMutationEndpointGenerator).Assembly,
        "RestMutationEndpointTemplate.cs",
        "Whizbang.Transports.FastEndpoints.Generators.Templates"
    );

    // Determine namespace (use first mutation's namespace or default)
    var targetNamespace = validMutations[0].Namespace + ".Generated";

    // Generate endpoint classes
    var endpointClasses = new StringBuilder();
    var mutationInfoProps = new StringBuilder();

    foreach (var mutation in validMutations) {
      // Generate endpoint class
      var endpointCode = _generateEndpointClass(mutation);
      endpointClasses.AppendLine(endpointCode);
      endpointClasses.AppendLine();

      // Generate info property
      var infoCode = _generateMutationInfoProperty(mutation);
      mutationInfoProps.AppendLine(infoCode);
    }

    // Replace regions in template
    var result = template;

    // Replace header
    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC", CultureInfo.InvariantCulture);
    var header = $"// <auto-generated/>\n// Generated by RestMutationEndpointGenerator at {timestamp}\n// DO NOT EDIT - Changes will be overwritten\n#nullable enable";
    result = TemplateUtilities.ReplaceRegion(result, "HEADER", header);

    result = result.Replace("__NAMESPACE__", targetNamespace);
    result = result.Replace("__MUTATION_COUNT__", validMutations.Length.ToString(CultureInfo.InvariantCulture));
    result = result.Replace("__TIMESTAMP__", timestamp);

    result = TemplateUtilities.ReplaceRegion(result, "ENDPOINT_CLASSES", endpointClasses.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "MUTATION_INFO_PROPERTIES", mutationInfoProps.ToString());

    // Add source
    context.AddSource("WhizbangRestMutationEndpoints.g.cs", result);
  }

  /// <summary>
  /// Generate an endpoint class for a mutation based on its configuration.
  /// </summary>
  private static string _generateEndpointClass(RestMutationInfo mutation) {
    var sb = new StringBuilder();

    // Determine request type - either custom or the command itself
    var requestType = mutation.RequestTypeName ?? mutation.CommandTypeName;
    var requestTypeShort = mutation.RequestTypeName is not null
        ? _getShortTypeName(mutation.RequestTypeName)
        : mutation.CommandTypeNameShort;
    var usesCustomRequest = mutation.RequestTypeName is not null;

    sb.AppendLine($"/// <summary>");
    sb.AppendLine($"/// Generated REST mutation endpoint for {mutation.CommandTypeNameShort}.");
    sb.AppendLine($"/// Route: POST {mutation.RestRoute}");
    sb.AppendLine($"/// </summary>");
    sb.AppendLine($"public partial class {mutation.EndpointClassName}");
    sb.AppendLine($"    : RestMutationEndpointBase<{mutation.CommandTypeName}, {mutation.ResultTypeName}>,");
    sb.AppendLine($"      IEndpoint {{");
    sb.AppendLine($"  private readonly IDispatcher _dispatcher;");
    sb.AppendLine();
    sb.AppendLine($"  /// <summary>");
    sb.AppendLine($"  /// Creates a new instance of {mutation.EndpointClassName}.");
    sb.AppendLine($"  /// </summary>");
    sb.AppendLine($"  public {mutation.EndpointClassName}(IDispatcher dispatcher) {{");
    sb.AppendLine($"    _dispatcher = dispatcher;");
    sb.AppendLine($"  }}");
    sb.AppendLine();
    sb.AppendLine($"  /// <summary>");
    sb.AppendLine($"  /// Configures the endpoint route and HTTP method.");
    sb.AppendLine($"  /// </summary>");
    sb.AppendLine($"  public void Configure(IEndpointRouteBuilder routeBuilder) {{");
    sb.AppendLine($"    routeBuilder.MapPost(\"{mutation.RestRoute}\", HandleAsync);");
    sb.AppendLine($"  }}");
    sb.AppendLine();
    sb.AppendLine($"  /// <summary>");
    sb.AppendLine($"  /// Dispatches the command to the handler via IDispatcher.");
    sb.AppendLine($"  /// </summary>");
    sb.AppendLine($"  protected override async ValueTask<{mutation.ResultTypeName}> DispatchCommandAsync(");
    sb.AppendLine($"      {mutation.CommandTypeName} command,");
    sb.AppendLine($"      CancellationToken ct) {{");
    sb.AppendLine($"    return await _dispatcher.LocalInvokeAsync<{mutation.CommandTypeName}, {mutation.ResultTypeName}>(command, ct);");
    sb.AppendLine($"  }}");
    sb.AppendLine();

    if (usesCustomRequest) {
      sb.AppendLine($"  /// <summary>");
      sb.AppendLine($"  /// Handles the incoming HTTP request with custom request type.");
      sb.AppendLine($"  /// </summary>");
      sb.AppendLine($"  public async Task<{mutation.ResultTypeName}> HandleAsync(");
      sb.AppendLine($"      {requestType} request,");
      sb.AppendLine($"      CancellationToken ct) {{");
      sb.AppendLine($"    return await ExecuteWithRequestAsync(request, ct);");
      sb.AppendLine($"  }}");
    } else {
      sb.AppendLine($"  /// <summary>");
      sb.AppendLine($"  /// Handles the incoming HTTP request where command is the request.");
      sb.AppendLine($"  /// </summary>");
      sb.AppendLine($"  public async Task<{mutation.ResultTypeName}> HandleAsync(");
      sb.AppendLine($"      {mutation.CommandTypeName} command,");
      sb.AppendLine($"      CancellationToken ct) {{");
      sb.AppendLine($"    return await ExecuteAsync(command, ct);");
      sb.AppendLine($"  }}");
    }

    sb.AppendLine($"}}");

    return sb.ToString();
  }

  /// <summary>
  /// Extract short type name from fully qualified name.
  /// </summary>
  private static string _getShortTypeName(string fullyQualifiedName) {
    // Handle "global::Namespace.TypeName" format
    var name = fullyQualifiedName;
    if (name.StartsWith("global::", StringComparison.Ordinal)) {
      name = name.Substring(8);
    }
    var lastDot = name.LastIndexOf('.');
    return lastDot >= 0 ? name.Substring(lastDot + 1) : name;
  }

  /// <summary>
  /// Generate a mutation info property for diagnostics.
  /// </summary>
  private static string _generateMutationInfoProperty(RestMutationInfo mutation) {
    return $@"  /// <summary>
  /// Information about the {mutation.EndpointClassName} mutation endpoint.
  /// </summary>
  public static (string Route, string CommandType, string ResultType) {mutation.EndpointClassName}Info =>
      (""{mutation.RestRoute}"", ""{mutation.CommandTypeName}"", ""{mutation.ResultTypeName}"");";
  }
}
