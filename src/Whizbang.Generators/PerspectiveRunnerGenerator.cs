using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Generates IPerspectiveRunner implementations for perspectives that implement IPerspectiveFor&lt;TModel, TEvent&gt;.
/// Runners handle unit-of-work event replay with UUID7 ordering, configurable batching, and checkpoint management.
/// Supports both single-stream (IPerspectiveFor) and multi-stream (IGlobalPerspectiveFor) perspectives.
/// </summary>
[Generator]
public class PerspectiveRunnerGenerator : IIncrementalGenerator {
  private const string PERSPECTIVE_FOR_INTERFACE_NAME = "Whizbang.Core.Perspectives.IPerspectiveFor";
  private const string GLOBAL_PERSPECTIVE_FOR_INTERFACE_NAME = "Whizbang.Core.Perspectives.IGlobalPerspectiveFor";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Reuse the same discovery logic as PerspectiveDiscoveryGenerator
    var perspectiveCandidates = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractPerspectiveInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Combine with compilation to get assembly name
    var compilationAndPerspectives = context.CompilationProvider.Combine(perspectiveCandidates.Collect());

    context.RegisterSourceOutput(
        compilationAndPerspectives,
        static (ctx, data) => {
          var compilation = data.Left;
          var perspectives = data.Right;
          _generatePerspectiveRunners(ctx, compilation, perspectives!);
        }
    );
  }

  /// <summary>
  /// Extracts perspective information from a class declaration.
  /// Returns null if the class doesn't implement IPerspectiveFor&lt;TModel, TEvent&gt; or IGlobalPerspectiveFor&lt;TModel, TPartitionKey, TEvent&gt;.
  /// </summary>
  private static PerspectiveInfo? _extractPerspectiveInfo(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    var classSymbol = RoslynGuards.GetClassSymbolOrThrow(classDeclaration, semanticModel, cancellationToken);

    // Skip abstract classes
    if (classSymbol.IsAbstract) {
      return null;
    }

    // Look for IPerspectiveFor<TModel, TEvent1..5> interfaces (single-stream)
    // Format: Whizbang.Core.Perspectives.IPerspectiveFor<TModel, TEvent1> (2 type args)
    // Format: Whizbang.Core.Perspectives.IPerspectiveFor<TModel, TEvent1, TEvent2> (3 type args)
    // Format: Whizbang.Core.Perspectives.IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3> (4 type args)
    // Format: Whizbang.Core.Perspectives.IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4> (5 type args)
    // Format: Whizbang.Core.Perspectives.IPerspectiveFor<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5> (6 type args)
    var singleStreamInterfaces = classSymbol.AllInterfaces
        .Where(i => {
          var originalDef = i.OriginalDefinition.ToDisplayString();
          return (originalDef == PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TEvent1>" ||
                  originalDef == PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TEvent1, TEvent2>" ||
                  originalDef == PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TEvent1, TEvent2, TEvent3>" ||
                  originalDef == PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TEvent1, TEvent2, TEvent3, TEvent4>" ||
                  originalDef == PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5>")
                 && i.TypeArguments.Length >= 2;
        })
        .ToList();

    // Look for IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1..3> interfaces (multi-stream)
    // Format: Whizbang.Core.Perspectives.IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1> (3 type args)
    var globalInterfaces = classSymbol.AllInterfaces
        .Where(i => {
          var originalDef = i.OriginalDefinition.ToDisplayString();
          return (originalDef == GLOBAL_PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TPartitionKey, TEvent1>" ||
                  originalDef == GLOBAL_PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TPartitionKey, TEvent1, TEvent2>" ||
                  originalDef == GLOBAL_PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TPartitionKey, TEvent1, TEvent2, TEvent3>")
                 && i.TypeArguments.Length >= 3;
        })
        .ToList();

    if (singleStreamInterfaces.Count == 0 && globalInterfaces.Count == 0) {
      return null;
    }

    // Extract model type (first type argument in both IPerspectiveFor and IGlobalPerspectiveFor)
    ITypeSymbol? modelType = null;
    if (singleStreamInterfaces.Count > 0) {
      modelType = singleStreamInterfaces[0].TypeArguments[0];
    } else if (globalInterfaces.Count > 0) {
      modelType = globalInterfaces[0].TypeArguments[0];
    }

    if (modelType is null) {
      return null;
    }

    var modelTypeName = modelType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    // Extract event types from all interfaces and collect event symbols
    var eventTypes = new List<string>();
    var eventTypeSymbols = new List<ITypeSymbol>();

    // Extract from single-stream: skip TModel (index 0), all others are events
    foreach (var iface in singleStreamInterfaces) {
      for (int i = 1; i < iface.TypeArguments.Length; i++) {
        var eventTypeSymbol = iface.TypeArguments[i];
        var eventType = eventTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (!eventTypes.Contains(eventType)) {
          eventTypes.Add(eventType);
          eventTypeSymbols.Add(eventTypeSymbol);
        }
      }
    }

    // Extract from global: skip TModel (index 0) and TPartitionKey (index 1), rest are events
    foreach (var iface in globalInterfaces) {
      for (int i = 2; i < iface.TypeArguments.Length; i++) {
        var eventTypeSymbol = iface.TypeArguments[i];
        var eventType = eventTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (!eventTypes.Contains(eventType)) {
          eventTypes.Add(eventType);
          eventTypeSymbols.Add(eventTypeSymbol);
        }
      }
    }

    if (eventTypes.Count == 0) {
      return null;
    }

    // Find property with [StreamKey] attribute on the model
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

    if (streamKeyPropertyName is null) {
      // Cannot generate runner without StreamKey - skip silently
      return null;
    }

    // Extract StreamKey property from each event type (for StreamId extraction)
    var eventStreamKeys = new List<EventStreamKeyInfo>();
    for (int i = 0; i < eventTypes.Count; i++) {
      var eventTypeName = eventTypes[i];
      var eventTypeSymbol = eventTypeSymbols[i];

      // Extract StreamKey property name from event
      var eventStreamKeyProp = _extractStreamKeyProperty(eventTypeSymbol);
      if (eventStreamKeyProp != null) {
        eventStreamKeys.Add(new EventStreamKeyInfo(
            EventTypeName: eventTypeName,
            StreamKeyPropertyName: eventStreamKeyProp
        ));
      }
    }

    // Build full interface type arguments for registration
    var typeArguments = new[] { modelTypeName }.Concat(eventTypes).ToArray();

    // Calculate DATABASE FORMAT (TypeName, AssemblyName - no global:: prefix)
    // This generator doesn't use database registration, but we need to provide the parameter
    var messageTypeNames = eventTypeSymbols
        .Select(t => {
          var typeName = t.ToDisplayString(new SymbolDisplayFormat(
              typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
              genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
          ));
          var assemblyName = t.ContainingAssembly.Name;
          return $"{typeName}, {assemblyName}";
        })
        .ToArray();

    return new PerspectiveInfo(
        ClassName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        InterfaceTypeArguments: typeArguments,
        EventTypes: eventTypes.ToArray(),
        MessageTypeNames: messageTypeNames,
        StreamKeyPropertyName: streamKeyPropertyName,
        EventStreamKeys: eventStreamKeys.Count > 0 ? eventStreamKeys.ToArray() : null
    );
  }

  /// <summary>
  /// Extracts the StreamKey property name from an event type.
  /// Returns the property name if exactly one [StreamKey] is found, null otherwise.
  /// </summary>
  private static string? _extractStreamKeyProperty(ITypeSymbol eventTypeSymbol) {
    foreach (var member in eventTypeSymbol.GetMembers()) {
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
  /// Generates IPerspectiveRunner implementations for all discovered perspectives with models.
  /// </summary>
  private static void _generatePerspectiveRunners(
      SourceProductionContext context,
      Compilation compilation,
      ImmutableArray<PerspectiveInfo> perspectives) {

    if (perspectives.IsEmpty) {
      return;
    }

    // Generate a runner for each perspective
    foreach (var perspective in perspectives) {
      var runnerSource = _generateRunnerSource(compilation, perspective);
      var runnerName = _getRunnerName(perspective.ClassName);
      context.AddSource($"{runnerName}.g.cs", runnerSource);

      // Report diagnostic
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.PerspectiveRunnerGenerated,
          Location.None,
          _getSimpleName(perspective.ClassName),
          runnerName
      ));
    }
  }

  /// <summary>
  /// Generates the C# source code for a perspective runner.
  /// Uses template-based generation with unit-of-work pattern and AOT-compatible switch statements.
  /// </summary>
  private static string _generateRunnerSource(Compilation compilation, PerspectiveInfo perspective) {
    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";

    // Load template from embedded resource
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(PerspectiveRunnerGenerator).Assembly,
        "PerspectiveRunnerTemplate.cs"
    );

    var runnerName = _getRunnerName(perspective.ClassName);
    var perspectiveSimpleName = _getSimpleName(perspective.ClassName);

    // Generate AOT-compatible switch cases for event application
    var applyCases = new StringBuilder();
    foreach (var eventType in perspective.EventTypes) {
      applyCases.AppendLine($"        case {eventType} typedEvent:");
      applyCases.AppendLine($"          return perspective.Apply(currentModel, typedEvent);");
      applyCases.AppendLine();
    }

    // Generate event types array for polymorphic deserialization
    var eventTypesArray = new StringBuilder();
    for (int i = 0; i < perspective.EventTypes.Length; i++) {
      eventTypesArray.Append($"      typeof({perspective.EventTypes[i]})");
      if (i < perspective.EventTypes.Length - 1) {
        eventTypesArray.AppendLine(",");
      } else {
        eventTypesArray.AppendLine();
      }
    }

    // Generate ExtractStreamId methods (one per event type with StreamKey)
    var extractStreamIdMethods = new StringBuilder();
    if (perspective.EventStreamKeys != null) {
      foreach (var eventStreamKey in perspective.EventStreamKeys) {
        extractStreamIdMethods.AppendLine($"  /// <summary>");
        extractStreamIdMethods.AppendLine($"  /// Extracts the stream ID from {_getSimpleName(eventStreamKey.EventTypeName)} event.");
        extractStreamIdMethods.AppendLine($"  /// </summary>");
        extractStreamIdMethods.AppendLine($"  private static string ExtractStreamId({eventStreamKey.EventTypeName} @event) {{");
        extractStreamIdMethods.AppendLine($"    return @event.{eventStreamKey.StreamKeyPropertyName}.ToString();");
        extractStreamIdMethods.AppendLine($"  }}");
        extractStreamIdMethods.AppendLine();
      }
    }

    // Replace template markers
    var result = template;
    result = TemplateUtilities.ReplaceRegion(result, "NAMESPACE", $"namespace {namespaceName};");
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(PerspectiveRunnerGenerator).Assembly, result);
    result = TemplateUtilities.ReplaceRegion(result, "EVENT_TYPES", eventTypesArray.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "EVENT_APPLY_CASES", applyCases.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "EXTRACT_STREAM_ID_METHODS", extractStreamIdMethods.ToString());

    // Model type is always the first type argument
    var modelTypeName = perspective.InterfaceTypeArguments[0];

    result = result.Replace("__RUNNER_CLASS_NAME__", runnerName);
    result = result.Replace("__PERSPECTIVE_CLASS_NAME__", perspective.ClassName);
    result = result.Replace("__MODEL_TYPE_NAME__", modelTypeName);
    result = result.Replace("__STREAM_KEY_PROPERTY__", perspective.StreamKeyPropertyName!);
    result = result.Replace("__PERSPECTIVE_SIMPLE_NAME__", perspectiveSimpleName);

    return result;
  }

  /// <summary>
  /// Gets the runner class name from a perspective class name.
  /// E.g., "MyApp.OrderPerspective" -> "OrderPerspectiveRunner"
  /// </summary>
  private static string _getRunnerName(string perspectiveClassName) {
    var simpleName = _getSimpleName(perspectiveClassName);
    return $"{simpleName}Runner";
  }

  /// <summary>
  /// Gets the simple name from a fully qualified type name.
  /// E.g., "global::MyApp.OrderPerspective" -> "OrderPerspective"
  /// </summary>
  private static string _getSimpleName(string fullyQualifiedName) {
    var lastDot = fullyQualifiedName.LastIndexOf('.');
    return lastDot >= 0 ? fullyQualifiedName[(lastDot + 1)..] : fullyQualifiedName;
  }
}
