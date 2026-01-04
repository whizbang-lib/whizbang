using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_EmptyCompilation_GeneratesNothingAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_SinglePerspectiveOneEvent_GeneratesRegistrationAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_SinglePerspectiveMultipleEvents_GeneratesMultipleRegistrationsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_MultiplePerspectives_GeneratesAllRegistrationsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_AbstractClass_IsIgnoredAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_GeneratesDiagnosticForDiscoveredPerspectiveAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_GeneratedCodeUsesCorrectNamespaceAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_UsesFullyQualifiedTypeNamesAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_ReturnsServiceCollectionForMethodChainingAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_ClassWithoutIPerspectiveOf_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_ArrayEventType_HandlesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_TypeInGlobalNamespace_HandlesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_NestedClass_DiscoversCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:PerspectiveDiscoveryGenerator_InterfaceWithPerspectiveOf_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs:Generator_WithArrayEventType_SimplifiesInDiagnosticAsync</tests>
/// Incremental source generator that discovers IPerspectiveFor implementations
/// and generates DI registration code.
/// Perspectives are registered as Scoped services and updated via Event Store.
/// </summary>
[Generator]
public class PerspectiveDiscoveryGenerator : IIncrementalGenerator {
  private const string PERSPECTIVE_INTERFACE_NAME = "Whizbang.Core.Perspectives.IPerspectiveFor";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Filter for classes that have a base list (potential interface implementations)
    var perspectiveCandidates = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractPerspectiveInfos(ctx, ct)
    ).Where(static infos => infos is not null && infos.Length > 0)
     .SelectMany(static (infos, _) => infos!.ToImmutableArray());

    // Collect all perspectives and generate registration code
    // Combine compilation with discovered perspectives to get assembly name for namespace
    var compilationAndPerspectives = context.CompilationProvider.Combine(perspectiveCandidates.Collect());

    context.RegisterSourceOutput(
        compilationAndPerspectives,
        static (ctx, data) => {
          var compilation = data.Left;
          var perspectives = data.Right;
          _generatePerspectiveRegistrations(ctx, compilation, perspectives);
        }
    );
  }

  /// <summary>
  /// Extracts perspective information from a class declaration.
  /// Returns array of PerspectiveInfo (one per implemented interface).
  /// Supports both patterns:
  /// - Single variadic: IPerspectiveFor&lt;TModel, TEvent1, TEvent2, ...&gt;
  /// - Multiple separate: IPerspectiveFor&lt;TModel, TEvent1&gt;, IPerspectiveFor&lt;TModel, TEvent2&gt;
  /// </summary>
  private static PerspectiveInfo[]? _extractPerspectiveInfos(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    // Defensive guard: throws if Roslyn returns null (indicates compiler bug)
    // See RoslynGuards.cs for rationale - no branch created, eliminates coverage gap
    var classSymbol = RoslynGuards.GetClassSymbolOrThrow(classDeclaration, semanticModel, cancellationToken);

    // Skip abstract classes - they can't be instantiated
    if (classSymbol.IsAbstract) {
      return null;
    }

    // Look for all IPerspectiveFor<TModel, TEvent1, ...> interfaces (all variants)
    // Check if interface name contains "IPerspectiveFor" (case-sensitive)
    // Skip the marker base interface (has only 1 type argument)
    var perspectiveInterfaces = classSymbol.AllInterfaces
        .Where(i => {
          var originalDef = i.OriginalDefinition.ToDisplayString();
          return originalDef.Contains("IPerspectiveFor") && i.TypeArguments.Length > 1;
        })
        .ToList();

    if (perspectiveInterfaces.Count == 0) {
      return null;
    }

    // Get model type and StreamKey property (same across all interfaces for this class)
    var modelType = perspectiveInterfaces.First().TypeArguments[0];

    string? streamKeyPropertyName = null;
    foreach (var member in modelType.GetMembers()) {
      if (member is IPropertySymbol property) {
        var hasStreamKeyAttribute = property.GetAttributes()
            .Any(a => a.AttributeClass?.ToDisplayString() == "Whizbang.Core.StreamKeyAttribute");

        if (hasStreamKeyAttribute) {
          streamKeyPropertyName = property.Name;
          break;
        }
      }
    }

    var className = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    var results = new List<PerspectiveInfo>();

    // Generate one PerspectiveInfo per implemented interface
    foreach (var perspectiveInterface in perspectiveInterfaces) {
      // Extract all type arguments: [TModel, TEvent1, TEvent2, ...]
      var typeArguments = perspectiveInterface.TypeArguments
          .Select(t => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
          .ToArray();

      // Extract event types (all except TModel at index 0) for validation and diagnostics
      var eventTypeSymbols = perspectiveInterface.TypeArguments.Skip(1).ToArray();
      var eventTypes = eventTypeSymbols
          .Select(t => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
          .ToArray();

      // Validate StreamKey for each event type and collect errors + StreamKey info
      var validationErrors = new List<EventValidationError>();
      var eventStreamKeys = new List<EventStreamKeyInfo>();

      foreach (var eventTypeSymbol in eventTypeSymbols) {
        var error = _validateEventStreamKey(eventTypeSymbol);
        if (error != null) {
          validationErrors.Add(error);
        } else {
          // Extract StreamKey property name (only if valid)
          var streamKeyProp = _extractStreamKeyProperty(eventTypeSymbol);
          if (streamKeyProp != null) {
            eventStreamKeys.Add(new EventStreamKeyInfo(
                EventTypeName: eventTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                StreamKeyPropertyName: streamKeyProp
            ));
          }
        }
      }

      results.Add(new PerspectiveInfo(
          ClassName: className,
          InterfaceTypeArguments: typeArguments,
          EventTypes: eventTypes,
          StreamKeyPropertyName: streamKeyPropertyName,
          EventStreamKeys: eventStreamKeys.Count > 0 ? eventStreamKeys.ToArray() : null,
          EventValidationErrors: validationErrors.Count > 0 ? validationErrors.ToArray() : null
      ));
    }

    return results.ToArray();
  }

  /// <summary>
  /// Validates that an event type has exactly one property marked with [StreamKey].
  /// Returns validation error if found, null if valid.
  /// Handles array types by validating the element type.
  /// </summary>
  private static EventValidationError? _validateEventStreamKey(ITypeSymbol eventTypeSymbol) {
    // If this is an array type, validate the element type instead
    var typeToValidate = eventTypeSymbol;
    if (eventTypeSymbol is IArrayTypeSymbol arrayType) {
      typeToValidate = arrayType.ElementType;
    }

    var streamKeyProperties = new List<string>();

    foreach (var member in typeToValidate.GetMembers()) {
      if (member is IPropertySymbol property) {
        var hasStreamKeyAttribute = property.GetAttributes()
            .Any(a => a.AttributeClass?.ToDisplayString() == "Whizbang.Core.StreamKeyAttribute");

        if (hasStreamKeyAttribute) {
          streamKeyProperties.Add(property.Name);
        }
      }
    }

    var eventTypeName = typeToValidate.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    var simpleEventName = _getSimpleName(eventTypeName);

    if (streamKeyProperties.Count == 0) {
      return new EventValidationError(simpleEventName, StreamKeyErrorType.MissingStreamKey);
    } else if (streamKeyProperties.Count > 1) {
      return new EventValidationError(simpleEventName, StreamKeyErrorType.MultipleStreamKeys);
    }

    return null;
  }

  /// <summary>
  /// Extracts the StreamKey property name from an event type.
  /// Returns the property name if exactly one [StreamKey] is found, null otherwise.
  /// Handles array types by extracting from the element type.
  /// </summary>
  private static string? _extractStreamKeyProperty(ITypeSymbol eventTypeSymbol) {
    // If this is an array type, extract from the element type instead
    var typeToExtract = eventTypeSymbol;
    if (eventTypeSymbol is IArrayTypeSymbol arrayType) {
      typeToExtract = arrayType.ElementType;
    }

    foreach (var member in typeToExtract.GetMembers()) {
      if (member is IPropertySymbol property) {
        var hasStreamKeyAttribute = property.GetAttributes()
            .Any(a => a.AttributeClass?.ToDisplayString() == "Whizbang.Core.StreamKeyAttribute");

        if (hasStreamKeyAttribute) {
          return property.Name;
        }
      }
    }

    return null;
  }

  /// <summary>
  /// Generates the perspective registration code for all discovered perspectives.
  /// Creates an AddWhizbangPerspectives extension method that registers all perspectives as Scoped services.
  /// Uses assembly-specific namespace to avoid conflicts when multiple assemblies use Whizbang.
  /// </summary>
  private static void _generatePerspectiveRegistrations(
      SourceProductionContext context,
      Compilation compilation,
      ImmutableArray<PerspectiveInfo> perspectives) {

    if (perspectives.IsEmpty) {
      // No perspectives found - skip code generation (WHIZ002 already handles this in ReceptorDiscoveryGenerator)
      return;
    }

    // Report each discovered perspective and any validation errors
    foreach (var perspective in perspectives) {
      var eventNames = string.Join(", ", perspective.EventTypes.Select(_getSimpleName));
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.PerspectiveDiscovered,
          Location.None,
          _getSimpleName(perspective.ClassName),
          eventNames
      ));

      // Report validation errors for this perspective
      if (perspective.EventValidationErrors != null) {
        foreach (var error in perspective.EventValidationErrors) {
          var simplePerspectiveName = _getSimpleName(perspective.ClassName);

          if (error.ErrorType == StreamKeyErrorType.MissingStreamKey) {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.PerspectiveEventMissingStreamKey,
                Location.None,
                error.EventTypeName,
                simplePerspectiveName
            ));
          } else if (error.ErrorType == StreamKeyErrorType.MultipleStreamKeys) {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.PerspectiveEventMultipleStreamKeys,
                Location.None,
                error.EventTypeName
            ));
          }
        }
      }
    }

    var registrationSource = _generateRegistrationSource(compilation, perspectives);
    context.AddSource("PerspectiveRegistrations.g.cs", registrationSource);
  }

  /// <summary>
  /// Generates the C# source code for the registration extension method.
  /// Uses template-based generation for IDE support.
  /// Handles perspectives that implement multiple IPerspectiveFor&lt;TModel, TEvent&gt; interfaces.
  /// Uses assembly-specific namespace to avoid conflicts when multiple assemblies use Whizbang.
  /// </summary>
  private static string _generateRegistrationSource(Compilation compilation, ImmutableArray<PerspectiveInfo> perspectives) {
    // Determine namespace from assembly name
    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";

    // Load template from embedded resource
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(PerspectiveDiscoveryGenerator).Assembly,
        "PerspectiveRegistrationsTemplate.cs"
    );

    // Load registration snippet
    var registrationSnippet = TemplateUtilities.ExtractSnippet(
        typeof(PerspectiveDiscoveryGenerator).Assembly,
        "PerspectiveSnippets.cs",
        "PERSPECTIVE_REGISTRATION_SNIPPET"
    );

    // Generate registration calls - one per perspective
    var registrations = new StringBuilder();
    int totalRegistrations = 0;

    foreach (var perspective in perspectives) {
      // Build type arguments string: "TModel, TEvent1, TEvent2, ..."
      var typeArgs = string.Join(", ", perspective.InterfaceTypeArguments);

      var generatedCode = registrationSnippet
          .Replace("__PERSPECTIVE_INTERFACE__", PERSPECTIVE_INTERFACE_NAME)
          .Replace("__TYPE_ARGUMENTS__", typeArgs)
          .Replace("__PERSPECTIVE_CLASS__", perspective.ClassName);

      registrations.AppendLine(TemplateUtilities.IndentCode(generatedCode, "            "));
      totalRegistrations++;
    }

    // Generate message associations JSON for database registration
    var associations = new StringBuilder();
    int associationCount = 0;
    bool isFirstAssociation = true;

    foreach (var perspective in perspectives) {
      // Extract perspective class name (without namespace)
      var perspectiveClassName = _getSimpleName(perspective.ClassName);

      // Each event type creates one association
      foreach (var eventType in perspective.EventTypes) {
        // Add comma separator (except for first item)
        if (!isFirstAssociation) {
          associations.AppendLine("    json.AppendLine(\",\");");
        }
        isFirstAssociation = false;

        // Format event type using TypeNameFormatter conventions (TypeName, AssemblyName)
        // Strip "global::" prefix if present
        var typeName = eventType.StartsWith("global::", StringComparison.Ordinal)
            ? eventType["global::".Length..]
            : eventType;

        // Extract assembly name from type name
        // For "ECommerce.Contracts.Events.ProductCreatedEvent", assembly is "ECommerce.Contracts"
        // Pattern: Find the first part before ".Events" or ".Commands" or take first two segments
        var eventAssemblyName = _extractAssemblyName(typeName);
        var formattedEventType = $"{typeName}, {eventAssemblyName}";

        // Generate C# code that appends JSON object
        associations.AppendLine($"    json.Append(\"    {{\");");
        associations.AppendLine($"    json.Append($\"\\\"MessageType\\\": \\\"{formattedEventType}\\\", \");");
        associations.AppendLine("    json.Append(\"\\\"AssociationType\\\": \\\"perspective\\\", \");");
        associations.AppendLine($"    json.Append($\"\\\"TargetName\\\": \\\"{perspectiveClassName}\\\", \");");
        associations.AppendLine("    json.Append(\"\\\"ServiceName\\\": \\\"\");");
        associations.AppendLine("    json.Append(serviceName);");
        associations.AppendLine("    json.Append(\"\\\"\");");
        associations.AppendLine("    json.Append(\"}\");");

        associationCount++;
      }
    }

    // Replace template markers
    var result = template;
    result = TemplateUtilities.ReplaceRegion(result, "NAMESPACE", $"namespace {namespaceName};");
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(PerspectiveDiscoveryGenerator).Assembly, result);
    result = result.Replace("{{PERSPECTIVE_CLASS_COUNT}}", perspectives.Length.ToString(CultureInfo.InvariantCulture));
    result = result.Replace("{{REGISTRATION_COUNT}}", totalRegistrations.ToString(CultureInfo.InvariantCulture));
    result = result.Replace("{{ASSOCIATION_COUNT}}", associationCount.ToString(CultureInfo.InvariantCulture));
    result = TemplateUtilities.ReplaceRegion(result, "PERSPECTIVE_REGISTRATIONS", registrations.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "MESSAGE_ASSOCIATIONS_JSON", associations.ToString());

    return result;
  }

  /// <summary>
  /// Extracts the assembly name from a fully qualified type name.
  /// Uses convention: for "Namespace.Events.TypeName" or "Namespace.Commands.TypeName",
  /// assembly name is "Namespace.Contracts" (assuming contracts assembly naming).
  /// For other patterns, takes first two segments of namespace.
  /// E.g., "ECommerce.Contracts.Events.ProductCreatedEvent" -> "ECommerce.Contracts"
  /// </summary>
  private static string _extractAssemblyName(string fullyQualifiedName) {
    // Split by dots to extract namespace segments
    var parts = fullyQualifiedName.Split('.');

    // For patterns like "Namespace.Contracts.Events.TypeName", return "Namespace.Contracts"
    if (parts.Length >= 3 && (parts[2] == "Events" || parts[2] == "Commands")) {
      return $"{parts[0]}.{parts[1]}";
    }

    // For patterns like "Namespace.Events.TypeName", return "Namespace"
    if (parts.Length >= 2 && (parts[1] == "Events" || parts[1] == "Commands")) {
      return parts[0];
    }

    // Fallback: return first two segments if available, otherwise first segment
    if (parts.Length >= 2) {
      return $"{parts[0]}.{parts[1]}";
    }

    return parts[0];
  }

  /// <summary>
  /// Gets the simple name from a fully qualified type name.
  /// Handles tuples, arrays, and nested types.
  /// E.g., "global::MyApp.Events.OrderCreatedEvent" -> "OrderCreatedEvent"
  /// </summary>
  private static string _getSimpleName(string fullyQualifiedName) {
    // Handle arrays: Type[]
    if (fullyQualifiedName.EndsWith("[]", StringComparison.Ordinal)) {
      var baseType = fullyQualifiedName[..^2];
      return _getSimpleName(baseType) + "[]";
    }

    // Handle simple types
    var lastDot = fullyQualifiedName.LastIndexOf('.');
    return lastDot >= 0 ? fullyQualifiedName[(lastDot + 1)..] : fullyQualifiedName;
  }
}
