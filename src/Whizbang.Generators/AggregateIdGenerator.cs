using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Generators;

/// <summary>
/// Incremental source generator that discovers properties marked with [AggregateId]
/// and generates compile-time extractors for PolicyContext, eliminating reflection.
/// </summary>
[Generator]
public class AggregateIdGenerator : IIncrementalGenerator {
  private const string AGGREGATE_ID_ATTRIBUTE = "Whizbang.Core.AggregateIdAttribute";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover properties with [AggregateId] attribute
    // Filter for types that could have properties with attributes
    var aggregateIdProperties = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => IsTypeWithAttributes(node),
        transform: static (ctx, ct) => ExtractAggregateIdInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Generate extractor registry from collected properties
    context.RegisterSourceOutput(
        aggregateIdProperties.Collect(),
        static (ctx, properties) => GenerateAggregateIdExtractors(ctx, properties!)
    );
  }

  /// <summary>
  /// Syntactic predicate: checks if node is a type that could have properties with attributes.
  /// This is a fast check before expensive semantic analysis.
  /// </summary>
  private static bool IsTypeWithAttributes(SyntaxNode node) {
    // Check for records or classes (message types)
    if (node is RecordDeclarationSyntax record) {
      return true;
    }

    if (node is ClassDeclarationSyntax @class) {
      return true;
    }

    return false;
  }

  /// <summary>
  /// Extracts aggregate ID information from a type declaration.
  /// Returns null if the type doesn't have any properties marked with [AggregateId].
  /// Tracks diagnostics for reporting during generation.
  /// </summary>
  private static AggregateIdInfo? ExtractAggregateIdInfo(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    // Get the type symbol
    var typeSymbol = context.SemanticModel.GetDeclaredSymbol(context.Node, cancellationToken) as INamedTypeSymbol;
    if (typeSymbol is null) {
      return null;
    }

    // Find all properties with [AggregateId] attribute (including inherited)
    var aggregateIdProperties = typeSymbol.GetMembers()
        .OfType<IPropertySymbol>()
        .Where(p => HasAggregateIdAttribute(p))
        .ToList();

    // Check base types for inherited properties
    var baseType = typeSymbol.BaseType;
    while (baseType != null && baseType.SpecialType != SpecialType.System_Object) {
      var inheritedProperties = baseType.GetMembers()
          .OfType<IPropertySymbol>()
          .Where(p => HasAggregateIdAttribute(p));
      aggregateIdProperties.AddRange(inheritedProperties);
      baseType = baseType.BaseType;
    }

    // No [AggregateId] attributes found
    if (aggregateIdProperties.Count == 0) {
      return null;
    }

    // Get the first property (if multiple, we'll track for warning later)
    var firstProperty = aggregateIdProperties[0];

    // Validate property type is Guid or Guid?
    var propertyType = firstProperty.Type;
    var isGuid = propertyType.SpecialType == SpecialType.None &&
                 propertyType.ToDisplayString() == "System.Guid";
    var isNullableGuid = propertyType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                        (propertyType as INamedTypeSymbol)?.TypeArguments[0].ToDisplayString() == "System.Guid";

    // Store diagnostics to report during generation
    var hasMultiple = aggregateIdProperties.Count > 1;
    var hasInvalidType = !isGuid && !isNullableGuid;

    // Return value-type record with discovered information
    return new AggregateIdInfo(
        MessageType: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        PropertyName: firstProperty.Name,
        IsNullable: isNullableGuid,
        HasMultipleAttributes: hasMultiple,
        HasInvalidType: hasInvalidType
    );
  }

  /// <summary>
  /// Checks if a property has the [AggregateId] attribute.
  /// </summary>
  private static bool HasAggregateIdAttribute(IPropertySymbol property) {
    return property.GetAttributes().Any(a =>
        a.AttributeClass?.ToDisplayString() == AGGREGATE_ID_ATTRIBUTE);
  }

  /// <summary>
  /// Generates the AggregateIdExtractors.g.cs file with static extraction methods.
  /// </summary>
  private static void GenerateAggregateIdExtractors(
      SourceProductionContext context,
      ImmutableArray<AggregateIdInfo> properties) {

    // Separate valid and invalid properties
    var validProperties = properties.Where(p => !p.HasInvalidType).ToImmutableArray();
    var invalidProperties = properties.Where(p => p.HasInvalidType).ToImmutableArray();

    // Report diagnostics for discovered and invalid properties
    foreach (var prop in validProperties) {
      // Info: Property discovered
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.AggregateIdPropertyDiscovered,
          Location.None,
          GetSimpleName(prop.MessageType),
          prop.PropertyName
      ));

      // Warning: Multiple attributes
      if (prop.HasMultipleAttributes) {
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.MultipleAggregateIdAttributes,
            Location.None,
            GetSimpleName(prop.MessageType),
            prop.PropertyName
        ));
      }
    }

    // Error: Invalid property type
    foreach (var prop in invalidProperties) {
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.AggregateIdMustBeGuid,
          Location.None,
          GetSimpleName(prop.MessageType),
          prop.PropertyName
      ));
    }

    // Generate source code (only for valid properties)
    var source = validProperties.IsEmpty
        ? GenerateEmptyExtractorRegistry()
        : GenerateExtractorRegistry(validProperties);

    context.AddSource("AggregateIdExtractors.g.cs", source);
  }

  /// <summary>
  /// Generates extractor registry with extraction logic for discovered properties.
  /// </summary>
  private static string GenerateExtractorRegistry(ImmutableArray<AggregateIdInfo> properties) {
    var sb = new StringBuilder();

    // Header
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine($"// Generated by Whizbang source generator at {System.DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
    sb.AppendLine("// DO NOT EDIT - Changes will be overwritten");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();
    sb.AppendLine("using System;");
    sb.AppendLine("using System.Diagnostics.CodeAnalysis;");
    sb.AppendLine();
    sb.AppendLine("namespace Whizbang.Core.Generated;");
    sb.AppendLine();

    // Summary
    sb.AppendLine("/// <summary>");
    sb.AppendLine($"/// Generated aggregate ID extractor registry for {properties.Length} message type(s).");
    sb.AppendLine("/// Provides zero-reflection extraction of aggregate IDs from messages.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("[ExcludeFromCodeCoverage]");
    sb.AppendLine("public static class AggregateIdExtractors {");

    // ExtractAggregateId method
    sb.AppendLine("  /// <summary>");
    sb.AppendLine("  /// Extracts aggregate ID from a message using compile-time type information.");
    sb.AppendLine("  /// Zero reflection - uses generated type switches for optimal performance.");
    sb.AppendLine("  /// </summary>");
    sb.AppendLine("  /// <param name=\"message\">The message instance</param>");
    sb.AppendLine("  /// <param name=\"messageType\">The runtime type of the message</param>");
    sb.AppendLine("  /// <returns>The aggregate ID if found and marked with [AggregateId], otherwise null</returns>");
    sb.AppendLine("  public static Guid? ExtractAggregateId(object message, Type messageType) {");

    // Generate type switches for each message type
    foreach (var prop in properties) {
      sb.AppendLine($"    if (messageType == typeof({prop.MessageType})) {{");
      sb.AppendLine($"      var typed = ({prop.MessageType})message;");
      sb.AppendLine($"      return typed.{prop.PropertyName};");
      sb.AppendLine("    }");
      sb.AppendLine();
    }

    // Fallback for unknown types
    sb.AppendLine("    return null;");
    sb.AppendLine("  }");
    sb.AppendLine("}");

    return sb.ToString();
  }

  /// <summary>
  /// Generates empty extractor registry when no [AggregateId] attributes are found.
  /// </summary>
  private static string GenerateEmptyExtractorRegistry() {
    return """
// <auto-generated/>
// Generated by Whizbang source generator
// NO [AggregateId] ATTRIBUTES FOUND - Empty registry generated
#nullable enable

using System;

namespace Whizbang.Core.Generated;

/// <summary>
/// Generated aggregate ID extractor registry (empty - no [AggregateId] attributes found).
/// </summary>
public static class AggregateIdExtractors {
  /// <summary>
  /// Extracts aggregate ID from a message.
  /// Returns null because no types have [AggregateId] attributes.
  /// </summary>
  public static Guid? ExtractAggregateId(object message, Type messageType) {
    return null;
  }
}
""";
  }

  /// <summary>
  /// Gets the simple name from a fully qualified type name.
  /// E.g., "global::MyApp.Commands.CreateOrder" -> "CreateOrder"
  /// </summary>
  private static string GetSimpleName(string fullyQualifiedName) {
    var lastDot = fullyQualifiedName.LastIndexOf('.');
    return lastDot >= 0 ? fullyQualifiedName.Substring(lastDot + 1) : fullyQualifiedName;
  }
}
