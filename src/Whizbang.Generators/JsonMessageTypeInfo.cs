namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered message type for JSON serialization.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="FullyQualifiedName">Fully qualified type name with global:: prefix (e.g., "global::MyApp.Commands.CreateOrder")</param>
/// <param name="SimpleName">Simple type name without namespace (e.g., "CreateOrder")</param>
/// <param name="IsCommand">True if type implements ICommand</param>
/// <param name="IsEvent">True if type implements IEvent</param>
/// <param name="Properties">Array of property information (name and fully qualified type)</param>
internal sealed record JsonMessageTypeInfo(
    string FullyQualifiedName,
    string SimpleName,
    bool IsCommand,
    bool IsEvent,
    PropertyInfo[] Properties
);

/// <summary>
/// Information about a property for JSON serialization.
/// </summary>
/// <param name="Name">Property name</param>
/// <param name="Type">Fully qualified type name</param>
internal sealed record PropertyInfo(
    string Name,
    string Type
);
