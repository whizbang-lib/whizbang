namespace Whizbang.Data.Schema;

/// <summary>
/// <docs>extensibility/database-schema-framework</docs>
/// Abstract base record for column default values.
/// Pure enum-based pattern matching - no string comparisons in implementations.
/// Uses sealed record variants for type safety and structural equality (critical for incremental generators).
/// </summary>
/// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:DefaultValue_IsAbstractTypeAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:DefaultValue_IsRecordAsync</tests>
public abstract record DefaultValue {
  /// <summary>
  /// Creates a function-based default value.
  /// </summary>
  /// <param name="function">The default value function enum</param>
  /// <returns>FunctionDefault instance</returns>
  /// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:DefaultValue_FunctionFactory_ReturnsFunctionDefaultAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:FunctionDefault_PreservesFunctionValueAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/PostgresSchemaBuilderTests.cs:BuildCreateTable_WithDefaultValue_GeneratesDefaultClauseAsync</tests>
  public static DefaultValue Function(DefaultValueFunction function) => new FunctionDefault(function);

  /// <summary>
  /// Creates an integer default value.
  /// </summary>
  /// <param name="value">The integer value</param>
  /// <returns>IntegerDefault instance</returns>
  /// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:DefaultValue_IntegerFactory_ReturnsIntegerDefaultAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:IntegerDefault_PreservesIntegerValueAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/ColumnDefinitionTests.cs:ColumnDefinition_WithDefaultValue_SetsPropertyAsync</tests>
  public static DefaultValue Integer(int value) => new IntegerDefault(value);

  /// <summary>
  /// Creates a string default value.
  /// </summary>
  /// <param name="value">The string value</param>
  /// <returns>StringDefault instance</returns>
  /// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:DefaultValue_StringFactory_ReturnsStringDefaultAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:StringDefault_PreservesStringValueAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/ColumnDefinitionTests.cs:ColumnDefinition_WithAllProperties_SetsAllAsync</tests>
  public static DefaultValue String(string value) => new StringDefault(value);

  /// <summary>
  /// Creates a boolean default value.
  /// </summary>
  /// <param name="value">The boolean value</param>
  /// <returns>BooleanDefault instance</returns>
  /// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:DefaultValue_BooleanFactory_ReturnsBooleanDefaultAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:BooleanDefault_PreservesBooleanValueAsync</tests>
  public static DefaultValue Boolean(bool value) => new BooleanDefault(value);

  /// <summary>
  /// NULL default value (singleton).
  /// </summary>
  /// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:DefaultValue_Null_ReturnsNullDefaultAsync</tests>
  /// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:NullDefault_ReturnsSameSingletonInstanceAsync</tests>
  public static DefaultValue Null => NullDefault.Instance;
}

/// <summary>
/// Function-based default value using DefaultValueFunction enum.
/// </summary>
/// <param name="FunctionType">The function to use for the default value</param>
/// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:FunctionDefault_IsSealedAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:FunctionDefault_SameFunctionValue_AreEqualAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:FunctionDefault_DifferentFunctionValue_AreNotEqualAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteTypeMapperTests.cs:MapDefaultValue_FunctionDateTimeNow_ReturnsCurrentTimestampAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteTypeMapperTests.cs:MapDefaultValue_FunctionDateTimeUtcNow_ReturnsDatetimeUtcAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteTypeMapperTests.cs:MapDefaultValue_FunctionUuidGenerate_ReturnsLowerHexAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteTypeMapperTests.cs:MapDefaultValue_FunctionBooleanTrue_Returns1Async</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteTypeMapperTests.cs:MapDefaultValue_FunctionBooleanFalse_Returns0Async</tests>
public sealed record FunctionDefault(DefaultValueFunction FunctionType) : DefaultValue;

/// <summary>
/// Integer literal default value.
/// </summary>
/// <param name="Value">The integer value</param>
/// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:IntegerDefault_IsSealedAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:IntegerDefault_SameValue_AreEqualAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:IntegerDefault_DifferentValue_AreNotEqualAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteTypeMapperTests.cs:MapDefaultValue_Integer_ReturnsIntegerStringAsync</tests>
public sealed record IntegerDefault(int Value) : DefaultValue;

/// <summary>
/// String literal default value.
/// </summary>
/// <param name="Value">The string value</param>
/// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:StringDefault_IsSealedAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:StringDefault_SameValue_AreEqualAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:StringDefault_DifferentValue_AreNotEqualAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteTypeMapperTests.cs:MapDefaultValue_String_ReturnsQuotedStringAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteTypeMapperTests.cs:MapDefaultValue_StringWithSingleQuote_EscapesSingleQuoteAsync</tests>
public sealed record StringDefault(string Value) : DefaultValue;

/// <summary>
/// Boolean literal default value.
/// </summary>
/// <param name="Value">The boolean value</param>
/// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:BooleanDefault_IsSealedAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:BooleanDefault_SameValue_AreEqualAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:BooleanDefault_DifferentValue_AreNotEqualAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteTypeMapperTests.cs:MapDefaultValue_BooleanTrue_Returns1Async</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteTypeMapperTests.cs:MapDefaultValue_BooleanFalse_Returns0Async</tests>
public sealed record BooleanDefault(bool Value) : DefaultValue;

/// <summary>
/// NULL default value (singleton pattern).
/// </summary>
/// <tests>tests/Whizbang.Data.Schema.Tests/DefaultValueTests.cs:NullDefault_IsSealedAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/SqliteTypeMapperTests.cs:MapDefaultValue_Null_ReturnsNullAsync</tests>
public sealed record NullDefault : DefaultValue {
  /// <summary>
  /// Singleton instance.
  /// </summary>
  public static readonly NullDefault Instance = new();

  /// <summary>
  /// Private constructor to enforce singleton pattern.
  /// </summary>
  private NullDefault() { }
}
