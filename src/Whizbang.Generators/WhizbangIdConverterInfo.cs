namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered WhizbangId type.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="TypeName">Simple type name (e.g., "ProductId")</param>
/// <param name="FullyQualifiedTypeName">Fully qualified type name (e.g., "global::MyApp.ProductId")</param>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdTypeInfoTests.cs:WhizbangIdTypeInfo_ValueEquality_ComparesFieldsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdTypeInfoTests.cs:WhizbangIdTypeInfo_Constructor_SetsPropertiesAsync</tests>
internal sealed record WhizbangIdTypeInfo(
  string TypeName,
  string FullyQualifiedTypeName
);
