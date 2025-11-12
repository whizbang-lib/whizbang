using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Generators;

/// <summary>
/// Incremental source generator that discovers IReceptor implementations
/// and generates dispatcher registration code.
/// Also checks for IPerspectiveOf implementations to avoid false WHIZ002 warnings.
/// </summary>
[Generator]
public class ReceptorDiscoveryGenerator : IIncrementalGenerator {
  private const string RECEPTOR_INTERFACE_NAME = "Whizbang.Core.IReceptor";
  private const string PERSPECTIVE_INTERFACE_NAME = "Whizbang.Core.IPerspectiveOf";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Pipeline 1: Discover IReceptor implementations
    var receptorCandidates = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => ExtractReceptorInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Pipeline 2: Check for IPerspectiveOf implementations (for WHIZ002 diagnostic)
    var perspectiveCandidates = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => HasPerspectiveInterface(ctx, ct)
    ).Where(static hasPerspective => hasPerspective);

    // Combine both pipelines to determine if any message handlers exist
    var combined = receptorCandidates.Collect()
        .Combine(perspectiveCandidates.Collect());

    // Generate registration code with awareness of both receptors and perspectives
    context.RegisterSourceOutput(
        combined,
        static (ctx, data) => GenerateDispatcherRegistrations(ctx, data.Left!, data.Right)
    );
  }

  /// <summary>
  /// Extracts receptor information from a class declaration.
  /// Returns null if the class doesn't implement IReceptor.
  /// Supports both IReceptor&lt;TMessage, TResponse&gt; and IReceptor&lt;TMessage&gt; (void) patterns.
  /// </summary>
  private static ReceptorInfo? ExtractReceptorInfo(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    // Get the symbol for the class
    var classSymbol = semanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken) as INamedTypeSymbol;
    if (classSymbol is null) {
      return null;
    }

    // Look for IReceptor<TMessage, TResponse> interface (2 type arguments)
    var receptorInterface = classSymbol.AllInterfaces.FirstOrDefault(i =>
        i.OriginalDefinition.ToDisplayString() == RECEPTOR_INTERFACE_NAME + "<TMessage, TResponse>");

    if (receptorInterface is not null && receptorInterface.TypeArguments.Length == 2) {
      // Found IReceptor<TMessage, TResponse> - regular receptor with response
      return new ReceptorInfo(
          ClassName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          MessageType: receptorInterface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
          ResponseType: receptorInterface.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
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
          ResponseType: null  // Void receptor - no response type
      );
    }

    // No IReceptor interface found
    return null;
  }

  /// <summary>
  /// Checks if a class implements IPerspectiveOf&lt;TEvent&gt;.
  /// Returns true if the class implements the perspective interface, false otherwise.
  /// Used for WHIZ002 diagnostic - only warn if BOTH IReceptor and IPerspectiveOf are absent.
  /// </summary>
  private static bool HasPerspectiveInterface(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    // Get the symbol for the class
    var classSymbol = semanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken) as INamedTypeSymbol;
    if (classSymbol is null) {
      return false;
    }

    // Look for IPerspectiveOf<TEvent> interface
    var hasPerspective = classSymbol.AllInterfaces.Any(i =>
        i.OriginalDefinition.ToDisplayString() == PERSPECTIVE_INTERFACE_NAME + "<TEvent>");

    return hasPerspective;
  }

  /// <summary>
  /// Generates the dispatcher registration code for all discovered receptors.
  /// Only reports WHIZ002 warning if BOTH receptors and perspectives are absent.
  /// </summary>
  private static void GenerateDispatcherRegistrations(
      SourceProductionContext context,
      ImmutableArray<ReceptorInfo> receptors,
      ImmutableArray<bool> hasPerspectives) {

    // Only warn if BOTH IReceptor and IPerspectiveOf implementations are missing
    // Example: BFF with 5 IPerspectiveOf but no IReceptor should NOT warn
    if (receptors.IsEmpty && hasPerspectives.IsEmpty) {
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.NoReceptorsFound,
          Location.None
      ));
      return;
    }

    // If we have perspectives but no receptors, skip code generation but don't warn
    if (receptors.IsEmpty) {
      return;
    }

    // Report each discovered receptor
    foreach (var receptor in receptors) {
      var responseTypeName = receptor.IsVoid ? "void" : GetSimpleName(receptor.ResponseType!);
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.ReceptorDiscovered,
          Location.None,
          GetSimpleName(receptor.ClassName),
          GetSimpleName(receptor.MessageType),
          responseTypeName
      ));
    }

    var registrationSource = GenerateRegistrationSource(receptors);
    context.AddSource("DispatcherRegistrations.g.cs", registrationSource);

    var dispatcherSource = GenerateDispatcherSource(receptors);
    context.AddSource("Dispatcher.g.cs", dispatcherSource);

    var diagnosticsSource = GenerateDiagnosticsSource(receptors);
    context.AddSource("ReceptorDiscoveryDiagnostics.g.cs", diagnosticsSource);
  }

  /// <summary>
  /// Generates the C# source code for the registration extension method.
  /// Uses template-based generation for IDE support.
  /// Handles both IReceptor&lt;TMessage, TResponse&gt; and IReceptor&lt;TMessage&gt; (void) patterns.
  /// </summary>
  private static string GenerateRegistrationSource(ImmutableArray<ReceptorInfo> receptors) {
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
    result = result.Replace("{{RECEPTOR_COUNT}}", receptors.Length.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "RECEPTOR_REGISTRATIONS", registrations.ToString());

    return result;
  }

  /// <summary>
  /// Generates a complete Dispatcher implementation with zero-reflection routing.
  /// Uses the DispatcherTemplate.cs file for full analyzer support.
  /// Handles both IReceptor&lt;TMessage, TResponse&gt; and IReceptor&lt;TMessage&gt; (void) patterns.
  /// </summary>
  private static string GenerateDispatcherSource(ImmutableArray<ReceptorInfo> receptors) {
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

    // Replace {{VARIABLE}} markers with simple string replacement
    result = result.Replace("{{RECEPTOR_COUNT}}", receptors.Length.ToString());

    // Replace #region markers using shared utilities (robust against whitespace)
    result = TemplateUtilities.ReplaceRegion(result, "SEND_ROUTING", sendRouting.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "VOID_SEND_ROUTING", voidSendRouting.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "PUBLISH_ROUTING", publishRouting.ToString());

    return result;
  }

  /// <summary>
  /// Generates diagnostic registration code that adds receptor discovery
  /// information to the central WhizbangDiagnostics collection.
  /// Uses template-based generation for IDE support.
  /// </summary>
  private static string GenerateDiagnosticsSource(ImmutableArray<ReceptorInfo> receptors) {
    var timestamp = System.DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC");

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
      var responseTypeName = receptor.IsVoid ? "void" : GetSimpleName(receptor.ResponseType!);
      var generatedCode = messageSnippet
          .Replace("__INDEX__", (i + 1).ToString())
          .Replace("__RECEPTOR_NAME__", GetSimpleName(receptor.ClassName))
          .Replace("__MESSAGE_NAME__", GetSimpleName(receptor.MessageType))
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
    result = result.Replace("{{RECEPTOR_COUNT}}", receptors.Length.ToString());
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
  private static string GetSimpleName(string fullyQualifiedName) {
    // Handle tuples: (Type1, Type2, ...)
    if (fullyQualifiedName.StartsWith("(") && fullyQualifiedName.EndsWith(")")) {
      var inner = fullyQualifiedName.Substring(1, fullyQualifiedName.Length - 2);
      var parts = SplitTupleParts(inner);
      var simplifiedParts = new string[parts.Length];
      for (int i = 0; i < parts.Length; i++) {
        simplifiedParts[i] = GetSimpleName(parts[i].Trim());
      }
      return "(" + string.Join(", ", simplifiedParts) + ")";
    }

    // Handle arrays: Type[]
    if (fullyQualifiedName.EndsWith("[]")) {
      var baseType = fullyQualifiedName.Substring(0, fullyQualifiedName.Length - 2);
      return GetSimpleName(baseType) + "[]";
    }

    // Handle simple types
    var lastDot = fullyQualifiedName.LastIndexOf('.');
    return lastDot >= 0 ? fullyQualifiedName.Substring(lastDot + 1) : fullyQualifiedName;
  }

  /// <summary>
  /// Splits tuple parts respecting nested tuples and parentheses.
  /// E.g., "A, B, (C, D)" -> ["A", "B", "(C, D)"]
  /// </summary>
  private static string[] SplitTupleParts(string tupleContent) {
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

    return parts.ToArray();
  }
}
