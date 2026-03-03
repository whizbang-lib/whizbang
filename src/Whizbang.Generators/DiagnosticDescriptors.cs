using Microsoft.CodeAnalysis;

namespace Whizbang.Generators;

/// <summary>
/// Diagnostic descriptors for the Whizbang source generator.
/// </summary>
/// <tests>tests/Whizbang.Generators.Tests/DiagnosticDescriptorsTests.cs</tests>
public static class DiagnosticDescriptors {
  private const string CATEGORY = "Whizbang.SourceGeneration";

  /// <summary>
  /// WHIZ001: Info - Receptor discovered during source generation.
  /// </summary>
  public static readonly DiagnosticDescriptor ReceptorDiscovered = new(
      id: "WHIZ001",
      title: "Receptor Discovered",
      messageFormat: "Found receptor '{0}' handling {1} → {2}",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "A receptor implementation was discovered and will be registered."
  );

  /// <summary>
  /// WHIZ002: Info - No receptors or perspectives found in the compilation.
  /// Only shows if BOTH IReceptor AND IPerspectiveFor are absent.
  /// Example: BFF with 5 IPerspectiveFor implementations but no IReceptor should NOT warn.
  /// </summary>
  public static readonly DiagnosticDescriptor NoReceptorsFound = new(
      id: "WHIZ002",
      title: "No Message Handlers Found",
      messageFormat: "No IReceptor or IPerspectiveFor implementations were found in the compilation",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "The source generator did not find any classes implementing IReceptor<TMessage, TResponse> or IPerspectiveFor<TEvent>."
  );

  /// <summary>
  /// WHIZ003: Error - Invalid receptor implementation detected.
  /// </summary>
  public static readonly DiagnosticDescriptor InvalidReceptor = new(
      id: "WHIZ003",
      title: "Invalid Receptor Implementation",
      messageFormat: "Invalid receptor '{0}': {1}",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "The receptor implementation has errors and cannot be registered."
  );

  /// <summary>
  /// WHIZ004: Info - Stream ID property discovered on command.
  /// </summary>
  public static readonly DiagnosticDescriptor CommandStreamIdDiscovered = new(
      id: "WHIZ004",
      title: "Command Stream ID Discovered",
      messageFormat: "Found [StreamId] on command {0}.{1}",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "A stream ID property was discovered on a command and will be accessible via PolicyContext."
  );

  /// <summary>
  /// WHIZ005: Error - [StreamId] must be on Guid property or type with .Value property.
  /// </summary>
  public static readonly DiagnosticDescriptor StreamIdMustBeGuid = new(
      id: "WHIZ005",
      title: "Stream ID Must Be Guid",
      messageFormat: "[StreamId] on {0}.{1} must be of type Guid, Guid?, or a type with a .Value property returning Guid",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "The [StreamId] attribute can only be applied to properties of type Guid, Guid?, or types with a .Value property that returns Guid (such as WhizbangId types)."
  );

  /// <summary>
  /// WHIZ006: Warning - Multiple [StreamId] attributes on same type.
  /// </summary>
  public static readonly DiagnosticDescriptor MultipleStreamIdAttributes = new(
      id: "WHIZ006",
      title: "Multiple Stream ID Attributes",
      messageFormat: "Type {0} has multiple [StreamId] attributes. Only the first property '{1}' will be used.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "A message type should only have one property marked with [StreamId]. Additional attributes are ignored."
  );

  /// <summary>
  /// WHIZ007: Info - Perspective discovered during source generation.
  /// </summary>
  public static readonly DiagnosticDescriptor PerspectiveDiscovered = new(
      id: "WHIZ007",
      title: "Perspective Discovered",
      messageFormat: "Found perspective '{0}' listening to {1}",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "A perspective implementation was discovered and will be registered."
  );

  /// <summary>
  /// WHIZ008: Warning - Perspective model may exceed JSONB size thresholds.
  /// </summary>
  public static readonly DiagnosticDescriptor PerspectiveSizeWarning = new(
      id: "WHIZ008",
      title: "Perspective Model Size Warning",
      messageFormat: "Perspective '{0}' model estimated at ~{1} bytes. Consider splitting if approaching 2KB (compression) or 7KB (externalization) thresholds.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "Static analysis estimates the perspective model may be large. PostgreSQL JSONB columns have performance cliffs at 2KB (compression) and 7KB (externalization)."
  );

  /// <summary>
  /// WHIZ009: Warning - IEvent or ICommand implementation missing [StreamId] attribute.
  /// </summary>
  public static readonly DiagnosticDescriptor MissingStreamIdAttribute = new(
      id: "WHIZ009",
      title: "Missing StreamId Attribute",
      messageFormat: "Type '{0}' implements {1} but has no property or parameter marked with [StreamId]. Stream ID resolution will fail at runtime.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "All IEvent and ICommand implementations should have exactly one property or constructor parameter marked with [StreamId] to identify the stream."
  );

  /// <summary>
  /// WHIZ010: Info - StreamId property discovered during source generation.
  /// </summary>
  public static readonly DiagnosticDescriptor StreamIdDiscovered = new(
      id: "WHIZ010",
      title: "StreamId Discovered",
      messageFormat: "Found [StreamId] on {0}.{1}",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "A stream ID property was discovered and an extractor method will be generated."
  );

  /// <summary>
  /// WHIZ011: Info - Message type discovered for JSON serialization.
  /// </summary>
  public static readonly DiagnosticDescriptor JsonSerializableTypeDiscovered = new(
      id: "WHIZ011",
      title: "JSON Serializable Type Discovered",
      messageFormat: "Found {1} type '{0}' - adding to JsonSerializerContext for AOT-compatible serialization",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "A message type (ICommand or IEvent) was discovered and will be included in the generated JsonSerializerContext."
  );

  /// <summary>
  /// WHIZ012: Info - Perspective invoker routing generated for event type.
  /// </summary>
  public static readonly DiagnosticDescriptor PerspectiveInvokerGenerated = new(
      id: "WHIZ012",
      title: "Perspective Invoker Routing Generated",
      messageFormat: "Generated perspective invoker routing for '{0}' → {1}",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "AOT-compatible routing code was generated to invoke perspectives when events are queued."
  );

  /// <summary>
  /// WHIZ020: Info - WhizbangId discovered during source generation.
  /// </summary>
  public static readonly DiagnosticDescriptor WhizbangIdDiscovered = new(
      id: "WHIZ020",
      title: "WhizbangId Discovered",
      messageFormat: "Found [WhizbangId] on {0} in namespace {1}",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "A strongly-typed ID was discovered and will be generated with UUIDv7 support."
  );

  /// <summary>
  /// WHIZ021: Warning - [WhizbangId] on non-partial struct.
  /// </summary>
  public static readonly DiagnosticDescriptor WhizbangIdMustBePartial = new(
      id: "WHIZ021",
      title: "WhizbangId Must Be Partial",
      messageFormat: "[WhizbangId] on {0} requires the struct to be declared as 'partial'",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "The [WhizbangId] attribute can only be used on partial structs to allow source generation."
  );

  /// <summary>
  /// WHIZ022: Info - Topic filter discovered for command.
  /// </summary>
  public static readonly DiagnosticDescriptor TopicFilterDiscovered = new(
      id: "WHIZ022",
      title: "Topic Filter Discovered",
      messageFormat: "Found topic filter '{1}' on command '{0}'",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "A topic filter was discovered on a command and will be included in the generated registry."
  );

  /// <summary>
  /// WHIZ023: Warning - Enum filter has no Description attribute, using symbol name.
  /// </summary>
  public static readonly DiagnosticDescriptor EnumFilterNoDescription = new(
      id: "WHIZ023",
      title: "Enum Filter No Description",
      messageFormat: "Enum value '{0}.{1}' has no [Description] attribute. Using enum symbol name '{1}' as filter.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "An enum-based topic filter has no Description attribute, so the enum symbol name will be used as the filter string."
  );

  /// <summary>
  /// WHIZ024: Warning - Duplicate WhizbangId type name in different namespace.
  /// </summary>
  public static readonly DiagnosticDescriptor WhizbangIdDuplicateName = new(
      id: "WHIZ024",
      title: "Duplicate WhizbangId Type Name",
      messageFormat: "WhizbangId type '{0}' exists in multiple namespaces ({1} and {2}). This may cause confusion. Set SuppressDuplicateWarning = true to suppress.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "Multiple WhizbangId types with the same name exist in different namespaces."
  );

  /// <summary>
  /// WHIZ025: Warning - TopicFilter attribute on non-ICommand type.
  /// </summary>
  public static readonly DiagnosticDescriptor TopicFilterOnNonCommand = new(
      id: "WHIZ025",
      title: "TopicFilter On Non-Command",
      messageFormat: "[TopicFilter] on type '{0}' which does not implement ICommand. Filter will be ignored.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "The [TopicFilter] attribute can only be used on types implementing ICommand."
  );

  /// <summary>
  /// WHIZ026: Info - No topic filters found in assembly.
  /// </summary>
  public static readonly DiagnosticDescriptor NoTopicFiltersFound = new(
      id: "WHIZ026",
      title: "No Topic Filters Found",
      messageFormat: "No [TopicFilter] attributes were found in the compilation. TopicFilterRegistry will not be generated.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "The source generator did not find any commands with [TopicFilter] attributes."
  );

  /// <summary>
  /// WHIZ027: Info - Perspective runner generated for perspective with model.
  /// </summary>
  public static readonly DiagnosticDescriptor PerspectiveRunnerGenerated = new(
      id: "WHIZ027",
      title: "Perspective Runner Generated",
      messageFormat: "Generated perspective runner '{1}' for perspective '{0}'",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "A perspective runner implementation was generated for a perspective implementing IPerspectiveModel<TModel>."
  );

  /// <summary>
  /// WHIZ028: Info - Perspective runner registry generated.
  /// </summary>
  public static readonly DiagnosticDescriptor PerspectiveRunnerRegistryGenerated = new(
      id: "WHIZ028",
      title: "Perspective Runner Registry Generated",
      messageFormat: "Generated perspective runner registry with {0} runner(s) for zero-reflection lookup (AOT-compatible)",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "A static registry was generated to enable zero-reflection perspective runner lookup in PerspectiveWorker."
  );

  /// <summary>
  /// WHIZ030: Error - Event type used in perspective is missing [StreamId] attribute.
  /// </summary>
  /// <docs>diagnostics/whiz030</docs>
  public static readonly DiagnosticDescriptor PerspectiveEventMissingStreamId = new(
      id: "WHIZ030",
      title: "Perspective Event Missing StreamId",
      messageFormat: "Event type '{0}' used in perspective '{1}' must have exactly one property marked with [StreamId] attribute",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "Events used in perspectives must have a property marked with [StreamId] to identify the stream/aggregate for ordered processing."
  );

  /// <summary>
  /// WHIZ031: Error - Event type has multiple [StreamId] attributes.
  /// </summary>
  /// <docs>diagnostics/whiz031</docs>
  public static readonly DiagnosticDescriptor PerspectiveEventMultipleStreamIds = new(
      id: "WHIZ031",
      title: "Multiple StreamId Attributes",
      messageFormat: "Event type '{0}' has multiple properties marked with [StreamId]. Only one property can be the stream ID.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "Each event type can only have one property marked with [StreamId] attribute."
  );

  /// <summary>
  /// WHIZ032: Error - Perspective name collision detected.
  /// </summary>
  /// <docs>diagnostics/whiz032</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveRunnerRegistryGeneratorTests.cs:Generator_WithDuplicateNames_EmitsCollisionErrorAsync</tests>
  public static readonly DiagnosticDescriptor PerspectiveNameCollision = new(
      id: "WHIZ032",
      title: "Perspective Name Collision",
      messageFormat: "Multiple perspectives found with name '{0}': {1}. Use unique class names.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "Two or more perspective classes resolve to the same name, which would cause duplicate switch cases in the runner registry."
  );

  /// <summary>
  /// WHIZ033: Warning - Perspective model missing [StreamId] attribute.
  /// </summary>
  /// <docs>diagnostics/whiz033</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveRunnerGeneratorTests.cs:PerspectiveRunnerGenerator_ModelMissingStreamId_EmitsWarningAsync</tests>
  public static readonly DiagnosticDescriptor PerspectiveModelMissingStreamId = new(
      id: "WHIZ033",
      title: "Perspective Model Missing StreamId",
      messageFormat: "Perspective '{0}' will not generate a runner because model '{1}' has no property with [StreamId] attribute",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "Perspectives require their model type to have a property marked with [StreamId] to identify the stream."
  );

  // ========================================
  // Service Registration Diagnostics (WHIZ040-049)
  // ========================================

  /// <summary>
  /// WHIZ040: Info - User service discovered during source generation.
  /// Reports when a user-defined interface extending Whizbang interfaces is registered.
  /// </summary>
  /// <docs>diagnostics/whiz040</docs>
  /// <tests>tests/Whizbang.Generators.Tests/ServiceRegistrationGeneratorTests.cs</tests>
  public static readonly DiagnosticDescriptor UserServiceDiscovered = new(
      id: "WHIZ040",
      title: "User Service Discovered",
      messageFormat: "Registered {0} '{1}' as scoped service implementing '{2}'",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "A user-defined service implementing a Whizbang interface was discovered and will be registered with the DI container."
  );

  /// <summary>
  /// WHIZ041: Info - Abstract class skipped for service registration.
  /// Reports when an abstract class implementing Whizbang interfaces is skipped.
  /// </summary>
  /// <docs>diagnostics/whiz041</docs>
  /// <tests>tests/Whizbang.Generators.Tests/ServiceRegistrationGeneratorTests.cs</tests>
  public static readonly DiagnosticDescriptor AbstractClassSkipped = new(
      id: "WHIZ041",
      title: "Abstract Class Skipped",
      messageFormat: "Abstract class '{0}' implementing '{1}' skipped for service registration",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "An abstract class implementing a Whizbang interface was skipped because abstract classes cannot be instantiated."
  );

  /// <summary>
  /// WHIZ042: Info - No user services found in the compilation.
  /// Reports when no lens or perspective services are discovered.
  /// </summary>
  /// <docs>diagnostics/whiz042</docs>
  /// <tests>tests/Whizbang.Generators.Tests/ServiceRegistrationGeneratorTests.cs</tests>
  public static readonly DiagnosticDescriptor NoUserServicesFound = new(
      id: "WHIZ042",
      title: "No User Services Found",
      messageFormat: "No user-defined lens or perspective services were found for service registration",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "The source generator did not find any user-defined interfaces extending ILensQuery or IPerspectiveFor."
  );

  // ========================================
  // Test Linking Diagnostics (WHIZ050-069)
  // ========================================

  /// <summary>
  /// WHIZ050: Warning - Public API has no associated tests.
  /// </summary>
  public static readonly DiagnosticDescriptor PublicApiMissingTests = new(
      id: "WHIZ050",
      title: "Public API Missing Tests",
      messageFormat: "Public {0} '{1}' has no associated tests. Consider adding test coverage or marking with [ExcludeFromCodeCoverage].",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "A public API member has no associated tests in the code-tests mapping."
  );

  /// <summary>
  /// WHIZ051: Warning - &lt;tests&gt; XML tag references non-existent test.
  /// </summary>
  public static readonly DiagnosticDescriptor InvalidTestReference = new(
      id: "WHIZ051",
      title: "Invalid Test Reference",
      messageFormat: "&lt;tests&gt; tag on '{0}' references test '{1}' which was not found. Verify test file path and method name.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "The &lt;tests&gt; XML documentation tag references a test that does not exist."
  );

  /// <summary>
  /// WHIZ052: Info - Test link discovered between code and test.
  /// </summary>
  public static readonly DiagnosticDescriptor TestLinkDiscovered = new(
      id: "WHIZ052",
      title: "Test Link Discovered",
      messageFormat: "Found test link: {0}.{1} ← {2} (via {3})",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "A code-to-test link was discovered through convention, semantic analysis, or XML tag."
  );

  /// <summary>
  /// WHIZ053: Info - Failed to load code-docs-map.json for VSCode tooling.
  /// </summary>
  public static readonly DiagnosticDescriptor FailedToLoadDocsMap = new(
      id: "WHIZ053",
      title: "Failed to Load Documentation Map",
      messageFormat: "Could not load code-docs-map.json from documentation repository: {0}. Documentation URLs will not be included in message-registry.json.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "The MessageRegistryGenerator could not load the documentation mapping file for VSCode tooling enhancement."
  );

  /// <summary>
  /// WHIZ054: Info - Failed to load code-tests-map.json for VSCode tooling.
  /// </summary>
  public static readonly DiagnosticDescriptor FailedToLoadTestsMap = new(
      id: "WHIZ054",
      title: "Failed to Load Tests Map",
      messageFormat: "Could not load code-tests-map.json from documentation repository: {0}. Test counts will not be included in message-registry.json.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "The MessageRegistryGenerator could not load the test mapping file for VSCode tooling enhancement."
  );

  // ========================================
  // Serialization Validation Diagnostics (WHIZ060-069)
  // ========================================

  /// <summary>
  /// WHIZ060: Error - Property uses non-serializable type 'object'.
  /// </summary>
  /// <docs>diagnostics/whiz060</docs>
  /// <tests>tests/Whizbang.Generators.Tests/SerializablePropertyAnalyzerTests.cs</tests>
  public static readonly DiagnosticDescriptor NonSerializablePropertyObject = new(
      id: "WHIZ060",
      title: "Property uses non-serializable type 'object'",
      messageFormat: "Property '{0}' on '{1}' uses type 'object' which cannot be serialized for AOT. Use a concrete type instead.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "Properties of type 'object' require runtime reflection for serialization, which is not compatible with AOT."
  );

  /// <summary>
  /// WHIZ061: Error - Property uses non-serializable type 'dynamic'.
  /// </summary>
  /// <docs>diagnostics/whiz061</docs>
  /// <tests>tests/Whizbang.Generators.Tests/SerializablePropertyAnalyzerTests.cs</tests>
  public static readonly DiagnosticDescriptor NonSerializablePropertyDynamic = new(
      id: "WHIZ061",
      title: "Property uses non-serializable type 'dynamic'",
      messageFormat: "Property '{0}' on '{1}' uses type 'dynamic' which cannot be serialized for AOT. Use a concrete type instead.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "Properties of type 'dynamic' require runtime reflection for serialization."
  );

  /// <summary>
  /// WHIZ062: Error - Property uses non-serializable interface type.
  /// </summary>
  /// <docs>diagnostics/whiz062</docs>
  /// <tests>tests/Whizbang.Generators.Tests/SerializablePropertyAnalyzerTests.cs</tests>
  public static readonly DiagnosticDescriptor NonSerializablePropertyInterface = new(
      id: "WHIZ062",
      title: "Property uses non-serializable interface type",
      messageFormat: "Property '{0}' on '{1}' uses interface type '{2}' which cannot be serialized for AOT. Use a concrete type or generic collection instead.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "Interface properties (without generic parameters) require runtime type discovery."
  );

  /// <summary>
  /// WHIZ063: Error - Nested type contains non-serializable property.
  /// </summary>
  /// <docs>diagnostics/whiz063</docs>
  /// <tests>tests/Whizbang.Generators.Tests/SerializablePropertyAnalyzerTests.cs</tests>
  public static readonly DiagnosticDescriptor NonSerializableNestedProperty = new(
      id: "WHIZ063",
      title: "Nested type contains non-serializable property",
      messageFormat: "Nested type '{0}' (used by '{1}.{2}') contains non-serializable property '{3}' of type '{4}'",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "Nested types used in messages must also have serializable properties."
  );

  // ========================================
  // Guid Usage Diagnostics (WHIZ055-057)
  // ========================================

  /// <summary>
  /// WHIZ055: Warning - Guid.NewGuid() detected.
  /// Will be upgraded to Error after Phase 5 migration is complete.
  /// </summary>
  /// <docs>diagnostics/whiz055</docs>
  public static readonly DiagnosticDescriptor GuidNewGuidUsage = new(
      id: "WHIZ055",
      title: "Guid.NewGuid() Usage",
      messageFormat: "Use TrackedGuid.NewMedo() or a [WhizbangId] type instead of Guid.NewGuid(). UUIDv4 is not time-ordered.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "Guid.NewGuid() creates UUIDv4 which is not time-ordered. Use TrackedGuid.NewMedo() or a [WhizbangId] type for UUIDv7 with sub-millisecond precision."
  );

  /// <summary>
  /// WHIZ056: Warning - Guid.CreateVersion7() detected.
  /// Will be upgraded to Error after Phase 5 migration is complete.
  /// </summary>
  /// <docs>diagnostics/whiz056</docs>
  public static readonly DiagnosticDescriptor GuidCreateVersion7Usage = new(
      id: "WHIZ056",
      title: "Guid.CreateVersion7() Usage",
      messageFormat: "Use TrackedGuid.NewMedo() for sub-millisecond precision instead of Guid.CreateVersion7()",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "Guid.CreateVersion7() only has millisecond precision. Use TrackedGuid.NewMedo() or a [WhizbangId] type for sub-millisecond precision."
  );

  /// <summary>
  /// WHIZ057: Warning - Raw Guid parameter where IWhizbangId expected.
  /// </summary>
  /// <docs>diagnostics/whiz057</docs>
  public static readonly DiagnosticDescriptor RawGuidWhereIdExpected = new(
      id: "WHIZ057",
      title: "Raw Guid Parameter",
      messageFormat: "Consider using a strongly-typed ID instead of raw Guid for parameter '{0}'. Raw Guid loses metadata about precision and ordering.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "Raw Guid parameters lose metadata about precision and ordering. Consider using IWhizbangId or a strongly-typed ID generated with [WhizbangId]."
  );

  /// <summary>
  /// WHIZ058: Info - Guid generation call intercepted and wrapped with TrackedGuid.
  /// </summary>
  /// <docs>diagnostics/whiz058</docs>
  public static readonly DiagnosticDescriptor GuidCallIntercepted = new(
      id: "WHIZ058",
      title: "Guid Call Intercepted",
      messageFormat: "Intercepted {0} at {1}:{2} - wrapped with TrackedGuid for metadata tracking",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "A Guid generation call was intercepted and wrapped with TrackedGuid to enable metadata tracking. This allows Whizbang to validate time-ordering requirements at runtime."
  );

  /// <summary>
  /// WHIZ059: Info - Guid interception suppressed via attribute or pragma.
  /// </summary>
  /// <docs>diagnostics/whiz059</docs>
  public static readonly DiagnosticDescriptor GuidInterceptionSuppressed = new(
      id: "WHIZ059",
      title: "Guid Interception Suppressed",
      messageFormat: "Interception suppressed for {0} at {1}:{2} via {3}",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "Guid interception was suppressed by a [SuppressGuidInterception] attribute or #pragma warning disable WHIZ058 directive."
  );

  // ========================================
  // RPC Handler Validation Diagnostics (WHIZ080-089)
  // ========================================

  /// <summary>
  /// WHIZ080: Warning - Multiple handlers detected for RPC message type (with return value).
  /// RPC patterns (LocalInvoke with result) require exactly one handler because we can only return one result.
  /// Multiple handlers are allowed for void receptors (event handlers) but not for RPC (command handlers with response).
  /// Note: Disabled by default pending implementation of key-based RPC handler selection.
  /// Future: Handlers can be decorated with [RpcKey] and RPC calls can specify which handler to use.
  /// </summary>
  /// <docs>diagnostics/whiz080</docs>
  public static readonly DiagnosticDescriptor MultipleHandlersForRpcMessage = new(
      id: "WHIZ080",
      title: "Multiple Handlers for RPC Message",
      messageFormat: "Multiple handlers found for '{0}' which returns a response (found: {1}), but RPC requires exactly one handler",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: false,  // Disabled pending key-based RPC handler selection feature
      description: "When using LocalInvoke<TMessage, TResponse>() (RPC pattern), only one handler can be registered because we need to return a single result. For event-style dispatch where multiple handlers should respond, use IReceptor<TMessage> (void receptor) instead."
  );

  // ========================================
  // Physical Field Diagnostics (WHIZ801-809)
  // ========================================

  /// <summary>
  /// WHIZ801: Error - [VectorField] can only be applied to float[] properties.
  /// </summary>
  /// <docs>diagnostics/whiz801</docs>
  public static readonly DiagnosticDescriptor VectorFieldInvalidType = new(
      id: "WHIZ801",
      title: "VectorField Invalid Type",
      messageFormat: "[VectorField] on {0}.{1} requires property type float[] or Single[]",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "The [VectorField] attribute can only be applied to properties of type float[] (System.Single[])."
  );

  /// <summary>
  /// WHIZ802: Error - [VectorField] dimensions must be positive.
  /// </summary>
  /// <docs>diagnostics/whiz802</docs>
  public static readonly DiagnosticDescriptor VectorFieldInvalidDimensions = new(
      id: "WHIZ802",
      title: "VectorField Invalid Dimensions",
      messageFormat: "[VectorField] on {0}.{1} has invalid dimensions {2}. Dimensions must be a positive integer.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "The [VectorField] attribute requires a positive integer for dimensions."
  );

  /// <summary>
  /// WHIZ803: Warning - [PhysicalField] on complex type may not benefit from indexing.
  /// </summary>
  /// <docs>diagnostics/whiz803</docs>
  public static readonly DiagnosticDescriptor PhysicalFieldComplexType = new(
      id: "WHIZ803",
      title: "PhysicalField Complex Type",
      messageFormat: "[PhysicalField] on {0}.{1} with type {2} may not benefit from indexing. Consider using simple types.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "Physical fields work best with simple types (string, int, decimal, bool, Guid). Complex types may not benefit from database indexing."
  );

  /// <summary>
  /// WHIZ805: Warning - Split mode with no [PhysicalField] is equivalent to JsonOnly.
  /// </summary>
  /// <docs>diagnostics/whiz805</docs>
  public static readonly DiagnosticDescriptor SplitModeNoPhysicalFields = new(
      id: "WHIZ805",
      title: "Split Mode No Physical Fields",
      messageFormat: "Perspective '{0}' uses Split mode but has no [PhysicalField] or [VectorField] attributes. This is equivalent to JsonOnly mode.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "Using Split mode without any physical fields is unnecessary. Consider removing [PerspectiveStorage] or adding physical fields."
  );

  /// <summary>
  /// WHIZ807: Info - Model has physical field(s) discovered.
  /// </summary>
  /// <docs>diagnostics/whiz807</docs>
  public static readonly DiagnosticDescriptor PhysicalFieldsDiscovered = new(
      id: "WHIZ807",
      title: "Physical Fields Discovered",
      messageFormat: "Model '{0}' has {1} physical field(s) in {2} mode",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "Physical fields were discovered on a perspective model and will be included as database columns."
  );

  // ========================================
  // Vector Dependency Diagnostics (WHIZ070)
  // ========================================

  /// <summary>
  /// WHIZ070: Error - [VectorField] requires Pgvector.EntityFrameworkCore package.
  /// </summary>
  /// <docs>diagnostics/whiz070</docs>
  /// <tests>tests/Whizbang.Generators.Tests/VectorDependencyAnalyzerTests.cs</tests>
  public static readonly DiagnosticDescriptor VectorFieldMissingPackage = new(
      id: "WHIZ070",
      title: "Missing Pgvector.EntityFrameworkCore Package",
      messageFormat: "Property '{0}' uses [VectorField] but Pgvector.EntityFrameworkCore package is not referenced. Add <PackageReference Include=\"Pgvector.EntityFrameworkCore\" Version=\"0.3.0\" /> to your .csproj file.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "The [VectorField] attribute requires the Pgvector.EntityFrameworkCore package for vector similarity queries."
  );

  // ========================================
  // Polymorphic Serialization Diagnostics (WHIZ071-079)
  // ========================================

  /// <summary>
  /// WHIZ071: Info - Polymorphic base type discovered with derived types.
  /// Reports when a base class or interface is discovered with derived types
  /// that will be registered for polymorphic JSON serialization.
  /// </summary>
  /// <docs>source-generators/polymorphic-serialization</docs>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithPolymorphicBase_ReportsWHIZ071DiagnosticAsync</tests>
  public static readonly DiagnosticDescriptor PolymorphicBaseTypeDiscovered = new(
      id: "WHIZ071",
      title: "Polymorphic Base Type Discovered",
      messageFormat: "Discovered polymorphic base type '{0}' with {1} derived type(s) for automatic JSON serialization",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "A polymorphic base type was discovered through inheritance tracking. All derived types will be registered for JSON serialization."
  );

  // ========================================
  // MessageTag Attribute Parameter Diagnostics (WHIZ090-099)
  // ========================================

  /// <summary>
  /// WHIZ090: Error - Constructor parameter in MessageTagAttribute subclass does not match any property.
  /// Whizbang's source generators extract attribute values using constructor parameter names.
  /// Parameters must match property names (case-insensitive) for values to be extracted correctly.
  /// </summary>
  /// <docs>diagnostics/whiz090</docs>
  /// <tests>tests/Whizbang.Generators.Tests/Analyzers/MessageTagParameterAnalyzerTests.cs</tests>
  public static readonly DiagnosticDescriptor MessageTagParameterMismatch = new(
      id: "WHIZ090",
      title: "MessageTag Parameter Naming",
      messageFormat: "Constructor parameter '{0}' in '{1}' does not match any property. Rename to '{2}' to match property '{3}'.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "Whizbang's source generators extract attribute values using constructor parameter names. Parameters must match property names (case-insensitive) for values to be extracted correctly."
  );
}
