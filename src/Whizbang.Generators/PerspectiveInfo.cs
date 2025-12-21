namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered perspective.
/// This record uses value equality which is critical for incremental generator performance.
/// A perspective class implements IPerspectiveFor&lt;TModel, TEvent1, TEvent2, ...&gt; with all type arguments.
/// </summary>
/// <param name="ClassName">Fully qualified class name implementing IPerspectiveFor</param>
/// <param name="InterfaceTypeArguments">All type arguments from IPerspectiveFor interface (TModel, TEvent1, TEvent2, ...) as fully qualified names</param>
/// <param name="EventTypes">Array of fully qualified event type names (extracted from InterfaceTypeArguments for diagnostics)</param>
/// <param name="StreamKeyPropertyName">Property name marked with [StreamKey] attribute (null if not found)</param>
/// <param name="EventValidationErrors">Array of validation errors for event types (event name, error type)</param>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDiscoveryGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveSchemaGeneratorTests.cs</tests>
internal sealed record PerspectiveInfo(
    string ClassName,
    string[] InterfaceTypeArguments,
    string[] EventTypes,
    string? StreamKeyPropertyName = null,
    EventValidationError[]? EventValidationErrors = null
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
