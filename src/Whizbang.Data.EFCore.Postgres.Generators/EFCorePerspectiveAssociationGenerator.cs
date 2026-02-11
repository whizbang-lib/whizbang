using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;
using CancellationToken = System.Threading.CancellationToken;

namespace Whizbang.Data.EFCore.Postgres.Generators;

/// <summary>
/// Source generator that discovers Perspective implementations and generates EF Core-specific
/// database registration code for perspective → event type associations.
/// This generator is in the EF Core package so users not using EF Core don't get this code.
/// </summary>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveAssociationGeneratorTests.cs:Generator_WithPerspective_GeneratesEFCoreRegistrationMethodAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveAssociationGeneratorTests.cs:Generator_EmptyCompilation_GeneratesNothingAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveAssociationGeneratorTests.cs:Generator_MultiplePerspectives_GeneratesAllAssociationsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveAssociationGeneratorTests.cs:Generator_GeneratesJsonFormatForDatabaseAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveAssociationGeneratorTests.cs:Generator_AbstractClass_IsIgnoredAsync</tests>
[Generator]
public class EFCorePerspectiveAssociationGenerator : IIncrementalGenerator {
  private const string PERSPECTIVE_INTERFACE_NAME = "Whizbang.Core.Perspectives.IPerspectiveFor";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Filter for classes that have a base list (potential interface implementations)
    var perspectiveCandidates = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractPerspectiveAssociationInfo(ctx, ct)
    ).Where(static infos => infos is not null && infos.Length > 0)
     .SelectMany(static (infos, _) => infos!.ToImmutableArray());

    // Collect all perspectives and generate registration code
    var compilationAndPerspectives = context.CompilationProvider.Combine(perspectiveCandidates.Collect());

    context.RegisterSourceOutput(
        compilationAndPerspectives,
        static (ctx, data) => {
          var compilation = data.Left;
          var perspectives = data.Right;
          _generateEFCoreRegistrations(ctx, compilation, perspectives);
        }
    );
  }

  /// <summary>
  /// Extracts perspective association information from a class that implements IPerspectiveFor interfaces.
  /// </summary>
  private static PerspectiveAssociationInfo[]? _extractPerspectiveAssociationInfo(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    var classSymbol = semanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken) as INamedTypeSymbol;

    if (classSymbol is null) {
      return null;
    }

    // Skip abstract classes - they can't be instantiated
    if (classSymbol.IsAbstract) {
      return null;
    }

    // Look for all IPerspectiveFor<TModel, TEvent1, ...> interfaces (all variants)
    var perspectiveInterfaces = classSymbol.AllInterfaces
        .Where(i => {
          var originalDef = i.OriginalDefinition.ToDisplayString();
          return originalDef.Contains("IPerspectiveFor") && i.TypeArguments.Length > 1;
        })
        .ToList();

    if (perspectiveInterfaces.Count == 0) {
      return null;
    }

    var className = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    var simpleClassName = _getSimpleName(className);

    // Generate one PerspectiveAssociationInfo per event type
    var results = perspectiveInterfaces.SelectMany(perspectiveInterface => {
      // Extract event types (all except TModel at index 0)
      var eventTypeSymbols = perspectiveInterface.TypeArguments.Skip(1).ToArray();

      return eventTypeSymbols.Select(eventTypeSymbol => {
        var messageTypeName = _formatTypeNameForRuntime(eventTypeSymbol);

        return new PerspectiveAssociationInfo(
            PerspectiveClassName: simpleClassName,
            MessageTypeName: messageTypeName
        );
      });
    }).ToArray();

    return results;
  }

  /// <summary>
  /// Formats a type name for database storage (TypeName, AssemblyName format).
  /// </summary>
  private static string _formatTypeNameForRuntime(ITypeSymbol typeSymbol) {
    if (typeSymbol == null) {
      throw new ArgumentNullException(nameof(typeSymbol));
    }

    // Get fully qualified type name WITHOUT global:: prefix
    var typeName = typeSymbol.ToDisplayString(new SymbolDisplayFormat(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
    ));

    // Get assembly name (simple name only)
    var assemblyName = typeSymbol is IArrayTypeSymbol arrayType
        ? arrayType.ElementType.ContainingAssembly.Name
        : typeSymbol.ContainingAssembly.Name;

    return $"{typeName}, {assemblyName}";
  }

  /// <summary>
  /// Gets the simple name from a fully qualified type name.
  /// </summary>
  private static string _getSimpleName(string fullyQualifiedName) {
    // Handle arrays
    if (fullyQualifiedName.EndsWith("[]", StringComparison.Ordinal)) {
      var baseType = fullyQualifiedName[..^2];
      return _getSimpleName(baseType) + "[]";
    }

    var lastDot = fullyQualifiedName.LastIndexOf('.');
    return lastDot >= 0 ? fullyQualifiedName[(lastDot + 1)..] : fullyQualifiedName;
  }

  /// <summary>
  /// Generates the EF Core-specific registration code for perspective associations.
  /// </summary>
  private static void _generateEFCoreRegistrations(
      SourceProductionContext context,
      Compilation compilation,
      ImmutableArray<PerspectiveAssociationInfo> perspectives) {

    if (perspectives.IsEmpty) {
      return;
    }

    // Skip generation if this IS the library project itself
    if (compilation.AssemblyName == "Whizbang.Data.EFCore.Postgres") {
      return;
    }

    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";

    // Load template
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(EFCorePerspectiveAssociationGenerator).Assembly,
        "EFCorePerspectiveAssociationsTemplate.cs",
        "Whizbang.Data.EFCore.Postgres.Generators.Templates"
    );

    // Replace header
    template = TemplateUtilities.ReplaceRegion(
        template,
        "HEADER",
        $"// <auto-generated/>\n// Generated by Whizbang.Data.EFCore.Postgres.Generators.EFCorePerspectiveAssociationGenerator at {System.DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n// DO NOT EDIT - Changes will be overwritten\n#nullable enable"
    );

    // Replace namespace
    template = TemplateUtilities.ReplaceRegion(template, "NAMESPACE", $"namespace {namespaceName};");

    // Generate JSON associations
    var associations = new StringBuilder();
    int associationCount = 0;
    bool isFirstAssociation = true;

    foreach (var perspective in perspectives) {
      // Add comma separator (except for first item)
      if (!isFirstAssociation) {
        associations.AppendLine("    json.AppendLine(\",\");");
      }
      isFirstAssociation = false;

      // Generate C# code that appends JSON object
      associations.AppendLine($"    json.Append(\"    {{\");");
      associations.AppendLine($"    json.Append($\"\\\"MessageType\\\": \\\"{perspective.MessageTypeName}\\\", \");");
      associations.AppendLine("    json.Append(\"\\\"AssociationType\\\": \\\"perspective\\\", \");");
      associations.AppendLine($"    json.Append($\"\\\"TargetName\\\": \\\"{perspective.PerspectiveClassName}\\\", \");");
      associations.AppendLine("    json.Append(\"\\\"ServiceName\\\": \\\"\");");
      associations.AppendLine("    json.Append(serviceName);");
      associations.AppendLine("    json.Append(\"\\\"\");");
      associations.AppendLine("    json.Append(\"}\");");

      associationCount++;
    }

    // Replace placeholders
    template = TemplateUtilities.ReplaceRegion(template, "MESSAGE_ASSOCIATIONS_JSON", associations.ToString());
    template = template.Replace("__ASSOCIATION_COUNT__", associationCount.ToString(CultureInfo.InvariantCulture));

    context.AddSource("EFCorePerspectiveAssociations.g.cs", template);

    // Report diagnostic
    var descriptor = new DiagnosticDescriptor(
        id: "EFCORE001",
        title: "EF Core Perspective Association Generator",
        messageFormat: "Generated EF Core registration for {0} perspective association(s)",
        category: "Whizbang.Generator",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);
    context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, associationCount));
  }
}

/// <summary>
/// Value type containing perspective association information for EF Core generation.
/// </summary>
/// <param name="PerspectiveClassName">Simple name of the perspective class</param>
/// <param name="MessageTypeName">Database format message type name (TypeName, AssemblyName)</param>
internal sealed record PerspectiveAssociationInfo(
    string PerspectiveClassName,
    string MessageTypeName
);
