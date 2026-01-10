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
  private const string PERSPECTIVE_INTERFACE_NAME = "Whizbang.Core.Perspectives.IPerspectiveFor";

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
  /// Returns null if the class doesn't implement IReceptor.
  /// Supports both IReceptor&lt;TMessage, TResponse&gt; and IReceptor&lt;TMessage&gt; (void) patterns.
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

    // Look for IReceptor<TMessage, TResponse> interface (2 type arguments)
    var receptorInterface = classSymbol.AllInterfaces.FirstOrDefault(i =>
        i.OriginalDefinition.ToDisplayString() == RECEPTOR_INTERFACE_NAME + "<TMessage, TResponse>");

    if (receptorInterface is not null && receptorInterface.TypeArguments.Length == 2) {
      // Found IReceptor<TMessage, TResponse> - regular receptor with response
      return new ReceptorInfo(
          ClassName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          MessageType: receptorInterface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          ResponseType: receptorInterface.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          LifecycleStages: lifecycleStages
      );
    }

    // Look for IReceptor<TMessage> interface (1 type argument) - void receptor
    var voidReceptorInterface = classSymbol.AllInterfaces.FirstOrDefault(i =>
        i.OriginalDefinition.ToDisplayString() == RECEPTOR_INTERFACE_NAME + "<TMessage>");

    if (voidReceptorInterface is not null && voidReceptorInterface.TypeArguments.Length == 1) {
      // Found IReceptor<TMessage> - void receptor with no response
      return new ReceptorInfo(
          ClassName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          MessageType: voidReceptorInterface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          ResponseType: null,  // Void receptor - no response type
          LifecycleStages: lifecycleStages
      );
    }

    // No IReceptor interface found
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
      var responseTypeName = receptor.IsVoid ? "void" : _getSimpleName(receptor.ResponseType!);
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.ReceptorDiscovered,
          Location.None,
          _getSimpleName(receptor.ClassName),
          _getSimpleName(receptor.MessageType),
          responseTypeName
      ));
    }

    var registrationSource = _generateRegistrationSource(compilation, receptors);
    context.AddSource("DispatcherRegistrations.g.cs", registrationSource);

    var dispatcherSource = _generateDispatcherSource(compilation, receptors);
    context.AddSource("Dispatcher.g.cs", dispatcherSource);

    var lifecycleInvokerSource = _generateLifecycleInvokerSource(compilation, receptors);
    context.AddSource("LifecycleInvoker.g.cs", lifecycleInvokerSource);

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
    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";

    // Load template from embedded resource
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        "DispatcherRegistrationsTemplate.cs"
    );

    // Load registration snippets
    var registrationSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        "DispatcherSnippets.cs",
        "RECEPTOR_REGISTRATION_SNIPPET"
    );

    var voidRegistrationSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        "DispatcherSnippets.cs",
        "VOID_RECEPTOR_REGISTRATION_SNIPPET"
    );

    // Generate registration calls using appropriate snippet
    var registrations = new StringBuilder();
    foreach (var receptor in receptors) {
      string generatedCode;

      if (receptor.IsVoid) {
        // Void receptor: IReceptor<TMessage>
        generatedCode = voidRegistrationSnippet
            .Replace("__RECEPTOR_INTERFACE__", RECEPTOR_INTERFACE_NAME)
            .Replace("__MESSAGE_TYPE__", receptor.MessageType)
            .Replace("__RECEPTOR_CLASS__", receptor.ClassName);
      } else {
        // Regular receptor: IReceptor<TMessage, TResponse>
        generatedCode = registrationSnippet
            .Replace("__RECEPTOR_INTERFACE__", RECEPTOR_INTERFACE_NAME)
            .Replace("__MESSAGE_TYPE__", receptor.MessageType)
            .Replace("__RESPONSE_TYPE__", receptor.ResponseType!)
            .Replace("__RECEPTOR_CLASS__", receptor.ClassName);
      }

      registrations.AppendLine(TemplateUtilities.IndentCode(generatedCode, "            "));
    }

    // Replace template markers
    var result = template;
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(ReceptorDiscoveryGenerator).Assembly, result);
    result = TemplateUtilities.ReplaceRegion(result, "NAMESPACE", $"namespace {namespaceName} {{");
    result = result.Replace("{{RECEPTOR_COUNT}}", receptors.Length.ToString(CultureInfo.InvariantCulture));
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
    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";
    // Separate void receptors from regular receptors
    var regularReceptors = receptors.Where(r => !r.IsVoid).ToImmutableArray();
    var voidReceptors = receptors.Where(r => r.IsVoid).ToImmutableArray();

    // Group regular receptors by message type to handle multi-destination routing
    var regularReceptorsByMessage = regularReceptors
        .GroupBy(r => r.MessageType)
        .ToDictionary(g => g.Key, g => g.ToList());

    // Group void receptors by message type
    var voidReceptorsByMessage = voidReceptors
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
        "DispatcherSnippets.cs",
        "SEND_ROUTING_SNIPPET"
    );

    // Generate Send routing code for regular receptors using snippet template
    var sendRouting = new StringBuilder();
    foreach (var messageType in regularReceptorsByMessage.Keys) {
      var receptorList = regularReceptorsByMessage[messageType];
      var firstReceptor = receptorList[0];

      // Replace placeholders with actual types
      var generatedCode = sendSnippet
          .Replace("__MESSAGE_TYPE__", messageType)
          .Replace("__RESPONSE_TYPE__", firstReceptor.ResponseType!)
          .Replace("__RECEPTOR_INTERFACE__", RECEPTOR_INTERFACE_NAME);

      sendRouting.AppendLine(TemplateUtilities.IndentCode(generatedCode, "      "));
    }

    // Load void Send routing snippet from template
    var voidSendSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        "DispatcherSnippets.cs",
        "VOID_SEND_ROUTING_SNIPPET"
    );

    // Generate void Send routing code for void receptors using snippet template
    var voidSendRouting = new StringBuilder();
    foreach (var messageType in voidReceptorsByMessage.Keys) {
      // Replace placeholders with actual types
      var generatedCode = voidSendSnippet
          .Replace("__MESSAGE_TYPE__", messageType)
          .Replace("__RECEPTOR_INTERFACE__", RECEPTOR_INTERFACE_NAME);

      voidSendRouting.AppendLine(TemplateUtilities.IndentCode(generatedCode, "      "));
    }

    // Load Publish routing snippet from template
    var publishSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        "DispatcherSnippets.cs",
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
          .Replace("__MESSAGE_TYPE__", messageType)
          .Replace("__RECEPTOR_INTERFACE__", RECEPTOR_INTERFACE_NAME);

      publishRouting.AppendLine(TemplateUtilities.IndentCode(generatedCode, "      "));
    }

    // Replace template markers using regex for robustness
    // This handles variations in whitespace and formatting
    var result = template;

    // Replace header with timestamp
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(ReceptorDiscoveryGenerator).Assembly, result);

    // Replace namespace with assembly-specific namespace
    result = TemplateUtilities.ReplaceRegion(result, "NAMESPACE", $"namespace {namespaceName};");

    // Replace {{VARIABLE}} markers with simple string replacement
    result = result.Replace("{{RECEPTOR_COUNT}}", receptors.Length.ToString(CultureInfo.InvariantCulture));

    // Replace #region markers using shared utilities (robust against whitespace)
    result = TemplateUtilities.ReplaceRegion(result, "SEND_ROUTING", sendRouting.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "VOID_SEND_ROUTING", voidSendRouting.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "PUBLISH_ROUTING", publishRouting.ToString());

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
    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";

    // Read template from embedded resource
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        "LifecycleInvokerTemplate.cs"
    );

    // Load snippets for lifecycle routing
    var voidSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        "DispatcherSnippets.cs",
        "LIFECYCLE_ROUTING_VOID_SNIPPET"
    );

    var responseSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        "DispatcherSnippets.cs",
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
            .Replace("__RECEPTOR_INTERFACE__", RECEPTOR_INTERFACE_NAME)
            .Replace("__MESSAGE_TYPE__", receptor.MessageType)
            .Replace("__LIFECYCLE_STAGE__", stage);
      } else {
        // Regular receptor: IReceptor<TMessage, TResponse>
        generatedCode = responseSnippet
            .Replace("__RECEPTOR_INTERFACE__", RECEPTOR_INTERFACE_NAME)
            .Replace("__MESSAGE_TYPE__", receptor.MessageType)
            .Replace("__RESPONSE_TYPE__", receptor.ResponseType!)
            .Replace("__LIFECYCLE_STAGE__", stage);
      }

      routingCode.AppendLine(TemplateUtilities.IndentCode(generatedCode, "    "));
    }

    // Replace template markers
    var result = template;
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(ReceptorDiscoveryGenerator).Assembly, result);
    result = TemplateUtilities.ReplaceRegion(result, "NAMESPACE", $"namespace {namespaceName};");
    result = result.Replace("{{RECEPTOR_COUNT}}", receptors.Length.ToString(CultureInfo.InvariantCulture));
    result = TemplateUtilities.ReplaceRegion(result, "LIFECYCLE_ROUTING", routingCode.ToString());

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
    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
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
        "DispatcherSnippets.cs",
        "DIAGNOSTIC_MESSAGE_SNIPPET"
    );

    // Generate diagnostic messages using snippet
    var messages = new StringBuilder();
    for (int i = 0; i < receptors.Length; i++) {
      var receptor = receptors[i];
      var responseTypeName = receptor.IsVoid ? "void" : _getSimpleName(receptor.ResponseType!);
      var generatedCode = messageSnippet
          .Replace("__INDEX__", (i + 1).ToString(CultureInfo.InvariantCulture))
          .Replace("__RECEPTOR_NAME__", _getSimpleName(receptor.ClassName))
          .Replace("__MESSAGE_NAME__", _getSimpleName(receptor.MessageType))
          .Replace("__RESPONSE_NAME__", responseTypeName);

      messages.Append(TemplateUtilities.IndentCode(generatedCode, "            "));

      // Add blank line between receptors (except after last one)
      if (i < receptors.Length - 1) {
        messages.AppendLine("            message.AppendLine();");
      }
    }

    // Replace template markers
    var result = template;
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(ReceptorDiscoveryGenerator).Assembly, result);
    result = TemplateUtilities.ReplaceRegion(result, "NAMESPACE", $"namespace {namespaceName} {{");
    result = result.Replace("{{RECEPTOR_COUNT}}", receptors.Length.ToString(CultureInfo.InvariantCulture));
    result = result.Replace("{{TIMESTAMP}}", timestamp);
    result = TemplateUtilities.ReplaceRegion(result, "DIAGNOSTIC_MESSAGES", messages.ToString());

    return result;
  }

  /// <summary>
  /// Gets the simple name from a fully qualified type name.
  /// Handles tuples, arrays, and nested types.
  /// E.g., "global::MyApp.Commands.CreateOrder" -> "CreateOrder"
  /// E.g., "(global::A.B, global::C.D)" -> "(B, D)"
  /// E.g., "global::MyApp.Events.NotificationEvent[]" -> "NotificationEvent[]"
  /// </summary>
  private static string _getSimpleName(string fullyQualifiedName) {
    // Handle tuples: (Type1, Type2, ...)
    if (fullyQualifiedName.StartsWith("(", StringComparison.Ordinal) && fullyQualifiedName.EndsWith(")", StringComparison.Ordinal)) {
      var inner = fullyQualifiedName[1..^1];
      var parts = _splitTupleParts(inner);
      var simplifiedParts = new string[parts.Length];
      for (int i = 0; i < parts.Length; i++) {
        simplifiedParts[i] = _getSimpleName(parts[i].Trim());
      }
      return "(" + string.Join(", ", simplifiedParts) + ")";
    }

    // Handle arrays: Type[]
    if (fullyQualifiedName.EndsWith("[]", StringComparison.Ordinal)) {
      var baseType = fullyQualifiedName[..^2];
      return _getSimpleName(baseType) + "[]";
    }

    // Handle simple types
    var lastDot = fullyQualifiedName.LastIndexOf('.');
    return lastDot >= 0 ? fullyQualifiedName[(lastDot + 1)..] : fullyQualifiedName;
  }

  /// <summary>
  /// Splits tuple parts respecting nested tuples and parentheses.
  /// E.g., "A, B, (C, D)" -> ["A", "B", "(C, D)"]
  /// </summary>
  private static string[] _splitTupleParts(string tupleContent) {
    var parts = new System.Collections.Generic.List<string>();
    var currentPart = new System.Text.StringBuilder();
    var depth = 0;

    foreach (var ch in tupleContent) {
      if (ch == ',' && depth == 0) {
        parts.Add(currentPart.ToString());
        currentPart.Clear();
      } else {
        if (ch == '(') {
          depth++;
        } else if (ch == ')') {
          depth--;
        }

        currentPart.Append(ch);
      }
    }

    if (currentPart.Length > 0) {
      parts.Add(currentPart.ToString());
    }

    return [.. parts];
  }
}
