using Microsoft.CodeAnalysis;

namespace Whizbang.Generators;

/// <summary>
/// Diagnostic descriptors for the Whizbang source generator.
/// </summary>
internal static class DiagnosticDescriptors {
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
  /// WHIZ002: Warning - No receptors or perspectives found in the compilation.
  /// Only shows if BOTH IReceptor AND IPerspectiveOf are absent.
  /// Example: BFF with 5 IPerspectiveOf implementations but no IReceptor should NOT warn.
  /// </summary>
  public static readonly DiagnosticDescriptor NoReceptorsFound = new(
      id: "WHIZ002",
      title: "No Message Handlers Found",
      messageFormat: "No IReceptor or IPerspectiveOf implementations were found in the compilation",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "The source generator did not find any classes implementing IReceptor<TMessage, TResponse> or IPerspectiveOf<TEvent>."
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
  /// WHIZ004: Info - Aggregate ID property discovered.
  /// </summary>
  public static readonly DiagnosticDescriptor AggregateIdPropertyDiscovered = new(
      id: "WHIZ004",
      title: "Aggregate ID Property Discovered",
      messageFormat: "Found [AggregateId] on {0}.{1}",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "An aggregate ID property was discovered and will be accessible via PolicyContext."
  );

  /// <summary>
  /// WHIZ005: Error - [AggregateId] must be on Guid property.
  /// </summary>
  public static readonly DiagnosticDescriptor AggregateIdMustBeGuid = new(
      id: "WHIZ005",
      title: "Aggregate ID Must Be Guid",
      messageFormat: "[AggregateId] on {0}.{1} must be of type Guid or Guid?",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "The [AggregateId] attribute can only be applied to properties of type Guid or Guid?."
  );

  /// <summary>
  /// WHIZ006: Warning - Multiple [AggregateId] attributes on same type.
  /// </summary>
  public static readonly DiagnosticDescriptor MultipleAggregateIdAttributes = new(
      id: "WHIZ006",
      title: "Multiple Aggregate ID Attributes",
      messageFormat: "Type {0} has multiple [AggregateId] attributes. Only the first property '{1}' will be used.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "A message type should only have one property marked with [AggregateId]. Additional attributes are ignored."
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
  /// WHIZ009: Warning - IEvent implementation missing [StreamKey] attribute.
  /// </summary>
  public static readonly DiagnosticDescriptor MissingStreamKeyAttribute = new(
      id: "WHIZ009",
      title: "Missing StreamKey Attribute",
      messageFormat: "Event type '{0}' implements IEvent but has no property or parameter marked with [StreamKey]. Stream key resolution will fail at runtime.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "All IEvent implementations should have exactly one property or constructor parameter marked with [StreamKey] to identify the event stream."
  );

  /// <summary>
  /// WHIZ010: Info - StreamKey property discovered during source generation.
  /// </summary>
  public static readonly DiagnosticDescriptor StreamKeyDiscovered = new(
      id: "WHIZ010",
      title: "StreamKey Discovered",
      messageFormat: "Found [StreamKey] on {0}.{1}",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "A stream key property was discovered and an extractor method will be generated."
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
}
