using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Generators;

/// <summary>
/// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_WithStringFilter_GeneratesRegistryAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_WithMultipleStringFilters_GeneratesAllMappingsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_WithEnumFilter_ExtractsDescriptionAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_WithEnumFilterNoDescription_UsesSymbolNameAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_WithMultipleCommands_GeneratesAllMappingsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_WithNoFilters_GeneratesEmptyRegistryAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_WithCustomDerivedAttribute_RecognizesFilterAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_WithMixedEnumAndStringFilters_GeneratesBothAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_GeneratesGetAllFiltersMethod_ForDiagnosticsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_UsesAssemblySpecificNamespace_AvoidingConflictsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_WithFilterOnNonCommand_ReportsErrorAsync</tests>
/// Discovers ICommand implementations with TopicFilter attributes
/// and generates AOT-compatible topic filter lookup registry.
/// Uses compile-time extraction of enum Description attributes for type-safe routing.
/// </summary>
/// <docs>source-generators/topic-filter-discovery</docs>
[Generator]
public class TopicFilterGenerator : IIncrementalGenerator {
  private const string ICOMMAND_INTERFACE = "Whizbang.Core.ICommand";
  private const string TOPIC_FILTER_ATTRIBUTE = "Whizbang.Core.TopicFilterAttribute";
  private const string DESCRIPTION_ATTRIBUTE = "System.ComponentModel.DescriptionAttribute";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Pipeline: Discover ICommand classes with TopicFilter attributes
    var topicFilters = context.SyntaxProvider.CreateSyntaxProvider(
        // Syntactic filtering: Classes/records with attributes (cheap, filters ~99% of nodes)
        predicate: static (node, _) =>
            (node is ClassDeclarationSyntax or RecordDeclarationSyntax) &&
            ((TypeDeclarationSyntax)node).AttributeLists.Count > 0,

        // Semantic analysis: Extract topic filter info (expensive, only on filtered nodes)
        transform: static (ctx, ct) => _extractTopicFilters(ctx, ct)
    ).Where(static infos => infos is not null && infos.Value.Length > 0);

    // Combine with compilation to get assembly name for namespace
    var compilationAndFilters = context.CompilationProvider.Combine(topicFilters.Collect());

    // Generate registry
    context.RegisterSourceOutput(
        compilationAndFilters,
        static (ctx, data) => {
          var compilation = data.Left;
          var allFilters = data.Right;

          // Flatten array of arrays to single list
          var filters = allFilters
              .Where(f => f.HasValue)
              .SelectMany(f => f!.Value)
              .ToImmutableArray();

          _generateRegistry(ctx, compilation, filters);
        }
    );
  }

  /// <summary>
  /// Extracts all topic filters from a class/record declaration.
  /// Returns array of TopicFilterInfo for all [TopicFilter] attributes found.
  /// Returns null if class doesn't implement ICommand or has no TopicFilter attributes.
  /// </summary>
  private static ImmutableArray<TopicFilterInfo>? _extractTopicFilters(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var typeDeclaration = (TypeDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    // Get class symbol
    var classSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) as INamedTypeSymbol;
    if (classSymbol is null) {
      return null;  // Early exit - Roslyn returned null or not a named type
    }

    // Check if implements ICommand
    var implementsICommand = classSymbol.AllInterfaces.Any(i =>
        i.ToDisplayString() == ICOMMAND_INTERFACE);

    if (!implementsICommand) {
      return null;  // Early exit - not a command
    }

    // Get fully qualified command type name
    var commandType = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    // Extract all TopicFilter attributes (AllowMultiple = true)
    var attributes = classSymbol.GetAttributes();
    var topicFilterAttrs = attributes.Where(attr => {
      var attrClass = attr.AttributeClass;
      if (attrClass is null) {
        return false;
      }

      // Check if attribute is TopicFilterAttribute or derived from it
      var currentClass = attrClass;
      while (currentClass is not null) {
        var fullName = currentClass.ToDisplayString();
        if (fullName == TOPIC_FILTER_ATTRIBUTE ||
            fullName.StartsWith(TOPIC_FILTER_ATTRIBUTE + "<", StringComparison.Ordinal)) {
          return true;
        }
        currentClass = currentClass.BaseType;
      }
      return false;
    }).ToList();

    if (topicFilterAttrs.Count == 0) {
      return null;  // Early exit - no TopicFilter attributes
    }

    // Extract filter strings from each attribute
    var filters = ImmutableArray.CreateBuilder<TopicFilterInfo>();

    foreach (var attr in topicFilterAttrs) {
      var filterString = _extractFilterString(attr, cancellationToken);
      if (filterString is not null) {
        filters.Add(new TopicFilterInfo(commandType, filterString));
      }
    }

    return filters.Count > 0 ? filters.ToImmutable() : null;
  }

  /// <summary>
  /// Extracts the filter string from a TopicFilter attribute.
  /// For string-based attributes: uses constructor argument directly.
  /// For enum-based attributes: extracts Description attribute or uses enum symbol name.
  /// </summary>
  private static string? _extractFilterString(
      AttributeData attribute,
      System.Threading.CancellationToken cancellationToken) {

    if (attribute.ConstructorArguments.Length == 0) {
      return null;  // No constructor arguments
    }

    var firstArg = attribute.ConstructorArguments[0];

    // Case 1: String-based filter (TopicFilterAttribute(string filter))
    if (firstArg.Type?.SpecialType == SpecialType.System_String) {
      return firstArg.Value?.ToString();
    }

    // Case 2: Enum-based filter (TopicFilterAttribute<TEnum>(TEnum value))
    if (firstArg.Type?.TypeKind == TypeKind.Enum) {
      var enumType = firstArg.Type;
      var enumValue = firstArg.Value;

      if (enumValue is null) {
        return null;
      }

      // Get the enum field for this value by matching the constant value
      // Note: enumValue is the numeric value (e.g., 0, 1, 2)
      var enumField = enumType.GetMembers()
          .OfType<IFieldSymbol>()
          .FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, enumValue));

      if (enumField is null) {
        return enumValue.ToString();  // Fallback - should be rare
      }

      // Try to extract Description attribute
      var descriptionAttr = enumField.GetAttributes()
          .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == DESCRIPTION_ATTRIBUTE);

      if (descriptionAttr is not null && descriptionAttr.ConstructorArguments.Length > 0) {
        var descriptionValue = descriptionAttr.ConstructorArguments[0].Value;
        if (descriptionValue is not null) {
          return descriptionValue.ToString();
        }
      }

      // No Description attribute - use enum symbol name
      return enumField.Name;
    }

    return null;
  }

  /// <summary>
  /// Generates the TopicFilterRegistry class with AOT-compatible lookups.
  /// </summary>
  private static void _generateRegistry(
      SourceProductionContext context,
      Compilation compilation,
      ImmutableArray<TopicFilterInfo> filters) {

    if (filters.IsEmpty) {
      // No filters found - skip generation or generate warning
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.NoTopicFiltersFound,
          Location.None
      ));
      return;
    }

    // Report discovered filters
    foreach (var filter in filters) {
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.TopicFilterDiscovered,
          Location.None,
          _getSimpleName(filter.CommandType),
          filter.Filter
      ));
    }

    // Group filters by command type (for multiple filters per command)
    var filtersByCommand = filters
        .GroupBy(f => f.CommandType)
        .ToDictionary(g => g.Key, g => g.Select(f => f.Filter).ToArray());

    // Generate registry source code
    var assemblyName = compilation.AssemblyName ?? "Generated";
    var namespaceName = $"{assemblyName}.Generated";

    var sb = new StringBuilder();

    // File header
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine($"// Generated by TopicFilterGenerator at {System.DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
    sb.AppendLine("// DO NOT EDIT - Changes will be overwritten");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();

    // Namespace
    sb.AppendLine($"namespace {namespaceName};");
    sb.AppendLine();

    // Using statements
    sb.AppendLine("using System;");
    sb.AppendLine("using System.Collections.Generic;");
    sb.AppendLine("using Whizbang.Core;");
    sb.AppendLine();

    // Class declaration
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Auto-generated registry for topic filter lookups.");
    sb.AppendLine($"/// Generated from {filters.Length} topic filter(s) across {filtersByCommand.Count} command(s).");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("public static class TopicFilterRegistry {");

    // GetTopicFilters<TCommand>() method
    sb.AppendLine("  /// <summary>");
    sb.AppendLine("  /// Gets all topic filters for the specified command type.");
    sb.AppendLine("  /// Returns empty array if command has no topic filters.");
    sb.AppendLine("  /// </summary>");
    sb.AppendLine("  /// <typeparam name=\"TCommand\">The command type to look up</typeparam>");
    sb.AppendLine("  /// <returns>Array of topic filter strings</returns>");
    sb.AppendLine("  public static string[] GetTopicFilters<TCommand>() where TCommand : ICommand {");

    foreach (var kvp in filtersByCommand) {
      var commandType = kvp.Key;
      var commandFilters = kvp.Value;

      sb.AppendLine($"    if (typeof(TCommand) == typeof({commandType})) {{");

      // Generate array of filters
      var filterArrayContent = string.Join(", ", commandFilters.Select(f => $"\"{_escapeString(f)}\""));
      sb.AppendLine($"      return new[] {{ {filterArrayContent} }};");

      sb.AppendLine("    }");
    }

    sb.AppendLine("    return Array.Empty<string>();");
    sb.AppendLine("  }");
    sb.AppendLine();

    // GetAllFilters() method for diagnostics/tooling
    sb.AppendLine("  /// <summary>");
    sb.AppendLine("  /// Gets all topic filters for all commands (for diagnostics and tooling).");
    sb.AppendLine("  /// </summary>");
    sb.AppendLine("  /// <returns>Dictionary mapping command names to their topic filters</returns>");
    sb.AppendLine("  public static IReadOnlyDictionary<string, string[]> GetAllFilters() {");
    sb.AppendLine("    return new Dictionary<string, string[]> {");

    foreach (var kvp in filtersByCommand) {
      var commandType = kvp.Key;
      var commandFilters = kvp.Value;

      var simpleName = _getSimpleName(commandType);
      var filterArrayContent = string.Join(", ", commandFilters.Select(f => $"\"{_escapeString(f)}\""));
      sb.AppendLine($"      {{ \"{_escapeString(simpleName)}\", new[] {{ {filterArrayContent} }} }},");
    }

    sb.AppendLine("    };");
    sb.AppendLine("  }");

    // Close class
    sb.AppendLine("}");

    // Add source
    context.AddSource("TopicFilterRegistry.g.cs", sb.ToString());
  }

  /// <summary>
  /// Gets the simple name from a fully qualified type name.
  /// E.g., "global::MyApp.Commands.CreateOrder" -> "CreateOrder"
  /// </summary>
  private static string _getSimpleName(string fullyQualifiedName) {
    var lastDot = fullyQualifiedName.LastIndexOf('.');
    return lastDot >= 0 ? fullyQualifiedName[(lastDot + 1)..] : fullyQualifiedName;
  }

  /// <summary>
  /// Escapes a string for use in C# string literal.
  /// </summary>
  private static string _escapeString(string value) {
    return value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\n", "\\n")
        .Replace("\r", "\\r")
        .Replace("\t", "\\t");
  }
}
