using System.Collections.Immutable;

namespace Whizbang.Generators;

/// <summary>
/// Aggregated view of a polymorphic base type and all its derived types.
/// Created by grouping InheritanceInfo records during code generation.
/// </summary>
/// <remarks>
/// <para>
/// This record is computed transiently during code generation, not cached long-term.
/// The primary cached type is <see cref="InheritanceInfo"/>.
/// </para>
/// <para>
/// Uses <see cref="ImmutableArray{T}"/> for DerivedTypes to ensure proper value equality.
/// </para>
/// </remarks>
/// <param name="BaseTypeName">Fully qualified base type name with global:: prefix</param>
/// <param name="BaseSimpleName">Simple type name without namespace, used for method naming</param>
/// <param name="DerivedTypes">All concrete derived types that inherit from or implement this base</param>
/// <param name="IsInterface">True if BaseTypeName is an interface, false if it's a class</param>
/// <docs>extending/source-generators/polymorphic-serialization</docs>
internal sealed record PolymorphicTypeInfo(
    string BaseTypeName,
    string BaseSimpleName,
    ImmutableArray<string> DerivedTypes,
    bool IsInterface
) {
  /// <summary>
  /// Unique identifier derived from fully qualified name, suitable for C# identifiers.
  /// Strips "global::" prefix and replaces "." with "_".
  /// E.g., "global::MyApp.Events.BaseEvent" becomes "MyApp_Events_BaseEvent".
  /// </summary>
  public string UniqueIdentifier => BaseTypeName.Replace("global::", "").Replace(".", "_");
}
