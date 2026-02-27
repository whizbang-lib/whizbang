namespace Whizbang.Generators;

/// <summary>
/// Minimal value type for tracking inheritance relationships between types.
/// Used during IEvent/ICommand scanning to build a registry of derived types
/// for automatic polymorphic JSON serialization.
/// </summary>
/// <remarks>
/// <para>
/// This record uses value equality which is critical for incremental generator performance.
/// String values are interned by Roslyn's ToDisplayString() method.
/// </para>
/// <para>
/// Memory footprint is minimal: 2 interned strings + 1 bool.
/// </para>
/// </remarks>
/// <param name="DerivedTypeName">Fully qualified derived type name with global:: prefix (e.g., "global::MyApp.Events.SeedCreatedEvent")</param>
/// <param name="BaseTypeName">Fully qualified base type name with global:: prefix (e.g., "global::MyApp.BaseJdxEvent" or "global::Whizbang.Core.IEvent")</param>
/// <param name="IsInterface">True if BaseTypeName is an interface, false if it's a class</param>
/// <docs>source-generators/polymorphic-serialization</docs>
internal sealed record InheritanceInfo(
    string DerivedTypeName,
    string BaseTypeName,
    bool IsInterface
);
