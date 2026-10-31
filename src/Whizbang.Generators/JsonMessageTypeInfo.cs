// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Generators.Shared.Utilities;

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
    EquatableArray<PropertyInfo> Properties,
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

  /// <summary>
  /// True when the type was discovered as a perspective model (the <c>TModel</c> of a perspective interface): its
  /// stored form is a perspective row, derived data the startup pass rewrites (#1874).
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextRequiredMemberTests.cs:Generator_RequiredMembersOfAPerspectiveModel_AreMarkedRequired_UnlessTheyAdmitNullAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextRequiredMemberTests.cs:Generator_ANestedModelAndAPolymorphicMember_AreMarkedThroughTheModelAsync</tests>
  public bool IsPerspectiveModel { get; init; }

  /// <summary>
  /// How the generated reader treats a document missing one of the type's <see cref="PropertyInfo.IsRequired"/>
  /// members, decided by which roots reach the type (#1874).
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextRequiredMemberTests.cs</tests>
  public RequiredMemberReading RequiredMembers { get; init; }
}

/// <summary>
/// How the generated reader treats a document missing a member the type declares required (#1874). Required is
/// enforced on perspective rows only: events are immutable history read tolerantly and upcast, so a replay or rebuild
/// never fails because a later change added <see langword="required"/>; new messages are protected by the compiler's
/// <see langword="required"/> at construction.
/// </summary>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextRequiredMemberTests.cs</tests>
internal enum RequiredMemberReading {
  /// <summary>
  /// The member reads as its default. Every type no perspective model reaches: events, commands, composites and
  /// other serializable types, and the types they carry.
  /// </summary>
  Tolerated,

  /// <summary>
  /// The reader refuses the document with a <c>JsonException</c>. A perspective model and the types only perspective
  /// models reach.
  /// </summary>
  Refused,

  /// <summary>
  /// The member reads as its default, because one JsonTypeInfo per type also reads the event or message that carries
  /// it, and that history must stay readable; the generated module initializer declares the member to
  /// <c>DeclaredRequiredMembers</c> so the startup check still reports perspective rows missing it, at Error.
  /// </summary>
  Reported,
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
/// <param name="IsRequired">True when the property is declared required: a <c>required</c> or <c>[JsonRequired]</c> member whose type does not admit null. Whether a document missing it is refused depends on the declaring type's <see cref="JsonMessageTypeInfo.RequiredMembers"/>.</param>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextPropertyNameTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextRequiredMemberTests.cs</tests>
internal sealed record PropertyInfo(
    string Name,
    string Type,
    bool IsValueType,
    bool IsInitOnly,
    bool CanWrite,
    string? JsonName = null,
    string? IgnoreCondition = null,
    bool IsRequired = false
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

/// <summary>
/// What the JSON context generator reads from the compilation, as values: the assembly name, the deduplicated
/// message types, the nested types their properties use, the enums among them, and the polymorphic base types.
/// Value-equal while those are, so an edit that changes none of them leaves the generated context cached.
/// </summary>
/// <param name="AssemblyName">The compilation's assembly name.</param>
/// <param name="Messages">The message types, deduplicated by fully qualified name, first entry kept.</param>
/// <param name="NestedTypes">The custom types the message properties use.</param>
/// <param name="EnumTypes">The enum types among the messages and nested types.</param>
/// <param name="PolymorphicTypes">The polymorphic base types with their derived types.</param>
internal sealed record JsonContextAnalysis(
    string? AssemblyName,
    EquatableArray<JsonMessageTypeInfo> Messages,
    EquatableArray<JsonMessageTypeInfo> NestedTypes,
    EquatableArray<JsonEnumInfo> EnumTypes,
    EquatableArray<PolymorphicTypeInfo> PolymorphicTypes
);
