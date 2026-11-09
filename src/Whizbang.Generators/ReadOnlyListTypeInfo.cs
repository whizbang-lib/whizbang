// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered collection interface or immutable collection type used in
/// messages: <c>IReadOnlyList&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>, <c>ICollection&lt;T&gt;</c>,
/// <c>IReadOnlyCollection&lt;T&gt;</c>, <c>IEnumerable&lt;T&gt;</c> or <c>ImmutableList&lt;T&gt;</c>, none of which the
/// serializer can construct without metadata of its own on the AOT path.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="IReadOnlyListTypeName">Fully qualified IReadOnlyList type name (e.g., "global::System.Collections.Generic.IReadOnlyList&lt;global::MyApp.CatalogItem&gt;")</param>
/// <param name="ElementTypeName">Fully qualified element type name (e.g., "global::MyApp.CatalogItem")</param>
/// <param name="ElementSimpleName">Simple element type name for display (e.g., "CatalogItem")</param>
/// <param name="CollectionDefinition">The collection's generic definition, fully qualified, without its type argument
/// (e.g., "global::System.Collections.Generic.IList"). IReadOnlyList when not given.</param>
/// <tests>tests/Whizbang.Generators.Tests/ReadOnlyListTypeInfoTests.cs:IReadOnlyListTypeInfo_ValueEquality_ComparesFieldsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReadOnlyListTypeInfoTests.cs:IReadOnlyListTypeInfo_Constructor_SetsPropertiesAsync</tests>
public sealed record ReadOnlyListTypeInfo(
    string IReadOnlyListTypeName,
    string ElementTypeName,
    string ElementSimpleName,
    string CollectionDefinition = ReadOnlyListTypeInfo.READ_ONLY_LIST
) {
  /// <summary>The generic definition of <c>IReadOnlyList&lt;T&gt;</c>.</summary>
  public const string READ_ONLY_LIST = "global::System.Collections.Generic.IReadOnlyList";

  /// <summary>The generic definition of <c>IList&lt;T&gt;</c>.</summary>
  public const string LIST_INTERFACE = "global::System.Collections.Generic.IList";

  /// <summary>The generic definition of <c>ICollection&lt;T&gt;</c>.</summary>
  public const string COLLECTION_INTERFACE = "global::System.Collections.Generic.ICollection";

  /// <summary>The generic definition of <c>IReadOnlyCollection&lt;T&gt;</c>.</summary>
  public const string READ_ONLY_COLLECTION = "global::System.Collections.Generic.IReadOnlyCollection";

  /// <summary>The generic definition of <c>IEnumerable&lt;T&gt;</c>.</summary>
  public const string ENUMERABLE = "global::System.Collections.Generic.IEnumerable";

  /// <summary>The generic definition of <c>ImmutableList&lt;T&gt;</c>.</summary>
  public const string IMMUTABLE_LIST = "global::System.Collections.Immutable.ImmutableList";

  /// <summary>The collection's simple name (<c>IList</c>), which prefixes its generated field and factory.</summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextInterfaceCollectionTests.cs:AnIListProperty_GetsAFactoryAndATypeCheckAsync</tests>
  public string CollectionName => CollectionDefinition[(CollectionDefinition.LastIndexOf('.') + 1)..];

  /// <summary>Whether the collection is immutable, so it is built from its elements in one call rather than filled.</summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextInterfaceCollectionTests.cs:AnImmutableListProperty_GetsAFactoryAndItsElementIsDiscoveredAsync</tests>
  public bool IsImmutable => CollectionDefinition == IMMUTABLE_LIST;

  /// <summary>
  /// Unique identifier derived from element type name, suitable for C# identifiers.
  /// Strips "global::" prefix and replaces special characters with "_".
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/ReadOnlyListTypeInfoTests.cs:IReadOnlyListTypeInfo_ElementUniqueIdentifier_GeneratesValidIdentifierAsync</tests>
  public string ElementUniqueIdentifier => ElementTypeName
    .Replace("global::", "")
    .Replace(".", "_")
    .Replace("<", "_")
    .Replace(">", "_")
    .Replace(",", "_")
    .Replace(" ", "")
    .Replace("?", "__Nullable")
    // An array type argument (Dictionary<string, string[]>) is not an identifier either.
    .Replace("[]", "_Array")
    .Replace("[", "_")
    .Replace("]", "_");
}
