using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Transports.HotChocolate.Generators;

/// <summary>
/// Source generator that discovers [CommandEndpoint] attributes with GraphQLMutation
/// and generates HotChocolate mutation type classes for Whizbang commands.
/// </summary>
[Generator]
public sealed class GraphQLMutationTypeGenerator : IIncrementalGenerator {
  private const string COMMAND_ENDPOINT_ATTRIBUTE_PREFIX = "Whizbang.Transports.Mutations.CommandEndpointAttribute";

  /// <inheritdoc />
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover types with [CommandEndpoint] attribute that have GraphQLMutation set
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
  /// Semantic transform: extract mutation information if type has [CommandEndpoint] with GraphQLMutation.
  /// </summary>
  private static GraphQLMutationInfo? _extractMutationInfo(
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

    // Get the GraphQLMutation name from the attribute
    var graphQLMutationName = _getAttributeStringValue(commandEndpointAttr, "GraphQLMutation");

    // If no GraphQLMutation, skip this command (might be REST-only)
    if (string.IsNullOrEmpty(graphQLMutationName)) {
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

    // Generate mutation class name from command name
    var mutationClassName = symbol.Name + "Mutation";

    return new GraphQLMutationInfo(
        CommandTypeName: commandType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        CommandTypeNameShort: commandType.Name,
        ResultTypeName: resultType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        ResultTypeNameShort: resultType.Name,
        GraphQLMutationName: graphQLMutationName!,
        RequestTypeName: requestTypeName,
        Namespace: symbol.ContainingNamespace.ToDisplayString(),
        MutationClassName: mutationClassName
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
      ImmutableArray<GraphQLMutationInfo?> mutations) {

    // Filter nulls to ensure type safety
    var validMutations = mutations.Where(m => m is not null).Select(m => m!).ToImmutableArray();

    if (validMutations.IsEmpty) {
      return;
    }

    // Load template
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(GraphQLMutationTypeGenerator).Assembly,
        "GraphQLMutationTypeTemplate.cs",
        "Whizbang.Transports.HotChocolate.Generators.Templates"
    );

    // Determine namespace (use first mutation's namespace or default)
    var targetNamespace = validMutations[0].Namespace + ".Generated";

    // Generate mutation classes
    var mutationClasses = new StringBuilder();
    var mutationInfoProps = new StringBuilder();

    foreach (var mutation in validMutations) {
      // Generate mutation class
      var mutationCode = _generateMutationClass(mutation);
      mutationClasses.AppendLine(mutationCode);
      mutationClasses.AppendLine();

      // Generate info property
      var infoCode = _generateMutationInfoProperty(mutation);
      mutationInfoProps.AppendLine(infoCode);
    }

    // Replace regions in template
    var result = template;

    // Replace header
    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC", CultureInfo.InvariantCulture);
    var header = $"// <auto-generated/>\n// Generated by GraphQLMutationTypeGenerator at {timestamp}\n// DO NOT EDIT - Changes will be overwritten\n#nullable enable";
    result = TemplateUtilities.ReplaceRegion(result, "HEADER", header);

    result = result.Replace("__NAMESPACE__", targetNamespace);
    result = result.Replace("__MUTATION_COUNT__", validMutations.Length.ToString(CultureInfo.InvariantCulture));
    result = result.Replace("__TIMESTAMP__", timestamp);

    result = TemplateUtilities.ReplaceRegion(result, "MUTATION_CLASSES", mutationClasses.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "MUTATION_INFO_PROPERTIES", mutationInfoProps.ToString());

    // Add source
    context.AddSource("WhizbangGraphQLMutations.g.cs", result);
  }

  /// <summary>
  /// Generate a mutation class for a mutation based on its configuration.
  /// </summary>
  private static string _generateMutationClass(GraphQLMutationInfo mutation) {
    var sb = new StringBuilder();

    // Determine parameter type - either custom request or command itself
    var parameterType = mutation.RequestTypeName ?? mutation.CommandTypeName;
    var parameterName = mutation.RequestTypeName is not null ? "request" : "command";
    var usesCustomRequest = mutation.RequestTypeName is not null;

    // Generate method name from GraphQL mutation name (capitalize first letter, add Async)
    var methodName = char.ToUpperInvariant(mutation.GraphQLMutationName[0])
        + mutation.GraphQLMutationName.Substring(1) + "Async";

    sb.AppendLine($"/// <summary>");
    sb.AppendLine($"/// Generated GraphQL mutation for {mutation.CommandTypeNameShort}.");
    sb.AppendLine($"/// GraphQL Field: {mutation.GraphQLMutationName}");
    sb.AppendLine($"/// </summary>");
    sb.AppendLine($"[ExtendObjectType(HotChocolate.Language.OperationTypeNames.Mutation)]");
    sb.AppendLine($"public partial class {mutation.MutationClassName}");
    sb.AppendLine($"    : GraphQLMutationBase<{mutation.CommandTypeName}, {mutation.ResultTypeName}> {{");
    sb.AppendLine($"  private readonly IDispatcher _dispatcher;");
    sb.AppendLine();
    sb.AppendLine($"  /// <summary>");
    sb.AppendLine($"  /// Creates a new instance of {mutation.MutationClassName}.");
    sb.AppendLine($"  /// </summary>");
    sb.AppendLine($"  public {mutation.MutationClassName}(IDispatcher dispatcher) {{");
    sb.AppendLine($"    _dispatcher = dispatcher;");
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

    sb.AppendLine($"  /// <summary>");
    sb.AppendLine($"  /// GraphQL mutation field: {mutation.GraphQLMutationName}");
    sb.AppendLine($"  /// </summary>");
    sb.AppendLine($"  public async Task<{mutation.ResultTypeName}> {methodName}(");
    sb.AppendLine($"      {parameterType} {parameterName},");
    sb.AppendLine($"      CancellationToken ct) {{");

    if (usesCustomRequest) {
      sb.AppendLine($"    return await ExecuteWithRequestAsync({parameterName}, ct);");
    } else {
      sb.AppendLine($"    return await ExecuteAsync({parameterName}, ct);");
    }

    sb.AppendLine($"  }}");
    sb.AppendLine($"}}");

    return sb.ToString();
  }

  /// <summary>
  /// Generate a mutation info property for diagnostics.
  /// </summary>
  private static string _generateMutationInfoProperty(GraphQLMutationInfo mutation) {
    return $@"  /// <summary>
  /// Information about the {mutation.MutationClassName} GraphQL mutation.
  /// </summary>
  public static (string MutationName, string CommandType, string ResultType) {mutation.MutationClassName}Info =>
      (""{mutation.GraphQLMutationName}"", ""{mutation.CommandTypeName}"", ""{mutation.ResultTypeName}"");";
  }
}
