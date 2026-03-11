using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Generators.Utilities;

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
  private const string PLACEHOLDER_SYNC_ATTRIBUTES = "__SYNC_ATTRIBUTES__";
  private const string PLACEHOLDER_SYNC_AWAIT_CODE = "__SYNC_AWAIT_CODE__";
  private const string PLACEHOLDER_HANDLER_COUNT = "__HANDLER_COUNT__";
  private const string PLACEHOLDER_IS_EXPLICIT = "__IS_EXPLICIT__";
  private const string REGION_NAMESPACE = "NAMESPACE";
  private const string PLACEHOLDER_RECEPTOR_COUNT = "{{RECEPTOR_COUNT}}";
  private const string DEFAULT_NAMESPACE = "Whizbang.Core";

  /// <summary>
  /// Custom SymbolDisplayFormat that includes nullable reference type modifiers.
  /// This preserves the '?' on nullable tuple elements like (List&lt;IEvent&gt;, FailedEvent?).
  /// </summary>
  private static readonly SymbolDisplayFormat _fullyQualifiedFormatWithNullability =
      SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(
          SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

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
  /// Enhanced in Phase 3 to extract [AwaitPerspectiveSync] attributes for perspective sync.
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

    // Extract perspective sync attributes from [AwaitPerspectiveSync] attributes
    var syncAttributes = _extractSyncAttributes(classSymbol);

    // Check for [WhizbangTrace] attribute
    var hasTraceAttribute = _hasWhizbangTraceAttribute(classSymbol);

    // Look for IReceptor<TMessage, TResponse> interface (2 type arguments)
    var receptorInterface = TypeNameHelper.FindInterfaceByOriginalDefinition(
        classSymbol, StandardInterfaceNames.I_RECEPTOR_WITH_RESPONSE_GENERIC_DEFINITION);

    if (receptorInterface is not null && receptorInterface.TypeArguments.Length == 2) {
      // Found IReceptor<TMessage, TResponse> - regular async receptor with response
      // Keep the full response type (including Routed<T>) for DI registration
      // Unwrapping happens later in _extractUniqueEventTypes for cascade generation
      // Use _fullyQualifiedFormatWithNullability for response type to preserve nullable tuple elements
      var messageTypeSymbol = receptorInterface.TypeArguments[0];
      return new ReceptorInfo(
          ClassName: TypeNameHelper.GetFullyQualifiedName(classSymbol),
          MessageType: TypeNameHelper.GetFullyQualifiedName(messageTypeSymbol),
          ResponseType: receptorInterface.TypeArguments[1].ToDisplayString(_fullyQualifiedFormatWithNullability),
          LifecycleStages: lifecycleStages,
          IsSync: false,
          DefaultRouting: defaultRouting,
          SyncAttributes: syncAttributes,
          HasTraceAttribute: hasTraceAttribute,
          IsMessageAnEvent: _implementsIEvent(messageTypeSymbol)
      );
    }

    // Look for IReceptor<TMessage> interface (1 type argument) - void receptor
    var voidReceptorInterface = TypeNameHelper.FindInterfaceByOriginalDefinition(
        classSymbol, StandardInterfaceNames.I_RECEPTOR_GENERIC_DEFINITION);

    if (voidReceptorInterface is not null && voidReceptorInterface.TypeArguments.Length == 1) {
      // Found IReceptor<TMessage> - void async receptor with no response
      var messageTypeSymbol = voidReceptorInterface.TypeArguments[0];
      return new ReceptorInfo(
          ClassName: TypeNameHelper.GetFullyQualifiedName(classSymbol),
          MessageType: TypeNameHelper.GetFullyQualifiedName(messageTypeSymbol),
          ResponseType: null,  // Void receptor - no response type
          LifecycleStages: lifecycleStages,
          IsSync: false,
          DefaultRouting: defaultRouting,
          SyncAttributes: syncAttributes,
          HasTraceAttribute: hasTraceAttribute,
          IsMessageAnEvent: _implementsIEvent(messageTypeSymbol)
      );
    }

    // Look for ISyncReceptor<TMessage, TResponse> interface (2 type arguments) - sync receptor
    var syncReceptorInterface = TypeNameHelper.FindInterfaceByOriginalDefinition(
        classSymbol, StandardInterfaceNames.I_SYNC_RECEPTOR_WITH_RESPONSE_GENERIC_DEFINITION);

    if (syncReceptorInterface is not null && syncReceptorInterface.TypeArguments.Length == 2) {
      // Found ISyncReceptor<TMessage, TResponse> - sync receptor with response
      // Keep the full response type (including Routed<T>) for DI registration
      // Unwrapping happens later in _extractUniqueEventTypes for cascade generation
      // Use _fullyQualifiedFormatWithNullability for response type to preserve nullable tuple elements
      var messageTypeSymbol = syncReceptorInterface.TypeArguments[0];
      return new ReceptorInfo(
          ClassName: TypeNameHelper.GetFullyQualifiedName(classSymbol),
          MessageType: TypeNameHelper.GetFullyQualifiedName(messageTypeSymbol),
          ResponseType: syncReceptorInterface.TypeArguments[1].ToDisplayString(_fullyQualifiedFormatWithNullability),
          LifecycleStages: lifecycleStages,
          IsSync: true,
          DefaultRouting: defaultRouting,
          SyncAttributes: syncAttributes,
          HasTraceAttribute: hasTraceAttribute,
          IsMessageAnEvent: _implementsIEvent(messageTypeSymbol)
      );
    }

    // Look for ISyncReceptor<TMessage> interface (1 type argument) - void sync receptor
    var voidSyncReceptorInterface = TypeNameHelper.FindInterfaceByOriginalDefinition(
        classSymbol, StandardInterfaceNames.I_SYNC_RECEPTOR_GENERIC_DEFINITION);

    if (voidSyncReceptorInterface is not null && voidSyncReceptorInterface.TypeArguments.Length == 1) {
      // Found ISyncReceptor<TMessage> - void sync receptor with no response
      var messageTypeSymbol = voidSyncReceptorInterface.TypeArguments[0];
      return new ReceptorInfo(
          ClassName: TypeNameHelper.GetFullyQualifiedName(classSymbol),
          MessageType: TypeNameHelper.GetFullyQualifiedName(messageTypeSymbol),
          ResponseType: null,  // Void receptor - no response type
          LifecycleStages: lifecycleStages,
          IsSync: true,
          DefaultRouting: defaultRouting,
          SyncAttributes: syncAttributes,
          HasTraceAttribute: hasTraceAttribute,
          IsMessageAnEvent: _implementsIEvent(messageTypeSymbol)
      );
    }

    // No receptor interface found
    return null;
  }

  /// <summary>
  /// Checks if a type symbol implements the IEvent interface.
  /// Used to determine if perspective sync should be generated for a receptor's message type.
  /// </summary>
  /// <param name="typeSymbol">The type symbol to check.</param>
  /// <returns>True if the type implements IEvent, false otherwise.</returns>
  private static bool _implementsIEvent(ITypeSymbol typeSymbol) {
    return TypeNameHelper.ImplementsInterface(typeSymbol, StandardInterfaceNames.I_EVENT);
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
  /// Extracts [AwaitPerspectiveSync] attributes from a receptor class.
  /// Returns an array of SyncAttributeInfo containing the extracted data.
  /// Returns null if no [AwaitPerspectiveSync] attributes are found.
  /// </summary>
  private static SyncAttributeInfo[]? _extractSyncAttributes(INamedTypeSymbol classSymbol) {
    const string AWAIT_SYNC_ATTRIBUTE = "Whizbang.Core.Perspectives.Sync.AwaitPerspectiveSyncAttribute";

    var syncAttributes = new System.Collections.Generic.List<SyncAttributeInfo>();

    foreach (var attribute in classSymbol.GetAttributes()) {
      if (attribute.AttributeClass?.ToDisplayString() != AWAIT_SYNC_ATTRIBUTE) {
        continue;
      }

      // Extract PerspectiveType from constructor argument
      if (attribute.ConstructorArguments.Length == 0) {
        continue;
      }

      var perspectiveTypeArg = attribute.ConstructorArguments[0];
      if (perspectiveTypeArg.Value is not INamedTypeSymbol perspectiveTypeSymbol) {
        continue;
      }

      var perspectiveType = perspectiveTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

      // Extract EventTypes from named argument (Type[]?)
      string[]? eventTypes = null;
      var eventTypesArg = attribute.NamedArguments.FirstOrDefault(na => na.Key == "EventTypes");
      if (eventTypesArg.Value.Kind == TypedConstantKind.Array && !eventTypesArg.Value.IsNull) {
        var eventTypesList = new System.Collections.Generic.List<string>();
        foreach (var typeConstant in eventTypesArg.Value.Values) {
          if (typeConstant.Value is INamedTypeSymbol eventTypeSymbol) {
            eventTypesList.Add(eventTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
          }
        }
        if (eventTypesList.Count > 0) {
          eventTypes = eventTypesList.ToArray();
        }
      }

      // Extract TimeoutMs (int, defaults to -1 which means use DefaultTimeoutMs)
      var timeoutMs = -1;
      var timeoutArg = attribute.NamedArguments.FirstOrDefault(na => na.Key == "TimeoutMs");
      if (timeoutArg.Value.Value is int timeoutValue) {
        timeoutMs = timeoutValue;
      }

      // Extract FireBehavior (enum, defaults to 0 = FireOnSuccess)
      var fireBehavior = 0;
      var fireBehaviorArg = attribute.NamedArguments.FirstOrDefault(na => na.Key == "FireBehavior");
      if (fireBehaviorArg.Value.Value is int fireBehaviorValue) {
        fireBehavior = fireBehaviorValue;
      }

      syncAttributes.Add(new SyncAttributeInfo(
          PerspectiveType: perspectiveType,
          EventTypes: eventTypes,
          TimeoutMs: timeoutMs,
          FireBehavior: fireBehavior
      ));
    }

    return syncAttributes.Count > 0 ? syncAttributes.ToArray() : null;
  }

  /// <summary>
  /// Generates C# code for sync attributes array.
  /// Returns "null" if no sync attributes, otherwise returns the array initializer.
  /// </summary>
  private static string _generateSyncAttributesCode(SyncAttributeInfo[]? syncAttributes) {
    if (syncAttributes is null || syncAttributes.Length == 0) {
      return "null";
    }

    var sb = new StringBuilder();
    sb.Append("new global::Whizbang.Core.Messaging.ReceptorSyncAttributeInfo[] { ");

    for (int i = 0; i < syncAttributes.Length; i++) {
      var attr = syncAttributes[i];

      sb.Append("new global::Whizbang.Core.Messaging.ReceptorSyncAttributeInfo(");
      sb.Append($"PerspectiveType: typeof({attr.PerspectiveType}), ");

      // EventTypes
      if (attr.EventTypes is { Length: > 0 }) {
        sb.Append("EventTypes: new global::System.Type[] { ");
        for (int j = 0; j < attr.EventTypes.Length; j++) {
          if (j > 0) {
            sb.Append(", ");
          }
          sb.Append($"typeof({attr.EventTypes[j]})");
        }
        sb.Append(" }, ");
      } else {
        sb.Append("EventTypes: null, ");
      }

      sb.Append($"TimeoutMs: {attr.TimeoutMs}, ");
      var fireBehaviorValue = attr.FireBehavior switch {
        0 => "global::Whizbang.Core.Perspectives.Sync.SyncFireBehavior.FireOnSuccess",
        1 => "global::Whizbang.Core.Perspectives.Sync.SyncFireBehavior.FireAlways",
        2 => "global::Whizbang.Core.Perspectives.Sync.SyncFireBehavior.FireOnEachEvent",
        _ => "global::Whizbang.Core.Perspectives.Sync.SyncFireBehavior.FireOnSuccess"
      };
      sb.Append($"FireBehavior: {fireBehaviorValue})");

      if (i < syncAttributes.Length - 1) {
        sb.Append(", ");
      }
    }

    sb.Append(" }");
    return sb.ToString();
  }

  /// <summary>
  /// Generates C# code for sync await operations to be inserted into invoker delegates.
  /// Called by SendAsync-generated invokers BEFORE calling the receptor.
  /// Returns empty string if no sync attributes or message is not an IEvent.
  /// </summary>
  /// <param name="syncAttributes">The sync attributes from the receptor.</param>
  /// <param name="messageType">The fully qualified message type.</param>
  /// <param name="isMessageAnEvent">True if the message type implements IEvent.</param>
  /// <returns>Generated sync await code, or empty string.</returns>
  private static string _generateSyncAwaitCode(SyncAttributeInfo[]? syncAttributes, string messageType, bool isMessageAnEvent) {
    if (syncAttributes is null || syncAttributes.Length == 0) {
      return "// No [AwaitPerspectiveSync] attributes - skip sync checking";
    }

    // Perspectives only process events, not commands or other message types.
    // Waiting for perspective sync on a non-event would wait forever and timeout.
    if (!isMessageAnEvent) {
      return "// [AwaitPerspectiveSync] ignored - message is not an IEvent (perspectives only process events)";
    }

    var sb = new StringBuilder();
    sb.AppendLine("var syncAwaiter = scope.ServiceProvider.GetService<global::Whizbang.Core.Perspectives.Sync.IPerspectiveSyncAwaiter>();");
    sb.AppendLine("          var streamIdExtractor = scope.ServiceProvider.GetService<global::Whizbang.Core.IStreamIdExtractor>();");
    sb.AppendLine("          if (syncAwaiter != null && streamIdExtractor != null) {");
    sb.AppendLine($"            var streamId = streamIdExtractor.ExtractStreamId(msg, typeof({messageType}));");
    sb.AppendLine("            if (streamId.HasValue) {");

    // Generate await call for each sync attribute
    foreach (var attr in syncAttributes) {
      // Determine timeout - use default if -1
      var timeoutMs = attr.TimeoutMs == -1 ? 5000 : attr.TimeoutMs;

      // Generate event types array
      string eventTypesCode;
      if (attr.EventTypes is { Length: > 0 }) {
        var eventTypesList = string.Join(", ", attr.EventTypes.Select(et => $"typeof({et})"));
        eventTypesCode = $"new global::System.Type[] {{ {eventTypesList} }}";
      } else {
        eventTypesCode = "null";
      }

      // Capture the sync result
      sb.AppendLine($"              var syncResult = await syncAwaiter.WaitForStreamAsync(");
      sb.AppendLine($"                typeof({attr.PerspectiveType}),");
      sb.AppendLine($"                streamId.Value,");
      sb.AppendLine($"                {eventTypesCode},");
      sb.AppendLine($"                global::System.TimeSpan.FromMilliseconds({timeoutMs}));");

      // Check the result based on FireBehavior
      // 0 = FireOnSuccess (throw on timeout - default)
      // 1 = FireAlways (don't throw, let handler check SyncContext)
      // 2 = FireOnEachEvent (future streaming mode)
      if (attr.FireBehavior == 0) {
        sb.AppendLine($"              if (syncResult.Outcome == global::Whizbang.Core.Perspectives.Sync.SyncOutcome.TimedOut) {{");
        sb.AppendLine($"                throw new global::Whizbang.Core.Perspectives.Sync.PerspectiveSyncTimeoutException(");
        sb.AppendLine($"                  typeof({attr.PerspectiveType}),");
        sb.AppendLine($"                  global::System.TimeSpan.FromMilliseconds({timeoutMs}),");
        sb.AppendLine($"                  $\"Perspective sync timed out waiting for {{typeof({attr.PerspectiveType}).Name}} to process stream {{streamId.Value}} within {timeoutMs}ms.\");");
        sb.AppendLine($"              }}");
      }
    }

    sb.AppendLine("            }");
    sb.AppendLine("          }");

    return sb.ToString();
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
  /// Checks if a class symbol has the [WhizbangTrace] attribute.
  /// Returns true if the attribute is present, false otherwise.
  /// Used to determine if tracing code should be generated for a receptor.
  /// </summary>
  private static bool _hasWhizbangTraceAttribute(INamedTypeSymbol classSymbol) {
    const string WHIZBANG_TRACE_ATTRIBUTE = "Whizbang.Core.Tracing.WhizbangTraceAttribute";

    foreach (var attribute in classSymbol.GetAttributes()) {
      if (attribute.AttributeClass?.ToDisplayString() == WHIZBANG_TRACE_ATTRIBUTE) {
        return true;
      }
    }

    return false;
  }

  /// <summary>
  /// Unwraps Routed&lt;T&gt; wrapper type names from string representation.
  /// Handles patterns like "global::Whizbang.Core.Dispatch.Routed&lt;global::MyApp.MyEvent&gt;".
  /// Returns null for RoutedNone types, the inner type for Routed&lt;T&gt;, or the original for non-Routed types.
  /// </summary>
  /// <param name="typeName">The fully qualified type name to unwrap.</param>
  /// <returns>The unwrapped type name, or null for RoutedNone.</returns>
  /// <tests>Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithTupleOfRoutedResponses_*</tests>
  private static string? _unwrapRoutedTypeString(string typeName) {
    // Check for RoutedNone - skip in cascade
    if (typeName.Contains("RoutedNone")) {
      return null;
    }

    // Check for Routed<T> pattern - extract inner type
    // Pattern: global::Whizbang.Core.Dispatch.Routed<InnerType> or Whizbang.Core.Dispatch.Routed<InnerType>
    const string routedPrefix1 = "global::Whizbang.Core.Dispatch.Routed<";
    const string routedPrefix2 = "Whizbang.Core.Dispatch.Routed<";

    if (typeName.StartsWith(routedPrefix1, StringComparison.Ordinal)) {
      // Extract inner type: remove prefix and trailing >
      var inner = typeName.Substring(routedPrefix1.Length, typeName.Length - routedPrefix1.Length - 1);
      return inner;
    }

    if (typeName.StartsWith(routedPrefix2, StringComparison.Ordinal)) {
      var inner = typeName.Substring(routedPrefix2.Length, typeName.Length - routedPrefix2.Length - 1);
      return inner;
    }

    // Not a Routed wrapper - return as-is
    return typeName;
  }

  /// <summary>
  /// Strips the trailing '?' nullable annotation from a type name.
  /// This is necessary because typeof() cannot be used with nullable reference types (CS8639).
  /// </summary>
  /// <param name="typeName">The type name that may have a trailing '?'.</param>
  /// <returns>The type name without the trailing '?'.</returns>
  private static string _stripNullableAnnotation(string typeName) {
    return typeName.EndsWith("?", StringComparison.Ordinal)
      ? typeName.Substring(0, typeName.Length - 1)
      : typeName;
  }

  /// <summary>
  /// Determines if a type name is a Whizbang interface type (IEvent, ICommand, IMessage).
  /// These interfaces require pattern matching (message is IEvent) instead of exact type matching
  /// (messageType == typeof(IEvent)) because concrete types implement these interfaces.
  /// </summary>
  /// <param name="typeName">The fully qualified type name.</param>
  /// <returns>True if the type is a Whizbang interface, false otherwise.</returns>
  private static bool _isWhizbangInterface(string typeName) {
    return typeName == "global::Whizbang.Core.IEvent" ||
           typeName == "global::Whizbang.Core.ICommand" ||
           typeName == "global::Whizbang.Core.IMessage" ||
           typeName == "global::Whizbang.Core.Messaging.IEvent" ||
           typeName == "global::Whizbang.Core.Messaging.ICommand" ||
           typeName == "global::Whizbang.Core.Messaging.IMessage";
  }

  /// <summary>
  /// Extracts the element type from a generic collection type string.
  /// Handles List&lt;T&gt;, IList&lt;T&gt;, IEnumerable&lt;T&gt;, ICollection&lt;T&gt;, IReadOnlyList&lt;T&gt;, etc.
  /// </summary>
  /// <param name="typeName">The type name that may be a generic collection.</param>
  /// <returns>The element type if it's a recognized collection, or null if not a collection.</returns>
  private static string? _extractCollectionElementType(string typeName) {
    // Collection type prefixes to check (fully qualified and simple names)
    string[] collectionPrefixes = {
      "global::System.Collections.Generic.List<",
      "global::System.Collections.Generic.IList<",
      "global::System.Collections.Generic.IEnumerable<",
      "global::System.Collections.Generic.ICollection<",
      "global::System.Collections.Generic.IReadOnlyList<",
      "global::System.Collections.Generic.IReadOnlyCollection<",
      "System.Collections.Generic.List<",
      "System.Collections.Generic.IList<",
      "System.Collections.Generic.IEnumerable<",
      "System.Collections.Generic.ICollection<",
      "System.Collections.Generic.IReadOnlyList<",
      "System.Collections.Generic.IReadOnlyCollection<",
      "List<",
      "IList<",
      "IEnumerable<",
      "ICollection<",
      "IReadOnlyList<",
      "IReadOnlyCollection<"
    };

    foreach (var prefix in collectionPrefixes) {
      if (typeName.StartsWith(prefix, StringComparison.Ordinal) && typeName.EndsWith(">", StringComparison.Ordinal)) {
        // Extract inner type: remove prefix and trailing >
        var inner = typeName.Substring(prefix.Length, typeName.Length - prefix.Length - 1);
        return inner;
      }
    }

    return null;  // Not a recognized collection type
  }

  /// <summary>
  /// Extracts unique event types from receptor response types for outbox cascade generation.
  /// Handles simple event types, tuples (extracts all event elements), arrays (extracts element type),
  /// and generic collections like List&lt;T&gt; (extracts element type).
  /// Also unwraps Routed&lt;T&gt; wrappers to extract inner types.
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
          // Unwrap Routed<T> if present
          var unwrappedElement = _unwrapRoutedTypeString(element);
          if (unwrappedElement is null) {
            continue;  // Skip RoutedNone
          }

          // Elements may be arrays, collections, or simple types - extract element type appropriately
          if (unwrappedElement.EndsWith("[]", StringComparison.Ordinal)) {
            // Array type: Type[] - extract element type
            var elementType = unwrappedElement.Substring(0, unwrappedElement.Length - 2);
            // Strip nullable annotation to avoid CS8639 (typeof cannot use nullable reference types)
            eventTypes.Add(_stripNullableAnnotation(elementType));
          } else {
            // Check if it's a generic collection like List<T>
            var collectionElementType = _extractCollectionElementType(unwrappedElement);
            if (collectionElementType is not null) {
              // Collection type: List<T>, IEnumerable<T>, etc. - extract element type
              // Strip nullable annotation to avoid CS8639 (typeof cannot use nullable reference types)
              eventTypes.Add(_stripNullableAnnotation(collectionElementType));
            } else {
              // Simple type - add as-is
              // Strip nullable annotation to avoid CS8639 (typeof cannot use nullable reference types)
              eventTypes.Add(_stripNullableAnnotation(unwrappedElement));
            }
          }
        }
      }
      // Handle array types: Type[] - extract element type
      else if (responseType.EndsWith("[]", StringComparison.Ordinal)) {
        var elementType = responseType.Substring(0, responseType.Length - 2);
        // Unwrap Routed<T> if present
        var unwrappedElementType = _unwrapRoutedTypeString(elementType);
        if (unwrappedElementType is not null) {
          // Strip nullable annotation to avoid CS8639 (typeof cannot use nullable reference types)
          eventTypes.Add(_stripNullableAnnotation(unwrappedElementType));
        }
      }
      // Check if it's a generic collection like List<T> (not in a tuple)
      else {
        var collectionElementType = _extractCollectionElementType(responseType);
        if (collectionElementType is not null) {
          // Collection type: List<T>, IEnumerable<T>, etc. - extract element type
          // Unwrap Routed<T> if present in the element type
          var unwrappedCollectionElement = _unwrapRoutedTypeString(collectionElementType);
          if (unwrappedCollectionElement is not null) {
            // Strip nullable annotation to avoid CS8639 (typeof cannot use nullable reference types)
            eventTypes.Add(_stripNullableAnnotation(unwrappedCollectionElement));
          }
        } else {
          // Simple type - unwrap Routed<T> if present
          var unwrappedType = _unwrapRoutedTypeString(responseType);
          if (unwrappedType is not null) {
            // Strip nullable annotation to avoid CS8639 (typeof cannot use nullable reference types)
            eventTypes.Add(_stripNullableAnnotation(unwrappedType));
          }
        }
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
    // Use FullyQualifiedFormat to include global:: prefix which matches our constant
    var hasPerspective = classSymbol.AllInterfaces.Any(i => {
      var originalDef = TypeNameHelper.GetOriginalDefinitionName(i);
      // Check if it starts with our perspective interface name and has at least 2 type arguments (model + events)
      return originalDef.StartsWith(StandardInterfaceNames.I_PERSPECTIVE_FOR + "<", StringComparison.Ordinal) && i.TypeArguments.Length >= 2;
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

    // Validate: RPC handlers (non-void receptors) must have exactly one handler per message type
    // Multiple handlers are allowed for void receptors (event-style dispatch)
    // Multiple handlers are also allowed for sync receptors (ISyncReceptor) which don't go through RPC path
    var rpcReceptorsByMessage = receptors
        .Where(r => !r.IsVoid && !r.IsSync)  // Only async receptors with response are RPC
        .GroupBy(r => r.MessageType)
        .Where(g => g.Count() > 1)  // Only groups with multiple handlers
        .ToList();

    foreach (var conflictGroup in rpcReceptorsByMessage) {
      var messageType = conflictGroup.Key;
      var handlerNames = string.Join(", ", conflictGroup.Select(r => TypeNameUtilities.GetSimpleName(r.ClassName)));

      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.MultipleHandlersForRpcMessage,
          Location.None,
          TypeNameUtilities.GetSimpleName(messageType),
          handlerNames
      ));
    }

    var registrationSource = _generateRegistrationSource(compilation, receptors);
    context.AddSource("DispatcherRegistrations.g.cs", registrationSource);

    var dispatcherSource = _generateDispatcherSource(compilation, receptors);
    context.AddSource("Dispatcher.g.cs", dispatcherSource);

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
              .Replace(PLACEHOLDER_SYNC_RECEPTOR_INTERFACE, StandardInterfaceNames.I_SYNC_RECEPTOR)
              .Replace(PLACEHOLDER_MESSAGE_TYPE, receptor.MessageType)
              .Replace(PLACEHOLDER_RECEPTOR_CLASS, receptor.ClassName);
        } else {
          // Regular sync receptor: ISyncReceptor<TMessage, TResponse>
          generatedCode = syncRegistrationSnippet
              .Replace(PLACEHOLDER_SYNC_RECEPTOR_INTERFACE, StandardInterfaceNames.I_SYNC_RECEPTOR)
              .Replace(PLACEHOLDER_MESSAGE_TYPE, receptor.MessageType)
              .Replace(PLACEHOLDER_RESPONSE_TYPE, receptor.ResponseType!)
              .Replace(PLACEHOLDER_RECEPTOR_CLASS, receptor.ClassName);
        }
      } else {
        // Async receptor
        if (receptor.IsVoid) {
          // Void receptor: IReceptor<TMessage>
          generatedCode = voidRegistrationSnippet
              .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, StandardInterfaceNames.I_RECEPTOR)
              .Replace(PLACEHOLDER_MESSAGE_TYPE, receptor.MessageType)
              .Replace(PLACEHOLDER_RECEPTOR_CLASS, receptor.ClassName);
        } else {
          // Regular receptor: IReceptor<TMessage, TResponse>
          generatedCode = registrationSnippet
              .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, StandardInterfaceNames.I_RECEPTOR)
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

      // Generate sync await code for this receptor's [AwaitPerspectiveSync] attributes
      var syncAwaitCode = _generateSyncAwaitCode(firstReceptor.SyncAttributes, messageType, firstReceptor.IsMessageAnEvent);

      // Replace placeholders with actual types
      var generatedCode = sendSnippet
          .Replace(PLACEHOLDER_MESSAGE_TYPE, messageType)
          .Replace(PLACEHOLDER_RESPONSE_TYPE, firstReceptor.ResponseType!)
          .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, StandardInterfaceNames.I_RECEPTOR)
          .Replace(PLACEHOLDER_RECEPTOR_CLASS, firstReceptor.ClassName)
          .Replace(PLACEHOLDER_SYNC_AWAIT_CODE, syncAwaitCode);

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
      var receptorList = voidReceptorsByMessage[messageType];
      var firstReceptor = receptorList[0];

      // Generate sync await code for this receptor's [AwaitPerspectiveSync] attributes
      var syncAwaitCode = _generateSyncAwaitCode(firstReceptor.SyncAttributes, messageType, firstReceptor.IsMessageAnEvent);

      // Replace placeholders with actual types
      var generatedCode = voidSendSnippet
          .Replace(PLACEHOLDER_MESSAGE_TYPE, messageType)
          .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, StandardInterfaceNames.I_RECEPTOR)
          .Replace(PLACEHOLDER_RECEPTOR_CLASS, firstReceptor.ClassName)
          .Replace(PLACEHOLDER_SYNC_AWAIT_CODE, syncAwaitCode);

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
          .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, StandardInterfaceNames.I_RECEPTOR);

      publishRouting.AppendLine(TemplateUtilities.IndentCode(generatedCode, "      "));
    }

    // Load Untyped Publish routing snippet from template (for auto-cascade)
    var untypedPublishSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "UNTYPED_PUBLISH_ROUTING_SNIPPET"
    );

    // Extract unique event types from receptor response types
    // These are the types that will be cascaded (returned from receptors)
    var eventTypes = _extractUniqueEventTypes(receptors);

    // Combine message types (handled by receptors) + event types (returned by receptors and cascaded)
    // This ensures cascaded events get proper security context establishment even if no receptor handles them
    var allTypesForUntypedPublish = allMessageTypes.Union(eventTypes).Distinct().ToList();

    // Generate Untyped Publish routing code using snippet template
    // This enables auto-cascade: events extracted from receptor return values are published
    var untypedPublishRouting = new StringBuilder();
    foreach (var messageType in allTypesForUntypedPublish) {
      // Replace placeholders with actual types
      var generatedCode = untypedPublishSnippet
          .Replace(PLACEHOLDER_MESSAGE_TYPE, messageType)
          .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, StandardInterfaceNames.I_RECEPTOR);

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
          .Replace(PLACEHOLDER_SYNC_RECEPTOR_INTERFACE, StandardInterfaceNames.I_SYNC_RECEPTOR)
          .Replace(PLACEHOLDER_RECEPTOR_CLASS, firstReceptor.ClassName);

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
      var receptorList = voidSyncReceptorsByMessage[messageType];
      var firstReceptor = receptorList[0];

      // Replace placeholders with actual types
      var generatedCode = voidSyncSendSnippet
          .Replace(PLACEHOLDER_MESSAGE_TYPE, messageType)
          .Replace(PLACEHOLDER_SYNC_RECEPTOR_INTERFACE, StandardInterfaceNames.I_SYNC_RECEPTOR)
          .Replace(PLACEHOLDER_RECEPTOR_CLASS, firstReceptor.ClassName);

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

      // Generate sync await code for this receptor's [AwaitPerspectiveSync] attributes
      var syncAwaitCode = _generateSyncAwaitCode(firstReceptor.SyncAttributes, messageType, firstReceptor.IsMessageAnEvent);

      // Replace placeholders with actual types
      var generatedCode = anySendNonVoidSnippet
          .Replace(PLACEHOLDER_MESSAGE_TYPE, messageType)
          .Replace(PLACEHOLDER_RESPONSE_TYPE, firstReceptor.ResponseType!)
          .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, StandardInterfaceNames.I_RECEPTOR)
          .Replace(PLACEHOLDER_RECEPTOR_CLASS, firstReceptor.ClassName)
          .Replace(PLACEHOLDER_SYNC_AWAIT_CODE, syncAwaitCode);

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
    // Reuse event types extracted earlier for untyped publish routing
    var outboxCascade = new StringBuilder();

    // Separate concrete types from interface types
    // Concrete types use exact typeof() matching; interface types use 'is' pattern matching
    var concreteTypes = eventTypes.Where(t => !_isWhizbangInterface(t)).ToList();
    var interfaceTypes = eventTypes.Where(t => _isWhizbangInterface(t)).ToList();

    // Generate concrete type cascades first (exact type matching)
    foreach (var eventType in concreteTypes) {
      outboxCascade.AppendLine($"      if (messageType == typeof({eventType})) {{");
      // CRITICAL: Use passed eventId for sync tracking consistency, or generate new if not provided
      // This ensures the same ID is used for tracking (singleton tracker) AND storage (outbox)
      outboxCascade.AppendLine($"        var messageId = eventId.HasValue ? new global::Whizbang.Core.ValueObjects.MessageId(eventId.Value) : global::Whizbang.Core.ValueObjects.MessageId.New();");
      outboxCascade.AppendLine($"        return PublishToOutboxAsync(({eventType})message, messageType, messageId, sourceEnvelope);");
      outboxCascade.AppendLine($"      }}");
      outboxCascade.AppendLine();
    }

    // Generate interface type cascades (pattern matching for any implementing type)
    // This handles List<IEvent>, IEvent[], etc. where concrete types implement the interface
    // Uses PublishToOutboxDynamicAsync which serializes using the runtime type, not the interface type
    foreach (var eventType in interfaceTypes) {
      outboxCascade.AppendLine($"      if (message is {eventType}) {{");
      // CRITICAL: Use passed eventId for sync tracking consistency, or generate new if not provided
      // This ensures the same ID is used for tracking (singleton tracker) AND storage (outbox)
      outboxCascade.AppendLine($"        var messageId = eventId.HasValue ? new global::Whizbang.Core.ValueObjects.MessageId(eventId.Value) : global::Whizbang.Core.ValueObjects.MessageId.New();");
      // Use PublishToOutboxDynamicAsync which serializes using messageType (runtime type), not the interface
      outboxCascade.AppendLine($"        return PublishToOutboxDynamicAsync(message, messageType, messageId, sourceEnvelope);");
      outboxCascade.AppendLine($"      }}");
      outboxCascade.AppendLine();
    }

    // Generate event store only cascade type-switch (for storing events without transport)
    // Uses eventStoreOnly: true to set destination=null, bypassing transport publishing
    var eventStoreOnlyCascade = new StringBuilder();

    // Generate concrete type cascades first (exact type matching)
    foreach (var eventType in concreteTypes) {
      eventStoreOnlyCascade.AppendLine($"      if (messageType == typeof({eventType})) {{");
      // CRITICAL: Use passed eventId for sync tracking consistency, or generate new if not provided
      // This ensures the same ID is used for tracking (singleton tracker) AND storage (event store)
      eventStoreOnlyCascade.AppendLine($"        var messageId = eventId.HasValue ? new global::Whizbang.Core.ValueObjects.MessageId(eventId.Value) : global::Whizbang.Core.ValueObjects.MessageId.New();");
      eventStoreOnlyCascade.AppendLine($"        return PublishToOutboxAsync(({eventType})message, messageType, messageId, sourceEnvelope, eventStoreOnly: true);");
      eventStoreOnlyCascade.AppendLine($"      }}");
      eventStoreOnlyCascade.AppendLine();
    }

    // Generate interface type cascades (pattern matching for any implementing type)
    // Uses PublishToOutboxDynamicAsync which serializes using the runtime type, not the interface type
    foreach (var eventType in interfaceTypes) {
      eventStoreOnlyCascade.AppendLine($"      if (message is {eventType}) {{");
      // CRITICAL: Use passed eventId for sync tracking consistency, or generate new if not provided
      // This ensures the same ID is used for tracking (singleton tracker) AND storage (event store)
      eventStoreOnlyCascade.AppendLine($"        var messageId = eventId.HasValue ? new global::Whizbang.Core.ValueObjects.MessageId(eventId.Value) : global::Whizbang.Core.ValueObjects.MessageId.New();");
      // Use PublishToOutboxDynamicAsync which serializes using messageType (runtime type), not the interface
      eventStoreOnlyCascade.AppendLine($"        return PublishToOutboxDynamicAsync(message, messageType, messageId, sourceEnvelope, eventStoreOnly: true);");
      eventStoreOnlyCascade.AppendLine($"      }}");
      eventStoreOnlyCascade.AppendLine();
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
    result = TemplateUtilities.ReplaceRegion(result, "EVENT_STORE_ONLY_CASCADE", eventStoreOnlyCascade.ToString());

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

    // Load traced snippets for receptors with [WhizbangTrace] attribute
    var tracedResponseSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "RECEPTOR_REGISTRY_TRACED_ROUTING_SNIPPET"
    );

    var tracedVoidSnippet = TemplateUtilities.ExtractSnippet(
        typeof(ReceptorDiscoveryGenerator).Assembly,
        TEMPLATE_SNIPPET_FILE,
        "RECEPTOR_REGISTRY_TRACED_VOID_ROUTING_SNIPPET"
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

    // Group routing pairs by (messageType, stage) to generate combined if-blocks
    // This ensures all receptors for the same (messageType, stage) are in a single array
    var groupedRoutingPairs = routingPairs
        .GroupBy(p => (p.Receptor.MessageType, p.Stage))
        .ToList();

    // Calculate handler count per (message type, stage) for tracing
    var handlerCountByKey = groupedRoutingPairs
        .ToDictionary(g => g.Key, g => g.Count());

    // Generate routing code for each (messageType, stage) group
    var routingCode = new StringBuilder();
    foreach (var group in groupedRoutingPairs) {
      var (messageType, stage) = group.Key;
      var receptorsInGroup = group.Select(g => g.Receptor).ToList();
      var handlerCount = receptorsInGroup.Count;

      // Generate if-block header
      routingCode.AppendLine($"    if (messageType == typeof({messageType}) && stage == {stage}) {{");
      routingCode.AppendLine("      compileTimeEntries = new global::Whizbang.Core.Messaging.ReceptorInfo[] {");

      // Generate each ReceptorInfo entry in the array
      for (int i = 0; i < receptorsInGroup.Count; i++) {
        var receptor = receptorsInGroup[i];
        var isLast = (i == receptorsInGroup.Count - 1);

        // Generate the sync attributes code for this receptor
        var syncAttributesCode = _generateSyncAttributesCode(receptor.SyncAttributes);

        // Generate ReceptorInfo entry
        var receptorEntry = _generateReceptorInfoEntry(
            receptor,
            syncAttributesCode,
            handlerCount,
            responseSnippet,
            voidSnippet,
            tracedResponseSnippet,
            tracedVoidSnippet);

        // Add comma if not last entry
        if (!isLast) {
          receptorEntry = receptorEntry.TrimEnd() + ",";
        }

        routingCode.AppendLine(TemplateUtilities.IndentCode(receptorEntry, "        "));
      }

      // Generate if-block footer
      routingCode.AppendLine("      };");
      routingCode.AppendLine("    }");
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
  /// Generates a single ReceptorInfo entry for the routing array.
  /// This is used when multiple receptors handle the same (messageType, stage) combination.
  /// </summary>
  private static string _generateReceptorInfoEntry(
      ReceptorInfo receptor,
      string syncAttributesCode,
      int handlerCount,
      string responseSnippet,
      string voidSnippet,
      string tracedResponseSnippet,
      string tracedVoidSnippet) {

    string snippet;
    if (receptor.HasTraceAttribute) {
      snippet = receptor.IsVoid ? tracedVoidSnippet : tracedResponseSnippet;
    } else {
      snippet = receptor.IsVoid ? voidSnippet : responseSnippet;
    }

    // Extract just the ReceptorInfo entry from the snippet (skip the if-block wrapper)
    // The snippets have structure: if (...) { return new ReceptorInfo[] { <entry> }; }
    // We need just the <entry> part
    var entryStart = snippet.IndexOf("new global::Whizbang.Core.Messaging.ReceptorInfo(", StringComparison.Ordinal);
    if (entryStart < 0) {
      // Fallback: generate manually if snippet structure is unexpected
      return _generateReceptorInfoEntryManually(receptor, syncAttributesCode, handlerCount);
    }

    // Find matching closing parenthesis for the ReceptorInfo constructor
    int parenDepth = 0;
    int entryEnd = entryStart;
    for (int i = entryStart; i < snippet.Length; i++) {
      if (snippet[i] == '(') {
        parenDepth++;
      } else if (snippet[i] == ')') {
        parenDepth--;
        if (parenDepth == 0) {
          entryEnd = i + 1;
          break;
        }
      }
    }

    var entryTemplate = snippet.Substring(entryStart, entryEnd - entryStart);

    // Apply replacements
    var result = entryTemplate
        .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, StandardInterfaceNames.I_RECEPTOR)
        .Replace(PLACEHOLDER_MESSAGE_TYPE, receptor.MessageType)
        .Replace(PLACEHOLDER_RECEPTOR_CLASS, receptor.ClassName)
        .Replace(PLACEHOLDER_SYNC_ATTRIBUTES, syncAttributesCode);

    if (!receptor.IsVoid && receptor.ResponseType is not null) {
      result = result.Replace(PLACEHOLDER_RESPONSE_TYPE, receptor.ResponseType);
    }

    if (receptor.HasTraceAttribute) {
      result = result
          .Replace(PLACEHOLDER_HANDLER_COUNT, handlerCount.ToString(CultureInfo.InvariantCulture))
          .Replace(PLACEHOLDER_IS_EXPLICIT, "true");
    }

    return result;
  }

  /// <summary>
  /// Fallback method to generate ReceptorInfo entry manually if snippet extraction fails.
  /// </summary>
  private static string _generateReceptorInfoEntryManually(
      ReceptorInfo receptor,
      string syncAttributesCode,
      int handlerCount) {

    var sb = new StringBuilder();
    sb.AppendLine($"new global::Whizbang.Core.Messaging.ReceptorInfo(");
    sb.AppendLine($"  MessageType: typeof({receptor.MessageType}),");
    sb.AppendLine($"  ReceptorId: \"{receptor.ClassName}\",");
    sb.AppendLine($"  InvokeAsync: async (sp, msg, ct) => {{");

    if (receptor.IsVoid) {
      sb.AppendLine($"    var receptor = sp.GetRequiredService<{StandardInterfaceNames.I_RECEPTOR}<{receptor.MessageType}>>();");
      sb.AppendLine($"    await receptor.HandleAsync(({receptor.MessageType})msg, ct);");
      sb.AppendLine($"    return null;");
    } else {
      sb.AppendLine($"    var receptor = sp.GetRequiredService<{StandardInterfaceNames.I_RECEPTOR}<{receptor.MessageType}, {receptor.ResponseType}>>();");
      sb.AppendLine($"    var result = await receptor.HandleAsync(({receptor.MessageType})msg, ct);");
      sb.AppendLine($"    if ((object)result is global::Whizbang.Core.Dispatch.IRouted routedResult) {{");
      sb.AppendLine($"      return routedResult.Value;");
      sb.AppendLine($"    }}");
      sb.AppendLine($"    return result;");
    }

    sb.AppendLine($"  }},");
    sb.AppendLine($"  SyncAttributes: {syncAttributesCode}");
    sb.Append(')');

    return sb.ToString();
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
