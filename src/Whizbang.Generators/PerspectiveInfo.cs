namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered perspective.
/// This record uses value equality which is critical for incremental generator performance.
/// A perspective class implements IPerspectiveFor&lt;TModel, TEvent1, TEvent2, ...&gt; with all type arguments.
/// </summary>
/// <param name="ClassName">Fully qualified class name implementing IPerspectiveFor (with global:: prefix for code generation)</param>
/// <param name="SimpleName">Simple class name including parent type for nested classes (e.g., "DraftJobStatus.Projection" or "OrderPerspective")</param>
/// <param name="ClrTypeName">CLR format type name for database storage (e.g., "Namespace.Parent+Child" - uses + for nested types)</param>
/// <param name="InterfaceTypeArguments">All type arguments from IPerspectiveFor interface (TModel, TEvent1, TEvent2, ...) as fully qualified names with global:: prefix for code generation</param>
/// <param name="EventTypes">Array of fully qualified event type names with global:: prefix for code generation (extracted from InterfaceTypeArguments for diagnostics)</param>
/// <param name="MessageTypeNames">Array of event type names in database format (TypeName, AssemblyName - no global:: prefix) for message association registration</param>
/// <param name="StreamIdPropertyName">Property name marked with [StreamId] attribute on the model (null if not found)</param>
/// <param name="EventStreamIds">Map of event type name to its StreamId property name</param>
/// <param name="EventValidationErrors">Array of validation errors for event types (event name, error type)</param>
/// <param name="MustExistEventTypes">Array of event type names (fully qualified) whose Apply methods have [MustExist] attribute</param>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveRunnerGeneratorTests.cs</tests>
internal sealed record PerspectiveInfo(
    string ClassName,
    string SimpleName,
    string ClrTypeName,
    string[] InterfaceTypeArguments,
    string[] EventTypes,
    string[] MessageTypeNames,
    string? StreamIdPropertyName = null,
    EventStreamIdInfo[]? EventStreamIds = null,
    EventValidationError[]? EventValidationErrors = null,
    string[]? MustExistEventTypes = null,
    EventReturnTypeInfo[]? EventReturnTypes = null,
    PhysicalFieldInfoCompact[]? PhysicalFields = null
);

/// <summary>
/// Compact physical field info for perspective runner generation.
/// Contains only the data needed for runtime extraction of values.
/// </summary>
/// <param name="PropertyName">Name of the property on the model</param>
/// <param name="ColumnName">Database column name (snake_case)</param>
/// <param name="IsVectorField">True if this is a [VectorField] requiring Pgvector.Vector conversion</param>
internal sealed record PhysicalFieldInfoCompact(
    string PropertyName,
    string ColumnName,
    bool IsVectorField = false
);

/// <summary>
/// Maps an event type to its StreamId property for stream ID extraction.
/// </summary>
/// <param name="EventTypeName">Fully qualified event type name</param>
/// <param name="StreamIdPropertyName">Name of the property marked with [StreamId]</param>
internal sealed record EventStreamIdInfo(
    string EventTypeName,
    string StreamIdPropertyName
);

/// <summary>
/// Represents a validation error for an event type in a perspective.
/// </summary>
/// <param name="EventTypeName">Simple name of the event type with the error</param>
/// <param name="ErrorType">Type of validation error (MissingStreamId or MultipleStreamIds)</param>
internal sealed record EventValidationError(
    string EventTypeName,
    StreamIdErrorType ErrorType
);

/// <summary>
/// Types of StreamId validation errors.
/// </summary>
internal enum StreamIdErrorType {
  MissingStreamId,
  MultipleStreamIds
}

/// <summary>
/// Maps an event type to the return type of its Apply method.
/// Used for generating correct handling code for different return patterns.
/// </summary>
/// <param name="EventTypeName">Fully qualified event type name</param>
/// <param name="ReturnType">Type of return value from Apply method</param>
internal sealed record EventReturnTypeInfo(
    string EventTypeName,
    ApplyReturnType ReturnType
);

/// <summary>
/// Specifies the return type pattern of an Apply method for code generation.
/// </summary>
internal enum ApplyReturnType {
  /// <summary>Returns TModel - standard update pattern</summary>
  Model,

  /// <summary>Returns TModel? - nullable means null = no change</summary>
  NullableModel,

  /// <summary>Returns ModelAction - action only (Delete, Purge)</summary>
  Action,

  /// <summary>Returns (TModel?, ModelAction) - tuple with optional model and action</summary>
  Tuple,

  /// <summary>Returns ApplyResult&lt;TModel&gt; - full flexibility wrapper</summary>
  ApplyResult
}

/// <summary>
/// Result of perspective extraction - either valid info or a warning about missing StreamId.
/// Used by PerspectiveRunnerGenerator to report WHIZ033 diagnostics.
/// </summary>
/// <param name="Info">Valid perspective info (null if warning)</param>
/// <param name="Warning">Warning about missing StreamId on model (null if valid)</param>
internal sealed record PerspectiveOrWarning(
    PerspectiveInfo? Info,
    PerspectiveMissingStreamIdWarning? Warning
);

/// <summary>
/// Warning data when a perspective model is missing [StreamId] attribute.
/// </summary>
/// <param name="PerspectiveName">Simple name of the perspective class</param>
/// <param name="ModelName">Simple name of the model class</param>
/// <param name="FilePath">Source file path for diagnostic location</param>
/// <param name="Line">Line number in source file</param>
/// <param name="Column">Column number in source file</param>
internal sealed record PerspectiveMissingStreamIdWarning(
    string PerspectiveName,
    string ModelName,
    string FilePath,
    int Line,
    int Column
);
