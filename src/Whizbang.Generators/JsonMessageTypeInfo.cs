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
/// <param name="HasParameterizedConstructor">True if type has a public parameterized constructor matching properties</param>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs</tests>
internal sealed record JsonMessageTypeInfo(
    string FullyQualifiedName,
    string SimpleName,
    bool IsCommand,
    bool IsEvent,
    PropertyInfo[] Properties,
    bool HasParameterizedConstructor
);

/// <summary>
/// Information about a property for JSON serialization.
/// </summary>
/// <param name="Name">Property name</param>
/// <param name="Type">Fully qualified type name</param>
/// <param name="IsInitOnly">True if property has init-only setter</param>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs</tests>
internal sealed record PropertyInfo(
    string Name,
    string Type,
    bool IsInitOnly
);

/// <summary>
/// Value type containing information about a discovered WhizbangId type for JSON serialization.
/// WhizbangId types are strongly-typed ID value objects with corresponding JSON converters.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="TypeName">Fully qualified type name with global:: prefix (e.g., "global::ECommerce.Contracts.Commands.ProductId")</param>
/// <param name="SimpleName">Simple type name without namespace (e.g., "ProductId")</param>
/// <param name="ConverterName">Fully qualified converter name with global:: prefix (e.g., "global::ECommerce.Contracts.Commands.ProductIdJsonConverter")</param>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs</tests>
internal sealed record JsonWhizbangIdInfo(
    string TypeName,
    string SimpleName,
    string ConverterName
);
