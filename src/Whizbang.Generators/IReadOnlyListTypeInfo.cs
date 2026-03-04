namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered IReadOnlyList&lt;T&gt; type used in messages.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="IReadOnlyListTypeName">Fully qualified IReadOnlyList type name (e.g., "global::System.Collections.Generic.IReadOnlyList&lt;global::MyApp.CatalogItem&gt;")</param>
/// <param name="ElementTypeName">Fully qualified element type name (e.g., "global::MyApp.CatalogItem")</param>
/// <param name="ElementSimpleName">Simple element type name for display (e.g., "CatalogItem")</param>
/// <tests>tests/Whizbang.Generators.Tests/IReadOnlyListTypeInfoTests.cs:IReadOnlyListTypeInfo_ValueEquality_ComparesFieldsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/IReadOnlyListTypeInfoTests.cs:IReadOnlyListTypeInfo_Constructor_SetsPropertiesAsync</tests>
public sealed record IReadOnlyListTypeInfo(
    string IReadOnlyListTypeName,
    string ElementTypeName,
    string ElementSimpleName
) {
  /// <summary>
  /// Unique identifier derived from element type name, suitable for C# identifiers.
  /// Strips "global::" prefix and replaces special characters with "_".
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/IReadOnlyListTypeInfoTests.cs:IReadOnlyListTypeInfo_ElementUniqueIdentifier_GeneratesValidIdentifierAsync</tests>
  public string ElementUniqueIdentifier => ElementTypeName
    .Replace("global::", "")
    .Replace(".", "_")
    .Replace("<", "_")
    .Replace(">", "_")
    .Replace(",", "_")
    .Replace(" ", "")
    .Replace("?", "__Nullable");
}
