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
internal sealed record ListTypeInfo(
    string ListTypeName,
    string ElementTypeName,
    string ElementSimpleName
);
