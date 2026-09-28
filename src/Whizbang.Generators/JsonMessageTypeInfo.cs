namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered message type for JSON serialization.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="FullyQualifiedName">Fully qualified type name with global:: prefix (e.g., "global::MyApp.Commands.CreateOrder")</param>
/// <param name="ClrTypeName">Type name in CLR format for runtime type resolution. Uses "+" for nested types (e.g., "MyApp.AuthContracts+LoginCommand")</param>
/// <param name="SimpleName">Simple type name without namespace (e.g., "CreateOrder")</param>
/// <param name="IsCommand">True if type implements ICommand</param>
/// <param name="IsEvent">True if type implements IEvent</param>
/// <param name="IsSerializable">True if type is marked with [WhizbangSerializable] attribute</param>
/// <param name="IsComposite">True if type implements ICompositeEvent (wire-only; registered as IMessage but NOT IEvent, so it is never persisted)</param>
/// <param name="Properties">Array of property information (name and fully qualified type)</param>
/// <param name="HasParameterizedConstructor">True if type has a public parameterized constructor matching properties</param>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs</tests>
internal sealed record JsonMessageTypeInfo(
    string FullyQualifiedName,
    string ClrTypeName,
    string SimpleName,
    bool IsCommand,
    bool IsEvent,
    bool IsSerializable,
    bool IsComposite,
    PropertyInfo[] Properties,
    bool HasParameterizedConstructor
) {
  /// <summary>
  /// Unique identifier derived from fully qualified name, suitable for C# identifiers.
  /// Strips "global::" prefix and replaces "." with "_".
  /// E.g., "global::MyApp.Commands.StartCommand" becomes "MyApp_Commands_StartCommand".
  /// This prevents duplicate field/method names when types have the same SimpleName.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithSameSimpleNameInDifferentNamespaces_GeneratesUniqueIdentifiersAsync</tests>
  public string UniqueIdentifier => FullyQualifiedName.Replace("global::", "").Replace(".", "_");
}

/// <summary>
/// Information about a property for JSON serialization.
/// </summary>
/// <param name="Name">Property name</param>
/// <param name="Type">Fully qualified type name</param>
/// <param name="IsValueType">True if the property's underlying type is a value type (struct, enum, primitive). Used to determine correct typeof() expression for nullable types.</param>
/// <param name="IsInitOnly">True if property has init-only setter (can only be set via constructor or init)</param>
/// <param name="CanWrite">True if property has a setter (init or regular). False for computed/read-only properties.</param>
/// <param name="JsonName">The name from the property's <c>[JsonPropertyName]</c>, or <see langword="null"/> when it has none and is serialized under <paramref name="Name"/>.</param>
/// <param name="IgnoreCondition">The <c>JsonIgnoreCondition</c> member name from a conditional <c>[JsonIgnore]</c> (for example <c>WhenWritingNull</c>), or <see langword="null"/> when the property is always written.</param>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextPropertyNameTests.cs</tests>
internal sealed record PropertyInfo(
    string Name,
    string Type,
    bool IsValueType,
    bool IsInitOnly,
    bool CanWrite,
    string? JsonName = null,
    string? IgnoreCondition = null
) {
  /// <summary>The name the property is written and read under: its <c>[JsonPropertyName]</c> when present, its C# name otherwise.</summary>
  public string WireName => JsonName ?? Name;
}

/// <summary>
/// Value type containing information about a discovered enum type for JSON serialization.
/// Enums are discovered from message properties and nested type properties.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="FullyQualifiedName">Fully qualified type name with global:: prefix (e.g., "global::MyApp.OrderStatus")</param>
/// <param name="SimpleName">Simple type name without namespace (e.g., "OrderStatus")</param>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithEnumProperty_DiscoversEnumAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_NestedTypeWithEnumProperty_DiscoversEnumAsync</tests>
internal sealed record JsonEnumInfo(
    string FullyQualifiedName,
    string SimpleName
) {
  /// <summary>
  /// Unique identifier derived from fully qualified name, suitable for C# identifiers.
  /// Strips "global::" prefix and replaces "." with "_".
  /// </summary>
  public string UniqueIdentifier => FullyQualifiedName.Replace("global::", "").Replace(".", "_");
}
