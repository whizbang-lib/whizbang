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
}
