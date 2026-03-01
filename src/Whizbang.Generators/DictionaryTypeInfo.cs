namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered Dictionary&lt;TKey, TValue&gt; type used in messages.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="DictionaryTypeName">Fully qualified Dictionary type name (e.g., "global::System.Collections.Generic.Dictionary&lt;string, global::MyApp.SeedSectionContext&gt;")</param>
/// <param name="KeyTypeName">Fully qualified key type name (e.g., "string")</param>
/// <param name="ValueTypeName">Fully qualified value type name (e.g., "global::MyApp.SeedSectionContext")</param>
/// <param name="ValueSimpleName">Simple value type name for method generation (e.g., "SeedSectionContext")</param>
/// <tests>tests/Whizbang.Generators.Tests/DictionaryTypeInfoTests.cs:DictionaryTypeInfo_ValueEquality_ComparesFieldsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/DictionaryTypeInfoTests.cs:DictionaryTypeInfo_Constructor_SetsPropertiesAsync</tests>
public sealed record DictionaryTypeInfo(
    string DictionaryTypeName,
    string KeyTypeName,
    string ValueTypeName,
    string ValueSimpleName
) {
  /// <summary>
  /// Unique identifier derived from key and value type names, suitable for C# identifiers.
  /// Strips "global::" prefix and replaces special characters with "_".
  /// E.g., "Dictionary&lt;string, global::MyApp.Models.SeedContext&gt;" becomes "string_MyApp_Models_SeedContext".
  /// This prevents duplicate field/method names when value types have the same SimpleName.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/DictionaryTypeInfoTests.cs:DictionaryTypeInfo_UniqueIdentifier_GeneratesValidIdentifierAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/DictionaryTypeInfoTests.cs:DictionaryTypeInfo_UniqueIdentifier_HandlesNullableValueTypeAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/DictionaryTypeInfoTests.cs:DictionaryTypeInfo_UniqueIdentifier_HandlesGenericValueTypeAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/DictionaryTypeInfoTests.cs:DictionaryTypeInfo_UniqueIdentifier_DifferentValuesProduceDifferentIdentifiersAsync</tests>
  public string UniqueIdentifier => $"{KeyTypeName}_{ValueTypeName}"
    .Replace("global::", "")
    .Replace(".", "_")
    .Replace("<", "_")
    .Replace(">", "_")
    .Replace(",", "_")
    .Replace(" ", "")
    .Replace("?", "__Nullable");
}
