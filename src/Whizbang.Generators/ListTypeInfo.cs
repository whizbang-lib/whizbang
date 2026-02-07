using System.Collections.Immutable;

namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered List&lt;T&gt; type used in messages.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="ListTypeName">Fully qualified List type name (e.g., "global::System.Collections.Generic.List&lt;global::MyApp.OrderLineItem&gt;")</param>
/// <param name="ElementTypeName">Fully qualified element type name (e.g., "global::MyApp.OrderLineItem")</param>
/// <param name="ElementSimpleName">Simple element type name for method generation (e.g., "OrderLineItem")</param>
/// <tests>tests/Whizbang.Generators.Tests/ListTypeInfoTests.cs:ListTypeInfo_ValueEquality_ComparesFieldsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ListTypeInfoTests.cs:ListTypeInfo_Constructor_SetsPropertiesAsync</tests>
public sealed record ListTypeInfo(
    string ListTypeName,
    string ElementTypeName,
    string ElementSimpleName
) {
  /// <summary>
  /// Unique identifier derived from element type name, suitable for C# identifiers.
  /// Strips "global::" prefix and replaces "." with "_".
  /// E.g., "global::MyApp.Models.OrderLineItem" becomes "MyApp_Models_OrderLineItem".
  /// This prevents duplicate field/method names when element types have the same SimpleName.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithSameSimpleNameInDifferentNamespaces_GeneratesUniqueIdentifiersAsync</tests>
  public string ElementUniqueIdentifier => ElementTypeName
    .Replace("global::", "")
    .Replace(".", "_")
    .Replace("?", "__Nullable");
}
