namespace Whizbang.Data.Dapper.Sqlite.Schema;

/// <summary>
/// Maps database-agnostic Whizbang types to SQLite-specific SQL types and expressions.
/// Uses pure enum-based pattern matching with zero string comparisons.
///
/// SQLite Type System Notes:
/// - SQLite uses dynamic typing with type affinity (TEXT, INTEGER, REAL, BLOB, NULL)
/// - UUIDs stored as TEXT (hex format) since SQLite has no native UUID type
/// - JSON stored as TEXT (SQLite has JSON1 extension for querying)
/// - Timestamps stored as TEXT in ISO8601 format
/// - Booleans stored as INTEGER (0 = false, 1 = true)
/// </summary>
public static class SqliteTypeMapper {
  /// <summary>
  /// Maps WhizbangDataType to SQLite SQL type.
  /// </summary>
  /// <param name="dataType">Database-agnostic data type</param>
  /// <param name="maxLength">Optional maximum length (ignored in SQLite, no length enforcement)</param>
  /// <returns>SQLite SQL type string (e.g., "TEXT", "INTEGER")</returns>
  public static string MapDataType(Whizbang.Data.Schema.WhizbangDataType dataType, int? maxLength = null) {
    return dataType switch {
      Whizbang.Data.Schema.WhizbangDataType.Uuid => "TEXT",        // Stored as hex string
      Whizbang.Data.Schema.WhizbangDataType.String => "TEXT",      // No length enforcement
      Whizbang.Data.Schema.WhizbangDataType.TimestampTz => "TEXT", // ISO8601 format: 'YYYY-MM-DD HH:MM:SS.SSS'
      Whizbang.Data.Schema.WhizbangDataType.Json => "TEXT",        // JSON as text (use JSON1 extension for querying)
      Whizbang.Data.Schema.WhizbangDataType.BigInt => "INTEGER",   // 64-bit signed integer
      Whizbang.Data.Schema.WhizbangDataType.Integer => "INTEGER",  // Stored as INTEGER affinity
      Whizbang.Data.Schema.WhizbangDataType.Boolean => "INTEGER",  // 0 or 1
      _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, "Unknown data type")
    };
  }

  /// <summary>
  /// Maps DefaultValue to SQLite default value expression.
  /// Handles function defaults, literal values, and proper escaping.
  /// </summary>
  /// <param name="defaultValue">Database-agnostic default value</param>
  /// <returns>SQLite default expression (e.g., "CURRENT_TIMESTAMP", "'Pending'", "42", "0", "1")</returns>
  public static string MapDefaultValue(Whizbang.Data.Schema.DefaultValue defaultValue) {
    return defaultValue switch {
      Whizbang.Data.Schema.FunctionDefault func => MapFunctionDefault(func.FunctionType),
      Whizbang.Data.Schema.IntegerDefault intVal => intVal.Value.ToString(),
      Whizbang.Data.Schema.StringDefault strVal => $"'{EscapeSingleQuote(strVal.Value)}'",
      Whizbang.Data.Schema.BooleanDefault boolVal => boolVal.Value ? "1" : "0",
      Whizbang.Data.Schema.NullDefault => "NULL",
      _ => throw new ArgumentOutOfRangeException(nameof(defaultValue), defaultValue, "Unknown default value type")
    };
  }

  /// <summary>
  /// Maps DefaultValueFunction to SQLite function expression.
  /// </summary>
  private static string MapFunctionDefault(Whizbang.Data.Schema.DefaultValueFunction function) {
    return function switch {
      // CURRENT_TIMESTAMP returns UTC time in 'YYYY-MM-DD HH:MM:SS' format
      Whizbang.Data.Schema.DefaultValueFunction.DateTime_Now => "CURRENT_TIMESTAMP",

      // datetime('now', 'utc') explicitly requests UTC
      Whizbang.Data.Schema.DefaultValueFunction.DateTime_UtcNow => "(datetime('now', 'utc'))",

      // SQLite doesn't have native UUID generation
      // Use randomblob(16) and convert to lowercase hex string
      Whizbang.Data.Schema.DefaultValueFunction.Uuid_Generate => "(lower(hex(randomblob(16))))",

      // Booleans as integers
      Whizbang.Data.Schema.DefaultValueFunction.Boolean_True => "1",
      Whizbang.Data.Schema.DefaultValueFunction.Boolean_False => "0",

      _ => throw new ArgumentOutOfRangeException(nameof(function), function, "Unknown function type")
    };
  }

  /// <summary>
  /// Escapes single quotes in string values for SQLite string literals.
  /// In SQLite, single quotes are escaped by doubling them: ' becomes ''
  /// </summary>
  private static string EscapeSingleQuote(string value) {
    return value.Replace("'", "''");
  }
}
