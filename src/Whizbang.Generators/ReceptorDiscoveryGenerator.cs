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
  private const string INDENT_6 = "      ";
  private const string INDENT_6_CLOSE_BRACE = "      }";
  private const string MESSAGE_ID_FROM_EVENT_ID = "        var messageId = eventId.HasValue ? new global::Whizbang.Core.ValueObjects.MessageId(eventId.Value) : global::Whizbang.Core.ValueObjects.MessageId.New();";

  private const string PLACEHOLDER_INDEX = "__INDEX__";
  private const string PLACEHOLDER_RECEPTOR_NAME = "__RECEPTOR_NAME__";
  private const string PLACEHOLDER_MESSAGE_NAME = "__MESSAGE_NAME__";
  private const string PLACEHOLDER_RESPONSE_NAME = "__RESPONSE_NAME__";
  private const string PLACEHOLDER_SYNC_ATTRIBUTES = "__SYNC_ATTRIBUTES__";
  private const string PLACEHOLDER_SYNC_AWAIT_CODE = "__SYNC_AWAIT_CODE__";
  private const string PLACEHOLDER_HANDLER_COUNT = "__HANDLER_COUNT__";
  private const string PLACEHOLDER_IS_EXPLICIT = "__IS_EXPLICIT__";
  private const string PLACEHOLDER_FIRE_DURING_REPLAY = "__FIRE_DURING_REPLAY__";
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

    // Check for [FireDuringReplay] attribute
    var hasFireDuringReplayAttribute = _hasFireDuringReplayAttribute(classSymbol);

    // Try each receptor interface in order: async with response, async void, sync with response, sync void
    var interfaceCandidates = new[] {
      (Definition: StandardInterfaceNames.I_RECEPTOR_WITH_RESPONSE_GENERIC_DEFINITION, ArgCount: 2, IsSync: false),
      (Definition: StandardInterfaceNames.I_RECEPTOR_GENERIC_DEFINITION, ArgCount: 1, IsSync: false),
      (Definition: StandardInterfaceNames.I_SYNC_RECEPTOR_WITH_RESPONSE_GENERIC_DEFINITION, ArgCount: 2, IsSync: true),
      (Definition: StandardInterfaceNames.I_SYNC_RECEPTOR_GENERIC_DEFINITION, ArgCount: 1, IsSync: true),
    };

    foreach (var (definition, argCount, isSync) in interfaceCandidates) {
      var found = TypeNameHelper.FindInterfaceByOriginalDefinition(classSymbol, definition);
      if (found?.TypeArguments.Length != argCount) {
        continue;
      }

      var messageTypeSymbol = found.TypeArguments[0];
      // 2-arg interfaces have a response type; 1-arg are void receptors
      var responseType = argCount == 2
          ? found.TypeArguments[1].ToDisplayString(_fullyQualifiedFormatWithNullability)
          : null;

      return new ReceptorInfo(
          ClassName: TypeNameHelper.GetFullyQualifiedName(classSymbol),
          MessageType: TypeNameHelper.GetFullyQualifiedName(messageTypeSymbol),
          ResponseType: responseType,
          LifecycleStages: lifecycleStages,
          IsSync: isSync,
          DefaultRouting: defaultRouting,
          SyncAttributes: syncAttributes,
          HasTraceAttribute: hasTraceAttribute,
          IsMessageAnEvent: _implementsIEvent(messageTypeSymbol),
          IsPolymorphicMessageType: _isPolymorphicType(messageTypeSymbol),
          HasFireDuringReplayAttribute: hasFireDuringReplayAttribute
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
  /// Returns true if the type is polymorphic — an interface or a non-sealed class —
  /// meaning concrete derived/implementing types should be expanded at compile time.
  /// </summary>
  private static bool _isPolymorphicType(ITypeSymbol type) =>
      type.TypeKind == TypeKind.Interface ||
      (type.TypeKind == TypeKind.Class && !type.IsSealed);

  /// <summary>
  /// Finds all concrete (non-abstract) types in the compilation that derive from or implement
  /// the given type. Used for polymorphic receptor expansion.
  /// </summary>
  private static System.Collections.Generic.List<string> _findConcreteSubtypes(
      Compilation compilation, string baseTypeFQN) {
    var baseSymbol = compilation.GetTypeByMetadataName(
        baseTypeFQN.Replace("global::", ""));
    if (baseSymbol is null) {
      return [];
    }

    var result = new System.Collections.Generic.List<string>();
    _walkNamespaceForSubtypes(compilation.GlobalNamespace, baseSymbol, result);
    return result;
  }

  private static void _walkNamespaceForSubtypes(
      INamespaceSymbol ns, INamedTypeSymbol target, System.Collections.Generic.List<string> result) {
    foreach (var type in ns.GetTypeMembers()) {
      _checkTypeAndNestedTypes(type, target, result);
    }
    foreach (var childNs in ns.GetNamespaceMembers()) {
      _walkNamespaceForSubtypes(childNs, target, result);
    }
  }

  private static void _checkTypeAndNestedTypes(
      INamedTypeSymbol type, INamedTypeSymbol target, System.Collections.Generic.List<string> result) {
    if (!type.IsAbstract && type.TypeKind == TypeKind.Class && _isSubtypeOf(type, target)) {
      result.Add(TypeNameHelper.GetFullyQualifiedName(type));
    }
    // Recurse into nested types (e.g., events nested inside static contract classes)
    foreach (var nested in type.GetTypeMembers()) {
      _checkTypeAndNestedTypes(nested, target, result);
    }
  }

  /// <summary>
  /// Checks if <paramref name="type"/> implements or inherits from <paramref name="target"/>.
  /// </summary>
  private static bool _isSubtypeOf(INamedTypeSymbol type, INamedTypeSymbol target) {
    if (target.TypeKind == TypeKind.Interface) {
      return type.AllInterfaces.Contains(target, SymbolEqualityComparer.Default);
    }

    var current = type.BaseType;
    while (current is not null) {
      if (SymbolEqualityComparer.Default.Equals(current, target)) {
        return true;
      }
      current = current.BaseType;
    }
    return false;
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

    return [.. stages];
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

      var syncInfo = _parseSingleSyncAttribute(attribute);
      if (syncInfo is not null) {
        syncAttributes.Add(syncInfo);
      }
    }

    return syncAttributes.Count > 0 ? [.. syncAttributes] : null;
  }

  private static SyncAttributeInfo? _parseSingleSyncAttribute(AttributeData attribute) {
    if (attribute.ConstructorArguments.Length == 0) {
      return null;
    }

    var perspectiveTypeArg = attribute.ConstructorArguments[0];
    if (perspectiveTypeArg.Value is not INamedTypeSymbol perspectiveTypeSymbol) {
      return null;
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
        eventTypes = [.. eventTypesList];
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

    return new SyncAttributeInfo(
        PerspectiveType: perspectiveType,
        EventTypes: eventTypes,
        TimeoutMs: timeoutMs,
        FireBehavior: fireBehavior
    );
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
      sb.AppendLine("              var syncResult = await syncAwaiter.WaitForStreamAsync(");
      sb.AppendLine($"                typeof({attr.PerspectiveType}),");
      sb.AppendLine("                streamId.Value,");
      sb.AppendLine($"                {eventTypesCode},");
      sb.AppendLine($"                global::System.TimeSpan.FromMilliseconds({timeoutMs}));");

      // Check the result based on FireBehavior
      // 0 = FireOnSuccess (throw on timeout - default)
      // 1 = FireAlways (don't throw, let handler check SyncContext)
      // 2 = FireOnEachEvent (future streaming mode)
      if (attr.FireBehavior == 0) {
        sb.AppendLine("              if (syncResult.Outcome == global::Whizbang.Core.Perspectives.Sync.SyncOutcome.TimedOut) {");
        sb.AppendLine("                throw new global::Whizbang.Core.Perspectives.Sync.PerspectiveSyncTimeoutException(");
        sb.AppendLine($"                  typeof({attr.PerspectiveType}),");
        sb.AppendLine($"                  global::System.TimeSpan.FromMilliseconds({timeoutMs}),");
        sb.AppendLine($"                  $\"Perspective sync timed out waiting for {{typeof({attr.PerspectiveType}).Name}} to process stream {{streamId.Value}} within {timeoutMs}ms.\");");
        sb.AppendLine("              }");
      }
    }

    sb.AppendLine("            }");
    sb.AppendLine("          }");

    return sb.ToString();
  }

  /// <summary>
  /// Extracts the [DefaultRouting] attribute value from a receptor class.
  /// Returns the fully qualified DispatchModes enum value (e.g., "global::Whizbang.Core.Dispatch.DispatchModes.Local")
  /// or null if no [DefaultRouting] attribute is found.
  /// </summary>
  private static string? _extractDefaultRouting(INamedTypeSymbol classSymbol) {
    const string DEFAULT_ROUTING_ATTRIBUTE = "Whizbang.Core.Dispatch.DefaultRoutingAttribute";

    foreach (var attribute in classSymbol.GetAttributes()) {
      if (attribute.AttributeClass?.ToDisplayString() != DEFAULT_ROUTING_ATTRIBUTE) {
        continue;
      }

      if (attribute.ConstructorArguments.Length == 0) {
        continue;
      }

      var modeArg = attribute.ConstructorArguments[0];
      if (modeArg.Value is not int modeValue) {
        continue;
      }

      return _resolveEnumValueName(attribute.AttributeClass, modeValue);
    }

    return null;
  }

  /// <summary>
  /// Resolves an enum constant value back to its fully qualified member name.
  /// Inspects the first constructor parameter type of the attribute class to find the enum.
  /// </summary>
  private static string? _resolveEnumValueName(INamedTypeSymbol attributeClass, int modeValue) {
    var modeType = attributeClass.GetMembers().OfType<IMethodSymbol>()
        .FirstOrDefault(m => m.MethodKind == MethodKind.Constructor)
        ?.Parameters.FirstOrDefault()?.Type;

    if (modeType is not INamedTypeSymbol enumType) {
      return null;
    }

    var enumMember = enumType.GetMembers().OfType<IFieldSymbol>()
        .FirstOrDefault(f => f.ConstantValue is int val && val == modeValue);

    if (enumMember is null) {
      return null;
    }

    return $"{enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{enumMember.Name}";
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
  /// Checks if a class symbol has the [FireDuringReplay] attribute.
  /// Returns true if the attribute is present, false otherwise.
  /// Used to generate FireDuringReplay metadata on ReceptorInfo so the invoker can
  /// suppress non-opted-in receptors during replay and rebuild operations.
  /// </summary>
  private static bool _hasFireDuringReplayAttribute(INamedTypeSymbol classSymbol) {
    const string FIRE_DURING_REPLAY_ATTRIBUTE = "Whizbang.Core.Messaging.FireDuringReplayAttribute";

    foreach (var attribute in classSymbol.GetAttributes()) {
      if (attribute.AttributeClass?.ToDisplayString() == FIRE_DURING_REPLAY_ATTRIBUTE) {
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
      ? typeName[..^1]
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
    string[] collectionPrefixes = [
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
    ];

    var matchedPrefix = collectionPrefixes
        .FirstOrDefault(prefix => typeName.StartsWith(prefix, StringComparison.Ordinal) && typeName.EndsWith(">", StringComparison.Ordinal));

    // Extract inner type: remove prefix and trailing >
    return matchedPrefix is not null
        ? typeName.Substring(matchedPrefix.Length, typeName.Length - matchedPrefix.Length - 1)
        : null;
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
  /// <docs>fundamentals/dispatcher/dispatcher#auto-cascade-to-outbox</docs>
  /// <tests>Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithEventReturningReceptor_GeneratesCascadeToOutboxAsync</tests>
  private static HashSet<string> _extractUniqueEventTypes(ImmutableArray<ReceptorInfo> receptors) {
    var eventTypes = new HashSet<string>(StringComparer.Ordinal);

    // S3267: Loop has side effects (mutating eventTypes set via helper method) — LINQ not appropriate
#pragma warning disable S3267
    foreach (var receptor in receptors) {
      // Skip void receptors (no response type to cascade)
      if (receptor.IsVoid || string.IsNullOrEmpty(receptor.ResponseType)) {
        continue;
      }

      _extractEventTypesFromResponseType(receptor.ResponseType!, eventTypes);
    }
#pragma warning restore S3267

    return eventTypes;
  }

  /// <summary>
  /// Extracts event types from a single response type string and adds them to the set.
  /// Handles tuples, arrays, collections, and simple types.
  /// </summary>
  private static void _extractEventTypesFromResponseType(string responseType, HashSet<string> eventTypes) {
    // Handle tuple types: (Type1, Type2, ...) - extract all elements
    if (responseType.StartsWith("(", StringComparison.Ordinal) && responseType.EndsWith(")", StringComparison.Ordinal)) {
      _extractEventTypesFromTuple(responseType, eventTypes);
      return;
    }

    // Handle array types: Type[] - extract element type
    if (responseType.EndsWith("[]", StringComparison.Ordinal)) {
      var elementType = responseType[..^2];
      _addUnwrappedEventType(elementType, eventTypes);
      return;
    }

    // Check if it's a generic collection like List<T> (not in a tuple)
    var collectionElementType = _extractCollectionElementType(responseType);
    if (collectionElementType is not null) {
      _addUnwrappedEventType(collectionElementType, eventTypes);
      return;
    }

    // Simple type - unwrap Routed<T> if present
    _addUnwrappedEventType(responseType, eventTypes);
  }

  /// <summary>
  /// Extracts event types from all elements of a tuple response type.
  /// </summary>
  private static void _extractEventTypesFromTuple(string tupleType, HashSet<string> eventTypes) {
    var tupleElements = _extractTupleElements(tupleType);
    foreach (var element in tupleElements) {
      var unwrappedElement = _unwrapRoutedTypeString(element);
      if (unwrappedElement is null) {
        continue;  // Skip RoutedNone
      }

      _addElementTypeFromUnwrapped(unwrappedElement, eventTypes);
    }
  }

  /// <summary>
  /// Adds an event type from an unwrapped (non-Routed) type string, handling arrays and collections.
  /// </summary>
  private static void _addElementTypeFromUnwrapped(string unwrappedType, HashSet<string> eventTypes) {
    if (unwrappedType.EndsWith("[]", StringComparison.Ordinal)) {
      var elementType = unwrappedType[..^2];
      eventTypes.Add(_stripNullableAnnotation(elementType));
      return;
    }

    var collectionElementType = _extractCollectionElementType(unwrappedType);
    if (collectionElementType is not null) {
      eventTypes.Add(_stripNullableAnnotation(collectionElementType));
      return;
    }

    eventTypes.Add(_stripNullableAnnotation(unwrappedType));
  }

  /// <summary>
  /// Unwraps a Routed wrapper and adds the resulting type to the event types set.
  /// </summary>
  private static void _addUnwrappedEventType(string typeName, HashSet<string> eventTypes) {
    var unwrappedType = _unwrapRoutedTypeString(typeName);
    if (unwrappedType is not null) {
      eventTypes.Add(_stripNullableAnnotation(unwrappedType));
    }
  }

  /// <summary>
  /// Extracts individual type names from a tuple type string.
  /// Handles nested tuples by tracking parenthesis depth.
  /// </summary>
  /// <param name="tupleType">Tuple type string like "(Type1, Type2)" or "(Type1, (Type2, Type3))"</param>
  /// <returns>List of extracted type names.</returns>
  /// <docs>fundamentals/dispatcher/dispatcher#auto-cascade-to-outbox</docs>
  /// <tests>Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithTupleResponse_ExtractsEventsForCascadeAsync</tests>
  private static List<string> _extractTupleElements(string tupleType) {
    var elements = new List<string>();

    // Remove outer parentheses
    var inner = tupleType[1..^1];

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
        _addTupleElement(elements, current.ToString().Trim());
        current.Clear();
      } else {
        current.Append(c);
      }
    }

    // Don't forget the last element
    _addTupleElement(elements, current.ToString().Trim());

    return elements;
  }

  /// <summary>
  /// Adds a parsed tuple element to the list. Recursively extracts nested tuples.
  /// </summary>
  private static void _addTupleElement(List<string> elements, string element) {
    if (string.IsNullOrEmpty(element)) {
      return;
    }

    if (element.StartsWith("(", StringComparison.Ordinal)) {
      elements.AddRange(_extractTupleElements(element));
    } else {
      elements.Add(element);
    }
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
    _reportMultipleRpcHandlerConflicts(context, receptors);

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
  /// Reports WHIZ080 errors for message types with multiple async RPC (non-void) handlers.
  /// Multiple handlers are allowed for void receptors (event-style dispatch) and sync receptors.
  /// </summary>
  private static void _reportMultipleRpcHandlerConflicts(
      SourceProductionContext context,
      ImmutableArray<ReceptorInfo> receptors) {
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
      var generatedCode = _generateRegistrationCode(
          receptor, registrationSnippet, voidRegistrationSnippet,
          syncRegistrationSnippet, voidSyncRegistrationSnippet);

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
  /// Generates the registration code for a single receptor using the appropriate snippet.
  /// Selects between async/sync and void/response variants.
  /// </summary>
  private static string _generateRegistrationCode(
      ReceptorInfo receptor,
      string registrationSnippet,
      string voidRegistrationSnippet,
      string syncRegistrationSnippet,
      string voidSyncRegistrationSnippet) {
    if (receptor.IsSync) {
      var interfaceName = StandardInterfaceNames.I_SYNC_RECEPTOR;
      var snippet = receptor.IsVoid ? voidSyncRegistrationSnippet : syncRegistrationSnippet;
      var result = snippet
          .Replace(PLACEHOLDER_SYNC_RECEPTOR_INTERFACE, interfaceName)
          .Replace(PLACEHOLDER_MESSAGE_TYPE, receptor.MessageType)
          .Replace(PLACEHOLDER_RECEPTOR_CLASS, receptor.ClassName);
      return receptor.IsVoid ? result : result.Replace(PLACEHOLDER_RESPONSE_TYPE, receptor.ResponseType!);
    } else {
      var interfaceName = StandardInterfaceNames.I_RECEPTOR;
      var snippet = receptor.IsVoid ? voidRegistrationSnippet : registrationSnippet;
      var result = snippet
          .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, interfaceName)
          .Replace(PLACEHOLDER_MESSAGE_TYPE, receptor.MessageType)
          .Replace(PLACEHOLDER_RECEPTOR_CLASS, receptor.ClassName);
      return receptor.IsVoid ? result : result.Replace(PLACEHOLDER_RESPONSE_TYPE, receptor.ResponseType!);
    }
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

      sendRouting.AppendLine(TemplateUtilities.IndentCode(generatedCode, INDENT_6));
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

      voidSendRouting.AppendLine(TemplateUtilities.IndentCode(generatedCode, INDENT_6));
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

      publishRouting.AppendLine(TemplateUtilities.IndentCode(generatedCode, INDENT_6));
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

      untypedPublishRouting.AppendLine(TemplateUtilities.IndentCode(generatedCode, INDENT_6));
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

      syncSendRouting.AppendLine(TemplateUtilities.IndentCode(generatedCode, INDENT_6));
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

      voidSyncSendRouting.AppendLine(TemplateUtilities.IndentCode(generatedCode, INDENT_6));
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

      anySendRouting.AppendLine(TemplateUtilities.IndentCode(generatedCode, INDENT_6));
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
        receptorDefaultRouting.AppendLine(INDENT_6_CLOSE_BRACE);
        receptorDefaultRouting.AppendLine();
      }
    }

    // Separate concrete types from interface types
    // Concrete types use exact typeof() matching; interface types use 'is' pattern matching
    var concreteTypes = eventTypes.Where(t => !_isWhizbangInterface(t)).ToList();
    var interfaceTypes = eventTypes.Where(t => _isWhizbangInterface(t)).ToList();

    // Generate outbox cascade type-switch (for auto-cascading events to outbox)
    var outboxCascade = _generateCascadeTypeSwitch(concreteTypes, interfaceTypes, eventStoreOnly: false);

    // Generate event store only cascade type-switch (for storing events without transport)
    var eventStoreOnlyCascade = _generateCascadeTypeSwitch(concreteTypes, interfaceTypes, eventStoreOnly: true);

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

    // Load all required snippets
    var snippets = _loadRegistrySnippets();

    // Build routing entries with polymorphic expansion
    var routingEntries = _buildRoutingEntries(compilation, receptors);

    // Group by (routingMessageType, stage) and generate routing code
    var routingCode = _generateRoutingCode(routingEntries, snippets);

    // Replace template markers
    var result = template;
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(ReceptorDiscoveryGenerator).Assembly, result);
    result = TemplateUtilities.ReplaceRegion(result, REGION_NAMESPACE, $"namespace {namespaceName};");
    result = result.Replace(PLACEHOLDER_RECEPTOR_COUNT, receptors.Length.ToString(CultureInfo.InvariantCulture));
    result = TemplateUtilities.ReplaceRegion(result, "RECEPTOR_ROUTING", routingCode);

    return result;
  }

  /// <summary>
  /// Generates a cascade type-switch for outbox or event-store-only publishing.
  /// Concrete types use exact typeof() matching; interface types use 'is' pattern matching.
  /// </summary>
  /// <param name="concreteTypes">Non-interface event types for exact type matching.</param>
  /// <param name="interfaceTypes">Whizbang interface types (IEvent, etc.) for pattern matching.</param>
  /// <param name="eventStoreOnly">If true, generates eventStoreOnly: true parameter.</param>
  /// <returns>StringBuilder with the generated cascade code.</returns>
  private static StringBuilder _generateCascadeTypeSwitch(
      List<string> concreteTypes,
      List<string> interfaceTypes,
      bool eventStoreOnly) {
    var sb = new StringBuilder();
    var eventStoreOnlyParam = eventStoreOnly ? ", eventStoreOnly: true" : "";

    // Generate concrete type cascades (exact type matching)
    foreach (var eventType in concreteTypes) {
      sb.AppendLine($"      if (messageType == typeof({eventType})) {{");
      // CRITICAL: Use passed eventId for sync tracking consistency, or generate new if not provided
      // This ensures the same ID is used for tracking (singleton tracker) AND storage
      sb.AppendLine(MESSAGE_ID_FROM_EVENT_ID);
      sb.AppendLine($"        return PublishToOutboxAsync(({eventType})message, messageType, messageId, sourceEnvelope{eventStoreOnlyParam});");
      sb.AppendLine(INDENT_6_CLOSE_BRACE);
      sb.AppendLine();
    }

    // Generate interface type cascades (pattern matching for any implementing type)
    // Uses PublishToOutboxDynamicAsync which serializes using the runtime type, not the interface type
    foreach (var eventType in interfaceTypes) {
      sb.AppendLine($"      if (message is {eventType}) {{");
      // CRITICAL: Use passed eventId for sync tracking consistency, or generate new if not provided
      sb.AppendLine(MESSAGE_ID_FROM_EVENT_ID);
      sb.AppendLine($"        return PublishToOutboxDynamicAsync(message, messageType, messageId, sourceEnvelope{eventStoreOnlyParam});");
      sb.AppendLine(INDENT_6_CLOSE_BRACE);
      sb.AppendLine();
    }

    return sb;
  }

  /// <summary>
  /// Holds the four snippet variants used for receptor registry code generation.
  /// </summary>
  private sealed record RegistrySnippets(
      string Response,
      string Void,
      string TracedResponse,
      string TracedVoid);

  /// <summary>
  /// Loads all required snippet templates for receptor registry routing.
  /// </summary>
  private static RegistrySnippets _loadRegistrySnippets() {
    var asm = typeof(ReceptorDiscoveryGenerator).Assembly;
    return new RegistrySnippets(
        Response: TemplateUtilities.ExtractSnippet(asm, TEMPLATE_SNIPPET_FILE, "RECEPTOR_REGISTRY_ROUTING_SNIPPET"),
        Void: TemplateUtilities.ExtractSnippet(asm, TEMPLATE_SNIPPET_FILE, "RECEPTOR_REGISTRY_VOID_ROUTING_SNIPPET"),
        TracedResponse: TemplateUtilities.ExtractSnippet(asm, TEMPLATE_SNIPPET_FILE, "RECEPTOR_REGISTRY_TRACED_ROUTING_SNIPPET"),
        TracedVoid: TemplateUtilities.ExtractSnippet(asm, TEMPLATE_SNIPPET_FILE, "RECEPTOR_REGISTRY_TRACED_VOID_ROUTING_SNIPPET"));
  }

  /// <summary>
  /// Builds routing entries from all receptors, expanding polymorphic types to concrete subtypes.
  /// </summary>
  private static System.Collections.Generic.List<(string RoutingMessageType, ReceptorInfo Receptor, string Stage)> _buildRoutingEntries(
      Compilation compilation,
      ImmutableArray<ReceptorInfo> receptors) {
    var defaultStages = new[] {
      "global::Whizbang.Core.Messaging.LifecycleStage.LocalImmediateInline",
      "global::Whizbang.Core.Messaging.LifecycleStage.PreOutboxInline",
      "global::Whizbang.Core.Messaging.LifecycleStage.PostInboxInline"
    };

    var routingEntries = new System.Collections.Generic.List<(string RoutingMessageType, ReceptorInfo Receptor, string Stage)>();
    foreach (var receptor in receptors) {
      var stages = receptor.HasDefaultStage ? defaultStages : receptor.LifecycleStages;
      foreach (var stage in stages) {
        routingEntries.Add((receptor.MessageType, receptor, stage));
      }
    }

    // Expand polymorphic receptors: for each concrete subtype, add a routing entry
    foreach (var entry in routingEntries.ToList()) {
      if (entry.Receptor.IsPolymorphicMessageType) {
        var concreteTypes = _findConcreteSubtypes(compilation, entry.Receptor.MessageType);
        foreach (var concreteType in concreteTypes) {
          routingEntries.Add((concreteType, entry.Receptor, entry.Stage));
        }
      }
    }

    return routingEntries;
  }

  /// <summary>
  /// Generates routing code for all grouped (messageType, stage) combinations.
  /// </summary>
  private static string _generateRoutingCode(
      System.Collections.Generic.List<(string RoutingMessageType, ReceptorInfo Receptor, string Stage)> routingEntries,
      RegistrySnippets snippets) {
    var groupedRoutingPairs = routingEntries
        .GroupBy(e => (e.RoutingMessageType, e.Stage))
        .ToList();

    var routingCode = new StringBuilder();
    foreach (var group in groupedRoutingPairs) {
      var (messageType, stage) = group.Key;
      var receptorsInGroup = group.Select(g => g.Receptor).ToList();
      var handlerCount = receptorsInGroup.Count;

      routingCode.AppendLine($"    if (messageType == typeof({messageType}) && stage == {stage}) {{");
      routingCode.AppendLine("      compileTimeEntries = new global::Whizbang.Core.Messaging.ReceptorInfo[] {");

      for (int i = 0; i < receptorsInGroup.Count; i++) {
        var receptor = receptorsInGroup[i];
        var syncAttributesCode = _generateSyncAttributesCode(receptor.SyncAttributes);

        var receptorEntry = _generateReceptorInfoEntry(
            receptor, syncAttributesCode, handlerCount,
            snippets.Response, snippets.Void, snippets.TracedResponse, snippets.TracedVoid);

        if (i < receptorsInGroup.Count - 1) {
          receptorEntry = receptorEntry.TrimEnd() + ",";
        }

        routingCode.AppendLine(TemplateUtilities.IndentCode(receptorEntry, "        "));
      }

      routingCode.AppendLine("      };");
      routingCode.AppendLine("    }");
    }

    return routingCode.ToString();
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

    var snippet = _selectSnippet(receptor, responseSnippet, voidSnippet, tracedResponseSnippet, tracedVoidSnippet);

    var entryTemplate = _extractReceptorInfoFromSnippet(snippet);
    if (entryTemplate is null) {
      return _generateReceptorInfoEntryManually(receptor, syncAttributesCode);
    }

    return _applyReceptorReplacements(entryTemplate, receptor, syncAttributesCode, handlerCount);
  }

  /// <summary>
  /// Selects the appropriate snippet based on receptor trace and void attributes.
  /// </summary>
  private static string _selectSnippet(
      ReceptorInfo receptor,
      string responseSnippet,
      string voidSnippet,
      string tracedResponseSnippet,
      string tracedVoidSnippet) {
    if (receptor.HasTraceAttribute) {
      return receptor.IsVoid ? tracedVoidSnippet : tracedResponseSnippet;
    }
    return receptor.IsVoid ? voidSnippet : responseSnippet;
  }

  /// <summary>
  /// Extracts the ReceptorInfo constructor call from a snippet by matching parentheses.
  /// Returns null if the expected pattern is not found.
  /// </summary>
  private static string? _extractReceptorInfoFromSnippet(string snippet) {
    const string marker = "new global::Whizbang.Core.Messaging.ReceptorInfo(";
    var entryStart = snippet.IndexOf(marker, StringComparison.Ordinal);
    if (entryStart < 0) {
      return null;
    }

    int parenDepth = 0;
    for (int i = entryStart; i < snippet.Length; i++) {
      if (snippet[i] == '(') {
        parenDepth++;
      } else if (snippet[i] == ')') {
        parenDepth--;
        if (parenDepth == 0) {
          return snippet[entryStart..(i + 1)];
        }
      }
    }

    return null;
  }

  /// <summary>
  /// Applies placeholder replacements to a ReceptorInfo entry template.
  /// </summary>
  private static string _applyReceptorReplacements(
      string entryTemplate,
      ReceptorInfo receptor,
      string syncAttributesCode,
      int handlerCount) {
    var result = entryTemplate
        .Replace(PLACEHOLDER_RECEPTOR_INTERFACE, StandardInterfaceNames.I_RECEPTOR)
        .Replace(PLACEHOLDER_MESSAGE_TYPE, receptor.MessageType)
        .Replace(PLACEHOLDER_RECEPTOR_CLASS, receptor.ClassName)
        .Replace(PLACEHOLDER_SYNC_ATTRIBUTES, syncAttributesCode)
        .Replace(PLACEHOLDER_FIRE_DURING_REPLAY, receptor.HasFireDuringReplayAttribute ? "true" : "false");

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
      string syncAttributesCode) {

    var sb = new StringBuilder();
    sb.AppendLine("new global::Whizbang.Core.Messaging.ReceptorInfo(");
    sb.AppendLine($"  MessageType: typeof({receptor.MessageType}),");
    sb.AppendLine($"  ReceptorId: \"{receptor.ClassName}\",");
    sb.AppendLine("  InvokeAsync: async (sp, msg, envelope, callerInfo, ct) => {");

    if (receptor.IsVoid) {
      sb.AppendLine($"    var receptor = sp.GetRequiredService<{StandardInterfaceNames.I_RECEPTOR}<{receptor.MessageType}>>();");
      sb.AppendLine($"    await receptor.HandleAsync(({receptor.MessageType})msg, ct);");
      sb.AppendLine("    return null;");
    } else {
      sb.AppendLine($"    var receptor = sp.GetRequiredService<{StandardInterfaceNames.I_RECEPTOR}<{receptor.MessageType}, {receptor.ResponseType}>>();");
      sb.AppendLine($"    var result = await receptor.HandleAsync(({receptor.MessageType})msg, ct);");
      sb.AppendLine("    if ((object)result is global::Whizbang.Core.Dispatch.IRouted routedResult) {");
      sb.AppendLine("      return routedResult.Value;");
      sb.AppendLine("    }");
      sb.AppendLine("    return result;");
    }

    sb.AppendLine("  },");
    sb.AppendLine($"  SyncAttributes: {syncAttributesCode},");
    sb.AppendLine($"  FireDuringReplay: {(receptor.HasFireDuringReplayAttribute ? "true" : "false")}");
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
