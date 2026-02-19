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
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithReceptor_GeneratesDispatcherAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithVoidReceptor_GeneratesDispatcherAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithMultipleReceptors_GeneratesAllRoutesAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithNoReceptors_GeneratesWarningAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithPerspectiveButNoReceptor_DoesNotWarnAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_GeneratesDispatcherRegistrationsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithClassNoBaseList_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_ReportsDiscoveredReceptorsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithAbstractReceptor_GeneratesAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithMultipleReceptorInterfaces_GeneratesForFirstAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithTypeInGlobalNamespace_HandlesCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_GeneratesReceptorDiscoveryDiagnosticsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithVoidReceptor_ReportsVoidResponseAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithClassWithoutReceptorInterface_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithTupleResponseType_SimplifiesInDiagnosticAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithArrayResponseType_SimplifiesInDiagnosticAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithNestedTupleResponseType_SimplifiesInDiagnosticAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_ZeroReceptors_WithPerspective_GeneratesEmptyDispatcherAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_ZeroReceptors_WithPerspective_GeneratesAddReceptorsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_ZeroReceptors_WithPerspective_GeneratedCodeCompilesAsync</tests>
/// Incremental source generator that discovers IReceptor implementations
/// and generates dispatcher registration code.
/// Also checks for IPerspectiveFor implementations to avoid false WHIZ002 warnings.
/// </summary>
[Generator]
public class ReceptorDiscoveryGenerator : IIncrementalGenerator {
  private const string RECEPTOR_INTERFACE_NAME = "Whizbang.Core.IReceptor";
  private const string SYNC_RECEPTOR_INTERFACE_NAME = "Whizbang.Core.ISyncReceptor";
  private const string PERSPECTIVE_INTERFACE_NAME = "Whizbang.Core.Perspectives.IPerspectiveFor";

  // Template and placeholder constants
  private const string TEMPLATE_SNIPPET_FILE = "DispatcherSnippets.cs";
  private const string PLACEHOLDER_RECEPTOR_INTERFACE = "__RECEPTOR_INTERFACE__";
  private const string PLACEHOLDER_SYNC_RECEPTOR_INTERFACE = "__SYNC_RECEPTOR_INTERFACE__";
  private const string PLACEHOLDER_MESSAGE_TYPE = "__MESSAGE_TYPE__";
  private const string PLACEHOLDER_RESPONSE_TYPE = "__RESPONSE_TYPE__";
  private const string PLACEHOLDER_RECEPTOR_CLASS = "__RECEPTOR_CLASS__";
  private const string PLACEHOLDER_LIFECYCLE_STAGE = "__LIFECYCLE_STAGE__";
  private const string PLACEHOLDER_INDEX = "__INDEX__";
  private const string PLACEHOLDER_RECEPTOR_NAME = "__RECEPTOR_NAME__";
  private const string PLACEHOLDER_MESSAGE_NAME = "__MESSAGE_NAME__";
  private const string PLACEHOLDER_RESPONSE_NAME = "__RESPONSE_NAME__";
  private const string REGION_NAMESPACE = "NAMESPACE";
  private const string PLACEHOLDER_RECEPTOR_COUNT = "{{RECEPTOR_COUNT}}";
  private const string DEFAULT_NAMESPACE = "Whizbang.Core";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Pipeline 1: Discover IReceptor implementations
    var receptorCandidates = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractReceptorInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Pipeline 2: Check for IPerspectiveFor implementations (for WHIZ002 diagnostic)
    var perspectiveCandidates = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _hasPerspectiveInterface(ctx, ct)
    ).Where(static hasPerspective => hasPerspective);

    // Combine both pipelines to determine if any message handlers exist
    var combined = receptorCandidates.Collect()
        .Combine(perspectiveCandidates.Collect());

    // Combine with compilation to get assembly name for namespace
    var compilationAndData = context.CompilationProvider.Combine(combined);

    // Generate registration code with awareness of both receptors and perspectives
    context.RegisterSourceOutput(
        compilationAndData,
        static (ctx, data) => {
          var compilation = data.Left;
          var receptors = data.Right.Left;
          var perspectives = data.Right.Right;
          _generateDispatcherRegistrations(ctx, compilation, receptors!, perspectives);
        }
    );
  }

  /// <summary>
  /// Extracts receptor information from a class declaration.
  /// Returns null if the class doesn't implement IReceptor or ISyncReceptor.
  /// Supports async receptors: IReceptor&lt;TMessage, TResponse&gt; and IReceptor&lt;TMessage&gt; (void).
  /// Supports sync receptors: ISyncReceptor&lt;TMessage, TResponse&gt; and ISyncReceptor&lt;TMessage&gt; (void).
  /// Enhanced in Phase 2 to extract [FireAt] attributes for lifecycle stage discovery.
  /// </summary>
  private static ReceptorInfo? _extractReceptorInfo(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    // Defensive guard: throws if Roslyn returns null (indicates compiler bug)
    // See RoslynGuards.cs for rationale - no branch created, eliminates coverage gap
    var classSymbol = RoslynGuards.GetClassSymbolOrThrow(classDeclaration, semanticModel, cancellationToken);

    // Skip generic open types (e.g., MyReceptor<T> where T is unbound)
    // These are used for runtime registration in tests but can't be routed at compile-time
    if (classSymbol.IsGenericType && classSymbol.TypeParameters.Length > 0) {
      return null;
    }

    // Extract lifecycle stages from [FireAt] attributes
    var lifecycleStages = _extractLifecycleStages(classSymbol);

    // Extract default routing from [DefaultRouting] attribute
    var defaultRouting = _extractDefaultRouting(classSymbol);

    // Look for IReceptor<TMessage, TResponse> interface (2 type arguments)
    var receptorInterface = classSymbol.AllInterfaces.FirstOrDefault(i =>
        i.OriginalDefinition.ToDisplayString() == RECEPTOR_INTERFACE_NAME + "<TMessage, TResponse>");

    if (receptorInterface is not null && receptorInterface.TypeArguments.Length == 2) {
      // Found IReceptor<TMessage, TResponse> - regular async receptor with response
      return new ReceptorInfo(
          ClassName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          MessageType: receptorInterface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          ResponseType: receptorInterface.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          LifecycleStages: lifecycleStages,
          IsSync: false,
          DefaultRouting: defaultRouting
      );
    }

    // Look for IReceptor<TMessage> interface (1 type argument) - void receptor
    var voidReceptorInterface = classSymbol.AllInterfaces.FirstOrDefault(i =>
        i.OriginalDefinition.ToDisplayString() == RECEPTOR_INTERFACE_NAME + "<TMessage>");

    if (voidReceptorInterface is not null && voidReceptorInterface.TypeArguments.Length == 1) {
      // Found IReceptor<TMessage> - void async receptor with no response
      return new ReceptorInfo(
          ClassName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          MessageType: voidReceptorInterface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          ResponseType: null,  // Void receptor - no response type
          LifecycleStages: lifecycleStages,
          IsSync: false,
          DefaultRouting: defaultRouting
      );
    }

    // Look for ISyncReceptor<TMessage, TResponse> interface (2 type arguments) - sync receptor
    var syncReceptorInterface = classSymbol.AllInterfaces.FirstOrDefault(i =>
        i.OriginalDefinition.ToDisplayString() == SYNC_RECEPTOR_INTERFACE_NAME + "<TMessage, TResponse>");

    if (syncReceptorInterface is not null && syncReceptorInterface.TypeArguments.Length == 2) {
      // Found ISyncReceptor<TMessage, TResponse> - sync receptor with response
      return new ReceptorInfo(
          ClassName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          MessageType: syncReceptorInterface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          ResponseType: syncReceptorInterface.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          LifecycleStages: lifecycleStages,
          IsSync: true,
          DefaultRouting: defaultRouting
      );
    }

    // Look for ISyncReceptor<TMessage> interface (1 type argument) - void sync receptor
    var voidSyncReceptorInterface = classSymbol.AllInterfaces.FirstOrDefault(i =>
        i.OriginalDefinition.ToDisplayString() == SYNC_RECEPTOR_INTERFACE_NAME + "<TMessage>");

    if (voidSyncReceptorInterface is not null && voidSyncReceptorInterface.TypeArguments.Length == 1) {
      // Found ISyncReceptor<TMessage> - void sync receptor with no response
      return new ReceptorInfo(
          ClassName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          MessageType: voidSyncReceptorInterface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          ResponseType: null,  // Void receptor - no response type
          LifecycleStages: lifecycleStages,
          IsSync: true,
          DefaultRouting: defaultRouting
      );
    }

    // No receptor interface found
    return null;
  }

  /// <summary>
  /// Extracts lifecycle stages from [FireAt] attributes on a receptor class.
  /// Returns an array of fully qualified lifecycle stage enum names (e.g., "Whizbang.Core.LifecycleStage.PostPerspectiveAsync").
  /// Returns empty array if no [FireAt] attributes found (receptor will default to ImmediateAsync).
  /// Supports multiple [FireAt] attributes on a single receptor.
  /// </summary>
  private static string[] _extractLifecycleStages(INamedTypeSymbol classSymbol) {
    const string FIRE_AT_ATTRIBUTE = "Whizbang.Core.Messaging.FireAtAttribute";

    var stages = new System.Collections.Generic.List<string>();

    foreach (var attribute in classSymbol.GetAttributes()) {
      if (attribute.AttributeClass?.ToDisplayString() != FIRE_AT_ATTRIBUTE) {
        continue;
      }

      // [FireAt(LifecycleStage.PostPerspectiveAsync)]
      // Constructor argument is LifecycleStage enum value
      if (attribute.ConstructorArguments.Length > 0) {
        var stageArg = attribute.ConstructorArguments[0];
        if (stageArg.Value is int stageValue) {
          // Get the enum type to convert int to enum name
          var stageType = attribute.AttributeClass.GetMembers().OfType<IMethodSymbol>()
              .FirstOrDefault(m => m.MethodKind == MethodKind.Constructor)
              ?.Parameters.FirstOrDefault()?.Type;

          if (stageType is INamedTypeSymbol enumType) {
            // Find the enum member with this value
            var enumMember = enumType.GetMembers().OfType<IFieldSymbol>()
                .FirstOrDefault(f => f.ConstantValue is int val && val == stageValue);

            if (enumMember is not null) {
              // Store fully qualified enum value (e.g., "Whizbang.Core.LifecycleStage.PostPerspectiveAsync")
              var fullyQualifiedStage = $"{enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{enumMember.Name}";
              stages.Add(fullyQualifiedStage);
            }
          }
        }
      }
    }

    return stages.ToArray();
  }

  /// <summary>
  /// Extracts the [DefaultRouting] attribute value from a receptor class.
  /// Returns the fully qualified DispatchMode enum value (e.g., "global::Whizbang.Core.Dispatch.DispatchMode.Local")
  /// or null if no [DefaultRouting] attribute is found.
  /// </summary>
  private static string? _extractDefaultRouting(INamedTypeSymbol classSymbol) {
    const string DEFAULT_ROUTING_ATTRIBUTE = "Whizbang.Core.Dispatch.DefaultRoutingAttribute";

    foreach (var attribute in classSymbol.GetAttributes()) {
      if (attribute.AttributeClass?.ToDisplayString() != DEFAULT_ROUTING_ATTRIBUTE) {
        continue;
      }

      // [DefaultRouting(DispatchMode.Local)]
      // Constructor argument is DispatchMode enum value
      if (attribute.ConstructorArguments.Length > 0) {
        var modeArg = attribute.ConstructorArguments[0];
        if (modeArg.Value is int modeValue) {
          // Get the enum type to convert int to enum name
          var modeType = attribute.AttributeClass.GetMembers().OfType<IMethodSymbol>()
              .FirstOrDefault(m => m.MethodKind == MethodKind.Constructor)
              ?.Parameters.FirstOrDefault()?.Type;

          if (modeType is INamedTypeSymbol enumType) {
            // Find the enum member with this value
            var enumMember = enumType.GetMembers().OfType<IFieldSymbol>()
                .FirstOrDefault(f => f.ConstantValue is int val && val == modeValue);

            if (enumMember is not null) {
              // Return fully qualified enum value (e.g., "global::Whizbang.Core.Dispatch.DispatchMode.Local")
              return $"{enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{enumMember.Name}";
            }
          }
        }
      }
    }

    return null;
  }

  /// <summary>
  /// Extracts unique event types from receptor response types for outbox cascade generation.
  /// Handles simple event types, tuples (extracts all event elements), and arrays (extracts element type).
  /// Returns fully qualified type names for AOT-compatible type-switch generation.
  /// </summary>
  /// <param name="receptors">The collection of discovered receptors.</param>
  /// <returns>Unique set of fully qualified event type names.</returns>
  /// <docs>core-concepts/dispatcher#auto-cascade-to-outbox</docs>
  /// <tests>Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithEventReturningReceptor_GeneratesCascadeToOutboxAsync</tests>
  private static HashSet<string> _extractUniqueEventTypes(ImmutableArray<ReceptorInfo> receptors) {
    var eventTypes = new HashSet<string>(StringComparer.Ordinal);

    foreach (var receptor in receptors) {
      // Skip void receptors (no response type to cascade)
      if (receptor.IsVoid || string.IsNullOrEmpty(receptor.ResponseType)) {
        continue;
      }

      var responseType = receptor.ResponseType!;

      // Handle tuple types: (Type1, Type2, ...) - extract all elements
      if (responseType.StartsWith("(", StringComparison.Ordinal) && responseType.EndsWith(")", StringComparison.Ordinal)) {
        var tupleElements = _extractTupleElements(responseType);
        foreach (var element in tupleElements) {
          // Elements from tuples may also be arrays - extract element type
          if (element.EndsWith("[]", StringComparison.Ordinal)) {
            var elementType = element.Substring(0, element.Length - 2);
            eventTypes.Add(elementType);
          } else {
            eventTypes.Add(element);
          }
        }
      }
      // Handle array types: Type[] - extract element type
      else if (responseType.EndsWith("[]", StringComparison.Ordinal)) {
        var elementType = responseType.Substring(0, responseType.Length - 2);
        eventTypes.Add(elementType);
      }
      // Simple event type
      else {
        eventTypes.Add(responseType);
      }
    }

    return eventTypes;
  }

  /// <summary>
  /// Extracts individual type names from a tuple type string.
  /// Handles nested tuples by tracking parenthesis depth.
  /// </summary>
  /// <param name="tupleType">Tuple type string like "(Type1, Type2)" or "(Type1, (Type2, Type3))"</param>
  /// <returns>List of extracted type names.</returns>
  /// <docs>core-concepts/dispatcher#auto-cascade-to-outbox</docs>
  /// <tests>Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithTupleResponse_ExtractsEventsForCascadeAsync</tests>
  private static List<string> _extractTupleElements(string tupleType) {
    var elements = new List<string>();

    // Remove outer parentheses
    var inner = tupleType.Substring(1, tupleType.Length - 2);

    var current = new StringBuilder();
    var depth = 0;

    for (var i = 0; i < inner.Length; i++) {
      var c = inner[i];

      if (c == '(') {
        depth++;
        current.Append(c);
      } else if (c == ')') {
        depth--;
        current.Append(c);
      } else if (c == ',' && depth == 0) {
        // Found a comma at top level - this separates elements
        var element = current.ToString().Trim();
        if (!string.IsNullOrEmpty(element)) {
          // If element is a nested tuple, recursively extract
          if (element.StartsWith("(", StringComparison.Ordinal)) {
            elements.AddRange(_extractTupleElements(element));
          } else {
            elements.Add(element);
          }
        }
        current.Clear();
      } else {
        current.Append(c);
      }
    }

    // Don't forget the last element
    var lastElement = current.ToString().Trim();
    if (!string.IsNullOrEmpty(lastElement)) {
      if (lastElement.StartsWith("(", StringComparison.Ordinal)) {
        elements.AddRange(_extractTupleElements(lastElement));
      } else {
        elements.Add(lastElement);
      }
    }

    return elements;
  }

  /// <summary>
  /// Checks if a class implements IPerspectiveFor&lt;TModel, TEvent1, ...&gt;.
  /// Returns true if the class implements the perspective interface, false otherwise.
  /// Used for WHIZ002 diagnostic - only warn if BOTH IReceptor and IPerspectiveFor are absent.
  /// Supports both variadic interface pattern (IPerspectiveFor&lt;TModel, TEvent1, TEvent2&gt;)
  /// and multiple separate interfaces (IPerspectiveFor&lt;TModel, TEvent1&gt;, IPerspectiveFor&lt;TModel, TEvent2&gt;).
  /// </summary>
  private static bool _hasPerspectiveInterface(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    // Defensive guard: throws if Roslyn returns null (indicates compiler bug)
    // See RoslynGuards.cs for rationale - no branch created, eliminates coverage gap
    var classSymbol = RoslynGuards.GetClassSymbolOrThrow(classDeclaration, semanticModel, cancellationToken);

    // Look for IPerspectiveFor<TModel, TEvent, ...> interface (any variant with 2+ type args)
    var hasPerspective = classSymbol.AllInterfaces.Any(i => {
      var originalDef = i.OriginalDefinition.ToDisplayString();
      // Check if it starts with our perspective interface name and has at least 2 type arguments (model + events)
      return originalDef.StartsWith(PERSPECTIVE_INTERFACE_NAME + "<", StringComparison.Ordinal) && i.TypeArguments.Length >= 2;
    });

    return hasPerspective;
  }

  /// <summary>
  /// Generates the dispatcher registration code for all discovered receptors.
  /// Only reports WHIZ002 warning if BOTH receptors and perspectives are absent.
  /// Uses assembly-specific namespace to avoid conflicts when multiple assemblies use Whizbang.
  /// </summary>
  private static void _generateDispatcherRegistrations(
      SourceProductionContext context,
      Compilation compilation,
      ImmutableArray<ReceptorInfo> receptors,
      ImmutableArray<bool> hasPerspectives) {

    var assemblyName = compilation.AssemblyName ?? "";

    // Only warn if BOTH IReceptor and IPerspectiveFor implementations are missing
    // Example: BFF with 5 IPerspectiveFor but no IReceptor should NOT warn
    if (receptors.IsEmpty && hasPerspectives.IsEmpty) {
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.NoReceptorsFound,
          Location.None
      ));

      // Only continue generation for test projects that need runtime receptor registration
      // Test projects use generic receptors like GenericLifecycleCompletionReceptor<TMessage>
      // which cannot be discovered at compile-time (source generators can't see type parameters)
      var isTestProject = assemblyName.Contains("Test", StringComparison.OrdinalIgnoreCase);

      if (!isTestProject) {
        return;  // Skip generation - not a test project and no receptors/perspectives
      }

      // Test project: continue generation to enable runtime receptor registration
    }

    // If we have perspectives but no receptors, generate empty dispatcher for outbox fallback routing
    // This enables BFF.API services to dispatch commands to remote services via outbox

    // Report each discovered receptor
    foreach (var receptor in receptors) {
      var responseTypeName = receptor.IsVoid ? "void" : TypeNameUtilities.GetSimpleName(receptor.ResponseType!);
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.ReceptorDiscovered,
          Location.None,
          TypeNameUtilities.GetSimpleName(receptor.ClassName),
          TypeNameUtilities.GetSimpleName(receptor.MessageType),
          responseTypeName
      ));
    }

    var registrationSource = _generateRegistrationSource(compilation, receptors);
    context.AddSource("DispatcherRegistrations.g.cs", registrationSource);

    var dispatcherSource = _generateDispatcherSource(compilation, receptors);
    context.AddSource("Dispatcher.g.cs", dispatcherSource);

    var lifecycleInvokerSource = _generateLifecycleInvokerSource(compilation, receptors);
    context.AddSource("LifecycleInvoker.g.cs", lifecycleInvokerSource);

    var receptorRegistrySource = _generateReceptorRegistrySource(compilation, receptors);
    context.AddSource("ReceptorRegistry.g.cs", receptorRegistrySource);

    var diagnosticsSource = _generateDiagnosticsSource(compilation, receptors);
    context.AddSource("ReceptorDiscoveryDiagnostics.g.cs", diagnosticsSource);
  }

  /// <summary>
  /// Generates the C# source code for the registration extension method.
  /// Uses template-based generation for IDE support.
  /// Handles both IReceptor&lt;TMessage, TResponse&gt; and IReceptor&lt;TMessage&gt; (void) patterns.
  /// Uses assembly-specific namespace to avoid conflicts when multiple assemblies use Whizbang.
  /// </summary>
  private static string _generateRegistrationSource(Compilation compilation, ImmutableArray<ReceptorInfo> receptors) {
    // Determine namespace from assembly name
    var assemblyName = compilation.AssemblyName ?? DEFAULT_NAMESPACE;
    var namespaceName = $"{assemblyName}.Generated";

    // Load template from embedded resource
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        "DispatcherRegistrationsTemplate.cs"
    );

    // Load registration snippets for async receptors
    var registrationSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "RECEPTOR_REGISTRATION_SNIPPET"
    );

    var voidRegistrationSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "VOID_RECEPTOR_REGISTRATION_SNIPPET"
    );

    // Load registration snippets for sync receptors
    var syncRegistrationSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "SYNC_RECEPTOR_REGISTRATION_SNIPPET"
    );

    var voidSyncRegistrationSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "VOID_SYNC_RECEPTOR_REGISTRATION_SNIPPET"
    );

    // Generate registration calls using appropriate snippet
    var registrations = new StringBuilder();
    foreach (var receptor in receptors) {
      string generatedCode;

      if (receptor.IsSync) {
        // Sync receptor
        if (receptor.IsVoid) {
          // Void sync receptor: ISyncReceptor<TMessage>
          generatedCode = voidSyncRegistrationSnippet
              .Replace(PLACEHOLDER_SYNC_RECEPTOR_INTERFACE, SYNC_RECEPTOR_INTERFACE_NAME)
              .Replace(PLACEHOLDER_MESSAGE_TYPE, receptor.MessageType)
              .Replace(PLACEHOLDER_RECEPTOR_CLASS, receptor.ClassName);
        } else {
          // Regular sync receptor: ISyncReceptor<TMessage, TResponse>
          generatedCode = syncRegistrationSnippet
              .Replace(PLACEHOLDER_SYNC_RECEPTOR_INTERFACE, SYNC_RECEPTOR_INTERFACE_NAME)
              .Replace(PLACEHOLDER_MESSAGE_TYPE, receptor.MessageType)
              .Replace(PLACEHOLDER_RESPONSE_TYPE, receptor.ResponseType!)
              .Replace(PLACEHOLDER_RECEPTOR_CLASS, receptor.ClassName);
        }
      } else {
        // Async receptor
        if (receptor.IsVoid) {
          // Void receptor: IReceptor<TMessage>
          generatedCode = voidRegistrationSnippet
              .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, RECEPTOR_INTERFACE_NAME)
              .Replace(PLACEHOLDER_MESSAGE_TYPE, receptor.MessageType)
              .Replace(PLACEHOLDER_RECEPTOR_CLASS, receptor.ClassName);
        } else {
          // Regular receptor: IReceptor<TMessage, TResponse>
          generatedCode = registrationSnippet
              .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, RECEPTOR_INTERFACE_NAME)
              .Replace(PLACEHOLDER_MESSAGE_TYPE, receptor.MessageType)
              .Replace(PLACEHOLDER_RESPONSE_TYPE, receptor.ResponseType!)
              .Replace(PLACEHOLDER_RECEPTOR_CLASS, receptor.ClassName);
        }
      }

      registrations.AppendLine(TemplateUtilities.IndentCode(generatedCode, "            "));
    }

    // Replace template markers
    var result = template;
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(ReceptorDiscoveryGenerator).Assembly, result);
    result = TemplateUtilities.ReplaceRegion(result, REGION_NAMESPACE, $"namespace {namespaceName} {{");
    result = result.Replace(PLACEHOLDER_RECEPTOR_COUNT, receptors.Length.ToString(CultureInfo.InvariantCulture));
    result = TemplateUtilities.ReplaceRegion(result, "RECEPTOR_REGISTRATIONS", registrations.ToString());

    return result;
  }

  /// <summary>
  /// Generates a complete Dispatcher implementation with zero-reflection routing.
  /// Uses the DispatcherTemplate.cs file for full analyzer support.
  /// Handles both IReceptor&lt;TMessage, TResponse&gt; and IReceptor&lt;TMessage&gt; (void) patterns.
  /// Uses assembly-specific namespace to avoid conflicts when multiple assemblies use Whizbang.
  /// </summary>
  private static string _generateDispatcherSource(Compilation compilation, ImmutableArray<ReceptorInfo> receptors) {
    // Determine namespace from assembly name
    var assemblyName = compilation.AssemblyName ?? DEFAULT_NAMESPACE;
    var namespaceName = $"{assemblyName}.Generated";

    // Separate async and sync receptors, then further by void/non-void
    var asyncReceptors = receptors.Where(r => !r.IsSync).ToImmutableArray();
    var syncReceptors = receptors.Where(r => r.IsSync).ToImmutableArray();

    // Async receptors: separate void from regular
    var regularReceptors = asyncReceptors.Where(r => !r.IsVoid).ToImmutableArray();
    var voidReceptors = asyncReceptors.Where(r => r.IsVoid).ToImmutableArray();

    // Sync receptors: separate void from regular
    var regularSyncReceptors = syncReceptors.Where(r => !r.IsVoid).ToImmutableArray();
    var voidSyncReceptors = syncReceptors.Where(r => r.IsVoid).ToImmutableArray();

    // Group regular async receptors by message type to handle multi-destination routing
    var regularReceptorsByMessage = regularReceptors
        .GroupBy(r => r.MessageType)
        .ToDictionary(g => g.Key, g => g.ToList());

    // Group void async receptors by message type
    var voidReceptorsByMessage = voidReceptors
        .GroupBy(r => r.MessageType)
        .ToDictionary(g => g.Key, g => g.ToList());

    // Group regular sync receptors by message type
    var regularSyncReceptorsByMessage = regularSyncReceptors
        .GroupBy(r => r.MessageType)
        .ToDictionary(g => g.Key, g => g.ToList());

    // Group void sync receptors by message type
    var voidSyncReceptorsByMessage = voidSyncReceptors
        .GroupBy(r => r.MessageType)
        .ToDictionary(g => g.Key, g => g.ToList());

    // Read template from embedded resource
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        "DispatcherTemplate.cs"
    );

    // Load Send routing snippet from template
    var sendSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "SEND_ROUTING_SNIPPET"
    );

    // Generate Send routing code for regular receptors using snippet template
    var sendRouting = new StringBuilder();
    foreach (var messageType in regularReceptorsByMessage.Keys) {
      var receptorList = regularReceptorsByMessage[messageType];
      var firstReceptor = receptorList[0];

      // Replace placeholders with actual types
      var generatedCode = sendSnippet
          .Replace(PLACEHOLDER_MESSAGE_TYPE, messageType)
          .Replace(PLACEHOLDER_RESPONSE_TYPE, firstReceptor.ResponseType!)
          .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, RECEPTOR_INTERFACE_NAME);

      sendRouting.AppendLine(TemplateUtilities.IndentCode(generatedCode, "      "));
    }

    // Load void Send routing snippet from template
    var voidSendSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "VOID_SEND_ROUTING_SNIPPET"
    );

    // Generate void Send routing code for void receptors using snippet template
    var voidSendRouting = new StringBuilder();
    foreach (var messageType in voidReceptorsByMessage.Keys) {
      // Replace placeholders with actual types
      var generatedCode = voidSendSnippet
          .Replace(PLACEHOLDER_MESSAGE_TYPE, messageType)
          .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, RECEPTOR_INTERFACE_NAME);

      voidSendRouting.AppendLine(TemplateUtilities.IndentCode(generatedCode, "      "));
    }

    // Load Publish routing snippet from template
    var publishSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "PUBLISH_ROUTING_SNIPPET"
    );

    // Generate Publish routing code using snippet template
    // Combine all message types from both regular and void receptors
    var allMessageTypes = regularReceptorsByMessage.Keys
        .Union(voidReceptorsByMessage.Keys)
        .Distinct()
        .ToList();

    var publishRouting = new StringBuilder();
    foreach (var messageType in allMessageTypes) {
      // Replace placeholders with actual types
      var generatedCode = publishSnippet
          .Replace(PLACEHOLDER_MESSAGE_TYPE, messageType)
          .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, RECEPTOR_INTERFACE_NAME);

      publishRouting.AppendLine(TemplateUtilities.IndentCode(generatedCode, "      "));
    }

    // Load Untyped Publish routing snippet from template (for auto-cascade)
    var untypedPublishSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "UNTYPED_PUBLISH_ROUTING_SNIPPET"
    );

    // Generate Untyped Publish routing code using snippet template
    // This enables auto-cascade: events extracted from receptor return values are published
    var untypedPublishRouting = new StringBuilder();
    foreach (var messageType in allMessageTypes) {
      // Replace placeholders with actual types
      var generatedCode = untypedPublishSnippet
          .Replace(PLACEHOLDER_MESSAGE_TYPE, messageType)
          .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, RECEPTOR_INTERFACE_NAME);

      untypedPublishRouting.AppendLine(TemplateUtilities.IndentCode(generatedCode, "      "));
    }

    // Load Sync Send routing snippet from template (for sync receptors)
    var syncSendSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "SYNC_SEND_ROUTING_SNIPPET"
    );

    // Generate Sync Send routing code for sync receptors using snippet template
    var syncSendRouting = new StringBuilder();
    foreach (var messageType in regularSyncReceptorsByMessage.Keys) {
      var receptorList = regularSyncReceptorsByMessage[messageType];
      var firstReceptor = receptorList[0];

      // Replace placeholders with actual types
      var generatedCode = syncSendSnippet
          .Replace(PLACEHOLDER_MESSAGE_TYPE, messageType)
          .Replace(PLACEHOLDER_RESPONSE_TYPE, firstReceptor.ResponseType!)
          .Replace(PLACEHOLDER_SYNC_RECEPTOR_INTERFACE, SYNC_RECEPTOR_INTERFACE_NAME);

      syncSendRouting.AppendLine(TemplateUtilities.IndentCode(generatedCode, "      "));
    }

    // Load Void Sync Send routing snippet from template (for void sync receptors)
    var voidSyncSendSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "VOID_SYNC_SEND_ROUTING_SNIPPET"
    );

    // Generate Void Sync Send routing code for void sync receptors using snippet template
    var voidSyncSendRouting = new StringBuilder();
    foreach (var messageType in voidSyncReceptorsByMessage.Keys) {
      // Replace placeholders with actual types
      var generatedCode = voidSyncSendSnippet
          .Replace(PLACEHOLDER_MESSAGE_TYPE, messageType)
          .Replace(PLACEHOLDER_SYNC_RECEPTOR_INTERFACE, SYNC_RECEPTOR_INTERFACE_NAME);

      voidSyncSendRouting.AppendLine(TemplateUtilities.IndentCode(generatedCode, "      "));
    }

    // Load Any Send routing snippets for cascade support
    var anySendNonVoidSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "ANY_SEND_ROUTING_NONVOID_SNIPPET"
    );

    // Generate Any Send routing code - prioritizes non-void async receptors (for cascading)
    // This enables void LocalInvokeAsync paths to find non-void receptors and cascade their results
    var anySendRouting = new StringBuilder();
    foreach (var messageType in regularReceptorsByMessage.Keys) {
      var receptorList = regularReceptorsByMessage[messageType];
      var firstReceptor = receptorList[0];

      // Replace placeholders with actual types
      var generatedCode = anySendNonVoidSnippet
          .Replace(PLACEHOLDER_MESSAGE_TYPE, messageType)
          .Replace(PLACEHOLDER_RESPONSE_TYPE, firstReceptor.ResponseType!)
          .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, RECEPTOR_INTERFACE_NAME);

      anySendRouting.AppendLine(TemplateUtilities.IndentCode(generatedCode, "      "));
    }

    // Generate receptor default routing lookup (for [DefaultRouting] attribute support)
    // Group all receptors by message type and find ones with DefaultRouting
    var receptorDefaultRouting = new StringBuilder();
    var allReceptorsByMessage = receptors
        .GroupBy(r => r.MessageType)
        .ToDictionary(g => g.Key, g => g.ToList());

    foreach (var messageType in allReceptorsByMessage.Keys) {
      var receptorList = allReceptorsByMessage[messageType];
      // Find first receptor with DefaultRouting attribute (if multiple, first wins)
      var receptorWithRouting = receptorList.FirstOrDefault(r => r.HasDefaultRouting);
      if (receptorWithRouting is not null) {
        receptorDefaultRouting.AppendLine($"      if (messageType == typeof({messageType})) {{");
        receptorDefaultRouting.AppendLine($"        return {receptorWithRouting.DefaultRouting};");
        receptorDefaultRouting.AppendLine($"      }}");
        receptorDefaultRouting.AppendLine();
      }
    }

    // Generate outbox cascade type-switch (for auto-cascading events to outbox)
    // Collect unique event types from receptor response types
    var outboxCascade = new StringBuilder();
    var eventTypes = _extractUniqueEventTypes(receptors);

    foreach (var eventType in eventTypes) {
      outboxCascade.AppendLine($"      if (messageType == typeof({eventType})) {{");
      outboxCascade.AppendLine($"        return PublishToOutboxAsync(({eventType})message, messageType, global::Whizbang.Core.ValueObjects.MessageId.New());");
      outboxCascade.AppendLine($"      }}");
      outboxCascade.AppendLine();
    }

    // Replace template markers using regex for robustness
    // This handles variations in whitespace and formatting
    var result = template;

    // Replace header with timestamp
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(ReceptorDiscoveryGenerator).Assembly, result);

    // Replace namespace with assembly-specific namespace
    result = TemplateUtilities.ReplaceRegion(result, REGION_NAMESPACE, $"namespace {namespaceName};");

    // Replace {{VARIABLE}} markers with simple string replacement
    result = result.Replace(PLACEHOLDER_RECEPTOR_COUNT, receptors.Length.ToString(CultureInfo.InvariantCulture));

    // Replace #region markers using shared utilities (robust against whitespace)
    result = TemplateUtilities.ReplaceRegion(result, "SEND_ROUTING", sendRouting.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "VOID_SEND_ROUTING", voidSendRouting.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "PUBLISH_ROUTING", publishRouting.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "UNTYPED_PUBLISH_ROUTING", untypedPublishRouting.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "SYNC_SEND_ROUTING", syncSendRouting.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "VOID_SYNC_SEND_ROUTING", voidSyncSendRouting.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "ANY_SEND_ROUTING", anySendRouting.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "RECEPTOR_DEFAULT_ROUTING", receptorDefaultRouting.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "OUTBOX_CASCADE", outboxCascade.ToString());

    return result;
  }

  /// <summary>
  /// Generates a complete LifecycleInvoker implementation with zero-reflection routing.
  /// Routes lifecycle invocations based on message type and lifecycle stage from [FireAt] attributes.
  /// Also checks ILifecycleReceptorRegistry for runtime-registered receptors.
  /// Uses assembly-specific namespace to avoid conflicts when multiple assemblies use Whizbang.
  /// </summary>
  private static string _generateLifecycleInvokerSource(Compilation compilation, ImmutableArray<ReceptorInfo> receptors) {
    // Determine namespace from assembly name
    var assemblyName = compilation.AssemblyName ?? DEFAULT_NAMESPACE;
    var namespaceName = $"{assemblyName}.Generated";

    // Read template from embedded resource
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        "LifecycleInvokerTemplate.cs"
    );

    // Load snippets for lifecycle routing
    var voidSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "LIFECYCLE_ROUTING_VOID_SNIPPET"
    );

    var responseSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "LIFECYCLE_ROUTING_RESPONSE_SNIPPET"
    );

    // Build list of (receptor, stage) pairs from all receptors
    var routingPairs = new System.Collections.Generic.List<(ReceptorInfo Receptor, string Stage)>();
    foreach (var receptor in receptors) {
      if (receptor.HasDefaultStage) {
        // No [FireAt] attributes - defaults to ImmediateAsync, not handled by lifecycle invoker
        continue;
      }

      foreach (var stage in receptor.LifecycleStages) {
        routingPairs.Add((receptor, stage));
      }
    }

    // Generate routing code for each (receptor, stage) pair
    var routingCode = new StringBuilder();
    foreach (var (receptor, stage) in routingPairs) {
      string generatedCode;

      if (receptor.IsVoid) {
        // Void receptor: IReceptor<TMessage>
        generatedCode = voidSnippet
            .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, RECEPTOR_INTERFACE_NAME)
            .Replace(PLACEHOLDER_MESSAGE_TYPE, receptor.MessageType)
            .Replace(PLACEHOLDER_LIFECYCLE_STAGE, stage);
      } else {
        // Regular receptor: IReceptor<TMessage, TResponse>
        generatedCode = responseSnippet
            .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, RECEPTOR_INTERFACE_NAME)
            .Replace(PLACEHOLDER_MESSAGE_TYPE, receptor.MessageType)
            .Replace(PLACEHOLDER_RESPONSE_TYPE, receptor.ResponseType!)
            .Replace(PLACEHOLDER_LIFECYCLE_STAGE, stage);
      }

      routingCode.AppendLine(TemplateUtilities.IndentCode(generatedCode, "    "));
    }

    // Replace template markers
    var result = template;
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(ReceptorDiscoveryGenerator).Assembly, result);
    result = TemplateUtilities.ReplaceRegion(result, REGION_NAMESPACE, $"namespace {namespaceName};");
    result = result.Replace(PLACEHOLDER_RECEPTOR_COUNT, receptors.Length.ToString(CultureInfo.InvariantCulture));
    result = TemplateUtilities.ReplaceRegion(result, "LIFECYCLE_ROUTING", routingCode.ToString());

    return result;
  }

  /// <summary>
  /// Generates IReceptorRegistry implementation that pre-categorizes ALL receptors by stage.
  /// - Receptors WITH [FireAt(X)] are registered at stage X only
  /// - Receptors WITHOUT [FireAt] are registered at LocalImmediateInline, PreOutboxInline, PostInboxInline
  /// This is the UNIFIED receptor invocation approach - no distinction between "lifecycle" and "business" receptors.
  /// </summary>
  private static string _generateReceptorRegistrySource(Compilation compilation, ImmutableArray<ReceptorInfo> receptors) {
    // Determine namespace from assembly name
    var assemblyName = compilation.AssemblyName ?? DEFAULT_NAMESPACE;
    var namespaceName = $"{assemblyName}.Generated";

    // Read template from embedded resource
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        "ReceptorRegistryTemplate.cs"
    );

    // Load snippets for receptor registry routing
    var responseSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "RECEPTOR_REGISTRY_ROUTING_SNIPPET"
    );

    var voidSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "RECEPTOR_REGISTRY_VOID_ROUTING_SNIPPET"
    );

    // Default stages for receptors WITHOUT [FireAt] attribute
    var defaultStages = new[] {
      "global::Whizbang.Core.Messaging.LifecycleStage.LocalImmediateInline",
      "global::Whizbang.Core.Messaging.LifecycleStage.PreOutboxInline",
      "global::Whizbang.Core.Messaging.LifecycleStage.PostInboxInline"
    };

    // Build list of (receptor, stage) pairs from ALL receptors
    var routingPairs = new System.Collections.Generic.List<(ReceptorInfo Receptor, string Stage)>();
    foreach (var receptor in receptors) {
      if (receptor.HasDefaultStage) {
        // No [FireAt] attributes - register at default stages
        foreach (var stage in defaultStages) {
          routingPairs.Add((receptor, stage));
        }
      } else {
        // Has [FireAt] attributes - register at specified stages only
        // Note: LifecycleStages already contains fully qualified names from _extractLifecycleStages()
        foreach (var stage in receptor.LifecycleStages) {
          routingPairs.Add((receptor, stage));
        }
      }
    }

    // Generate routing code for each (receptor, stage) pair
    var routingCode = new StringBuilder();
    foreach (var (receptor, stage) in routingPairs) {
      string generatedCode;

      if (receptor.IsVoid) {
        // Void receptor: IReceptor<TMessage>
        generatedCode = voidSnippet
            .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, RECEPTOR_INTERFACE_NAME)
            .Replace(PLACEHOLDER_MESSAGE_TYPE, receptor.MessageType)
            .Replace(PLACEHOLDER_RECEPTOR_CLASS, receptor.ClassName)
            .Replace(PLACEHOLDER_LIFECYCLE_STAGE, stage);
      } else {
        // Regular receptor: IReceptor<TMessage, TResponse>
        generatedCode = responseSnippet
            .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, RECEPTOR_INTERFACE_NAME)
            .Replace(PLACEHOLDER_MESSAGE_TYPE, receptor.MessageType)
            .Replace(PLACEHOLDER_RESPONSE_TYPE, receptor.ResponseType!)
            .Replace(PLACEHOLDER_RECEPTOR_CLASS, receptor.ClassName)
            .Replace(PLACEHOLDER_LIFECYCLE_STAGE, stage);
      }

      routingCode.AppendLine(TemplateUtilities.IndentCode(generatedCode, "    "));
    }

    // Replace template markers
    var result = template;
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(ReceptorDiscoveryGenerator).Assembly, result);
    result = TemplateUtilities.ReplaceRegion(result, REGION_NAMESPACE, $"namespace {namespaceName};");
    result = result.Replace(PLACEHOLDER_RECEPTOR_COUNT, receptors.Length.ToString(CultureInfo.InvariantCulture));
    result = TemplateUtilities.ReplaceRegion(result, "RECEPTOR_ROUTING", routingCode.ToString());

    return result;
  }

  /// <summary>
  /// Generates diagnostic registration code that adds receptor discovery
  /// information to the central WhizbangDiagnostics collection.
  /// Uses template-based generation for IDE support.
  /// Uses assembly-specific namespace to avoid conflicts when multiple assemblies use Whizbang.
  /// </summary>
  private static string _generateDiagnosticsSource(Compilation compilation, ImmutableArray<ReceptorInfo> receptors) {
    // Determine namespace from assembly name
    var assemblyName = compilation.AssemblyName ?? DEFAULT_NAMESPACE;
    var namespaceName = $"{assemblyName}.Generated";

    var timestamp = System.DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC", CultureInfo.InvariantCulture);

    // Load template from embedded resource
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        "ReceptorDiscoveryDiagnosticsTemplate.cs"
    );

    // Load diagnostic message snippet
    var messageSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "DIAGNOSTIC_MESSAGE_SNIPPET"
    );

    // Generate diagnostic messages using snippet
    var messages = new StringBuilder();
    for (int i = 0; i < receptors.Length; i++) {
      var receptor = receptors[i];
      var responseTypeName = receptor.IsVoid ? "void" : TypeNameUtilities.GetSimpleName(receptor.ResponseType!);
      var generatedCode = messageSnippet
          .Replace(PLACEHOLDER_INDEX, (i + 1).ToString(CultureInfo.InvariantCulture))
          .Replace(PLACEHOLDER_RECEPTOR_NAME, TypeNameUtilities.GetSimpleName(receptor.ClassName))
          .Replace(PLACEHOLDER_MESSAGE_NAME, TypeNameUtilities.GetSimpleName(receptor.MessageType))
          .Replace(PLACEHOLDER_RESPONSE_NAME, responseTypeName);

      messages.Append(TemplateUtilities.IndentCode(generatedCode, "            "));

      // Add blank line between receptors (except after last one)
      if (i < receptors.Length - 1) {
        messages.AppendLine("            message.AppendLine();");
      }
    }

    // Replace template markers
    var result = template;
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(ReceptorDiscoveryGenerator).Assembly, result);
    result = TemplateUtilities.ReplaceRegion(result, REGION_NAMESPACE, $"namespace {namespaceName} {{");
    result = result.Replace(PLACEHOLDER_RECEPTOR_COUNT, receptors.Length.ToString(CultureInfo.InvariantCulture));
    result = result.Replace("{{TIMESTAMP}}", timestamp);
    result = TemplateUtilities.ReplaceRegion(result, "DIAGNOSTIC_MESSAGES", messages.ToString());

    return result;
  }
}
