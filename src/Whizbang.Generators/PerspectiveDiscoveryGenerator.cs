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
  /// Extracts perspective information from a class that implements IPerspectiveFor interfaces.
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

    // Look for all perspective interfaces (IPerspectiveFor, IPerspectiveWithActionsFor, etc.)
    // Check if interface name contains "IPerspectiveFor" (matches both IPerspectiveFor and IPerspectiveWithActionsFor)
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

    // Get model type and StreamId property (same across all interfaces for this class)
    var modelType = perspectiveInterfaces[0].TypeArguments[0];
    var streamKeyPropertyName = _findStreamIdProperty(modelType);

    var className = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    // Compute nested-aware simple name
    var simpleName = TypeNameUtilities.GetSimpleName(classSymbol);

    // Compute CLR format name for database storage (uses + for nested types)
    var clrTypeName = TypeNameUtilities.BuildClrTypeName(classSymbol);

    // Generate one PerspectiveInfo per implemented interface
    var results = perspectiveInterfaces.Select(perspectiveInterface => {
      // Extract all type arguments: [TModel, TEvent1, TEvent2, ...]
      // Use FullyQualifiedFormat for CODE GENERATION (includes global:: prefix)
      var typeArguments = perspectiveInterface.TypeArguments
          .Select(t => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
          .ToArray();

      // Extract event types (all except TModel at index 0) for validation and diagnostics
      var eventTypeSymbols = perspectiveInterface.TypeArguments.Skip(1).ToArray();
      var eventTypes = eventTypeSymbols
          .Select(t => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
          .ToArray();

      // Calculate DATABASE FORMAT (TypeName, AssemblyName - no global:: prefix)
      // This format is used for registration in wh_message_associations table
      var messageTypeNames = eventTypeSymbols
          .Select(t => TypeNameUtilities.FormatTypeNameForRuntime(t))
          .ToArray();

      // Validate event types and extract StreamId information
      var (validationErrors, eventStreamIds) = _validateAndExtractEventInfo(eventTypeSymbols);

      return new PerspectiveInfo(
          ClassName: className,
          SimpleName: simpleName,
          ClrTypeName: clrTypeName,
          InterfaceTypeArguments: typeArguments,
          EventTypes: eventTypes,
          MessageTypeNames: messageTypeNames,
          StreamIdPropertyName: streamKeyPropertyName,
          EventStreamIds: eventStreamIds.Count > 0 ? eventStreamIds.ToArray() : null,
          EventValidationErrors: validationErrors.Count > 0 ? validationErrors.ToArray() : null
      );
    }).ToArray();

    return results;
  }

  /// <summary>
  /// Finds the StreamId property in a model type.
  /// Returns the property name if found, null otherwise.
  /// Searches the type hierarchy to find [StreamId] on inherited properties.
  /// </summary>
  private static string? _findStreamIdProperty(ITypeSymbol modelType) {
    var currentType = modelType as INamedTypeSymbol;
    while (currentType is not null) {
      foreach (var member in currentType.GetMembers()) {
        if (member is IPropertySymbol property) {
          var hasStreamIdAttribute = property.GetAttributes()
              .Any(a => a.AttributeClass?.ToDisplayString() == "Whizbang.Core.StreamIdAttribute");

          if (hasStreamIdAttribute) {
            return property.Name;
          }
        }
      }
      currentType = currentType.BaseType;
    }
    return null;
  }

  /// <summary>
  /// Validates event types and extracts StreamId information.
  /// Returns validation errors and StreamId info for valid events.
  /// </summary>
  private static (List<EventValidationError> ValidationErrors, List<EventStreamIdInfo> StreamIds) _validateAndExtractEventInfo(
      ITypeSymbol[] eventTypeSymbols) {

    var validationErrors = new List<EventValidationError>();
    var eventStreamIds = new List<EventStreamIdInfo>();

    foreach (var eventTypeSymbol in eventTypeSymbols) {
      var error = _validateEventStreamId(eventTypeSymbol);
      if (error != null) {
        validationErrors.Add(error);
      } else {
        // Extract StreamId property name (only if valid)
        var streamKeyProp = _extractStreamIdProperty(eventTypeSymbol);
        if (streamKeyProp != null) {
          eventStreamIds.Add(new EventStreamIdInfo(
              EventTypeName: eventTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
              StreamIdPropertyName: streamKeyProp
          ));
        }
      }
    }

    return (validationErrors, eventStreamIds);
  }

  /// <summary>
  /// Validates that an event type has exactly one property marked with [StreamId].
  /// Returns validation error if found, null if valid.
  /// Handles array types by validating the element type.
  /// Searches the type hierarchy to find [StreamId] on inherited properties.
  /// </summary>
  private static EventValidationError? _validateEventStreamId(ITypeSymbol eventTypeSymbol) {
    // If this is an array type, validate the element type instead
    var typeToValidate = eventTypeSymbol;
    if (eventTypeSymbol is IArrayTypeSymbol arrayType) {
      typeToValidate = arrayType.ElementType;
    }

    var streamKeyProperties = new List<string>();

    // Traverse the type hierarchy to find [StreamId] on inherited properties
    var currentType = typeToValidate as INamedTypeSymbol;
    while (currentType is not null) {
      foreach (var member in currentType.GetMembers()) {
        if (member is IPropertySymbol property) {
          var hasStreamIdAttribute = property.GetAttributes()
              .Any(a => a.AttributeClass?.ToDisplayString() == "Whizbang.Core.StreamIdAttribute");

          if (hasStreamIdAttribute) {
            streamKeyProperties.Add(property.Name);
          }
        }
      }
      currentType = currentType.BaseType;
    }

    var eventTypeName = typeToValidate.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    var simpleEventName = TypeNameUtilities.GetSimpleName(eventTypeName);

    if (streamKeyProperties.Count == 0) {
      return new EventValidationError(simpleEventName, StreamIdErrorType.MissingStreamId);
    } else if (streamKeyProperties.Count > 1) {
      return new EventValidationError(simpleEventName, StreamIdErrorType.MultipleStreamIds);
    }

    return null;
  }

  /// <summary>
  /// Extracts the StreamId property name from an event type.
  /// Returns the property name if exactly one [StreamId] is found, null otherwise.
  /// Handles array types by extracting from the element type.
  /// Searches the type hierarchy to find [StreamId] on inherited properties.
  /// </summary>
  private static string? _extractStreamIdProperty(ITypeSymbol eventTypeSymbol) {
    // If this is an array type, extract from the element type instead
    var typeToExtract = eventTypeSymbol;
    if (eventTypeSymbol is IArrayTypeSymbol arrayType) {
      typeToExtract = arrayType.ElementType;
    }

    // Traverse the type hierarchy to find [StreamId] on inherited properties
    var currentType = typeToExtract as INamedTypeSymbol;
    while (currentType is not null) {
      foreach (var member in currentType.GetMembers()) {
        if (member is IPropertySymbol property) {
          var hasStreamIdAttribute = property.GetAttributes()
              .Any(a => a.AttributeClass?.ToDisplayString() == "Whizbang.Core.StreamIdAttribute");

          if (hasStreamIdAttribute) {
            return property.Name;
          }
        }
      }
      currentType = currentType.BaseType;
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
      var eventNames = string.Join(", ", perspective.EventTypes.Select(TypeNameUtilities.GetSimpleName));
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.PerspectiveDiscovered,
          Location.None,
          TypeNameUtilities.GetSimpleName(perspective.ClassName),
          eventNames
      ));

      // Report validation errors for this perspective
      if (perspective.EventValidationErrors != null) {
        foreach (var error in perspective.EventValidationErrors) {
          var simplePerspectiveName = TypeNameUtilities.GetSimpleName(perspective.ClassName);

          if (error.ErrorType == StreamIdErrorType.MissingStreamId) {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.PerspectiveEventMissingStreamId,
                Location.None,
                error.EventTypeName,
                simplePerspectiveName
            ));
          } else if (error.ErrorType == StreamIdErrorType.MultipleStreamIds) {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.PerspectiveEventMultipleStreamIds,
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

    // Generate message associations array for C# querying
    var associationsArray = new StringBuilder();
    associationsArray.AppendLine("    return new MessageAssociation[] {");
    bool isFirst = true;

    foreach (var perspective in perspectives) {
      // Use CLR format name for database storage (e.g., "Namespace.Parent+Child")
      // This is consistent with registry lookup and avoids naming collisions
      var perspectiveClassName = perspective.ClrTypeName;

      // Use MessageTypeNames which already has the correct database format
      foreach (var messageTypeName in perspective.MessageTypeNames) {
        // Add comma separator (except for first item)
        if (!isFirst) {
          associationsArray.AppendLine(",");
        }
        isFirst = false;

        // MessageTypeNames already in correct format: "TypeName, AssemblyName"

        // Generate MessageAssociation instantiation
        associationsArray.Append($"      new MessageAssociation(\"{messageTypeName}\", \"perspective\", \"{perspectiveClassName}\", serviceName)");
      }
    }

    associationsArray.AppendLine();
    associationsArray.AppendLine("    };");

    // Replace template markers
    var result = template;
    result = TemplateUtilities.ReplaceRegion(result, "NAMESPACE", $"namespace {namespaceName};");
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(PerspectiveDiscoveryGenerator).Assembly, result);
    result = result.Replace("{{PERSPECTIVE_CLASS_COUNT}}", perspectives.Length.ToString(CultureInfo.InvariantCulture));
    result = result.Replace("{{REGISTRATION_COUNT}}", totalRegistrations.ToString(CultureInfo.InvariantCulture));
    result = TemplateUtilities.ReplaceRegion(result, "PERSPECTIVE_REGISTRATIONS", registrations.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "MESSAGE_ASSOCIATIONS_ARRAY", associationsArray.ToString());

    // Generate PERSPECTIVE_ASSOCIATIONS_TYPED region (Phase 3: Delegates)
    var typedAssociations = _generateTypedAssociations(perspectives, assemblyName);
    result = TemplateUtilities.ReplaceRegion(result, "PERSPECTIVE_ASSOCIATIONS_TYPED", typedAssociations);

    return result;
  }

  /// <summary>
  /// Generates the GetPerspectiveAssociations<TModel, TEvent>() method body with AOT-compatible delegates.
  /// Uses compile-time type checking (typeof) to match TModel and TEvent, then instantiates
  /// perspective classes directly (no reflection) and creates lambda delegates to Apply methods.
  /// </summary>
  private static string _generateTypedAssociations(
      ImmutableArray<PerspectiveInfo> perspectives,
      string serviceName) {

    if (perspectives.IsEmpty) {
      return "return Array.Empty<PerspectiveAssociationInfo<TModel, TEvent>>();";
    }

    var sb = new StringBuilder();

    // Generate type checks for each model/event combination
    foreach (var perspective in perspectives) {
      var modelType = perspective.InterfaceTypeArguments[0]; // TModel

      // Each perspective can handle multiple event types
      foreach (var eventType in perspective.EventTypes) {
        // Strip "global::" prefix if present for consistency
        var cleanEventType = eventType.StartsWith("global::", StringComparison.Ordinal)
            ? eventType["global::".Length..]
            : eventType;

        // Generate type-check condition for model and event types
        sb.AppendLine($"    if (typeof(TModel) == typeof({modelType}) && typeof(TEvent) == typeof({eventType})) {{");
        sb.AppendLine($"      return new[] {{");
        sb.AppendLine($"        new PerspectiveAssociationInfo<TModel, TEvent>(");
        sb.AppendLine($"          \"{cleanEventType}\",");
        sb.AppendLine($"          \"{perspective.ClassName.Split('.')[^1]}\",");
        sb.AppendLine($"          \"{serviceName}\",");
        sb.AppendLine($"          (model, evt) => {{");
        sb.AppendLine($"            var perspective = new {perspective.ClassName}();");
        sb.AppendLine($"            var typedModel = ({modelType})((object)model!);");
        sb.AppendLine($"            var typedEvent = ({eventType})((object)evt!);");
        sb.AppendLine($"            var result = perspective.Apply(typedModel, typedEvent);");
        sb.AppendLine($"            return (TModel)((object)result!);");
        sb.AppendLine($"          }}");
        sb.AppendLine($"        )");
        sb.AppendLine($"      }};");
        sb.AppendLine($"    }}");
        sb.AppendLine();
      }
    }

    // Default: return empty array
    sb.AppendLine("    return Array.Empty<PerspectiveAssociationInfo<TModel, TEvent>>();");

    return sb.ToString();
  }
}
