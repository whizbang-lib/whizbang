using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

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
    // Combine compilation with discovered properties to get assembly name for namespace
    var compilationAndProperties = context.CompilationProvider.Combine(aggregateIdProperties.Collect());

    context.RegisterSourceOutput(
        compilationAndProperties,
        static (ctx, data) => {
          var compilation = data.Left;
          var properties = data.Right;
          GenerateAggregateIdExtractors(ctx, compilation, properties!);
        }
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

    // Defensive guard: throws if Roslyn returns null (indicates compiler bug)
    // See RoslynGuards.cs for rationale - no branch created, eliminates coverage gap
    var typeSymbol = RoslynGuards.GetTypeSymbolFromNode(context.Node, context.SemanticModel, cancellationToken);

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
    var isNullableGuid = RoslynGuards.IsNullableOfType(propertyType, "System.Guid");

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
  /// Generates the AggregateIdExtractors.g.cs file with static extraction methods
  /// and DI wrapper class for zero-reflection PolicyContext integration.
  /// Uses assembly-specific namespace to avoid conflicts when multiple assemblies use Whizbang.
  /// </summary>
  private static void GenerateAggregateIdExtractors(
      SourceProductionContext context,
      Compilation compilation,
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

    // Generate source code with DI wrapper
    var source = validProperties.IsEmpty
        ? GenerateEmptyExtractorRegistry(compilation)
        : GenerateExtractorRegistryWithDI(compilation, validProperties);

    context.AddSource("AggregateIdExtractors.g.cs", source);
  }

  /// <summary>
  /// Generates extractor registry with DI wrapper for zero-reflection integration.
  /// Uses templates and snippets for code generation (following project standards).
  /// Uses assembly-specific namespace to avoid conflicts when multiple assemblies use Whizbang.
  /// </summary>
  private static string GenerateExtractorRegistryWithDI(Compilation compilation, ImmutableArray<AggregateIdInfo> properties) {
    // Determine namespace from assembly name
    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";

    // Load template
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(AggregateIdGenerator).Assembly,
        "AggregateIdExtractorsTemplate.cs"
    );

    // Replace header with timestamp
    template = TemplateUtilities.ReplaceHeaderRegion(typeof(AggregateIdGenerator).Assembly, template);

    // Replace namespace region with assembly-specific namespace
    template = TemplateUtilities.ReplaceRegion(template, "NAMESPACE", $"namespace {namespaceName};");

    // Generate extractor cases using snippets
    var extractorSnippet = TemplateUtilities.ExtractSnippet(
        typeof(AggregateIdGenerator).Assembly,
        "AggregateIdSnippets.cs",
        "EXTRACTOR"
    );

    var extractorsCode = new StringBuilder();
    for (int i = 0; i < properties.Length; i++) {
      var prop = properties[i];
      var extractorCode = extractorSnippet
          .Replace("__MESSAGE_TYPE__", prop.MessageType)
          .Replace("__PROPERTY_NAME__", prop.PropertyName);

      extractorsCode.AppendLine(extractorCode);

      // Add blank line between extractors (but not after the last one)
      if (i < properties.Length - 1) {
        extractorsCode.AppendLine();
      }
    }

    template = TemplateUtilities.ReplaceRegion(template, "EXTRACTORS", extractorsCode.ToString().TrimEnd());

    // Generate DI registration using snippet
    var diSnippet = TemplateUtilities.ExtractSnippet(
        typeof(AggregateIdGenerator).Assembly,
        "AggregateIdSnippets.cs",
        "DI_REGISTRATION"
    );

    var diCode = diSnippet.Replace("__COUNT__", properties.Length.ToString());

    template = TemplateUtilities.ReplaceRegion(template, "DI_REGISTRATION", diCode);

    return template;
  }

  /// <summary>
  /// Generates empty extractor registry when no [AggregateId] attributes are found.
  /// Uses assembly-specific namespace to avoid conflicts when multiple assemblies use Whizbang.
  /// </summary>
  private static string GenerateEmptyExtractorRegistry(Compilation compilation) {
    // Determine namespace from assembly name
    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";

    return $$"""
// <auto-generated/>
// Generated by Whizbang AggregateIdGenerator
// NO [AggregateId] ATTRIBUTES FOUND - Empty registry generated
#nullable enable

using System;

namespace {{namespaceName}};

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
    return lastDot >= 0 ? fullyQualifiedName[(lastDot + 1)..] : fullyQualifiedName;
  }
}
