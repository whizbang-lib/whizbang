namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered perspective.
/// This record uses value equality which is critical for incremental generator performance.
/// A perspective class implements IPerspectiveFor&lt;TModel, TEvent1, TEvent2, ...&gt; with all type arguments.
/// </summary>
/// <param name="ClassName">Fully qualified class name implementing IPerspectiveFor</param>
/// <param name="InterfaceTypeArguments">All type arguments from IPerspectiveFor interface (TModel, TEvent1, TEvent2, ...) as fully qualified names with global:: prefix for code generation</param>
/// <param name="EventTypes">Array of fully qualified event type names with global:: prefix for code generation (extracted from InterfaceTypeArguments for diagnostics)</param>
/// <param name="MessageTypeNames">Array of event type names in database format (TypeName, AssemblyName - no global:: prefix) for message association registration</param>
/// <param name="StreamKeyPropertyName">Property name marked with [StreamKey] attribute on the model (null if not found)</param>
/// <param name="EventStreamKeys">Map of event type name to its StreamKey property name</param>
/// <param name="EventValidationErrors">Array of validation errors for event types (event name, error type)</param>
/// <param name="MustExistEventTypes">Array of event type names (fully qualified) whose Apply methods have [MustExist] attribute</param>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveRunnerGeneratorTests.cs</tests>
internal sealed record PerspectiveInfo(
    string ClassName,
    string[] InterfaceTypeArguments,
    string[] EventTypes,
    string[] MessageTypeNames,
    string? StreamKeyPropertyName = null,
    EventStreamKeyInfo[]? EventStreamKeys = null,
    EventValidationError[]? EventValidationErrors = null,
    string[]? MustExistEventTypes = null,
    EventReturnTypeInfo[]? EventReturnTypes = null
);

/// <summary>
/// Maps an event type to its StreamKey property for stream ID extraction.
/// </summary>
/// <param name="EventTypeName">Fully qualified event type name</param>
/// <param name="StreamKeyPropertyName">Name of the property marked with [StreamKey]</param>
internal sealed record EventStreamKeyInfo(
    string EventTypeName,
    string StreamKeyPropertyName
);

/// <summary>
/// Represents a validation error for an event type in a perspective.
/// </summary>
/// <param name="EventTypeName">Simple name of the event type with the error</param>
/// <param name="ErrorType">Type of validation error (MissingStreamKey or MultipleStreamKeys)</param>
internal sealed record EventValidationError(
    string EventTypeName,
    StreamKeyErrorType ErrorType
);

/// <summary>
/// Types of StreamKey validation errors.
/// </summary>
internal enum StreamKeyErrorType {
  MissingStreamKey,
  MultipleStreamKeys
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
