namespace Whizbang.Data.Dapper.Postgres.Schema;

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
  public static string MapDataType(Whizbang.Data.Schema.WhizbangDataType dataType, int? maxLength = null) {
    return dataType switch {
      Whizbang.Data.Schema.WhizbangDataType.Uuid => "UUID",
      Whizbang.Data.Schema.WhizbangDataType.String => maxLength.HasValue ? $"VARCHAR({maxLength.Value})" : "TEXT",
      Whizbang.Data.Schema.WhizbangDataType.TimestampTz => "TIMESTAMPTZ",
      Whizbang.Data.Schema.WhizbangDataType.Json => "JSONB",
      Whizbang.Data.Schema.WhizbangDataType.BigInt => "BIGINT",
      Whizbang.Data.Schema.WhizbangDataType.Integer => "INTEGER",
      Whizbang.Data.Schema.WhizbangDataType.SmallInt => "SMALLINT",
      Whizbang.Data.Schema.WhizbangDataType.Boolean => "BOOLEAN",
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
      Whizbang.Data.Schema.FunctionDefault func => MapFunctionDefault(func.FunctionType),
      Whizbang.Data.Schema.IntegerDefault intVal => intVal.Value.ToString(),
      Whizbang.Data.Schema.StringDefault strVal => $"'{EscapeSingleQuote(strVal.Value)}'",
      Whizbang.Data.Schema.BooleanDefault boolVal => boolVal.Value ? "TRUE" : "FALSE",
      Whizbang.Data.Schema.NullDefault => "NULL",
      _ => throw new ArgumentOutOfRangeException(nameof(defaultValue), defaultValue, "Unknown default value type")
    };
  }

  /// <summary>
  /// Maps DefaultValueFunction to Postgres function expression.
  /// </summary>
  private static string MapFunctionDefault(Whizbang.Data.Schema.DefaultValueFunction function) {
    return function switch {
      Whizbang.Data.Schema.DefaultValueFunction.DateTime_Now => "CURRENT_TIMESTAMP",
      Whizbang.Data.Schema.DefaultValueFunction.DateTime_UtcNow => "(NOW() AT TIME ZONE 'UTC')",
      Whizbang.Data.Schema.DefaultValueFunction.Uuid_Generate => "gen_random_uuid()",
      Whizbang.Data.Schema.DefaultValueFunction.Boolean_True => "TRUE",
      Whizbang.Data.Schema.DefaultValueFunction.Boolean_False => "FALSE",
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
