namespace Whizbang.Data.Schema.Postgres;

/// <summary>
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
  public static string MapDataType(WhizbangDataType dataType, int? maxLength = null) {
    return dataType switch {
      WhizbangDataType.Uuid => "UUID",
      WhizbangDataType.String => maxLength.HasValue ? $"VARCHAR({maxLength.Value})" : "TEXT",
      WhizbangDataType.TimestampTz => "TIMESTAMPTZ",
      WhizbangDataType.Json => "JSONB",
      WhizbangDataType.BigInt => "BIGINT",
      WhizbangDataType.Integer => "INTEGER",
      WhizbangDataType.Boolean => "BOOLEAN",
      _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, "Unknown data type")
    };
  }

  /// <summary>
  /// Maps DefaultValue to Postgres default value expression.
  /// Handles function defaults, literal values, and proper escaping.
  /// </summary>
  /// <param name="defaultValue">Database-agnostic default value</param>
  /// <returns>Postgres default expression (e.g., "CURRENT_TIMESTAMP", "'Pending'", "42")</returns>
  public static string MapDefaultValue(DefaultValue defaultValue) {
    return defaultValue switch {
      FunctionDefault func => MapFunctionDefault(func.FunctionType),
      IntegerDefault intVal => intVal.Value.ToString(),
      StringDefault strVal => $"'{EscapeSingleQuote(strVal.Value)}'",
      BooleanDefault boolVal => boolVal.Value ? "TRUE" : "FALSE",
      NullDefault => "NULL",
      _ => throw new ArgumentOutOfRangeException(nameof(defaultValue), defaultValue, "Unknown default value type")
    };
  }

  /// <summary>
  /// Maps DefaultValueFunction to Postgres function expression.
  /// </summary>
  private static string MapFunctionDefault(DefaultValueFunction function) {
    return function switch {
      DefaultValueFunction.DateTime_Now => "CURRENT_TIMESTAMP",
      DefaultValueFunction.DateTime_UtcNow => "(NOW() AT TIME ZONE 'UTC')",
      DefaultValueFunction.Uuid_Generate => "gen_random_uuid()",
      DefaultValueFunction.Boolean_True => "TRUE",
      DefaultValueFunction.Boolean_False => "FALSE",
      _ => throw new ArgumentOutOfRangeException(nameof(function), function, "Unknown function type")
    };
  }

  /// <summary>
  /// Escapes single quotes in string values for Postgres string literals.
  /// In Postgres, single quotes are escaped by doubling them: ' becomes ''
  /// </summary>
  private static string EscapeSingleQuote(string value) {
    return value.Replace("'", "''");
  }
}
