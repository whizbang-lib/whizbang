namespace Whizbang.Data.Postgres.Schema;

/// <summary>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresTypeMapperTests.cs:MapDataType_Uuid_ReturnsUuidAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresTypeMapperTests.cs:MapDataType_String_ReturnsTextAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresTypeMapperTests.cs:MapDataType_StringWithMaxLength_ReturnsVarcharAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresTypeMapperTests.cs:MapDataType_TimestampTz_ReturnsTimestamptzAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresTypeMapperTests.cs:MapDataType_Json_ReturnsJsonbAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresTypeMapperTests.cs:MapDataType_BigInt_ReturnsBigintAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresTypeMapperTests.cs:MapDataType_Integer_ReturnsIntegerAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresTypeMapperTests.cs:MapDataType_Boolean_ReturnsBooleanAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresTypeMapperTests.cs:MapDefaultValue_FunctionDateTimeNow_ReturnsCurrentTimestampAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresTypeMapperTests.cs:MapDefaultValue_FunctionDateTimeUtcNow_ReturnsUtcExpressionAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresTypeMapperTests.cs:MapDefaultValue_FunctionUuidGenerate_ReturnsGenRandomUuidAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresTypeMapperTests.cs:MapDefaultValue_FunctionBooleanTrue_ReturnsTrueAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresTypeMapperTests.cs:MapDefaultValue_FunctionBooleanFalse_ReturnsFalseAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresTypeMapperTests.cs:MapDefaultValue_Integer_ReturnsIntegerStringAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresTypeMapperTests.cs:MapDefaultValue_String_ReturnsQuotedStringAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresTypeMapperTests.cs:MapDefaultValue_StringWithSingleQuote_EscapesSingleQuoteAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresTypeMapperTests.cs:MapDefaultValue_BooleanTrue_ReturnsTrueAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresTypeMapperTests.cs:MapDefaultValue_BooleanFalse_ReturnsFalseAsync</tests>
/// <tests>tests/Whizbang.Data.Schema.Tests/PostgresTypeMapperTests.cs:MapDefaultValue_Null_ReturnsNullAsync</tests>
/// Maps database-agnostic Whizbang types to Postgres-specific SQL types and expressions.
/// Uses pure enum-based pattern matching with zero string comparisons.
/// </summary>
public static class PostgresTypeMapper {
  /// <summary>
  /// Maps WhizbangDataType to Postgres SQL type.
  /// </summary>
  /// <param name="dataType">Database-agnostic data type</param>
  /// <param name="maxLength">Optional maximum length for string types</param>
  /// <returns>Postgres SQL type string (e.g., "UUID", "VARCHAR(255)", "JSONB")</returns>
  public static string MapDataType(Whizbang.Data.Schema.WhizbangDataType dataType, int? maxLength = null) {
    return dataType switch {
      Whizbang.Data.Schema.WhizbangDataType.UUID => "UUID",
      Whizbang.Data.Schema.WhizbangDataType.STRING => maxLength.HasValue ? $"VARCHAR({maxLength.Value})" : "TEXT",
      Whizbang.Data.Schema.WhizbangDataType.TIMESTAMP_TZ => "TIMESTAMPTZ",
      Whizbang.Data.Schema.WhizbangDataType.JSON => "JSONB",
      Whizbang.Data.Schema.WhizbangDataType.BIG_INT => "BIGINT",
      Whizbang.Data.Schema.WhizbangDataType.INTEGER => "INTEGER",
      Whizbang.Data.Schema.WhizbangDataType.SMALL_INT => "SMALLINT",
      Whizbang.Data.Schema.WhizbangDataType.BOOLEAN => "BOOLEAN",
      _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, "Unknown data type")
    };
  }

  /// <summary>
  /// Maps DefaultValue to Postgres default value expression.
  /// Handles function defaults, literal values, and proper escaping.
  /// </summary>
  /// <param name="defaultValue">Database-agnostic default value</param>
  /// <returns>Postgres default expression (e.g., "CURRENT_TIMESTAMP", "'Pending'", "42")</returns>
  public static string MapDefaultValue(Whizbang.Data.Schema.DefaultValue defaultValue) {
    return defaultValue switch {
      Whizbang.Data.Schema.FunctionDefault func => _mapFunctionDefault(func.FunctionType),
      Whizbang.Data.Schema.IntegerDefault intVal => intVal.Value.ToString(),
      Whizbang.Data.Schema.StringDefault strVal => $"'{_escapeSingleQuote(strVal.Value)}'",
      Whizbang.Data.Schema.BooleanDefault boolVal => boolVal.Value ? "TRUE" : "FALSE",
      Whizbang.Data.Schema.NullDefault => "NULL",
      _ => throw new ArgumentOutOfRangeException(nameof(defaultValue), defaultValue, "Unknown default value type")
    };
  }

  /// <summary>
  /// Maps DefaultValueFunction to Postgres function expression.
  /// </summary>
  private static string _mapFunctionDefault(Whizbang.Data.Schema.DefaultValueFunction function) {
    return function switch {
      Whizbang.Data.Schema.DefaultValueFunction.DATE_TIME__NOW => "CURRENT_TIMESTAMP",
      Whizbang.Data.Schema.DefaultValueFunction.DATE_TIME__UTC_NOW => "(NOW() AT TIME ZONE 'UTC')",
      Whizbang.Data.Schema.DefaultValueFunction.UUID__GENERATE => "gen_random_uuid()",
      Whizbang.Data.Schema.DefaultValueFunction.BOOLEAN__TRUE => "TRUE",
      Whizbang.Data.Schema.DefaultValueFunction.BOOLEAN__FALSE => "FALSE",
      _ => throw new ArgumentOutOfRangeException(nameof(function), function, "Unknown function type")
    };
  }

  /// <summary>
  /// Escapes single quotes in string values for Postgres string literals.
  /// In Postgres, single quotes are escaped by doubling them: ' becomes ''
  /// </summary>
  private static string _escapeSingleQuote(string value) {
    return value.Replace("'", "''");
  }
}
