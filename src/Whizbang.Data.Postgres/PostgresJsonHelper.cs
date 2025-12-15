using System;
using Npgsql;
using NpgsqlTypes;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Helper methods for working with PostgreSQL JSONB columns.
/// Provides AOT-compatible conversion between JSON strings and PostgreSQL JSONB type.
/// Note: For serialization/deserialization, use your application's JsonSerializerContext
/// with source-generated JsonTypeInfo to ensure AOT compatibility.
/// </summary>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresJsonHelperTests.cs</tests>
public static class PostgresJsonHelper {
  /// <summary>
  /// Converts a JSON string to a PostgreSQL JSONB parameter.
  /// Caller is responsible for serializing objects to JSON using source-generated JsonTypeInfo.
  /// </summary>
  /// <param name="json">JSON string to use as JSONB value</param>
  /// <returns>NpgsqlParameter configured for JSONB type</returns>
  /// <example>
  /// // Using source-generated JsonSerializerContext:
  /// var json = JsonSerializer.Serialize(myObject, MyJsonContext.Default.MyObjectType);
  /// var param = PostgresJsonHelper.JsonStringToJsonb(json);
  /// </example>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresJsonHelperTests.cs:JsonStringToJsonb_ValidJsonString_CreatesJsonbParameterAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresJsonHelperTests.cs:JsonStringToJsonb_NullInput_CreatesNullJsonbParameterAsync</tests>
  public static NpgsqlParameter JsonStringToJsonb(string? json) {
    return new NpgsqlParameter {
      Value = json ?? "null",
      NpgsqlDbType = NpgsqlDbType.Jsonb
    };
  }

  /// <summary>
  /// Creates an empty JSONB array parameter [].
  /// Useful for passing empty arrays to PostgreSQL functions.
  /// </summary>
  /// <returns>NpgsqlParameter configured for empty JSONB array</returns>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresJsonHelperTests.cs:EmptyJsonbArray_CreatesEmptyArrayParameterAsync</tests>
  public static NpgsqlParameter EmptyJsonbArray() {
    return new NpgsqlParameter {
      Value = "[]",
      NpgsqlDbType = NpgsqlDbType.Jsonb
    };
  }

  /// <summary>
  /// Creates a null JSONB parameter.
  /// </summary>
  /// <returns>NpgsqlParameter configured for null JSONB value</returns>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresJsonHelperTests.cs:NullJsonb_CreatesNullJsonbParameterAsync</tests>
  public static NpgsqlParameter NullJsonb() {
    return new NpgsqlParameter {
      Value = "null",
      NpgsqlDbType = NpgsqlDbType.Jsonb
    };
  }
}
